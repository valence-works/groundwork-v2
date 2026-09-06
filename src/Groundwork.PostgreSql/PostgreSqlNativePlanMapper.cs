using System.Text.Json;
using Groundwork.Kernel;

namespace Groundwork.PostgreSql;

/// <summary>
/// Maps the closed subset of PostgreSQL's estimated JSON plan that the structured evidence
/// contract can represent. The mapper follows only the native winning <c>Plan</c>/<c>Plans</c>
/// tree; arbitrary JSON metadata is never treated as an operator or an index choice.
/// </summary>
internal static class PostgreSqlNativePlanMapper
{
    internal static ProviderPlanForest? Map(
        string rawPlan,
        string physicalTarget,
        string physicalSchema,
        ProviderOpaqueIdentity targetId,
        Func<string, ProviderOpaqueIdentity> indexIdentity,
        IReadOnlyDictionary<string, string> logicalIndexesByPhysicalName,
        IReadOnlySet<string>? catalogIndexes = null)
    {
        ArgumentNullException.ThrowIfNull(rawPlan);
        if (string.IsNullOrWhiteSpace(rawPlan))
            return null;
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalTarget);
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalSchema);
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(indexIdentity);
        ArgumentNullException.ThrowIfNull(logicalIndexesByPhysicalName);

        try
        {
            using var document = JsonDocument.Parse(rawPlan);
            if (!TryGetPlanRoot(document.RootElement, out var plan))
                return null;

            var nodes = new List<ProviderPlanNode>();
            if (!TryMapNode(
                    plan,
                    parentId: null,
                    nodes,
                    physicalTarget,
                    physicalSchema,
                    targetId,
                    indexIdentity,
                    logicalIndexesByPhysicalName,
                    catalogIndexes))
                return null;

            // The initial relational evidence slice is deliberately single-source. A complete
            // forest must retain one actual access node; joins and other multi-source plans are
            // rejected by the closed operator mapping rather than flattened.
            if (nodes.Count(node => node.TargetId is not null) != 1)
                return null;

            return new ProviderPlanForest(nodes);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            // Fail closed when a malformed native tree cannot satisfy the kernel guards.
            return null;
        }
    }

    internal static bool ContainsChosenIndex(JsonElement root, string physicalIndex)
    {
        if (root.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(physicalIndex))
            return false;

        if (IsIndexAccess(root) &&
            root.TryGetProperty("Index Name", out var indexName) &&
            indexName.ValueKind == JsonValueKind.String &&
            string.Equals(indexName.GetString(), physicalIndex, StringComparison.Ordinal))
            return true;

        return root.TryGetProperty("Plans", out var plans) &&
            plans.ValueKind == JsonValueKind.Array &&
            plans.EnumerateArray().Any(child => ContainsChosenIndex(child, physicalIndex));
    }

    private static bool TryGetPlanRoot(JsonElement root, out JsonElement plan)
    {
        plan = default;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != 1)
            return false;

        var envelope = root[0];
        return envelope.ValueKind == JsonValueKind.Object &&
            envelope.TryGetProperty("Plan", out plan) &&
            plan.ValueKind == JsonValueKind.Object;
    }

    private static bool TryMapNode(
        JsonElement source,
        int? parentId,
        List<ProviderPlanNode> nodes,
        string physicalTarget,
        string physicalSchema,
        ProviderOpaqueIdentity targetId,
        Func<string, ProviderOpaqueIdentity> indexIdentity,
        IReadOnlyDictionary<string, string> logicalIndexesByPhysicalName,
        IReadOnlySet<string>? catalogIndexes)
    {
        if (source.ValueKind != JsonValueKind.Object ||
            !source.TryGetProperty("Node Type", out var nodeType) ||
            nodeType.ValueKind != JsonValueKind.String)
            return false;

        var operationName = nodeType.GetString();
        if (string.IsNullOrWhiteSpace(operationName))
            return false;

        ProviderPlanNode node;
        switch (operationName)
        {
            case "Limit":
                node = new ProviderPlanNode(nodes.Count, parentId, ProviderPlanOperator.Limit);
                break;

            case "Materialize":
                node = new ProviderPlanNode(nodes.Count, parentId, ProviderPlanOperator.Materialize);
                break;

            case "Aggregate":
                if (source.TryGetProperty("Strategy", out var strategy) &&
                    (strategy.ValueKind != JsonValueKind.String ||
                     strategy.GetString() is not ("Plain" or "Sorted" or "Hashed" or "Mixed")))
                    return false;
                node = new ProviderPlanNode(nodes.Count, parentId, ProviderPlanOperator.Aggregate);
                break;

            case "Function Scan":
                node = new ProviderPlanNode(nodes.Count, parentId, ProviderPlanOperator.FunctionScan);
                break;

            case "Sort":
                if (!TryReadSortPurpose(source, out var sortPurpose))
                    return false;
                node = new ProviderPlanNode(nodes.Count, parentId, ProviderPlanOperator.Sort,
                    sortPurpose: sortPurpose);
                break;

            case "Seq Scan":
                if (!TryReadTarget(source, physicalTarget, physicalSchema))
                    return false;
                node = new ProviderPlanNode(nodes.Count, parentId, ProviderPlanOperator.TableScan,
                    targetId: targetId);
                break;

            case "Index Scan":
            case "Index Only Scan":
                if (!TryReadTarget(source, physicalTarget, physicalSchema) ||
                    !TryReadIndex(
                        source,
                        indexIdentity,
                        logicalIndexesByPhysicalName,
                        catalogIndexes,
                        out var indexId,
                        out var logicalIndexName))
                    return false;

                if (!TryReadIndexCondition(source, out var hasIndexCondition))
                    return false;
                var operation = hasIndexCondition
                    ? ProviderPlanOperator.IndexSearch
                    : ProviderPlanOperator.IndexScan;
                node = new ProviderPlanNode(
                    nodes.Count,
                    parentId,
                    operation,
                    targetId,
                    indexId,
                    logicalIndexName,
                    isCovering: operationName == "Index Only Scan");
                break;

            default:
                // Do not flatten an unknown native operator into a known but different fact.
                return false;
        }

        var nodeId = node.Id;
        nodes.Add(node);
        var expectedChildren = node.TargetId is null && node.Operation != ProviderPlanOperator.FunctionScan ? 1 : 0;
        if (!source.TryGetProperty("Plans", out var plans))
            return expectedChildren == 0;
        if (plans.ValueKind != JsonValueKind.Array)
            return false;

        var inputChildren = 0;
        foreach (var child in plans.EnumerateArray())
        {
            if (child.ValueKind != JsonValueKind.Object)
                return false;
            if (child.TryGetProperty("Parent Relationship", out var relationship))
            {
                if (relationship.ValueKind != JsonValueKind.String)
                    return false;
                switch (relationship.GetString())
                {
                    case "Outer":
                        inputChildren++;
                        break;
                    case "SubPlan":
                    case "InitPlan":
                        break;
                    default:
                        return false;
                }
            }
            else
                return false;
            if (!TryMapNode(
                    child,
                    nodeId,
                    nodes,
                    physicalTarget,
                    physicalSchema,
                    targetId,
                    indexIdentity,
                    logicalIndexesByPhysicalName,
                    catalogIndexes))
                return false;
        }

        // Scalar/initialization subplans are nested native structure, not extra relational
        // inputs. Preserve them in the forest without flattening them or inventing a target.
        return inputChildren == expectedChildren;
    }

    private static bool TryReadTarget(JsonElement source, string physicalTarget, string physicalSchema) =>
        source.TryGetProperty("Relation Name", out var relation) &&
        relation.ValueKind == JsonValueKind.String &&
        string.Equals(relation.GetString(), physicalTarget, StringComparison.Ordinal) &&
        source.TryGetProperty("Schema", out var schema) &&
        schema.ValueKind == JsonValueKind.String &&
        string.Equals(schema.GetString(), physicalSchema, StringComparison.Ordinal);

    private static bool TryReadIndex(
        JsonElement source,
        Func<string, ProviderOpaqueIdentity> indexIdentity,
        IReadOnlyDictionary<string, string> logicalIndexesByPhysicalName,
        IReadOnlySet<string>? catalogIndexes,
        out ProviderOpaqueIdentity indexId,
        out string? logicalIndexName)
    {
        indexId = null!;
        logicalIndexName = null;
        if (!source.TryGetProperty("Index Name", out var indexName) ||
            indexName.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(indexName.GetString()))
            return false;

        var physicalIndex = indexName.GetString()!;
        if (logicalIndexesByPhysicalName.TryGetValue(physicalIndex, out var resolvedLogicalIndexName))
        {
            if (string.IsNullOrWhiteSpace(resolvedLogicalIndexName))
                return false;
            logicalIndexName = resolvedLogicalIndexName;
        }
        else if (catalogIndexes?.Contains(physicalIndex) != true)
            return false;
        indexId = indexIdentity(physicalIndex);
        return indexId is not null;
    }

    private static bool TryReadIndexCondition(JsonElement source, out bool hasCondition)
    {
        hasCondition = false;
        if (!source.TryGetProperty("Index Cond", out var condition))
            return true;
        if (condition.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(condition.GetString()))
            return false;
        hasCondition = true;
        return true;
    }

    private static bool TryReadSortPurpose(JsonElement source, out ProviderPlanSortPurpose? purpose)
    {
        purpose = null;
        if (!source.TryGetProperty("Sort Key", out var sortKey))
            return true;
        if (sortKey.ValueKind != JsonValueKind.Array ||
            sortKey.EnumerateArray().Any(key => key.ValueKind != JsonValueKind.String))
            return false;

        // Sort Key identifies what is sorted, not why. Do not infer ORDER BY
        // from a field also used for aggregation and other native strategies.
        return true;
    }

    private static bool IsIndexAccess(JsonElement source) =>
        source.TryGetProperty("Node Type", out var nodeType) &&
        nodeType.ValueKind == JsonValueKind.String &&
        nodeType.GetString() is "Index Scan" or "Index Only Scan";
}
