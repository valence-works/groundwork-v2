using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Groundwork.Kernel;
using Groundwork.Query.Model;

namespace Groundwork.SqlServer;

/// <summary>
/// Maps the deliberately closed subset of SQL Server's runtime Showplan XML that the structured
/// evidence contract can represent. Native plan text never leaves this provider-owned mapper.
/// </summary>
internal static class SqlServerNativePlanMapper
{
    private static readonly XNamespace ShowPlanNamespace =
        "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    internal static ProviderPlanForest? Map(
        string rawPlan,
        string physicalDatabase,
        string physicalSchema,
        string physicalTarget,
        ProviderOpaqueIdentity targetId,
        Func<string, ProviderOpaqueIdentity> indexIdentity,
        IReadOnlyDictionary<string, string> logicalIndexesByPhysicalName,
        IReadOnlySet<string> catalogIndexes,
        IReadOnlyDictionary<string, string>? logicalColumnsByPhysical = null,
        string? primaryKeyIndex = null)
    {
        ArgumentNullException.ThrowIfNull(rawPlan);
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalDatabase);
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalSchema);
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalTarget);
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(indexIdentity);
        ArgumentNullException.ThrowIfNull(logicalIndexesByPhysicalName);
        ArgumentNullException.ThrowIfNull(catalogIndexes);
        if (string.IsNullOrWhiteSpace(rawPlan))
            return null;

        try
        {
            var document = XDocument.Parse(rawPlan, LoadOptions.None);
            if (document.Root?.Name != ShowPlanNamespace + "ShowPlanXML")
                return null;

            var batchSequences = document.Root.Elements().ToArray();
            var batches = batchSequences.Length == 1 &&
                          batchSequences[0].Name == ShowPlanNamespace + "BatchSequence"
                ? batchSequences[0].Elements().ToArray()
                : [];
            var statementContainers = batches.Length == 1 &&
                                      batches[0].Name == ShowPlanNamespace + "Batch"
                ? batches[0].Elements().ToArray()
                : [];
            var statements = statementContainers.Length == 1
                && statementContainers[0].Name == ShowPlanNamespace + "Statements"
                ? statementContainers[0].Elements().ToArray()
                : [];
            if (batchSequences.Length != 1 || batches.Length != 1 || statementContainers.Length != 1 ||
                statements.Length != 1 || statements[0].Name != ShowPlanNamespace + "StmtSimple" ||
                !string.Equals((string?)statements[0].Attribute("StatementType"), "SELECT", StringComparison.Ordinal))
                return null;

            var queryPlans = statements[0].Elements()
                .Where(element => element.Name.LocalName == "QueryPlan")
                .ToArray();
            if (queryPlans.Length != 1 || queryPlans[0].Name != ShowPlanNamespace + "QueryPlan")
                return null;

            var roots = queryPlans[0].Elements()
                .Where(element => element.Name.LocalName == "RelOp")
                .ToArray();
            if (roots.Length != 1 || roots[0].Name != ShowPlanNamespace + "RelOp")
                return null;

            var nodes = new List<ProviderPlanNode>();
            var ids = new HashSet<int>();
            if (!TryMapNode(
                    roots[0],
                    parentId: null,
                    nodes,
                    ids,
                    physicalDatabase,
                    physicalSchema,
                    physicalTarget,
                    targetId,
                    indexIdentity,
                    logicalIndexesByPhysicalName,
                    catalogIndexes,
                    logicalColumnsByPhysical,
                    primaryKeyIndex))
                return null;

            // This first SQL Server slice is intentionally single-source. A complete forest must
            // retain exactly one storage access node; joins and other multi-source plans withhold
            // the whole forest rather than flattening away an operator.
            if (nodes.Count(node => node.TargetId is not null) != 1)
                return null;

            return new ProviderPlanForest(nodes);
        }
        catch (XmlException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            // Malformed native data must not escape as a partial structured plan.
            return null;
        }
    }

    private static bool TryMapNode(
        XElement source,
        int? parentId,
        ICollection<ProviderPlanNode> nodes,
        ISet<int> ids,
        string physicalDatabase,
        string physicalSchema,
        string physicalTarget,
        ProviderOpaqueIdentity targetId,
        Func<string, ProviderOpaqueIdentity> indexIdentity,
        IReadOnlyDictionary<string, string> logicalIndexesByPhysicalName,
        IReadOnlySet<string> catalogIndexes,
        IReadOnlyDictionary<string, string>? logicalColumnsByPhysical,
        string? primaryKeyIndex)
    {
        if (!TryReadNodeId(source, out var id) || !ids.Add(id))
            return false;

        var physicalOperation = (string?)source.Attribute("PhysicalOp");
        ProviderPlanNode node;
        XElement[] children;
        switch (physicalOperation)
        {
            case "Index Seek":
            case "Clustered Index Seek":
            case "Index Scan":
            case "Clustered Index Scan":
                {
                    if (!TryReadOperatorChildren(source, "IndexScan", out children) || children.Length != 0 ||
                        !TryReadAccess(
                            source,
                            physicalDatabase,
                            physicalSchema,
                            physicalTarget,
                            targetId,
                            indexIdentity,
                            logicalIndexesByPhysicalName,
                            catalogIndexes,
                            out var indexId,
                            out var logicalIndex))
                        return false;
                    var seek = physicalOperation is "Index Seek" or "Clustered Index Seek";
                    // A seek on the table's primary-key index is the key search itself, the fact SQLite's
                    // rowid lookup and MongoDB's _id search report; the key is not an index identity.
                    node = seek && primaryKeyIndex is not null && IsIndexNamed(source, primaryKeyIndex)
                        ? new ProviderPlanNode(id, parentId, ProviderPlanOperator.PrimaryKeySearch, targetId: targetId)
                        : new ProviderPlanNode(id, parentId, seek ? ProviderPlanOperator.IndexSearch : ProviderPlanOperator.IndexScan, targetId, indexId, logicalIndex);
                    break;
                }

            case "Table Scan":
                {
                    var tableObjects = ReadAccessObjects(source);
                    if (!TryReadOperatorChildren(source, "TableScan", out children) || children.Length != 0 ||
                        !TryReadTarget(source, physicalDatabase, physicalSchema, physicalTarget) ||
                        tableObjects.Length != 1 ||
                        tableObjects[0].Attribute("Index") is not null)
                        return false;
                    node = new ProviderPlanNode(id, parentId, ProviderPlanOperator.TableScan, targetId: targetId);
                    break;
                }

            case "Compute Scalar":
                if (!TryReadOperatorChildren(source, "ComputeScalar", out children) || children.Length != 1)
                    return false;
                node = new ProviderPlanNode(id, parentId, ProviderPlanOperator.Compute);
                break;

            case "Filter":
                if (!TryReadOperatorChildren(source, "Filter", out children) || children.Length != 1)
                    return false;
                node = new ProviderPlanNode(id, parentId, ProviderPlanOperator.Filter);
                break;

            case "Nested Loops":
                {
                    // A bookmark lookup is the row fetch of its driving seek, not a second source: SQL Server
                    // reads the columns an index does not cover through a RID Lookup (heap) or Key Lookup
                    // (clustered table) joined to the seek by a Nested Loops. Both operators map to
                    // Materialize without a target, the fact MongoDB's FETCH stage reports, and the seek
                    // stays the forest's single access node. Any other join still withholds the forest.
                    if (!TryReadOperatorChildren(source, "NestedLoops", out children) || children.Length != 2 ||
                        !IsBookmarkLookup(children[1], physicalDatabase, physicalSchema, physicalTarget))
                        return false;
                    node = new ProviderPlanNode(id, parentId, ProviderPlanOperator.Materialize);
                    nodes.Add(node);
                    if (!TryMapNode(children[0], id, nodes, ids, physicalDatabase, physicalSchema, physicalTarget, targetId,
                            indexIdentity, logicalIndexesByPhysicalName, catalogIndexes, logicalColumnsByPhysical, primaryKeyIndex) ||
                        !TryReadNodeId(children[1], out var lookupId) || !ids.Add(lookupId))
                        return false;
                    nodes.Add(new ProviderPlanNode(lookupId, id, ProviderPlanOperator.Materialize));
                    return true;
                }

            case "Sort":
                {
                    if (!TryReadOperatorChildren(source, "Sort", out children) || children.Length != 1)
                        return false;
                    var topSort = source.Element(ShowPlanNamespace + "TopSort");
                    var payload = topSort ?? source.Element(ShowPlanNamespace + "Sort");
                    node = new ProviderPlanNode(id, parentId,
                        topSort is not null ? ProviderPlanOperator.TopNSort : ProviderPlanOperator.Sort,
                        null, null, null, null, null,
                        ReadSortDetails(source, payload, topSort, physicalTarget, logicalColumnsByPhysical));
                    break;
                }

            case "Top":
                {
                    if (!TryReadOperatorChildren(source, "Top", out children) || children.Length != 1)
                        return false;
                    var limit = ReadTopLimit(source.Element(ShowPlanNamespace + "Top"));
                    node = new ProviderPlanNode(id, parentId, ProviderPlanOperator.Limit, null, null, null, null, null,
                        limit.Kind == ProviderNativeBoundKind.Unknown ? null : new ProviderPlanNodeDetails(nativeLimit: limit));
                    break;
                }

            default:
                // Never turn an unfamiliar SQL Server operator into a known but different fact.
                return false;
        }

        nodes.Add(node);
        foreach (var child in children)
        {
            if (!TryMapNode(
                    child,
                    id,
                    nodes,
                    ids,
                    physicalDatabase,
                    physicalSchema,
                    physicalTarget,
                    targetId,
                    indexIdentity,
                    logicalIndexesByPhysicalName,
                    catalogIndexes,
                    logicalColumnsByPhysical,
                    primaryKeyIndex))
                return false;
        }

        return true;
    }

    /// <summary>
    /// A RID Lookup or Key Lookup against the statement target: showplan renders both as an
    /// <c>IndexScan</c> payload flagged <c>Lookup="1"</c> with no child operator.
    /// </summary>
    private static bool IsBookmarkLookup(XElement source, string physicalDatabase, string physicalSchema, string physicalTarget)
    {
        var physicalOperation = (string?)source.Attribute("PhysicalOp");
        if (physicalOperation is not ("RID Lookup" or "Key Lookup") ||
            !TryReadOperatorChildren(source, "IndexScan", out var children) || children.Length != 0 ||
            !TryReadTarget(source, physicalDatabase, physicalSchema, physicalTarget))
            return false;
        var payload = source.Element(ShowPlanNamespace + "IndexScan");
        return string.Equals((string?)payload?.Attribute("Lookup"), "1", StringComparison.Ordinal) ||
               string.Equals((string?)payload?.Attribute("Lookup"), "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadAccess(
        XElement source,
        string physicalDatabase,
        string physicalSchema,
        string physicalTarget,
        ProviderOpaqueIdentity targetId,
        Func<string, ProviderOpaqueIdentity> indexIdentity,
        IReadOnlyDictionary<string, string> logicalIndexesByPhysicalName,
        IReadOnlySet<string> catalogIndexes,
        out ProviderOpaqueIdentity indexId,
        out string? logicalIndexName)
    {
        indexId = null!;
        logicalIndexName = null;
        if (!TryReadTarget(source, physicalDatabase, physicalSchema, physicalTarget))
            return false;

        var objects = ReadAccessObjects(source);
        if (objects.Length != 1)
            return false;
        var index = objects[0].Attribute("Index")?.Value;
        if (!TryReadQuotedIdentifier(index, out var physicalIndex))
            return false;
        if (!catalogIndexes.Contains(physicalIndex))
            return false;
        logicalIndexesByPhysicalName.TryGetValue(physicalIndex, out logicalIndexName);

        indexId = indexIdentity(physicalIndex);
        return indexId is not null;
    }

    private static bool IsIndexNamed(XElement source, string physicalIndex)
    {
        var objects = ReadAccessObjects(source);
        return objects.Length == 1 &&
               TryReadQuotedIdentifier(objects[0].Attribute("Index")?.Value, out var index) &&
               string.Equals(index, physicalIndex, StringComparison.Ordinal);
    }

    private static bool TryReadTarget(
        XElement source,
        string physicalDatabase,
        string physicalSchema,
        string physicalTarget)
    {
        var objects = ReadAccessObjects(source);
        if (objects.Length != 1)
            return false;
        var target = objects[0];
        return TryReadQuotedIdentifier(target.Attribute("Database")?.Value, out var database) &&
               TryReadQuotedIdentifier(target.Attribute("Schema")?.Value, out var schema) &&
               TryReadQuotedIdentifier(target.Attribute("Table")?.Value, out var table) &&
               string.Equals(database, physicalDatabase, StringComparison.Ordinal) &&
               string.Equals(schema, physicalSchema, StringComparison.Ordinal) &&
               string.Equals(table, physicalTarget, StringComparison.Ordinal);
    }

    private static XElement[] ReadAccessObjects(XElement source) =>
        source.Descendants(ShowPlanNamespace + "Object")
            .Where(element => element.Ancestors(ShowPlanNamespace + "RelOp").FirstOrDefault() == source)
            .Where(element => element.Parent?.Name == ShowPlanNamespace + "IndexScan" ||
                              element.Parent?.Name == ShowPlanNamespace + "TableScan")
            .ToArray();

    private static bool TryReadOperatorChildren(
        XElement source,
        string operatorElement,
        out XElement[] children)
    {
        children = [];
        var payloads = source.Descendants()
            .Where(IsOperatorPayload)
            .Where(element => element.Ancestors().FirstOrDefault(a => a.Name.LocalName == "RelOp") == source)
            .ToArray();
        if (payloads.Length != 1 || payloads[0].Parent != source ||
            (payloads[0].Name != ShowPlanNamespace + operatorElement &&
             !(operatorElement == "Sort" && payloads[0].Name == ShowPlanNamespace + "TopSort")))
            return false;

        var expected = payloads[0];
        var mappedChildren = expected.Elements(ShowPlanNamespace + "RelOp").ToArray();
        children = mappedChildren;
        // A RelOp hidden below an arbitrary wrapper is not a child shape this mapper understands.
        // Scalar metadata such as OutputList, Predicate, and RunTimeInformation remains ignored.
        var directRelOps = source.Descendants()
            .Where(element => element.Name.LocalName == "RelOp")
            .Where(element => element.Ancestors().FirstOrDefault(a => a.Name.LocalName == "RelOp") == source)
            .ToArray();
        return directRelOps.All(element => mappedChildren.Contains(element)) &&
               mappedChildren.All(element => element.Name == ShowPlanNamespace + "RelOp");
    }

    /// <summary>
    /// Observed sort facts for one Sort/TopSort operator: its ORDER BY columns resolved to logical
    /// columns (a <c>datalength(column)</c> key is the ordinal string-key transform), the TopSort row
    /// bound when the plan carries it as a literal, and the replayed execution's spill observation.
    /// A key that references a foreign table, an unresolvable expression or a provider-owned column
    /// without a recorded mapping leaves the sort keys unobserved.
    /// </summary>
    private static ProviderPlanNodeDetails? ReadSortDetails(
        XElement relOp,
        XElement? payload,
        XElement? topSort,
        string physicalTarget,
        IReadOnlyDictionary<string, string>? logicalColumnsByPhysical)
    {
        var keys = payload is null ? null : ReadOrderBy(payload, relOp, physicalTarget, logicalColumnsByPhysical);
        var limit = ProviderPlanLimit.Unknown;
        if (topSort is not null &&
            long.TryParse((string?)topSort.Attribute("Rows"), NumberStyles.None, CultureInfo.InvariantCulture, out var rows) &&
            rows > 0)
            limit = ProviderPlanLimit.Explicit(rows);
        var spill = ReadSpill(relOp);
        if (keys is null && limit.Kind == ProviderNativeBoundKind.Unknown && spill is null)
            return null;
        return new ProviderPlanNodeDetails(keys, limit, spill);
    }

    /// <summary>
    /// SQL Server renders an ordinal string key as the binary-collated column followed by
    /// <c>datalength(column)</c>, so one logical ordering term arrives as two native keys; the pair folds
    /// into one <see cref="ProviderOrderingTransform.OrdinalStringKey"/> term. The optimizer drops the
    /// trailing length key once a unique column precedes it, which leaves the collated column alone; that
    /// key is still an ordinal comparison because every column the rendered query maps ordinally is
    /// declared with the binary collation.
    /// </summary>
    private static IReadOnlyList<ProviderOrderTerm>? ReadOrderBy(
        XElement payload,
        XElement relOp,
        string physicalTarget,
        IReadOnlyDictionary<string, string>? logicalColumnsByPhysical)
    {
        var orderBy = payload.Element(ShowPlanNamespace + "OrderBy");
        if (orderBy is null)
            return null;
        var keys = new List<OrderKey>();
        foreach (var column in orderBy.Elements(ShowPlanNamespace + "OrderByColumn"))
        {
            var ascending = (string?)column.Attribute("Ascending");
            OrderDirection? direction = ascending switch
            {
                "1" or "true" => OrderDirection.Ascending,
                "0" or "false" => OrderDirection.Descending,
                _ => null
            };
            var reference = column.Element(ShowPlanNamespace + "ColumnReference");
            if (direction is null || reference is null)
                return null;
            if (!TryResolveOrderColumn(reference, relOp, physicalTarget, logicalColumnsByPhysical, direction.Value, out var key))
                return null;
            keys.Add(key);
        }
        if (keys.Count == 0)
            return null;
        var terms = new List<ProviderOrderTerm>(keys.Count);
        for (var index = 0; index < keys.Count; index++)
        {
            var key = keys[index];
            if (key.Form == OrderKeyForm.NullRank)
            {
                terms.Add(new ProviderOrderTerm(key.LogicalColumn, key.Direction, null, [ProviderOrderingTransform.NullRank]));
                continue;
            }
            if (key.Form == OrderKeyForm.Length)
            {
                terms.Add(new ProviderOrderTerm(key.LogicalColumn, key.Direction, null, [ProviderOrderingTransform.OrdinalStringKey]));
                continue;
            }
            var transforms = new List<ProviderOrderingTransform>();
            if (key.PhysicalSearchKey)
                transforms.Add(ProviderOrderingTransform.PhysicalSearchKey);
            var comparison = ProviderPredicateComparison.Unknown;
            if (key.OrdinalMapping)
            {
                comparison = ProviderPredicateComparison.Ordinal;
                if (index + 1 < keys.Count &&
                    keys[index + 1] is { Form: OrderKeyForm.Length } length &&
                    string.Equals(length.LogicalColumn, key.LogicalColumn, StringComparison.Ordinal) &&
                    length.Direction == key.Direction)
                {
                    transforms.Add(ProviderOrderingTransform.OrdinalStringKey);
                    index++;
                }
            }
            terms.Add(new ProviderOrderTerm(key.LogicalColumn, key.Direction, null, transforms, comparison));
        }
        return terms;
    }

    private enum OrderKeyForm
    {
        /// <summary>The target column itself.</summary>
        Column,
        /// <summary><c>datalength(column)</c>, the trailing half of the ordinal string key.</summary>
        Length,
        /// <summary><c>CASE WHEN column IS NULL THEN 1 ELSE 0 END</c>.</summary>
        NullRank
    }

    /// <summary>
    /// One native order key resolved to its logical column. <paramref name="OrdinalMapping"/> is true when
    /// the rendered query maps the column through an ordinal search-key mapping, the only kind the plan
    /// receives; <paramref name="PhysicalSearchKey"/> when that mapping orders a separate physical column.
    /// </summary>
    private readonly record struct OrderKey(
        string LogicalColumn,
        OrderDirection Direction,
        OrderKeyForm Form,
        bool OrdinalMapping,
        bool PhysicalSearchKey);

    private static bool TryResolveOrderColumn(
        XElement reference,
        XElement relOp,
        string physicalTarget,
        IReadOnlyDictionary<string, string>? logicalColumnsByPhysical,
        OrderDirection direction,
        out OrderKey key)
    {
        key = default;
        var columnName = (string?)reference.Attribute("Column");
        if (string.IsNullOrWhiteSpace(columnName))
            return false;
        var table = (string?)reference.Attribute("Table");
        if (table is not null)
        {
            if (!TryReadQuotedIdentifier(table, out var tableName) ||
                !string.Equals(tableName, physicalTarget, StringComparison.Ordinal))
                return false;
            var logical = LogicalColumn(columnName, logicalColumnsByPhysical);
            if (logical is null)
                return false;
            var mapped = logicalColumnsByPhysical?.ContainsKey(columnName) == true;
            key = new OrderKey(logical, direction, OrderKeyForm.Column, mapped, mapped && !string.Equals(logical, columnName, StringComparison.Ordinal));
            return true;
        }
        // A bare column is a computed value defined elsewhere in the statement. Two renderer forms are
        // transforms this mapper vouches for: datalength(column), the ordinal string key, and
        // CASE WHEN column IS NULL THEN 1 ELSE 0 END, the null rank. Anything else stays unobserved.
        var defined = relOp.Document?.Descendants(ShowPlanNamespace + "DefinedValue")
            .FirstOrDefault(value =>
                (string?)value.Element(ShowPlanNamespace + "ColumnReference")?.Attribute("Column") == columnName &&
                value.Element(ShowPlanNamespace + "ColumnReference")?.Attribute("Table") is null);
        var scalar = defined?.Element(ShowPlanNamespace + "ScalarOperator");
        var intrinsic = scalar?.Element(ShowPlanNamespace + "Intrinsic");
        if (intrinsic is not null &&
            string.Equals((string?)intrinsic.Attribute("FunctionName"), "datalength", StringComparison.OrdinalIgnoreCase) &&
            TryResolveTargetColumn(intrinsic.Element(ShowPlanNamespace + "ScalarOperator"), physicalTarget, logicalColumnsByPhysical, out var lengthColumn))
        {
            key = new OrderKey(lengthColumn, direction, OrderKeyForm.Length, false, false);
            return true;
        }
        var conditional = scalar?.Element(ShowPlanNamespace + "IF");
        if (conditional is null)
            return false;
        var compare = conditional.Element(ShowPlanNamespace + "Condition")?
            .Element(ShowPlanNamespace + "ScalarOperator")?
            .Element(ShowPlanNamespace + "Compare");
        var operands = compare?.Elements(ShowPlanNamespace + "ScalarOperator").ToArray() ?? [];
        if (compare is null ||
            !string.Equals((string?)compare.Attribute("CompareOp"), "IS", StringComparison.OrdinalIgnoreCase) ||
            operands.Length != 2 ||
            !IsConstant(operands[1], "NULL") ||
            !IsConstant(conditional.Element(ShowPlanNamespace + "Then")?.Element(ShowPlanNamespace + "ScalarOperator"), "1") ||
            !IsConstant(conditional.Element(ShowPlanNamespace + "Else")?.Element(ShowPlanNamespace + "ScalarOperator"), "0") ||
            !TryResolveTargetColumn(operands[0], physicalTarget, logicalColumnsByPhysical, out var rankedColumn))
            return false;
        key = new OrderKey(rankedColumn, direction, OrderKeyForm.NullRank, false, false);
        return true;
    }

    private static bool TryResolveTargetColumn(
        XElement? scalarOperator,
        string physicalTarget,
        IReadOnlyDictionary<string, string>? logicalColumnsByPhysical,
        out string logicalColumn)
    {
        logicalColumn = string.Empty;
        var reference = scalarOperator?.Element(ShowPlanNamespace + "Identifier")?.Element(ShowPlanNamespace + "ColumnReference");
        var table = (string?)reference?.Attribute("Table");
        var column = (string?)reference?.Attribute("Column");
        if (reference is null || table is null || string.IsNullOrWhiteSpace(column) ||
            !TryReadQuotedIdentifier(table, out var tableName) ||
            !string.Equals(tableName, physicalTarget, StringComparison.Ordinal))
            return false;
        var resolved = LogicalColumn(column, logicalColumnsByPhysical);
        if (resolved is null)
            return false;
        logicalColumn = resolved;
        return true;
    }

    private static bool IsConstant(XElement? scalarOperator, string expected)
    {
        var value = (string?)scalarOperator?.Element(ShowPlanNamespace + "Const")?.Attribute("ConstValue");
        if (value is null)
            return false;
        value = value.Trim();
        if (value.StartsWith('(') && value.EndsWith(')'))
            value = value[1..^1];
        return string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
    }

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

    /// <summary>
    /// The replayed statement's spill observation for this operator. A spill warning is an observed
    /// spill; runtime information without one is an observed no-spill; a plan without runtime
    /// information for this operator leaves the fact unobserved.
    /// </summary>
    private static ProviderPlanSpillDetail? ReadSpill(XElement relOp)
    {
        var spilled = relOp.Elements(ShowPlanNamespace + "Warnings")
            .SelectMany(warnings => warnings.Elements(ShowPlanNamespace + "SpillToTempDb"))
            .Any();
        if (spilled)
            return new ProviderPlanSpillDetail(spilled: true);
        return relOp.Elements(ShowPlanNamespace + "RunTimeInformation").Any()
            ? new ProviderPlanSpillDetail(spilled: false)
            : null;
    }

    /// <summary>A Top operator's bound is observed only when the plan states it as a literal constant.</summary>
    private static ProviderPlanLimit ReadTopLimit(XElement? top)
    {
        var constant = top?.Element(ShowPlanNamespace + "TopExpression")?
            .Element(ShowPlanNamespace + "ScalarOperator")?
            .Element(ShowPlanNamespace + "Const");
        var value = (string?)constant?.Attribute("ConstValue");
        if (value is null)
            return ProviderPlanLimit.Unknown;
        value = value.Trim();
        if (value.StartsWith('(') && value.EndsWith(')'))
            value = value[1..^1];
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var rows) && rows > 0
            ? ProviderPlanLimit.Explicit(rows)
            : ProviderPlanLimit.Unknown;
    }

    private static bool IsOperatorPayload(XElement element) =>
        element.Name.LocalName is "IndexScan" or "TableScan" or "Sort" or "Top" or "TopSort" or "ComputeScalar" or "Filter" or "NestedLoops";

    private static bool TryReadNodeId(XElement source, out int id) =>
        int.TryParse((string?)source.Attribute("NodeId"), out id) && id >= 0;

    private static bool TryReadQuotedIdentifier(string? value, out string identifier)
    {
        identifier = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Length < 2 || value[0] != '[' || value[^1] != ']' ||
            value[1..^1].Contains(']'))
            return false;
        identifier = value[1..^1];
        return !string.IsNullOrWhiteSpace(identifier) &&
               !identifier.Contains("].[", StringComparison.Ordinal) &&
               !identifier.Contains('[', StringComparison.Ordinal);
    }
}
