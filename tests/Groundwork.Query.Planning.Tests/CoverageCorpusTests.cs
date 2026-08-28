using System.Collections.Immutable;
using Groundwork.Query.Model;
using Groundwork.Query.Planning;
using Xunit;

namespace Groundwork.Query.Planning.Tests;

/// <summary>
/// Public-AST equivalents of the pinned v1 plan-compiler coverage cases. Each case builds a
/// QueryRequest and CoverageIndex instead of importing v1 declarations or provider types.
/// </summary>
public sealed class CoverageCorpusTests
{
    private static readonly TableId Table = new("coverage-corpus");
    private static readonly ColumnRef Text = new(Table, "text", QueryType.String);
    private static readonly ColumnRef Amount = new(Table, "amount", QueryType.Decimal, decimalPrecision: 18, decimalScale: 4);
    private static readonly ColumnRef Created = new(Table, "created", QueryType.DateTimeOffset);
    private static readonly ColumnRef Id = new(Table, "id", QueryType.String, isNullable: false);

    public static IEnumerable<object[]> Cases()
    {
        yield return Case("equal-non-null", Request(new Predicate.Equal(Text, QueryConstant.Of(Text, "open"))), Index(Text), true);
        yield return Case("equal-null-included", Request(new Predicate.Equal(Text, QueryConstant.Of(Text, null))), Index(Text), true);
        yield return Case("equal-null-excluded", Request(new Predicate.Equal(Text, QueryConstant.Of(Text, null))), SparseIndex(Text), false);
        yield return Case("membership", Request(new Predicate.In(Text, [
            QueryConstant.Of(Text, "open"), QueryConstant.Of(Text, "closed")])), Index(Text), true);
        yield return Case("range", Request(new Predicate.Range(Amount, Bound.Inclusive(QueryConstant.Of(Amount, 0m)), null)), Index(Amount), true);
        yield return Case("starts-with-range", Request(new Predicate.StartsWith(Text, "op")), Index(Text), true);
        yield return Case("substring-refused", Request(new Predicate.Substring(Text, "pen", Anchor.Contains)), Index(Text), false);
        yield return Case("negated-equality-refused", Request(new Predicate.Not(new Predicate.Equal(Text, QueryConstant.Of(Text, "open")))), Index(Text), false);
        yield return Case("same-column-or", Request(new Predicate.Or([
            new Predicate.Equal(Text, QueryConstant.Of(Text, "open")),
            new Predicate.Equal(Text, QueryConstant.Of(Text, "closed"))])), Index(Text), true);
        yield return Case("cross-column-or", Request(new Predicate.Or([
            new Predicate.Equal(Text, QueryConstant.Of(Text, "open")),
            new Predicate.Equal(Amount, QueryConstant.Of(Amount, 1m))])), Index(Text, Amount), false);
        yield return Case("column-compare-refused", Request(new Predicate.ColumnCompare(Text, CompareOp.Equal, Id)), Index(Text, Id), false);
        yield return Case("element-set-refused", Request(new Predicate.ElementOf(
            new ElementSetRef("ticket_id", QueryType.String),
            [QueryConstant.Of(Text, "open")],
            SetQuantifier.Any)), Index(Text), false);
        yield return Case("unfiltered-bounded-order", Request(
            Predicate.AlwaysTrue.Instance,
            [new OrderTerm(Created, OrderDirection.Descending, NullOrder.Last)],
            Paging.OffsetLimit(0, 20)), Index(Created, OrderDirection.Descending), true);
        yield return Case("unfiltered-order-no-take", Request(
            Predicate.AlwaysTrue.Instance,
            [new OrderTerm(Created, OrderDirection.Descending, NullOrder.Last)],
            Paging.None), Index(Created, OrderDirection.Descending), false);
        yield return Case("unbounded-count", Request(
            Predicate.AlwaysTrue.Instance,
            result: ResultShape.TotalCount.Instance), Index(Text), false);
        yield return Case("bounded-count", Request(
            new Predicate.Equal(Text, QueryConstant.Of(Text, "open")),
            result: ResultShape.TotalCount.Instance), Index(Text), true);

        foreach (var direction in Enum.GetValues<OrderDirection>())
        {
            yield return Case(
                "compound-suffix-" + direction,
                Request(
                    new Predicate.Equal(Text, QueryConstant.Of(Text, "open")),
                    [new OrderTerm(Created, direction, NullOrderFor(direction))],
                    Paging.Continuation("cursor")),
                new CoverageIndex("ix_text_created", [
                    new CoverageIndexColumn(Text.Name, direction),
                    new CoverageIndexColumn(Created.Name, direction)]),
                true);
        }

        yield return Case("compound-membership-suffix-refused", Request(
            new Predicate.In(Text, [QueryConstant.Of(Text, "open"), QueryConstant.Of(Text, "closed")]),
            [new OrderTerm(Created, OrderDirection.Ascending, NullOrder.First)],
            Paging.Continuation("cursor")),
            new CoverageIndex("ix_text_created", [new CoverageIndexColumn(Text.Name), new CoverageIndexColumn(Created.Name)]),
            false);
        yield return Case("compound-range-leading-order", Request(
            new Predicate.And([
                new Predicate.Equal(Text, QueryConstant.Of(Text, "open")),
                new Predicate.Range(Created, Bound.Inclusive(QueryConstant.Of(Created, DateTimeOffset.UnixEpoch)), null)]),
            [new OrderTerm(Created, OrderDirection.Ascending, NullOrder.First), new OrderTerm(Id, OrderDirection.Ascending, NullOrder.First)],
            Paging.Continuation("cursor")),
            new CoverageIndex("ix_text_created_id", [new CoverageIndexColumn(Text.Name), new CoverageIndexColumn(Created.Name), new CoverageIndexColumn(Id.Name, isNullable: false)]),
            true);
        yield return Case("range-before-equality-refused", Request(
            new Predicate.And([
                new Predicate.Range(Created, Bound.Inclusive(QueryConstant.Of(Created, DateTimeOffset.UnixEpoch)), null),
                new Predicate.Equal(Text, QueryConstant.Of(Text, "open"))]),
            [],
            Paging.None),
            new CoverageIndex("ix_created_text", [new CoverageIndexColumn(Created.Name), new CoverageIndexColumn(Text.Name)]),
            false);
        yield return Case("sparse-non-null-equality", Request(new Predicate.Equal(Text, QueryConstant.Of(Text, "open"))), SparseIndex(Text), true);
        yield return Case("sparse-unfiltered-nullable-order", Request(
            Predicate.AlwaysTrue.Instance,
            [new OrderTerm(Text, OrderDirection.Ascending, NullOrder.First)],
            Paging.OffsetLimit(0, 20)), SparseIndex(Text), false);
        yield return Case("sparse-unfiltered-non-nullable-order", Request(
            Predicate.AlwaysTrue.Instance,
            [new OrderTerm(Id, OrderDirection.Ascending, NullOrder.First)],
            Paging.OffsetLimit(0, 20)),
            new CoverageIndex("ix_id_sparse", [new CoverageIndexColumn(Id.Name, isNullable: false)], IndexMissingValueBehavior.Excluded),
            true);
        yield return Case("distinct-unbounded-indexed-projection-refused", Request(
            Predicate.AlwaysTrue.Instance,
            projection: Projection.ColumnsOnly(Text),
            distinct: true), Index(Text), false);
        yield return Case("distinct-uncovered-projection", Request(
            Predicate.AlwaysTrue.Instance,
            projection: Projection.ColumnsOnly(Text),
            distinct: true), Index(Amount), false);
        yield return Case("first-ordered", Request(
            Predicate.AlwaysTrue.Instance,
            [new OrderTerm(Created, OrderDirection.Ascending, NullOrder.First)],
            result: ResultShape.First.Instance), Index(Created), true);
        yield return Case("first-without-order", Request(
            Predicate.AlwaysTrue.Instance,
            result: ResultShape.First.Instance), Index(Text), false);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Pinned_v1_cases_are_repointed_to_public_ast(
        string name,
        QueryRequest request,
        CoverageIndex[] indexes,
        bool expectedCovered)
    {
        var result = QueryCoverageChecker.Check(request, indexes);

        Assert.True(result.IsCovered == expectedCovered, name);
        if (!expectedCovered)
        {
            Assert.NotNull(result.Refusal);
            Assert.Contains(result.Refusal!.SuggestedDeclaration, result.Refusal.Message, StringComparison.Ordinal);
        }
    }

    private static object[] Case(string name, QueryRequest request, CoverageIndex index, bool expectedCovered) =>
        [name, request, new[] { index }, expectedCovered];

    private static QueryRequest Request(
        Predicate predicate,
        ImmutableArray<OrderTerm> order = default,
        Paging? paging = null,
        ResultShape? result = null,
        Projection? projection = null,
        bool distinct = false) =>
        new(Table, predicate, order, projection ?? Projection.All, paging ?? Paging.None, result ?? ResultShape.Rows.Instance, distinct: distinct);

    private static CoverageIndex Index(ColumnRef column, OrderDirection direction = OrderDirection.Ascending) =>
        new("ix_" + column.Name, [new CoverageIndexColumn(column.Name, direction, column.IsNullable)]);

    private static CoverageIndex Index(ColumnRef first, ColumnRef second) =>
        new("ix_" + first.Name + "_" + second.Name, [
            new CoverageIndexColumn(first.Name, isNullable: first.IsNullable),
            new CoverageIndexColumn(second.Name, isNullable: second.IsNullable)]);

    private static CoverageIndex SparseIndex(ColumnRef column) =>
        new("ix_" + column.Name + "_sparse", [new CoverageIndexColumn(column.Name, isNullable: column.IsNullable)], IndexMissingValueBehavior.Excluded);

    private static NullOrder NullOrderFor(OrderDirection direction) =>
        direction == OrderDirection.Ascending ? NullOrder.First : NullOrder.Last;
}
