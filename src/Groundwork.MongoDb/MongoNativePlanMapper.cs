using Groundwork.Kernel;
using Groundwork.Query.Model;
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
        IReadOnlyDictionary<string, string> logicalIndexesByPhysicalName,
        IReadOnlyDictionary<string, string>? logicalColumnsByPhysical = null)
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

            var context = new MappingContext(indexIdentity, logicalIndexesByPhysicalName, logicalColumnsByPhysical);
            var rootOrder = new List<int>();
            foreach (var root in roots)
            {
                if (root.NativeDocument is not null)
                {
                    var rootId = context.NextId;
                    if (!context.ReadPlanNode(root.NativeDocument, parentId: null))
                        return null;
                    rootOrder.Add(rootId);
                }
                else if (root.PipelineOperation is { } operation)
                {
                    rootOrder.Add(context.NextId);
                    if (!context.AddPipelineStage(operation, root.Stage!, root.StageValue!))
                        return null;
                }
                else
                {
                    return null;
                }
            }

            if (context.Nodes.Count == 0 || !context.ApplyCoveringFacts())
                return null;

            // Outer aggregation stages and the cursor's winning plan are sibling roots. Their observed
            // pipeline order is a fact; parentage between them is not, so it is recorded separately.
            return new ProviderPlanForest(context.Nodes.Select(node => node.ToPublic(targetId)), rootOrder);
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
            return winningRoots?.Select(document => new PlanRoot(document, null, null, null)).ToArray();
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
                roots.AddRange(winningRoots.Select(document => new PlanRoot(document, null, null, null)));
                continue;
            }

            if (!TryMapAggregationStage(element.Name, element.Value, out var operation))
                return null;

            // Outer aggregation stages have no parent edge in Mongo's explain output. They
            // remain sibling roots; assigning a chain would invent an execution dependency
            // that ProviderPlanForest deliberately does not claim.
            roots.Add(new PlanRoot(null, operation, stage, element.Value));
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
            ProviderPlanOperator.Projection)
            return value.IsBsonDocument;

        if (operation == ProviderPlanOperator.Sort)
        {
            if (!value.IsBsonDocument ||
                !TryReadFusedLimit(value.AsBsonDocument, "limit", "limitAmount", true, out var fused))
                return false;

            if (fused)
                operation = ProviderPlanOperator.TopNSort;
            return true;
        }

        if (operation is ProviderPlanOperator.Offset or ProviderPlanOperator.Limit)
        {
            if (!value.IsInt32 && !value.IsInt64)
                return false;

            var number = value.IsInt32 ? value.AsInt32 : value.AsInt64;
            return operation == ProviderPlanOperator.Offset ? number >= 0 : number > 0;
        }

        return false;
    }

    private static bool TryReadFusedLimit(
        BsonDocument document,
        string limitField,
        string conflictingLimitField,
        bool requireSortKey,
        out bool fused)
    {
        fused = false;
        var limitCount = Count(document, limitField);
        var conflictingLimitCount = Count(document, conflictingLimitField);
        if (limitCount == 0 && conflictingLimitCount == 0)
            return true;

        // Mongo uses `limit` in an optimized aggregation `$sort` payload and
        // `limitAmount` in a classic SORT plan. Do not silently reinterpret one
        // native dialect as the other when both or the wrong alias is present.
        if (limitCount != 1 || conflictingLimitCount != 0 ||
            !document.TryGetValue(limitField, out var value) ||
            !IsPositiveInteger(value))
            return false;

        if (requireSortKey &&
            (Count(document, "sortKey") != 1 ||
             !document.TryGetValue("sortKey", out var sortKey) ||
             !sortKey.IsBsonDocument))
            return false;

        // The bound witnesses fusion only; ProviderPlanNode deliberately exposes no numeric
        // limit, so the value is validated and then discarded.
        fused = true;
        return true;
    }

    private static bool IsPositiveInteger(BsonValue value) =>
        value.IsInt32
            ? value.AsInt32 > 0
            : value.IsInt64 && value.AsInt64 > 0;

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
        ProviderPlanOperator? PipelineOperation,
        BsonDocument? Stage,
        BsonValue? StageValue);

    private sealed class MappingContext
    {
        private readonly Func<string, ProviderOpaqueIdentity> indexIdentity;
        private readonly IReadOnlyDictionary<string, string> logicalIndexesByPhysicalName;
        private readonly IReadOnlyDictionary<string, string>? logicalColumnsByPhysical;
        private readonly Dictionary<string, ProviderOpaqueIdentity> indexIdentities = new(StringComparer.Ordinal);
        private readonly HashSet<int> nativePlanIds = [];
        private int nextId;

        internal MappingContext(
            Func<string, ProviderOpaqueIdentity> indexIdentity,
            IReadOnlyDictionary<string, string> logicalIndexesByPhysicalName,
            IReadOnlyDictionary<string, string>? logicalColumnsByPhysical)
        {
            this.indexIdentity = indexIdentity;
            this.logicalIndexesByPhysicalName = logicalIndexesByPhysicalName;
            this.logicalColumnsByPhysical = logicalColumnsByPhysical;
        }

        internal List<MutableNode> Nodes { get; } = [];

                internal int NextId => nextId;

        /// <summary>
        /// Fields the renderer computes for ordering, keyed by the name a later sort stage refers to:
        /// a null-rank <c>$cond</c>, an ordinal-key <c>$function</c>, or a plain field copy. A field
        /// computed by any other expression is recorded without a source so a sort over it stays
        /// unobserved instead of exporting the computed name as if it were a logical column (#432).
        /// </summary>
        private readonly Dictionary<string, (string Source, ProviderOrderingTransform? Transform)?> computedFields = new(StringComparer.Ordinal);

        /// <summary>
        /// An outer aggregation stage: its observed sort keys, its literal limit (fused into the sort or
        /// standalone) and, when the explain ran with execution statistics, whether the stage used disk.
        /// Unreadable keys leave the sort unobserved rather than wrong; a malformed literal fails the map.
        /// </summary>
        internal bool AddPipelineStage(ProviderPlanOperator operation, BsonDocument stage, BsonValue value)
        {
            var node = new MutableNode(nextId++, parentId: null, operation, nativeStage: null);
            if (operation == ProviderPlanOperator.Compute)
                RecordComputedFields(value.AsBsonDocument);
            if (operation is ProviderPlanOperator.Sort or ProviderPlanOperator.TopNSort)
            {
                var document = value.AsBsonDocument;
                var keys = document.TryGetValue("sortKey", out var sortKey) && sortKey.IsBsonDocument
                    ? sortKey.AsBsonDocument
                    : new BsonDocument(document.Where(element => element.Name is not ("limit" or "limitAmount")));
                var limit = ProviderPlanLimit.Unknown;
                if (operation == ProviderPlanOperator.TopNSort)
                {
                    if (!TryReadPositiveLiteral(document, "limit", out var fused))
                        return false;
                    limit = ProviderPlanLimit.Explicit(fused);
                }
                node.Details = Details(ReadSortKeys(keys), limit, ReadSpill(stage));
            }
            else if (operation == ProviderPlanOperator.Limit)
            {
                if (!value.IsInt32 && !value.IsInt64)
                    return false;
                var literal = value.IsInt32 ? value.AsInt32 : value.AsInt64;
                if (literal <= 0)
                    return false;
                node.Details = new ProviderPlanNodeDetails(nativeLimit: ProviderPlanLimit.Explicit(literal));
            }
            Nodes.Add(node);
            return true;
        }

        private static ProviderPlanNodeDetails? Details(
            IReadOnlyList<ProviderOrderTerm>? keys,
            ProviderPlanLimit limit,
            ProviderPlanSpillDetail? spill) =>
            keys is null && limit.Kind == ProviderNativeBoundKind.Unknown && spill is null
                ? null
                : new ProviderPlanNodeDetails(keys, limit, spill);

        private void RecordComputedFields(BsonDocument set)
        {
            foreach (var field in set)
                computedFields[field.Name] = ReadComputedSource(field.Value);
        }

        private static (string Source, ProviderOrderingTransform? Transform)? ReadComputedSource(BsonValue expression)
        {
            if (expression.IsString)
                return FieldPath(expression.AsString) is { } copied ? (copied, null) : null;
            if (!expression.IsBsonDocument || expression.AsBsonDocument.ElementCount != 1)
                return null;
            var element = expression.AsBsonDocument.GetElement(0);
            if (element.Name == "$function" && element.Value.IsBsonDocument &&
                element.Value.AsBsonDocument.TryGetValue("args", out var args) && args.IsBsonArray &&
                args.AsBsonArray.Count == 1 && args.AsBsonArray[0].IsString)
                return FieldPath(args.AsBsonArray[0].AsString) is { } keyed ? (keyed, ProviderOrderingTransform.OrdinalStringKey) : null;
            if (element.Name == "$cond" && element.Value.IsBsonArray && element.Value.AsBsonArray.Count == 3 &&
                element.Value.AsBsonArray[0].IsBsonDocument &&
                element.Value.AsBsonArray[0].AsBsonDocument.TryGetValue("$eq", out var equality) && equality.IsBsonArray &&
                equality.AsBsonArray.Count == 2 && equality.AsBsonArray[0].IsString && equality.AsBsonArray[1].IsBsonNull)
                return FieldPath(equality.AsBsonArray[0].AsString) is { } ranked ? (ranked, ProviderOrderingTransform.NullRank) : null;
            return null;
        }

        private static string? FieldPath(string reference) =>
            reference.Length > 1 && reference[0] == '$' && reference[1] != '$' ? reference[1..] : null;

        private IReadOnlyList<ProviderOrderTerm>? ReadSortKeys(BsonDocument keys)
        {
            if (keys.ElementCount == 0)
                return null;
            var terms = new List<ProviderOrderTerm>();
            foreach (var element in keys)
            {
                if (!element.Value.IsNumeric)
                    return null;
                OrderDirection? direction = element.Value.ToDouble() switch
                {
                    1 => OrderDirection.Ascending,
                    -1 => OrderDirection.Descending,
                    _ => null
                };
                if (direction is null)
                    return null;
                var physical = element.Name;
                var transforms = new List<ProviderOrderingTransform>();
                if (computedFields.TryGetValue(physical, out var computed))
                {
                    if (computed is not { } definition)
                        return null;
                    physical = definition.Source;
                    if (definition.Transform is { } transform)
                        transforms.Add(transform);
                }
                else if (physical.StartsWith("_groundwork_", StringComparison.Ordinal))
                {
                    // A renderer-computed field whose computing stage is not in the explain output.
                    return null;
                }
                string? logical;
                if (logicalColumnsByPhysical is not null && logicalColumnsByPhysical.TryGetValue(physical, out var mapped))
                {
                    logical = mapped;
                    if (!string.Equals(mapped, physical, StringComparison.Ordinal))
                        transforms.Add(ProviderOrderingTransform.PhysicalSearchKey);
                }
                else
                    logical = physical.StartsWith("__groundwork_", StringComparison.Ordinal) && physical != ProviderOwnedColumns.Scope
                        ? null
                        : physical;
                if (logical is null || logical.Contains('.', StringComparison.Ordinal))
                    return null;
                // A computed ordinal key and an identity-preserving persisted search key both order the
                // logical column by its ordinal comparison; only an unmapped physical field stays unknown.
                terms.Add(new ProviderOrderTerm(
                    logical,
                    direction.Value,
                    null,
                    transforms,
                    transforms.Contains(ProviderOrderingTransform.OrdinalStringKey) || transforms.Contains(ProviderOrderingTransform.PhysicalSearchKey)
                        ? ProviderPredicateComparison.Ordinal
                        : ProviderPredicateComparison.Unknown));
            }
            return terms;
        }

        /// <summary>Execution-statistics explain reports whether a stage used disk; planner-only explain says nothing.</summary>
        private static ProviderPlanSpillDetail? ReadSpill(BsonDocument stage)
        {
            if (!stage.TryGetValue("usedDisk", out var usedDisk) || !usedDisk.IsBoolean)
                return null;
            long? bytes = stage.TryGetValue("spilledDataStorageSize", out var size) && size.IsNumeric && size.ToInt64() >= 0 ? size.ToInt64() : null;
            long? rows = stage.TryGetValue("spilledRecords", out var records) && records.IsNumeric && records.ToInt64() >= 0 ? records.ToInt64() : null;
            return usedDisk.AsBoolean
                ? new ProviderPlanSpillDetail(spilled: true, bytes, rows)
                : new ProviderPlanSpillDetail(spilled: false);
        }

        private static bool TryReadPositiveLiteral(BsonDocument document, string field, out long literal)
        {
            literal = 0;
            if (Count(document, field) != 1 || !document.TryGetValue(field, out var value) || !value.IsInt32 && !value.IsInt64)
                return false;
            literal = value.IsInt32 ? value.AsInt32 : value.AsInt64;
            return literal > 0;
        }

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
            if (operation is ProviderPlanOperator.Sort or ProviderPlanOperator.TopNSort)
            {
                var keys = document.TryGetValue("sortPattern", out var pattern) && pattern.IsBsonDocument
                    ? ReadSortKeys(pattern.AsBsonDocument)
                    : null;
                var limit = ProviderPlanLimit.Unknown;
                if (operation == ProviderPlanOperator.TopNSort)
                {
                    if (!TryReadPositiveLiteral(document, "limitAmount", out var fused))
                        return false;
                    limit = ProviderPlanLimit.Explicit(fused);
                }
                node.Details = Details(keys, limit, ReadSpill(document));
            }
            else if (operation == ProviderPlanOperator.MergeOrdered)
            {
                if (Count(document, "sortPattern") != 1 ||
                    !document.TryGetValue("sortPattern", out var mergePattern) ||
                    !mergePattern.IsBsonDocument ||
                    ReadSortKeys(mergePattern.AsBsonDocument) is not { } mergeKeys)
                    return false;
                node.Details = Details(mergeKeys, ProviderPlanLimit.Unknown, ReadSpill(document));
            }
            else if (operation == ProviderPlanOperator.Limit)
            {
                if (TryReadPositiveLiteral(document, "limitAmount", out var literal))
                    node.Details = new ProviderPlanNodeDetails(nativeLimit: ProviderPlanLimit.Explicit(literal));
                else if (Count(document, "limitAmount") != 0)
                    return false;
            }
            // A primary-key search carries the target only; the kernel forbids an index identity on it.
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
                    .Where(node => node.Operation is ProviderPlanOperator.IndexScan or ProviderPlanOperator.IndexSearch or ProviderPlanOperator.PrimaryKeySearch)
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
                ProviderPlanOperator.PrimaryKeySearch or
                ProviderPlanOperator.TableScan => childCount == 0,
            ProviderPlanOperator.Materialize or
                ProviderPlanOperator.Sort or
                ProviderPlanOperator.TopNSort or
                ProviderPlanOperator.Limit or
                ProviderPlanOperator.Compute or
                ProviderPlanOperator.Projection or
                ProviderPlanOperator.Offset => childCount == 1,
            ProviderPlanOperator.MergeOrdered => childCount >= 2,
            _ => false
        };

        private static bool IsLeaf(ProviderPlanOperator operation) => operation is
            ProviderPlanOperator.IndexScan or
            ProviderPlanOperator.IndexSearch or
            ProviderPlanOperator.PrimaryKeySearch or
            ProviderPlanOperator.TableScan;

        private const string PrimaryKeyIndexName = "_id_";

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
                // The `_id` fast path: classic IDHACK carries no index name, the 8.0 express path
                // names the index it searches. Either is one key search of the target.
                "IDHACK" => ProviderPlanOperator.PrimaryKeySearch,
                "EXPRESS_IXSCAN" => ProviderPlanOperator.IndexSearch,
                "FETCH" => ProviderPlanOperator.Materialize,
                "SORT" => ProviderPlanOperator.Sort,
                // The planner explodes a keyset `$or` into one bounded scan per branch and merges them
                // on the sort pattern without a blocking sort.
                "SORT_MERGE" => ProviderPlanOperator.MergeOrdered,
                "LIMIT" => ProviderPlanOperator.Limit,
                "SKIP" => ProviderPlanOperator.Offset,
                "PROJECTION_SIMPLE" or "PROJECTION_DEFAULT" or "PROJECTION_COVERED" => ProviderPlanOperator.Projection,
                _ => ProviderPlanOperator.Unknown
            };
            if (operation == ProviderPlanOperator.PrimaryKeySearch)
            {
                if (Count(document, "indexName") != 0)
                    return false;
                indexName = PrimaryKeyIndexName;
            }
            else if (operation is ProviderPlanOperator.IndexScan or ProviderPlanOperator.IndexSearch)
            {
                if (Count(document, "indexName") != 1 ||
                    !document.TryGetValue("indexName", out var indexValue) ||
                    !indexValue.IsString ||
                    string.IsNullOrWhiteSpace(indexValue.AsString))
                    return false;
                indexName = indexValue.AsString;
                if (operation == ProviderPlanOperator.IndexSearch && indexName == PrimaryKeyIndexName)
                    operation = ProviderPlanOperator.PrimaryKeySearch;
            }

            if (operation == ProviderPlanOperator.Sort)
            {
                if (!TryReadFusedLimit(document, "limitAmount", "limit", false, out var fused))
                    return false;
                if (fused)
                    operation = ProviderPlanOperator.TopNSort;
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
            internal ProviderPlanNodeDetails? Details { get; set; }

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
                null,
                Details);
        }
    }
}
