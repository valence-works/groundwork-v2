using Groundwork.Kernel;
using MongoDB.Bson;

namespace Groundwork.MongoDb;

/// <summary>
/// Maps the deliberately small, provider-owned subset of MongoDB's explain output. The
/// explain format is not a public serialization contract, so anything outside this closed
/// grammar withholds the complete forest rather than returning a partial access path.
/// </summary>
internal static class MongoNativePlanMapper
{
    private static readonly string[] UnsupportedChildFields =
    [
        "thenStage",
        "elseStage",
        "innerStage",
        "outerStage",
        "shards"
    ];

    // Aggregate executionStats places these scalar counters beside each pipeline operator.
    // They are deliberately validated and discarded: no execution telemetry is part of this
    // structural mapper. An unrecognized sibling is not assumed to be harmless metadata.
    private static readonly HashSet<string> PipelineExecutionMetadata = new(StringComparer.Ordinal)
    {
        "nReturned",
        "executionTimeMillisEstimate",
        "executionTimeMillis",
        "executionSuccess",
        "totalKeysExamined",
        "totalDocsExamined",
        "collectionScans",
        "totalDataSizeSorted",
        "totalDataSizeSortedBytesEstimate",
        "usedDisk",
        "spills",
        "spilledBytes",
        "spilledRecords",
        "spilledDataStorageSize",
        "peakTrackedMemBytes",
        "maxAccumulatorMemoryUsageBytes",
        "maxOutputDocumentSizeBytes",
        "indexesUsed"
    };

    internal static ProviderPlanForest? Map(
        BsonDocument explain,
        string expectedNamespace,
        ProviderOpaqueIdentity targetId,
        Func<string, ProviderOpaqueIdentity> indexIdentity,
        IReadOnlyDictionary<string, string> logicalIndexesByPhysicalName)
    {
        ArgumentNullException.ThrowIfNull(explain);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedNamespace);
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(indexIdentity);
        ArgumentNullException.ThrowIfNull(logicalIndexesByPhysicalName);

        if (!ValidateLogicalIndexMap(logicalIndexesByPhysicalName))
            return null;

