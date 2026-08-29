using System.Collections.Immutable;

namespace Groundwork.Query.Model;

/// <summary>One equality pair from a declared reference's source columns to its target key.</summary>
public sealed record JoinColumnPair
{
    public JoinColumnPair(ColumnRef source, ColumnRef target)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Target = target ?? throw new ArgumentNullException(nameof(target));
        if (source.Table == TableId.Empty)
            throw new ArgumentException("A join source column must be table-qualified.", nameof(source));
        if (target.Table == TableId.Empty)
            throw new ArgumentException("Join columns must be table-qualified.", nameof(target));
        if (source.Type != target.Type)
            throw new ArgumentException("Join columns must use the same portable type.", nameof(target));
    }

    public ColumnRef Source { get; }
    public ColumnRef Target { get; }
}

/// <summary>
/// A portable inner equi-join bound to one declared logical reference. The ordered column pairs
/// map the referencing unit's columns to the target unit's complete key in key order.
/// </summary>
/// <remarks>
/// <see cref="ReferenceName"/> identifies the declaration that the shared coverage and admission
/// layer resolves and validates, including its target key and same-scope policy. Arbitrary join
/// conditions and outer joins are deliberately not represented by this node. Self-reference joins
/// also remain refused until the portable model has distinct source and target aliases.
/// </remarks>
public sealed record ReferenceJoin
{
    public ReferenceJoin(
        string referenceName,
        TableId targetTable,
        IEnumerable<JoinColumnPair> columnPairs)
    {
        if (string.IsNullOrWhiteSpace(referenceName))
            throw new ArgumentException("A reference join requires a declared reference name.", nameof(referenceName));
        TargetTable = targetTable ?? throw new ArgumentNullException(nameof(targetTable));
        if (targetTable == TableId.Empty)
            throw new ArgumentException("A reference join requires a qualified target table.", nameof(targetTable));

        ColumnPairs = (columnPairs ?? throw new ArgumentNullException(nameof(columnPairs))).ToImmutableArray();
        if (ColumnPairs.Length == 0)
            throw new ArgumentException("A reference join requires at least one column pair.", nameof(columnPairs));
        if (ColumnPairs.Any(pair => pair is null))
            throw new ArgumentException("Join column pairs cannot contain null references.", nameof(columnPairs));

        SourceTable = ColumnPairs[0].Source.Table;
        if (ColumnPairs.Any(pair => pair.Source.Table != SourceTable))
            throw new ArgumentException("Every join source column must belong to the same table.", nameof(columnPairs));
        if (SourceTable == targetTable)
        {
            throw new ArgumentException(
                "Self-reference joins require distinct table aliases, which this join node does not model.",
                nameof(columnPairs));
        }
        if (ColumnPairs.Any(pair => pair.Target.Table != targetTable))
            throw new ArgumentException("Every join target column must belong to the target table.", nameof(columnPairs));
        if (HasDuplicate(ColumnPairs.Select(pair => pair.Source.Name)) ||
            HasDuplicate(ColumnPairs.Select(pair => pair.Target.Name)))
        {
            throw new ArgumentException("A reference join must map each source and target column exactly once.", nameof(columnPairs));
        }

        ReferenceName = referenceName;
    }

    public string ReferenceName { get; }
    public TableId SourceTable { get; }
    public TableId TargetTable { get; }
    public ImmutableArray<JoinColumnPair> ColumnPairs { get; }

    private static bool HasDuplicate(IEnumerable<string> names) =>
        names.GroupBy(name => name, StringComparer.Ordinal).Any(group => group.Count() != 1);
}

internal static class JoinedQueryValidation
{
    internal static void Validate(
        TableId table,
        ReferenceJoin join,
        Predicate where,
        ImmutableArray<OrderTerm> order,
        Projection projection,
        ResultShape result,
        LatestPerKey? latestPerKey)
    {
        if (join.SourceTable != table)
            throw new ArgumentException("The reference join source must be the query table.", nameof(join));
        if (ContainsElementSet(where))
        {
            throw new ArgumentException(
                "Element-set predicates require a table-qualified set identity in joined queries.",
                nameof(join));
        }

        foreach (var column in Columns(where)
                     .Concat(order.Select(term => term.Column))
                     .Concat(projection.AllColumns ? [] : projection.Columns)
                     .Concat(result is ResultShape.Reduction reduction ? [reduction.Column] : [])
                     .Concat(latestPerKey is null ? [] : [latestPerKey.Key, latestPerKey.Timestamp]))
        {
            if (column is null || column.Table == TableId.Empty)
                throw new ArgumentException("Every column in a joined query must be table-qualified.", nameof(join));
            if (column.Table != table && column.Table != join.TargetTable)
            {
                throw new ArgumentException(
                    $"Joined query column '{column}' does not belong to source '{table}' or target '{join.TargetTable}'.",
                    nameof(join));
            }
        }
    }

    private static bool ContainsElementSet(Predicate predicate) => predicate switch
    {
        Predicate.ElementOf => true,
        Predicate.Not not => ContainsElementSet(not.Inner),
        Predicate.And and => and.Terms.Any(ContainsElementSet),
        Predicate.Or or => or.Terms.Any(ContainsElementSet),
        _ => false
    };

    private static IEnumerable<ColumnRef> Columns(Predicate predicate)
    {
        switch (predicate)
        {
            case Predicate.Equal equal:
                yield return equal.Column;
                yield break;
            case Predicate.In membership:
                yield return membership.Column;
                yield break;
            case Predicate.Range range:
                yield return range.Column;
                yield break;
            case Predicate.StartsWith startsWith:
                yield return startsWith.Column;
                yield break;
            case Predicate.Substring substring:
                yield return substring.Column;
                yield break;
            case Predicate.ColumnCompare comparison:
                yield return comparison.Left;
                yield return comparison.Right;
                yield break;
            case Predicate.Not not:
                foreach (var column in Columns(not.Inner))
                    yield return column;
                yield break;
            case Predicate.And and:
                foreach (var column in and.Terms.SelectMany(Columns))
                    yield return column;
                yield break;
            case Predicate.Or or:
                foreach (var column in or.Terms.SelectMany(Columns))
                    yield return column;
                yield break;
        }
    }
}
