using System.Collections.Immutable;

namespace Groundwork.Kernel;

/// <summary>A provider-mapped native winning-plan operator, not a workload verdict.</summary>
public enum ProviderPlanOperator
{
    Unknown,
    IndexSearch,
    IndexScan,
    TableScan,
    PrimaryKeySearch,
    Sort,
    Materialize,
    /// <summary>A native row-limiting operator; its presence alone does not establish a numeric bound.</summary>
    Limit,
    /// <summary>A native aggregation operator, including aggregation inside a scalar subplan.</summary>
    Aggregate,
    /// <summary>A native function-produced row source, not a physical storage target.</summary>
    FunctionScan,
    /// <summary>A native scalar-expression stage that computes or replaces fields on input rows.</summary>
    Compute,
    /// <summary>A native stage selecting the output fields of its input rows.</summary>
    Projection,
    /// <summary>A native row-skipping stage; its presence does not establish a numeric offset.</summary>
    Offset
}

/// <summary>The purpose of a sort when the native plan identifies it.</summary>
public enum ProviderPlanSortPurpose
{
    Unknown,
    OrderBy,
    GroupBy,
    Distinct
}

/// <summary>
/// One mapped native node. IDs are local to its forest; physical identities are opaque and
/// capture-local. Absent optional facts are unknown, never inferred from a query declaration.
/// </summary>
public sealed record ProviderPlanNode
{
    public ProviderPlanNode(int id, int? parentId, ProviderPlanOperator operation,
        ProviderOpaqueIdentity? targetId = null, ProviderOpaqueIdentity? indexId = null,
        string? logicalIndexName = null, bool? isCovering = null, ProviderPlanSortPurpose? sortPurpose = null)
    {
        if (id < 0)
            throw new ArgumentOutOfRangeException(nameof(id));
        if (parentId is < 0)
            throw new ArgumentOutOfRangeException(nameof(parentId));
        if (parentId == id)
            throw new ArgumentException("A plan node cannot parent itself.", nameof(parentId));
        if (!Enum.IsDefined(operation) || operation == ProviderPlanOperator.Unknown)
            throw new ArgumentOutOfRangeException(nameof(operation));
        var indexAccess = operation is ProviderPlanOperator.IndexSearch or ProviderPlanOperator.IndexScan;
        var access = indexAccess || operation is ProviderPlanOperator.TableScan or ProviderPlanOperator.PrimaryKeySearch;
        if (access != (targetId is not null))
            throw new ArgumentException("Only access nodes carry a required physical target identity.", nameof(targetId));
        if (indexAccess != (indexId is not null))
            throw new ArgumentException("Only index access nodes carry a required physical index identity.", nameof(indexId));
        if (!indexAccess && (logicalIndexName is not null || isCovering is not null))
            throw new ArgumentException("Index attributes require an index access node.", nameof(operation));
        if (logicalIndexName is not null && string.IsNullOrWhiteSpace(logicalIndexName))
            throw new ArgumentException("A mapped logical index identity cannot be blank.", nameof(logicalIndexName));
        if (sortPurpose is { } purpose)
        {
            if (!Enum.IsDefined(purpose) || purpose == ProviderPlanSortPurpose.Unknown)
                throw new ArgumentOutOfRangeException(nameof(sortPurpose));
            if (operation != ProviderPlanOperator.Sort)
                throw new ArgumentException("A sort purpose requires a sort node.", nameof(sortPurpose));
        }

        Id = id;
        ParentId = parentId;
        Operation = operation;
        TargetId = targetId;
        IndexId = indexId;
        LogicalIndexName = logicalIndexName;
        IsCovering = isCovering;
        SortPurpose = sortPurpose;
    }

    public int Id { get; }
    /// <summary>Null denotes a native root, not an invented execution dependency.</summary>
    public int? ParentId { get; }
    public ProviderPlanOperator Operation { get; }
    public ProviderOpaqueIdentity? TargetId { get; }
    public ProviderOpaqueIdentity? IndexId { get; }
    /// <summary>A declared identity resolved from the actual selected physical index, if known.</summary>
    public string? LogicalIndexName { get; }
    public bool? IsCovering { get; }
    public ProviderPlanSortPurpose? SortPurpose { get; }
}

/// <summary>
/// A complete mapping of the native winning-plan operator structure for one statement.
/// Multiple native roots are preserved. A provider must withhold the entire forest if any
/// native node or relationship is unmapped; a partial forest cannot prove absence of an operator.
/// Completeness applies only to operator structure, not row counts, spill, key enforcement or
/// ordering details. Those facts cannot be inferred from this structure or its absence.
/// </summary>
public sealed record ProviderPlanForest
{
    public ProviderPlanForest(IEnumerable<ProviderPlanNode> nodes)
    {
        Nodes = (nodes ?? throw new ArgumentNullException(nameof(nodes))).ToImmutableArray();
        if (Nodes.Length == 0 || Nodes.Any(node => node is null))
            throw new ArgumentException("A complete plan requires non-null nodes.", nameof(nodes));
        var byId = new Dictionary<int, ProviderPlanNode>();
        foreach (var node in Nodes)
            if (!byId.TryAdd(node.Id, node))
                throw new ArgumentException("Plan node identities cannot repeat.", nameof(nodes));

        var verified = new HashSet<int>();
        foreach (var node in Nodes)
        {
            var path = new HashSet<int>();
            var current = node;
            while (!verified.Contains(current.Id))
            {
                if (!path.Add(current.Id))
                    throw new ArgumentException("Plan relationships cannot contain cycles.", nameof(nodes));
                if (current.ParentId is not { } parent)
                    break;
                if (!byId.TryGetValue(parent, out current))
                    throw new ArgumentException("Every parent must exist in the plan.", nameof(nodes));
            }
            verified.UnionWith(path);
        }
    }

    /// <summary>All mapped nodes in provider observation order, without implied execution order.</summary>
    public ImmutableArray<ProviderPlanNode> Nodes { get; }
}