        try
        {
            var roots = TryReadRoots(explain, expectedNamespace);
            if (roots is null)
                return null;

            var context = new MappingContext(indexIdentity, logicalIndexesByPhysicalName);
            foreach (var root in roots)
            {
                if (root.NativeDocument is not null)
                {
                    if (!context.ReadPlanNode(root.NativeDocument, parentId: null))
                        return null;
                }
                else if (root.PipelineOperation is { } operation)
                {
                    context.AddPipelineStage(operation);
                }
                else
                {
                    return null;
                }
            }

            if (context.Nodes.Count == 0 || !context.ApplyCoveringFacts())
                return null;

            return new ProviderPlanForest(context.Nodes.Select(node => node.ToPublic(targetId)));
        }
        catch (ArgumentException)
        {
            // A malformed native payload is not a provider execution failure. It is an
            // unmapped plan, and the caller must retain no partial forest.
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static IReadOnlyList<PlanRoot>? TryReadRoots(
        BsonDocument explain,
        string expectedNamespace)
    {
        var queryPlannerCount = Count(explain, "queryPlanner");
        var stagesCount = Count(explain, "stages");
        if (queryPlannerCount > 1 || stagesCount > 1 || (queryPlannerCount != 0 && stagesCount != 0))
            return null;

        if (queryPlannerCount == 1)
        {
            if (!explain.TryGetValue("queryPlanner", out var queryPlannerValue) ||
                !queryPlannerValue.IsBsonDocument)
                return null;

            var winningRoots = TryReadWinningPlan(queryPlannerValue.AsBsonDocument, expectedNamespace);
            return winningRoots?.Select(document => new PlanRoot(document, null)).ToArray();
        }

        if (stagesCount != 1 ||
            !explain.TryGetValue("stages", out var stagesValue) ||
            !stagesValue.IsBsonArray)
            return null;

        var stages = stagesValue.AsBsonArray;
        if (stages.Count == 0)
            return null;

        var roots = new List<PlanRoot>();
        var cursorCount = 0;
        foreach (var value in stages)
        {
            if (!value.IsBsonDocument)
                return null;

            var stage = value.AsBsonDocument;
            if (!TryReadPipelineOperator(stage, out var element))
                return null;

            if (string.Equals(element.Name, "$cursor", StringComparison.Ordinal))
            {
                cursorCount++;
                if (cursorCount != 1 || roots.Count != 0 || !element.Value.IsBsonDocument)
                    return null;

                var cursor = element.Value.AsBsonDocument;
                if (Count(cursor, "queryPlanner") != 1 ||
                    !cursor.TryGetValue("queryPlanner", out var queryPlanner) ||
                    !queryPlanner.IsBsonDocument)
                    return null;

                var winningRoots = TryReadWinningPlan(queryPlanner.AsBsonDocument, expectedNamespace);
                if (winningRoots is null)
                    return null;
                roots.AddRange(winningRoots.Select(document => new PlanRoot(document, null)));
                continue;
            }

            if (!TryMapAggregationStage(element.Name, element.Value, out var operation))
                return null;

            // Outer aggregation stages have no parent edge in Mongo's explain output. They
            // remain sibling roots; assigning a chain would invent an execution dependency
            // that ProviderPlanForest deliberately does not claim.
            roots.Add(new PlanRoot(null, operation));
        }

        return cursorCount == 1 ? roots : null;
    }

    private static bool TryReadPipelineOperator(BsonDocument stage, out BsonElement operation)
    {
        operation = default;
        var operatorCount = 0;
        foreach (var element in stage)
        {
            if (element.Name.StartsWith("$", StringComparison.Ordinal))
            {
                operatorCount++;
                if (operatorCount > 1)
                    return false;
                operation = element;
                continue;
            }

            if (!PipelineExecutionMetadata.Contains(element.Name) ||
                Count(stage, element.Name) != 1 ||
                !IsValidPipelineMetadata(element.Name, element.Value))
                return false;
        }

        return operatorCount == 1;
    }

    private static bool IsValidPipelineMetadata(string name, BsonValue value)
    {
        if (name is "executionSuccess" or "usedDisk")
            return value.IsBoolean;

        if (name == "maxAccumulatorMemoryUsageBytes")
            return value.IsBsonDocument;

        if (name == "indexesUsed")
            return value.IsBsonArray && value.AsBsonArray.All(item => item.IsString);

        return value.IsNumeric;
    }

    private static IReadOnlyList<BsonDocument>? TryReadWinningPlan(
        BsonDocument queryPlanner,
        string expectedNamespace)
    {
        if (Count(queryPlanner, "namespace") != 1 ||
            !queryPlanner.TryGetValue("namespace", out var namespaceValue) ||
            !namespaceValue.IsString ||
            !string.Equals(namespaceValue.AsString, expectedNamespace, StringComparison.Ordinal))
            return null;

        if (Count(queryPlanner, "winningPlan") != 1 ||
            !queryPlanner.TryGetValue("winningPlan", out var winningValue) ||
            !winningValue.IsBsonDocument)
            return null;

        var winning = winningValue.AsBsonDocument;
        if (Count(winning, "queryPlan") > 1 ||
            (Count(winning, "queryPlan") == 1 && Count(winning, "stage") != 0))
            return null;

        if (Count(winning, "queryPlan") == 1)
        {
            if (!winning.TryGetValue("queryPlan", out var queryPlan) || !queryPlan.IsBsonDocument)
                return null;
            return [queryPlan.AsBsonDocument];
        }

        return [winning];
    }

    private static bool TryMapAggregationStage(
        string name,
        BsonValue value,
        out ProviderPlanOperator operation)
    {
        operation = name switch
        {
            "$set" or "$addFields" => ProviderPlanOperator.Compute,
            "$project" => ProviderPlanOperator.Projection,
            "$skip" => ProviderPlanOperator.Offset,
            "$sort" => ProviderPlanOperator.Sort,
            "$limit" => ProviderPlanOperator.Limit,
            _ => ProviderPlanOperator.Unknown
        };

        if (operation == ProviderPlanOperator.Unknown)
            return false;

        if (operation is ProviderPlanOperator.Compute or
            ProviderPlanOperator.Projection or
            ProviderPlanOperator.Sort)
            return value.IsBsonDocument;

        if (operation is ProviderPlanOperator.Offset or ProviderPlanOperator.Limit)
        {
            if (!value.IsInt32 && !value.IsInt64)
                return false;

            var number = value.IsInt32 ? value.AsInt32 : value.AsInt64;
            return operation == ProviderPlanOperator.Offset ? number >= 0 : number > 0;
        }

        return false;
    }

    private static bool ValidateLogicalIndexMap(IReadOnlyDictionary<string, string> map)
    {
        foreach (var pair in map)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                return false;
        }

        return true;
    }

