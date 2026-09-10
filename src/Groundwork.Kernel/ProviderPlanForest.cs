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
    Offset,
    /// <summary>A single native operator that sorts and limits rows; no numeric bound is implied.</summary>
    TopNSort,
    /// <summary>A native stage retaining input rows that satisfy a predicate; no predicate values or selectivity are implied.</summary>
    Filter,
    /// <summary>A native streaming merge of already-ordered inputs on the merge keys it reports; no blocking sort is implied.</summary>
    MergeOrdered,
    /// <summary>
    /// A native parallelism exchange that gathers, distributes or repartitions its one input's rows across
    /// threads without changing the row set; an ordered gather merges already-ordered streams on the keys
    /// it reports. No target and no blocking sort are implied.
    /// </summary>
    Exchange
}

/// <summary>The purpose of a sort when the native plan identifies it.</summary>
public enum ProviderPlanSortPurpose
{
    Unknown,
    OrderBy,
    GroupBy,
    Distinct
}

/// <summary>A positive numeric bound observed on a native limiting operator.</summary>
public readonly record struct ProviderPlanLimit
{
    private ProviderPlanLimit(ProviderNativeBoundKind kind, long? value)
    {
        Kind = kind;
        Value = value;
    }

    /// <summary>Whether the bound was not observed, observed absent, or observed as an explicit literal.</summary>
    public ProviderNativeBoundKind Kind { get; }
    /// <summary>The literal bound; present only for an explicit kind.</summary>
    public long? Value { get; }

    /// <summary>No bound observation; never a claim that no bound exists.</summary>
    public static ProviderPlanLimit Unknown => default;
    /// <summary>The provider observed that the operator carries no bound.</summary>
    public static ProviderPlanLimit Absent => new(ProviderNativeBoundKind.Absent, null);

    /// <summary>An explicit positive literal bound the provider stated for the operator.</summary>
    public static ProviderPlanLimit Explicit(long value)
    {
        if (value <= 0)
            throw new ArgumentOutOfRangeException(nameof(value));
        return new(ProviderNativeBoundKind.Explicit, value);
    }
}

/// <summary>Runtime spill facts observed for one native plan node.</summary>
public sealed record ProviderPlanSpillDetail
{
    /// <summary>Creates an observed spill fact; positive metrics are only valid for an observed spill.</summary>
    public ProviderPlanSpillDetail(bool spilled, long? spilledBytes = null, long? spilledRows = null)
    {
        if (spilledBytes is < 0)
            throw new ArgumentOutOfRangeException(nameof(spilledBytes));
        if (spilledRows is < 0)
            throw new ArgumentOutOfRangeException(nameof(spilledRows));
        if (!spilled && (spilledBytes is > 0 || spilledRows is > 0))
            throw new ArgumentException("No-spill observations cannot carry positive spill metrics.");

        Spilled = spilled;
        SpilledBytes = spilledBytes;
        SpilledRows = spilledRows;
    }

    /// <summary>Whether the executed operator spilled, as the provider observed it.</summary>
    public bool Spilled { get; }
    /// <summary>A provider-reported metric, not necessarily physical bytes written to disk.</summary>
    public long? SpilledBytes { get; }
    /// <summary>A provider-reported row metric; its physical meaning is provider-specific.</summary>
    public long? SpilledRows { get; }
}

/// <summary>
/// Optional details mapped from one native plan node. A null sort-key collection means that
/// sort keys were not observed; it does not attest to an empty sort or to no sort.
/// </summary>
public sealed record ProviderPlanNodeDetails
{
    /// <summary>Creates a detail set; an empty sort-key collection is rejected while null stays unobserved.</summary>
    public ProviderPlanNodeDetails(
        IEnumerable<ProviderOrderTerm>? nativeSortKeys = null,
        ProviderPlanLimit nativeLimit = default,
        ProviderPlanSpillDetail? spill = null)
    {
        if (nativeSortKeys is null)
        {
            NativeSortKeys = null;
        }
        else
        {
            var keys = nativeSortKeys.ToImmutableArray();
            if (keys.Length == 0)
                throw new ArgumentException("Native sort keys cannot be empty.", nameof(nativeSortKeys));
            if (keys.Any(key => key is null))
                throw new ArgumentException("Native sort keys cannot contain null references.", nameof(nativeSortKeys));
            NativeSortKeys = keys;
        }

        NativeLimit = nativeLimit;
        Spill = spill;
    }

