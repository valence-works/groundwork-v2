using System.Text.Json;
using System.Text.RegularExpressions;
using Groundwork.Kernel;
using Groundwork.Query.Model;

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
        IReadOnlySet<string>? catalogIndexes = null,
        IReadOnlyDictionary<string, string>? logicalColumnsByPhysical = null)
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
                    catalogIndexes,
                    logicalColumnsByPhysical))
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
        IReadOnlySet<string>? catalogIndexes,
        IReadOnlyDictionary<string, string>? logicalColumnsByPhysical)
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
                    null, null, null, null, sortPurpose,
                    ReadSortDetails(source, physicalTarget, logicalColumnsByPhysical));
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
                    catalogIndexes,
                    logicalColumnsByPhysical))
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

    private static readonly Regex SortKeyPattern = new(
        @"^\s*(?:(?<relation>""[^""]+""|[A-Za-z_][A-Za-z0-9_]*)\.)?(?<column>""[^""]+""|[A-Za-z_][A-Za-z0-9_]*)(?:\s+(?<direction>ASC|DESC))?(?:\s+NULLS\s+(?<nulls>FIRST|LAST))?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

    /// <summary>
    /// Maps the observed <c>Sort Key</c> terms of an estimated plan to logical columns. Only a plain,
    /// optionally relation-qualified column with an optional direction and null placement is a supported
    /// form; a function, expression, collation clause or foreign relation leaves the keys unobserved
    /// rather than guessed. PostgreSQL's estimated explain exposes no numeric bound on a Limit node and no
    /// runtime spill fact, so neither is claimed here.
    /// </summary>
    private static ProviderPlanNodeDetails? ReadSortDetails(
        JsonElement source,
        string physicalTarget,
        IReadOnlyDictionary<string, string>? logicalColumnsByPhysical)
    {
        if (!source.TryGetProperty("Sort Key", out var sortKey) || sortKey.ValueKind != JsonValueKind.Array)
            return null;
        var terms = new List<ProviderOrderTerm>();
        var subplanColumns = ReadOrdinalSubplanColumns(source, physicalTarget, logicalColumnsByPhysical);
        foreach (var key in sortKey.EnumerateArray())
        {
            if (key.ValueKind != JsonValueKind.String)
                return null;
            var text = key.GetString()!;
            if (TryReadTransformKey(text, physicalTarget, logicalColumnsByPhysical, subplanColumns, out var transformed))
            {
                terms.Add(transformed);
                continue;
            }
            var match = SortKeyPattern.Match(text);
            if (!match.Success)
                return null;
            var relation = Unquote(match.Groups["relation"].Value);
            if (relation.Length != 0 && !string.Equals(relation, physicalTarget, StringComparison.Ordinal))
                return null;
            var logicalColumn = LogicalColumn(Unquote(match.Groups["column"].Value), logicalColumnsByPhysical);
            if (logicalColumn is null)
                return null;
            var direction = match.Groups["direction"].Value == "DESC" ? OrderDirection.Descending : OrderDirection.Ascending;
            NullOrder? nulls = match.Groups["nulls"].Value switch
            {
                "FIRST" => NullOrder.First,
                "LAST" => NullOrder.Last,
                _ => null
            };
            terms.Add(new ProviderOrderTerm(logicalColumn, direction, nulls));
        }
        return terms.Count == 0 ? null : new ProviderPlanNodeDetails(nativeSortKeys: terms);
    }

    private static readonly Regex NullRankPattern = new(
        @"^\s*\(?CASE\s+WHEN\s+\(?(?:(?<relation>""[^""]+""|[A-Za-z_][A-Za-z0-9_]*)\.)?(?<column>""[^""]+""|[A-Za-z_][A-Za-z0-9_]*)\s+IS\s+NULL\)?\s+THEN\s+1\s+ELSE\s+0\s+END\)?(?:\s+(?<direction>ASC|DESC))?(?:\s+NULLS\s+(?<nulls>FIRST|LAST))?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase);

    private static readonly Regex OrdinalSubplanPattern = new(
        // PostgreSQL 17 wraps a computed sort expression in parentheses ("(COALESCE((SubPlan 1), ''::text)) NULLS FIRST").
        @"^\s*\(?COALESCE\(\(SubPlan\s+(?<subplan>[0-9]+)\),\s*''(?:::text)?\)\)?(?:\s+(?<direction>ASC|DESC))?(?:\s+NULLS\s+(?<nulls>FIRST|LAST))?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase);

    private static readonly Regex OrdinalSubplanCallPattern = new(
        @"^\s*unnest\(string_to_array\(\((?:(?<relation>""[^""]+""|[A-Za-z_][A-Za-z0-9_]*)\.)?(?<column>""[^""]+""|[A-Za-z_][A-Za-z0-9_]*)\)::text,\s*NULL::text\)\)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

    /// <summary>
    /// The two renderer transforms a sort key can carry: the null-rank CASE, and the ordinal string key
    /// computed by an <c>unnest(string_to_array(...))</c> subplan whose ordinal names the column.
    /// </summary>
    private static bool TryReadTransformKey(
        string key,
        string physicalTarget,
        IReadOnlyDictionary<string, string>? logicalColumnsByPhysical,
        IReadOnlyDictionary<int, string> subplanColumns,
        out ProviderOrderTerm term)
    {
        term = null!;
        var nullRank = NullRankPattern.Match(key);
        if (nullRank.Success)
        {
            var relation = Unquote(nullRank.Groups["relation"].Value);
            if (relation.Length != 0 && !string.Equals(relation, physicalTarget, StringComparison.Ordinal))
                return false;
            var logical = LogicalColumn(Unquote(nullRank.Groups["column"].Value), logicalColumnsByPhysical);
            if (logical is null)
                return false;
            term = new ProviderOrderTerm(logical, Direction(nullRank), Nulls(nullRank), [ProviderOrderingTransform.NullRank]);
            return true;
        }
        var ordinal = OrdinalSubplanPattern.Match(key);
        if (!ordinal.Success ||
            !int.TryParse(ordinal.Groups["subplan"].Value, out var subplan) ||
            !subplanColumns.TryGetValue(subplan, out var column))
            return false;
        term = new ProviderOrderTerm(column, Direction(ordinal), Nulls(ordinal), [ProviderOrderingTransform.OrdinalStringKey],
            ProviderPredicateComparison.Ordinal);
        return true;
    }

    private static OrderDirection Direction(Match match) =>
        string.Equals(match.Groups["direction"].Value, "DESC", StringComparison.OrdinalIgnoreCase)
            ? OrderDirection.Descending
            : OrderDirection.Ascending;

    private static NullOrder? Nulls(Match match) => match.Groups["nulls"].Value.ToUpperInvariant() switch
    {
        "FIRST" => NullOrder.First,
        "LAST" => NullOrder.Last,
        _ => null
    };

    /// <summary>
    /// Resolves each "SubPlan N" beneath the sort to the column its ordinal string key is computed
    /// over, by the function scan's call. A subplan of any other shape is simply not resolved.
    /// </summary>
    private static IReadOnlyDictionary<int, string> ReadOrdinalSubplanColumns(
        JsonElement sort,
        string physicalTarget,
        IReadOnlyDictionary<string, string>? logicalColumnsByPhysical)
    {
        var columns = new Dictionary<int, string>();
        foreach (var node in Descendants(sort))
        {
            if (!node.TryGetProperty("Subplan Name", out var name) || name.ValueKind != JsonValueKind.String)
                continue;
            var label = name.GetString()!;
            if (!label.StartsWith("SubPlan ", StringComparison.Ordinal) ||
                !int.TryParse(label["SubPlan ".Length..], out var ordinal))
                continue;
            foreach (var function in Descendants(node))
            {
                if (!function.TryGetProperty("Function Call", out var call) || call.ValueKind != JsonValueKind.String)
                    continue;
                var match = OrdinalSubplanCallPattern.Match(call.GetString()!);
                if (!match.Success)
                    continue;
                var relation = Unquote(match.Groups["relation"].Value);
                if (relation.Length != 0 && !string.Equals(relation, physicalTarget, StringComparison.Ordinal))
                    continue;
                var logical = LogicalColumn(Unquote(match.Groups["column"].Value), logicalColumnsByPhysical);
                if (logical is not null)
                    columns[ordinal] = logical;
            }
        }
        return columns;
    }

    private static IEnumerable<JsonElement> Descendants(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty("Plans", out var plans) || plans.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var child in plans.EnumerateArray())
        {
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }

    private static string Unquote(string identifier) =>
        identifier.Length >= 2 && identifier[0] == '"' && identifier[^1] == '"'
            ? identifier[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal)
            : identifier;

    /// <summary>A provider-owned physical column is logical only through its recorded search-key mapping.</summary>
    internal static string? LogicalColumn(string physicalColumn, IReadOnlyDictionary<string, string>? logicalColumnsByPhysical)
    {
        if (logicalColumnsByPhysical is not null && logicalColumnsByPhysical.TryGetValue(physicalColumn, out var logical))
            return logical;
        // Provider-owned physical columns are logical only through a recorded mapping, except the scope
        // column, whose identity is already the logical name evidence uses for scope facts.
        return IsProviderOwned(physicalColumn) && physicalColumn != ProviderOwnedColumns.Scope ? null : physicalColumn;
    }

    private static bool IsProviderOwned(string column) =>
        column.StartsWith("__groundwork_", StringComparison.Ordinal);

    private static bool IsIndexAccess(JsonElement source) =>
        source.TryGetProperty("Node Type", out var nodeType) &&
        nodeType.ValueKind == JsonValueKind.String &&
        nodeType.GetString() is "Index Scan" or "Index Only Scan";
}