    private static int Count(BsonDocument document, string name) =>
        document.Count(element => string.Equals(element.Name, name, StringComparison.Ordinal));

    private readonly record struct PlanRoot(
        BsonDocument? NativeDocument,
        ProviderPlanOperator? PipelineOperation);

    private sealed class MappingContext
    {
        private readonly Func<string, ProviderOpaqueIdentity> indexIdentity;
        private readonly IReadOnlyDictionary<string, string> logicalIndexesByPhysicalName;
        private readonly Dictionary<string, ProviderOpaqueIdentity> indexIdentities = new(StringComparer.Ordinal);
        private readonly HashSet<int> nativePlanIds = [];
        private int nextId;

        internal MappingContext(
            Func<string, ProviderOpaqueIdentity> indexIdentity,
            IReadOnlyDictionary<string, string> logicalIndexesByPhysicalName)
        {
            this.indexIdentity = indexIdentity;
            this.logicalIndexesByPhysicalName = logicalIndexesByPhysicalName;
        }

        internal List<MutableNode> Nodes { get; } = [];

        internal void AddPipelineStage(ProviderPlanOperator operation) =>
            Nodes.Add(new MutableNode(nextId++, parentId: null, operation, nativeStage: null));

        internal bool ReadPlanNode(BsonDocument document, int? parentId)
        {
            if (document.ElementCount == 0 ||
                Count(document, "stage") != 1 ||
                !document.TryGetValue("stage", out var stageValue) ||
                !stageValue.IsString)
                return false;

            if (UnsupportedChildFields.Any(field => Count(document, field) != 0))
                return false;

            if (!TryReadNativePlanId(document))
                return false;

            if (!TryMapPlanStage(document, stageValue.AsString, out var operation, out var indexName))
                return false;

            var children = ReadChildren(document);
            if (children is null ||
                IsLeaf(operation) && (Count(document, "inputStage") != 0 || Count(document, "inputStages") != 0) ||
                !HasValidArity(operation, children.Count))
                return false;

            var id = nextId++;
            var node = new MutableNode(id, parentId, operation, stageValue.AsString);
            if (operation is ProviderPlanOperator.IndexScan or ProviderPlanOperator.IndexSearch)
            {
                if (indexName is null)
                    return false;

                node.IndexName = indexName;
                if (!indexIdentities.TryGetValue(indexName, out var indexId))
                {
                    indexId = indexIdentity(indexName);
                    if (indexId is null)
                        return false;
                    indexIdentities.Add(indexName, indexId);
                }

                node.IndexId = indexId;
                if (logicalIndexesByPhysicalName.TryGetValue(indexName, out var logicalName))
                    node.LogicalIndexName = logicalName;
            }

            Nodes.Add(node);
            foreach (var child in children)
            {
                if (!ReadPlanNode(child, id))
                    return false;
            }

            return true;
        }

        internal bool ApplyCoveringFacts()
        {
            foreach (var projection in Nodes.Where(node => node.NativeStage == "PROJECTION_COVERED"))
            {
                var descendants = Descendants(projection.Id).ToArray();
                var accesses = descendants
                    .Where(node => node.Operation is ProviderPlanOperator.IndexScan or ProviderPlanOperator.IndexSearch)
                    .ToArray();
                if (accesses.Length == 0 || descendants.Any(node => node.Operation == ProviderPlanOperator.Materialize))
                    return false;

                foreach (var access in accesses)
                    access.IsCovering = true;
            }

            return true;
        }

        private IEnumerable<MutableNode> Descendants(int parentId)
        {
            var children = Nodes.Where(node => node.ParentId == parentId).ToArray();
            foreach (var child in children)
            {
                yield return child;
                foreach (var descendant in Descendants(child.Id))
                    yield return descendant;
            }
        }