    /// <summary>The observed native sort keys as logical order terms; null means not observed.</summary>
    public ImmutableArray<ProviderOrderTerm>? NativeSortKeys { get; }
    /// <summary>The observed native bound on a limiting operator; unknown means not observed.</summary>
    public ProviderPlanLimit NativeLimit { get; }
    /// <summary>The observed spill fact for an executed operator; null means not observed.</summary>
    public ProviderPlanSpillDetail? Spill { get; }
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
        : this(id, parentId, operation, targetId, indexId, logicalIndexName, isCovering, sortPurpose, null)
    {
    }

    /// <summary>Creates a node with optional observed details, which must fit the operator kind.</summary>
    public ProviderPlanNode(int id, int? parentId, ProviderPlanOperator operation,
        ProviderOpaqueIdentity? targetId, ProviderOpaqueIdentity? indexId,
        string? logicalIndexName, bool? isCovering, ProviderPlanSortPurpose? sortPurpose,
        ProviderPlanNodeDetails? details)
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
            if (operation is not (ProviderPlanOperator.Sort or ProviderPlanOperator.TopNSort))
                throw new ArgumentException("A sort purpose requires a sort node.", nameof(sortPurpose));
        }
        if (details is not null)
        {
            if (details.NativeSortKeys is not null &&
                operation is not (ProviderPlanOperator.Sort or ProviderPlanOperator.TopNSort or ProviderPlanOperator.MergeOrdered or ProviderPlanOperator.Exchange))
                throw new ArgumentException("Native sort keys require a sort, ordered-merge or exchange node.", nameof(details));
            if (details.NativeLimit.Kind != ProviderNativeBoundKind.Unknown &&
                operation is not (ProviderPlanOperator.Limit or ProviderPlanOperator.TopNSort))
                throw new ArgumentException("A native limit requires a limiting node.", nameof(details));
        }

        Id = id;
        ParentId = parentId;
        Operation = operation;
        TargetId = targetId;
        IndexId = indexId;
        LogicalIndexName = logicalIndexName;
        IsCovering = isCovering;
        SortPurpose = sortPurpose;
        Details = details;
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
    /// <summary>Optional observed details for sort and limiting operators; null means none observed.</summary>
    public ProviderPlanNodeDetails? Details { get; }
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
        : this(nodes, observedRootOrder: null)
    {
    }

    /// <summary>Creates a forest and optionally records the observed order of its native roots.</summary>
    public ProviderPlanForest(IEnumerable<ProviderPlanNode> nodes, IEnumerable<int>? observedRootOrder)
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

        if (observedRootOrder is null)
        {
            ObservedRootOrder = null;
            return;
        }

        var rootIds = Nodes
            .Where(node => node.ParentId is null)
            .Select(node => node.Id)
            .ToHashSet();
        var order = observedRootOrder.ToImmutableArray();
        if (order.Length != rootIds.Count || order.Distinct().Count() != order.Length || order.Any(id => !rootIds.Contains(id)))
            throw new ArgumentException("Observed root order must contain every native root exactly once.", nameof(observedRootOrder));
        ObservedRootOrder = order;
    }

    /// <summary>All mapped nodes in provider observation order, without implied execution order.</summary>
    public ImmutableArray<ProviderPlanNode> Nodes { get; }

    /// <summary>Optional provider-observed order of native roots, without implied parentage.</summary>
    public ImmutableArray<int>? ObservedRootOrder { get; }
}
