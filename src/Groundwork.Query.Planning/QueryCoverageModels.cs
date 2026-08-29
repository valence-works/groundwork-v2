using System.Collections.Immutable;
using Groundwork.Query.Model;

namespace Groundwork.Query.Planning;

/// <summary>Whether a query can be served by one declared ordered index without a scan.</summary>
public enum CoverageDecision
{
    Covered,
    Refuse
}

/// <summary>Whether an index keeps or omits rows whose indexed value is null/missing.</summary>
public enum IndexMissingValueBehavior
{
    Included,
    Excluded
}

/// <summary>A provider-neutral ordered index column used by the coverage checker.</summary>
public sealed record CoverageIndexColumn
{
    public CoverageIndexColumn(
        string column,
        OrderDirection direction = OrderDirection.Ascending,
        bool isNullable = true)
    {
        if (string.IsNullOrWhiteSpace(column))
            throw new ArgumentException("An index column name cannot be blank.", nameof(column));
        if (!Enum.IsDefined(typeof(OrderDirection), direction))
            throw new ArgumentOutOfRangeException(nameof(direction), direction, null);

        Column = column;
        Direction = direction;
        IsNullable = isNullable;
    }

    public string Column { get; }
    public OrderDirection Direction { get; }

    /// <summary>Whether the declared key can omit rows because its value is null or missing.</summary>
    public bool IsNullable { get; }
}

/// <summary>A provider-neutral index declaration accepted by query planning.</summary>
public sealed record CoverageIndex
{
    public CoverageIndex(
        string name,
        IEnumerable<CoverageIndexColumn> columns,
        IndexMissingValueBehavior missingValues = IndexMissingValueBehavior.Included)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("An index name cannot be blank.", nameof(name));

        Name = name;
        Columns = (columns ?? throw new ArgumentNullException(nameof(columns))).ToImmutableArray();
        if (Columns.Any(column => column is null))
            throw new ArgumentException("Index columns cannot contain null references.", nameof(columns));
        if (Columns.Length == 0)
            throw new ArgumentException("An index must declare at least one column.", nameof(columns));
        if (Columns.Select(column => column.Column).Distinct(StringComparer.Ordinal).Count() != Columns.Length)
            throw new ArgumentException("An index cannot declare the same column more than once.", nameof(columns));
        if (!Enum.IsDefined(typeof(IndexMissingValueBehavior), missingValues))
            throw new ArgumentOutOfRangeException(nameof(missingValues), missingValues, null);
        MissingValues = missingValues;
    }

    public CoverageIndex(
        string name,
        IEnumerable<string> columns,
        IndexMissingValueBehavior missingValues = IndexMissingValueBehavior.Included)
        : this(name, (columns ?? throw new ArgumentNullException(nameof(columns)))
            .Select(column => new CoverageIndexColumn(column)), missingValues)
    {
    }

    public string Name { get; }
    public ImmutableArray<CoverageIndexColumn> Columns { get; }
    public IndexMissingValueBehavior MissingValues { get; }

    /// <summary>
    /// Whether this candidate is the unit's declared key rather than a declared secondary index.
    /// It is set by <see cref="CoverageCandidates.Derive"/>, the one place candidates are derived.
    /// The checker reads it to recognise the one predicate shape a point read answers — a
    /// conjunction of single-value equalities over every key column, which matches at most one row
    /// — and withholds the index suggestion there, because no index improves on a single key
    /// lookup. Naming the key's columns is not itself that shape: a disjunction, a range, or an
    /// equality over part of a composite key can name exactly those columns and still need an
    /// index when the refusal is actionable. A nonportable <c>GW-COVER-016</c> refusal never keeps
    /// the suggestion, because declaring an ordered index cannot clear that refusal.
    /// </summary>
    public bool IsDeclaredKey { get; init; }

    public string Declaration =>
        "[GwIndex(\"" + Escape(Name) + "\", " +
        "\"" + Escape(string.Join(", ", Columns.Select(column =>
            column.Column + " " + DirectionName(column.Direction)))) + "\")]";

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string DirectionName(OrderDirection direction) => direction == OrderDirection.Ascending ? "ASC" : "DESC";
}

/// <summary>
/// The table-scoped index candidates used to prove a query with one declared reference join.
/// </summary>
/// <remarks>
/// A coverage index is intentionally provider- and table-neutral. This context keeps driving and
/// target declarations separate so an index from one side can never be used as evidence for the
/// other. Both collections are immutable snapshots.
/// </remarks>
public sealed class QueryCoverageCandidates
{
    public QueryCoverageCandidates(
        IEnumerable<CoverageIndex> driving,
        IEnumerable<CoverageIndex> target)
    {
        Driving = Snapshot(driving, nameof(driving));
        Target = Snapshot(target, nameof(target));
    }

    public ImmutableArray<CoverageIndex> Driving { get; }

    public ImmutableArray<CoverageIndex> Target { get; }

    private static ImmutableArray<CoverageIndex> Snapshot(
        IEnumerable<CoverageIndex> indexes,
        string parameterName)
    {
        if (indexes is null)
            throw new ArgumentNullException(parameterName);
        var snapshot = indexes.ToImmutableArray();
        if (snapshot.Any(index => index is null))
            throw new ArgumentException("Coverage indexes cannot contain null references.", parameterName);
        return snapshot;
    }
}

/// <summary>A single provider-neutral planning diagnostic.</summary>
public sealed record CoverageRefusal
{
    internal CoverageRefusal(string code, string message, CoverageIndex? nearestIndex, CoverageIndex? suggestedIndex)
    {
        Code = code;
        Message = message;
        NearestIndex = nearestIndex;
        SuggestedIndex = suggestedIndex;
    }

    public string Code { get; }
    public string Message { get; }
    public CoverageIndex? NearestIndex { get; }
    public CoverageIndex? SuggestedIndex { get; }
    public string SuggestedDeclaration => SuggestedIndex?.Declaration ?? string.Empty;
}

/// <summary>The immutable result of checking a query against candidate indexes.</summary>
public sealed class QueryCoverageResult
{
    internal QueryCoverageResult(
        CoverageDecision decision,
        CoverageIndex? index,
        IEnumerable<CoverageRefusal> refusals,
        string reason)
    {
        Decision = decision;
        Index = index;
        Refusals = (refusals ?? throw new ArgumentNullException(nameof(refusals))).ToImmutableArray();
        Reason = reason;
    }

    public CoverageDecision Decision { get; }
    public bool IsCovered => Decision == CoverageDecision.Covered;
    public CoverageIndex? Index { get; }
    public ImmutableArray<CoverageRefusal> Refusals { get; }
    public string Reason { get; }
    public CoverageRefusal? Refusal => Refusals.Length == 0 ? null : Refusals[0];
}
