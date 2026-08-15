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

    public string Declaration =>
        "[GwIndex(\"" + Escape(Name) + "\", " +
        "\"" + Escape(string.Join(", ", Columns.Select(column =>
            column.Column + " " + DirectionName(column.Direction)))) + "\")]";

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string DirectionName(OrderDirection direction) => direction == OrderDirection.Ascending ? "ASC" : "DESC";
}

/// <summary>A single provider-neutral planning diagnostic.</summary>
public sealed record CoverageRefusal
{
    internal CoverageRefusal(string code, string message, CoverageIndex? nearestIndex, CoverageIndex suggestedIndex)
    {
        Code = code;
        Message = message;
        NearestIndex = nearestIndex;
        SuggestedIndex = suggestedIndex;
    }

    public string Code { get; }
    public string Message { get; }
    public CoverageIndex? NearestIndex { get; }
    public CoverageIndex SuggestedIndex { get; }
    public string SuggestedDeclaration => SuggestedIndex.Declaration;
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