        private bool TryReadNativePlanId(BsonDocument document)
        {
            if (Count(document, "planNodeId") == 0)
                return true;
            if (Count(document, "planNodeId") != 1 ||
                !document.TryGetValue("planNodeId", out var idValue) ||
                !idValue.IsInt32 ||
                idValue.AsInt32 < 0)
                return false;
            return nativePlanIds.Add(idValue.AsInt32);
        }

        private static IReadOnlyList<BsonDocument>? ReadChildren(BsonDocument document)
        {
            var inputStageCount = Count(document, "inputStage");
            var inputStagesCount = Count(document, "inputStages");
            if (inputStageCount > 1 || inputStagesCount > 1 || inputStageCount != 0 && inputStagesCount != 0)
                return null;

            if (inputStageCount == 1)
            {
                if (!document.TryGetValue("inputStage", out var value) || !value.IsBsonDocument)
                    return null;
                return [value.AsBsonDocument];
            }

            if (inputStagesCount == 1)
            {
                if (!document.TryGetValue("inputStages", out var value) || !value.IsBsonArray)
                    return null;
                var children = new List<BsonDocument>();
                foreach (var child in value.AsBsonArray)
                {
                    if (!child.IsBsonDocument)
                        return null;
                    children.Add(child.AsBsonDocument);
                }

                return children;
            }

            return [];
        }

        private static bool HasValidArity(ProviderPlanOperator operation, int childCount) => operation switch
        {
            ProviderPlanOperator.IndexScan or
                ProviderPlanOperator.IndexSearch or
                ProviderPlanOperator.TableScan => childCount == 0,
            ProviderPlanOperator.Materialize or
                ProviderPlanOperator.Sort or
                ProviderPlanOperator.Limit or
                ProviderPlanOperator.Compute or
                ProviderPlanOperator.Projection or
                ProviderPlanOperator.Offset => childCount == 1,
            _ => false
        };

        private static bool IsLeaf(ProviderPlanOperator operation) => operation is
            ProviderPlanOperator.IndexScan or
            ProviderPlanOperator.IndexSearch or
            ProviderPlanOperator.TableScan;

        private static bool TryMapPlanStage(
            BsonDocument document,
            string stage,
            out ProviderPlanOperator operation,
            out string? indexName)
        {
            indexName = null;
            operation = stage switch
            {
                "COLLSCAN" => ProviderPlanOperator.TableScan,
                "IXSCAN" => ProviderPlanOperator.IndexScan,
                "FETCH" => ProviderPlanOperator.Materialize,
                "SORT" => ProviderPlanOperator.Sort,
                "LIMIT" => ProviderPlanOperator.Limit,
                "SKIP" => ProviderPlanOperator.Offset,
                "PROJECTION_SIMPLE" or "PROJECTION_DEFAULT" or "PROJECTION_COVERED" => ProviderPlanOperator.Projection,
                _ => ProviderPlanOperator.Unknown
            };
            if (operation is ProviderPlanOperator.IndexScan or ProviderPlanOperator.IndexSearch)
            {
                if (Count(document, "indexName") != 1 ||
                    !document.TryGetValue("indexName", out var indexValue) ||
                    !indexValue.IsString ||
                    string.IsNullOrWhiteSpace(indexValue.AsString))
                    return false;
                indexName = indexValue.AsString;
            }

            return operation != ProviderPlanOperator.Unknown;
        }

        internal sealed class MutableNode
        {
            internal MutableNode(int id, int? parentId, ProviderPlanOperator operation, string? nativeStage)
            {
                Id = id;
                ParentId = parentId;
                Operation = operation;
                NativeStage = nativeStage;
            }

            internal int Id { get; }
            internal int? ParentId { get; }
            internal ProviderPlanOperator Operation { get; }
            internal string? NativeStage { get; }
            internal string? IndexName { get; set; }
            internal ProviderOpaqueIdentity? IndexId { get; set; }
            internal string? LogicalIndexName { get; set; }
            internal bool? IsCovering { get; set; }

            internal ProviderPlanNode ToPublic(ProviderOpaqueIdentity targetId) => new(
                Id,
                ParentId,
                Operation,
                Operation is ProviderPlanOperator.IndexScan or
                    ProviderPlanOperator.IndexSearch or
                    ProviderPlanOperator.TableScan or
                    ProviderPlanOperator.PrimaryKeySearch
                    ? targetId
                    : null,
                IndexId,
                LogicalIndexName,
                IsCovering,
                null);
        }
    }
}
