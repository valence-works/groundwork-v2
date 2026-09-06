using System.Text.Json;
using Groundwork.Kernel;
using Groundwork.Query.Model;
using Groundwork.SqlServer;
using Groundwork.Substrate.Relational;
using Xunit;

namespace Groundwork.SqlServer.Tests;

public sealed class SqlServerQueryExecutionEvidenceTests
{
    [Fact]
    public void Bounded_string_equality_collects_ordinal_length_companion_projection_and_actual_paging()
    {
        var (table, id, payload) = Columns(nullablePayload: false);
        var request = new QueryRequest(
            table,
            new Predicate.Equal(payload, QueryConstant.Of(payload, "secret")),
            [new OrderTerm(id, nullOrder: NullOrder.First)],
            Projection.ColumnsOnly(id, payload),
            Paging.Keyset(2));

        var rendered = new SqlServerQueryRenderer().RenderForExecution(request, QueryRenderOptions.Default, hasLookahead: true);

        var shape = Assert.IsType<ProviderBoundedQueryEvidence>(rendered.Shape);
        var equality = Assert.Single(shape.Predicate.Facts);
        Assert.Equal("payload", equality.LogicalColumn);
        Assert.Equal(ProviderPredicateOperator.Equal, equality.Operator);
        Assert.Equal(ProviderPredicateComparison.Ordinal, equality.Comparison);
        Assert.Equal(ProviderPredicateBindingRole.Caller, equality.BindingRole);
        Assert.False(shape.Projection.AllColumns);
        Assert.Collection(shape.Projection.LogicalColumns,
            column => Assert.Equal("id", column),
            column => Assert.Equal("payload", column));
        Assert.Equal(ProviderNativeBoundKind.Explicit, shape.NativeOffset.Kind);
        Assert.Equal(0, shape.NativeOffset.Value);
        Assert.Equal(ProviderNativeBoundKind.Explicit, shape.NativeLimit.Kind);
        Assert.Equal(2, shape.NativeLimit.Value);
        Assert.Contains("DATALENGTH", rendered.Command.CommandText, StringComparison.Ordinal);
        Assert.Contains("OFFSET 0 ROWS FETCH NEXT @", rendered.Command.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(shape), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Bounded_string_range_collects_each_endpoint_and_actual_bounds(bool lowerInclusive, bool upperInclusive)
    {
        var (table, id, payload) = Columns(nullablePayload: false);
        var request = new QueryRequest(
            table,
            new Predicate.Range(
                payload,
                lowerInclusive
                    ? Bound.Inclusive(QueryConstant.Of(payload, "a"))
                    : Bound.Exclusive(QueryConstant.Of(payload, "a")),
                upperInclusive
                    ? Bound.Inclusive(QueryConstant.Of(payload, "z"))
                    : Bound.Exclusive(QueryConstant.Of(payload, "z"))),
            [new OrderTerm(id, nullOrder: NullOrder.First)],
            Projection.ColumnsOnly(id, payload),
            Paging.OffsetLimit(4, 3));

        var rendered = new SqlServerQueryRenderer().RenderForExecution(request, QueryRenderOptions.Default, hasLookahead: false);

        var shape = Assert.IsType<ProviderBoundedQueryEvidence>(rendered.Shape);
        var lower = Assert.Single(shape.Predicate.Facts, fact => fact.Operator == ProviderPredicateOperator.LowerBound);
        var upper = Assert.Single(shape.Predicate.Facts, fact => fact.Operator == ProviderPredicateOperator.UpperBound);
        Assert.Equal(lowerInclusive ? ProviderPredicateBoundInclusivity.Inclusive : ProviderPredicateBoundInclusivity.Exclusive,
            lower.BoundInclusivity);
        Assert.Equal(upperInclusive ? ProviderPredicateBoundInclusivity.Inclusive : ProviderPredicateBoundInclusivity.Exclusive,
            upper.BoundInclusivity);
        Assert.All(new[] { lower, upper }, fact =>
        {
            Assert.Equal("payload", fact.LogicalColumn);
            Assert.Equal(ProviderPredicateComparison.Ordinal, fact.Comparison);
            Assert.Equal(ProviderPredicateBindingRole.Caller, fact.BindingRole);
        });
        Assert.NotEqual(lower.BindingId, upper.BindingId);
        Assert.Equal(4, shape.NativeOffset.Value);
        Assert.Equal(3, shape.NativeLimit.Value);
        Assert.Contains("DATALENGTH", rendered.Command.CommandText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(NullOrder.First)]
    [InlineData(NullOrder.Last)]
    public void Nullable_string_order_reports_null_rank_and_ordinal_length_key(NullOrder nullOrder)
    {
        var (table, id, payload) = Columns(nullablePayload: true);
        var request = new QueryRequest(
            table,
            Predicate.AlwaysTrue.Instance,
            [new OrderTerm(payload, OrderDirection.Descending, nullOrder), new OrderTerm(id, nullOrder: NullOrder.First)],
            Projection.ColumnsOnly(id, payload),
            Paging.Keyset(2));

        var rendered = new SqlServerQueryRenderer().RenderForExecution(request, QueryRenderOptions.Default, hasLookahead: true);

        var shape = Assert.IsType<ProviderBoundedQueryEvidence>(rendered.Shape);
        var order = Assert.Single(shape.Ordering, term => term.LogicalColumn == "payload");
        Assert.Equal(nullOrder, order.NullPlacement);
        Assert.Equal(
            new[] { ProviderOrderingTransform.NullRank, ProviderOrderingTransform.OrdinalStringKey },
            order.Transforms.ToArray());
        Assert.Equal(ProviderPredicateComparison.Ordinal, order.Comparison);
        Assert.Contains("CASE WHEN", rendered.Command.CommandText, StringComparison.Ordinal);
        Assert.Contains("DATALENGTH", rendered.Command.CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public void Persisted_ordinal_identity_reports_logical_source_and_physical_order_transform()
    {
        var (table, id, payload) = Columns(nullablePayload: false);
        var mapping = new QuerySearchKeyColumn(
            payload.Name,
            "__groundwork_ordinal_payload",
            QuerySearchKeyPolicy.Ordinal,
            orderByPhysicalColumn: true,
            supportsPrefixPredicates: false,
            preservesOrdinalIdentity: true);
        var options = new QueryRenderOptions(
            [new QueryIndexDeclaration(
                "ix_payload_ordinal",
                ["__groundwork_ordinal_payload", "id"],
                nullableColumns: [])],
            selectedIndex: "ix_payload_ordinal")
        {
            SearchKeyColumns = new Dictionary<string, QuerySearchKeyColumn>(StringComparer.Ordinal)
            {
                [mapping.SourceColumn] = mapping
            }
        };
        var request = new QueryRequest(
            table,
            new Predicate.Equal(payload, QueryConstant.Of(payload, "secret")),
            [new OrderTerm(payload, nullOrder: NullOrder.First)],
            Projection.ColumnsOnly(id, payload),
            Paging.Keyset(2));

        var rendered = new SqlServerQueryRenderer().RenderForExecution(request, options, hasLookahead: true);

        var shape = Assert.IsType<ProviderBoundedQueryEvidence>(rendered.Shape);
        Assert.Equal(payload.Name, Assert.Single(shape.Predicate.Facts).LogicalColumn);
        var order = Assert.Single(shape.Ordering, term => term.LogicalColumn == payload.Name);
        Assert.Equal([ProviderOrderingTransform.PhysicalSearchKey], order.Transforms.ToArray());
        Assert.Contains("ORDER BY [__groundwork_ordinal_payload] COLLATE Latin1_General_100_BIN2 ASC", rendered.Command.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("ORDER BY [__groundwork_ordinal_payload] COLLATE Latin1_General_100_BIN2 ASC, DATALENGTH", rendered.Command.CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public void Unmapped_provider_owned_column_withholds_the_whole_shape()
    {
        var table = new TableId("records");
        var physical = new ColumnRef(table, "__groundwork_ordinal_payload", QueryType.String, isNullable: false);
        var request = new QueryRequest(
            table,
            new Predicate.Equal(physical, QueryConstant.Of(physical, "secret")),
            [],
            Projection.All,
            Paging.Keyset(2));

        var rendered = new SqlServerQueryRenderer().RenderForExecution(request, QueryRenderOptions.Default, hasLookahead: true);

        Assert.Null(rendered.Shape);
    }

    [Fact]
    public void Null_equality_withholds_the_shape()
    {
        var (table, id, payload) = Columns(nullablePayload: true);
        var request = new QueryRequest(
            table,
            new Predicate.Equal(payload, QueryConstant.Of(payload, null)),
            [new OrderTerm(id, nullOrder: NullOrder.First)],
            Projection.ColumnsOnly(id, payload),
            Paging.Keyset(2));

        Assert.Null(new SqlServerQueryRenderer().RenderForExecution(request, QueryRenderOptions.Default, hasLookahead: true).Shape);
    }

    [Fact]
    public void Guid_order_withholds_the_shape_until_its_cast_transform_is_mapped()
    {
        var table = new TableId("records");
        var id = new ColumnRef(table, "id", QueryType.Guid, isNullable: false);
        var request = new QueryRequest(
            table,
            Predicate.AlwaysTrue.Instance,
            [new OrderTerm(id, nullOrder: NullOrder.First)],
            Projection.ColumnsOnly(id),
            Paging.Keyset(2));

        Assert.Null(new SqlServerQueryRenderer().RenderForExecution(request, QueryRenderOptions.Default, hasLookahead: true).Shape);
    }

    private static (TableId Table, ColumnRef Id, ColumnRef Payload) Columns(bool nullablePayload)
    {
        var table = new TableId("records");
        return (
            table,
            new ColumnRef(table, "id", QueryType.String, isNullable: false),
            new ColumnRef(table, "payload", QueryType.String, isNullable: nullablePayload));
    }
}
