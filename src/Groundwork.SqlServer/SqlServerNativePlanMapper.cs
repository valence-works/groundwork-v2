using System.Xml;
using System.Xml.Linq;
using Groundwork.Kernel;

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
        IReadOnlySet<string> catalogIndexes)
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
                    catalogIndexes))
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
        IReadOnlySet<string> catalogIndexes)
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
                    var operation = physicalOperation is "Index Seek" or "Clustered Index Seek"
                        ? ProviderPlanOperator.IndexSearch
                        : ProviderPlanOperator.IndexScan;
                    node = new ProviderPlanNode(id, parentId, operation, targetId, indexId, logicalIndex);
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

            case "Sort":
                if (!TryReadOperatorChildren(source, "Sort", out children) || children.Length != 1)
                    return false;
                node = new ProviderPlanNode(id, parentId,
                    source.Element(ShowPlanNamespace + "TopSort") is not null
                        ? ProviderPlanOperator.TopNSort
                        : ProviderPlanOperator.Sort);
                break;

            case "Top":
                if (!TryReadOperatorChildren(source, "Top", out children) || children.Length != 1)
                    return false;
                node = new ProviderPlanNode(id, parentId, ProviderPlanOperator.Limit);
                break;

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
                    catalogIndexes))
                return false;
        }

        return true;
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

    private static bool IsOperatorPayload(XElement element) =>
        element.Name.LocalName is "IndexScan" or "TableScan" or "Sort" or "Top" or "TopSort" or "ComputeScalar" or "Filter";

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
