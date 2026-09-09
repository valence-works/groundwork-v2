using Groundwork.Kernel;
using Groundwork.PostgreSql;
using Groundwork.Query.Model;
using Groundwork.Substrate.Relational;
using Xunit;

namespace Groundwork.PostgreSql.Tests;

public sealed class PostgreSqlQueryExecutionEvidenceTests
{
    [Fact]
    public void Bounded_string_equality_collects_actual_comparison_and_page_limit()
    {
        var (table, id, payload) = Columns(nullablePayload: false);
        var request = new QueryRequest(
            table,
            new Predicate.Equal(payload, QueryConstant.Of(payload, "secret")),
            [new OrderTerm(id, nullOrder: NullOrder.First)],
            Projection.ColumnsOnly(id, payload),
            Paging.Keyset(2));

        var rendered = new PostgreSqlQueryRenderer().RenderForExecution(request, QueryRenderOptions.Default, hasLookahead: true);

        var shape = Assert.IsType<ProviderBoundedQueryEvidence>(rendered.Shape);
        var equality = Assert.Single(shape.Predicate.Facts);
        Assert.Equal("payload", equality.LogicalColumn);
        Assert.Equal(ProviderPredicateOperator.Equal, equality.Operator);
        Assert.Equal(QueryType.String, equality.ValueType);
        Assert.Equal(ProviderPredicateComparison.Ordinal, equality.Comparison);
        Assert.Equal(ProviderPredicateBindingRole.Caller, equality.BindingRole);
        Assert.Equal(ProviderNativeBoundKind.Explicit, shape.NativeLimit.Kind);
        Assert.Equal(2, shape.NativeLimit.Value);
        Assert.True(shape.HasLookahead);
        Assert.Contains("COLLATE \"C\"", rendered.Command.CommandText, StringComparison.Ordinal);
        Assert.Contains("LIMIT @", rendered.Command.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(shape), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Bounded_string_range_collects_each_emitted_endpoint(bool lowerInclusive, bool upperInclusive)
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

        var rendered = new PostgreSqlQueryRenderer().RenderForExecution(request, QueryRenderOptions.Default, hasLookahead: false);

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
        Assert.Equal(ProviderNativeBoundKind.Explicit, shape.NativeOffset.Kind);
        Assert.Equal(4, shape.NativeOffset.Value);
        Assert.Equal(ProviderNativeBoundKind.Explicit, shape.NativeLimit.Kind);
        Assert.Equal(3, shape.NativeLimit.Value);
        Assert.Contains(lowerInclusive ? ">=" : ">", rendered.Command.CommandText, StringComparison.Ordinal);
        Assert.Contains(upperInclusive ? "<=" : "<", rendered.Command.CommandText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(NullOrder.First)]
    [InlineData(NullOrder.Last)]
    public void Nullable_string_order_reports_null_rank_and_ordinal_key(NullOrder nullOrder)
    {
        var (table, id, payload) = Columns(nullablePayload: true);
        var request = new QueryRequest(
            table,
            new Predicate.Equal(payload, QueryConstant.Of(payload, "secret")),
            [new OrderTerm(payload, OrderDirection.Descending, nullOrder), new OrderTerm(id, nullOrder: NullOrder.First)],
            Projection.ColumnsOnly(id, payload),
            Paging.Keyset(2));

        var rendered = new PostgreSqlQueryRenderer().RenderForExecution(request, QueryRenderOptions.Default, hasLookahead: true);

        var shape = Assert.IsType<ProviderBoundedQueryEvidence>(rendered.Shape);
        var order = Assert.Single(shape.Ordering, term => term.LogicalColumn == "payload");
        Assert.Equal(nullOrder, order.NullPlacement);
        Assert.Equal(
            new[] { ProviderOrderingTransform.NullRank, ProviderOrderingTransform.OrdinalStringKey },
            order.Transforms.ToArray());
        Assert.Equal(ProviderPredicateComparison.Ordinal, order.Comparison);
        Assert.Contains("CASE WHEN", rendered.Command.CommandText, StringComparison.Ordinal);
        Assert.Contains("string_to_array", rendered.Command.CommandText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Persisted_ordinal_identity_reports_logical_source_and_physical_transform(bool projectPhysicalColumn)
    {
        var (table, id, payload) = Columns(nullablePayload: false);
        var options = new QueryRenderOptions
        {
            SearchKeyColumns = new Dictionary<string, QuerySearchKeyColumn>(StringComparer.Ordinal)
            {
                [payload.Name] = new QuerySearchKeyColumn(
                    payload.Name,
                    "__groundwork_ordinal_payload",
                    QuerySearchKeyPolicy.Ordinal,
                    orderByPhysicalColumn: true,
                    supportsPrefixPredicates: false,
                    preservesOrdinalIdentity: true)
            }
        };
        var request = new QueryRequest(
            table,
            new Predicate.Equal(payload, QueryConstant.Of(payload, "secret")),
            [new OrderTerm(payload, nullOrder: NullOrder.First)],
            Projection.ColumnsOnly(id, projectPhysicalColumn
                ? new ColumnRef(table, "__groundwork_ordinal_payload", QueryType.String, isNullable: false)
                : payload),
            Paging.Keyset(2));

        var rendered = new PostgreSqlQueryRenderer().RenderForExecution(request, options, hasLookahead: true);

        var shape = Assert.IsType<ProviderBoundedQueryEvidence>(rendered.Shape);
        var equality = Assert.Single(shape.Predicate.Facts);
        Assert.Equal(payload.Name, equality.LogicalColumn);
        var order = Assert.Single(shape.Ordering);
        Assert.Equal(payload.Name, order.LogicalColumn);
        Assert.Equal(
            new[] { ProviderOrderingTransform.NullRank, ProviderOrderingTransform.PhysicalSearchKey },
            order.Transforms.ToArray());
        Assert.Equal(ProviderPredicateComparison.Ordinal, order.Comparison);
        Assert.Contains("__groundwork_ordinal_payload", rendered.Command.CommandText, StringComparison.Ordinal);
        Assert.Equal(new[] { id.Name, payload.Name }, shape.Projection.LogicalColumns);
        Assert.DoesNotContain("__groundwork_ordinal_payload", System.Text.Json.JsonSerializer.Serialize(shape), StringComparison.Ordinal);
    }

    [Fact]
    public void Unsupported_continuation_and_distinct_shapes_withhold_the_whole_shape()
    {
        var (table, id, payload) = Columns(nullablePayload: false);
        var renderer = new PostgreSqlQueryRenderer();
        var order = new OrderTerm(id, nullOrder: NullOrder.First);
        var baseRequest = new QueryRequest(
            table,
            new Predicate.Equal(payload, QueryConstant.Of(payload, "secret")),
            [order],
            Projection.ColumnsOnly(id, payload),
            Paging.Keyset(2));

        var distinct = new QueryRequest(
            table,
            baseRequest.Where,
            baseRequest.Order,
            baseRequest.Projection,
            baseRequest.Paging,
            latestPerKey: null,
            acceptedScan: null,
            distinct: true);
        var token = QueryContinuationToken.Encode(baseRequest, QueryRenderOptions.Default,
            [QueryConstant.Of(id, "cursor")]);
        var continuation = new QueryRequest(
            table,
            baseRequest.Where,
            baseRequest.Order,
            baseRequest.Projection,
            Paging.Continuation(token, 2));

        Assert.Null(renderer.RenderForExecution(distinct, QueryRenderOptions.Default, hasLookahead: true).Shape);
        Assert.Null(renderer.RenderForExecution(continuation, QueryRenderOptions.Default, hasLookahead: true).Shape);
    }

    [Fact]
    public void Declared_but_unreferenced_element_and_non_identity_search_keys_do_not_withhold_the_shape()
    {
        // A unit may declare element search keys and folded search keys for other columns; only a query
        // that emits such a provider-owned physical column fails closed (#432).
        var (table, id, payload) = Columns(nullablePayload: false);
        var request = new QueryRequest(
            table,
            new Predicate.Equal(id, QueryConstant.Of(id, "secret")),
            [new OrderTerm(id, nullOrder: NullOrder.First)],
            Projection.ColumnsOnly(id),
            Paging.Keyset(2));
        var options = new QueryRenderOptions
        {
            SearchKeyColumns = new Dictionary<string, QuerySearchKeyColumn>(StringComparer.Ordinal)
            {
                [payload.Name] = new QuerySearchKeyColumn(
                    payload.Name,
                    "__groundwork_folded_payload",
                    QuerySearchKeyPolicy.AsciiIgnoreCase,
                    orderByPhysicalColumn: true)
            },
            ElementSearchKeyColumns = new Dictionary<string, QueryElementSearchKeyColumn>(StringComparer.Ordinal)
            {
                ["tags"] = new QueryElementSearchKeyColumn("tags", "__groundwork_elements_tags", QuerySearchKeyPolicy.UnicodeOrdinalIgnoreCase)
            }
        };

        var rendered = new PostgreSqlQueryRenderer().RenderForExecution(request, options, hasLookahead: true);

        var shape = Assert.IsType<ProviderBoundedQueryEvidence>(rendered.Shape);
        Assert.Equal(id.Name, Assert.Single(shape.Ordering).LogicalColumn);
        Assert.DoesNotContain("__groundwork_", System.Text.Json.JsonSerializer.Serialize(shape), StringComparison.Ordinal);
    }

    [Fact]
    public void Referenced_non_identity_search_keys_and_count_shapes_withhold_the_whole_shape()
    {
        var (table, id, payload) = Columns(nullablePayload: false);
        var request = new QueryRequest(
            table,
            new Predicate.Equal(payload, QueryConstant.Of(payload, "secret")),
            [new OrderTerm(id, nullOrder: NullOrder.First)],
            Projection.ColumnsOnly(id, payload),
            Paging.Keyset(2));
        var nonIdentityOptions = new QueryRenderOptions
        {
            SearchKeyColumns = new Dictionary<string, QuerySearchKeyColumn>(StringComparer.Ordinal)
            {
                [payload.Name] = new QuerySearchKeyColumn(
                    payload.Name,
                    "__groundwork_folded_payload",
                    QuerySearchKeyPolicy.AsciiIgnoreCase,
                    orderByPhysicalColumn: true)
            }
        };
        var count = new QueryRequest(
            table,
            request.Where,
            request.Order,
            request.Projection,
            request.Paging,
            ResultShape.TotalCount.Instance);

        var renderer = new PostgreSqlQueryRenderer();
        // A folded search key has no logical ordinal order or comparison the fact contract can state, so a
        // query that emits it, in a predicate or an ordering, withholds the whole shape (#432).
        Assert.Null(renderer.RenderForExecution(request, nonIdentityOptions, hasLookahead: true).Shape);
        var foldedOrder = new QueryRequest(
            table,
            new Predicate.Equal(id, QueryConstant.Of(id, "secret")),
            [new OrderTerm(payload, nullOrder: NullOrder.First)],
            request.Projection,
            request.Paging);
        Assert.Null(renderer.RenderForExecution(foldedOrder, nonIdentityOptions, hasLookahead: true).Shape);
        Assert.Null(renderer.RenderForExecution(count, QueryRenderOptions.Default, hasLookahead: true).Shape);
    }

    [Fact]
    public void Guid_text_ordering_withholds_the_shape_until_its_cast_transform_is_mapped()
    {
        var table = new TableId("records");
        var id = new ColumnRef(table, "id", QueryType.Guid, isNullable: false);
        var request = new QueryRequest(
            table,
            Predicate.AlwaysTrue.Instance,
            [new OrderTerm(id, nullOrder: NullOrder.First)],
            Projection.ColumnsOnly(id),
            Paging.Keyset(2));

        var rendered = new PostgreSqlQueryRenderer().RenderForExecution(
            request,
            QueryRenderOptions.Default,
            hasLookahead: true);

        Assert.Null(rendered.Shape);
    }

    [Fact]
    public void Physical_logical_mapping_collisions_withhold_the_shape_in_either_enumeration_order()
    {
        var (table, id, payload) = Columns(nullablePayload: false);
        var request = new QueryRequest(
            table,
            Predicate.AlwaysTrue.Instance,
            [new OrderTerm(payload, nullOrder: NullOrder.First)],
            Projection.ColumnsOnly(id, payload),
            Paging.Keyset(2));
        var identity = new QuerySearchKeyColumn(
            "logical",
            "physical",
            QuerySearchKeyPolicy.Ordinal,
            preservesOrdinalIdentity: true);
        var ordinary = new QuerySearchKeyColumn(
            "physical",
            "physical",
            QuerySearchKeyPolicy.Ordinal);
        var renderer = new PostgreSqlQueryRenderer();

        foreach (var mappings in new[]
        {
            new Dictionary<string, QuerySearchKeyColumn>(StringComparer.Ordinal)
            {
                [identity.SourceColumn] = identity,
                [ordinary.SourceColumn] = ordinary
            },
            new Dictionary<string, QuerySearchKeyColumn>(StringComparer.Ordinal)
            {
                [ordinary.SourceColumn] = ordinary,
                [identity.SourceColumn] = identity
            }
        })
        {
            var options = new QueryRenderOptions { SearchKeyColumns = mappings };
            Assert.Null(renderer.RenderForExecution(request, options, hasLookahead: true).Shape);
        }
    }

    [Fact]
    public void Shared_base_order_hook_adds_the_physical_search_key_transform_once()
    {
        var (table, id, payload) = Columns(nullablePayload: false);
        var mapping = new QuerySearchKeyColumn(
            payload.Name,
            "__groundwork_ordinal_payload",
            QuerySearchKeyPolicy.Ordinal,
            orderByPhysicalColumn: true,
            preservesOrdinalIdentity: true);
        var options = new QueryRenderOptions
        {
            SearchKeyColumns = new Dictionary<string, QuerySearchKeyColumn>(StringComparer.Ordinal)
            {
                [mapping.SourceColumn] = mapping
            }
        };
        var request = new QueryRequest(
            table,
            Predicate.AlwaysTrue.Instance,
            [new OrderTerm(payload, nullOrder: NullOrder.First)],
            Projection.ColumnsOnly(id, payload),
            Paging.Keyset(2));

        var rendered = new BaseEvidenceRenderer().RenderForExecution(request, options, hasLookahead: true);

        var shape = Assert.IsType<ProviderBoundedQueryEvidence>(rendered.Shape);
        var order = Assert.Single(shape.Ordering);
        Assert.Equal(payload.Name, order.LogicalColumn);
        Assert.Equal(
            new[] { ProviderOrderingTransform.NullRank, ProviderOrderingTransform.PhysicalSearchKey },
            order.Transforms.ToArray());
    }

    private static (TableId Table, ColumnRef Id, ColumnRef Payload) Columns(bool nullablePayload)
    {
        var table = new TableId("records");
        return (
            table,
            new ColumnRef(table, "id", QueryType.String, isNullable: false),
            new ColumnRef(table, "payload", QueryType.String, isNullable: nullablePayload));
    }

    private sealed class BaseEvidenceRenderer : RelationalQueryRenderer
    {
        internal BaseEvidenceRenderer()
            : base(new PostgreSqlDialect(), PostgreSqlQueryRenderer.ParameterBudget, supportsIndexHints: false)
        {
        }

        protected override string ProviderName => "Base";
        protected override bool SupportsExecutionEvidence => true;

        protected override string RenderColumn(ColumnRef column) =>
            EvidenceColumn(base.RenderColumn(column), column, column.Type == QueryType.String
                ? ProviderPredicateComparison.Ordinal
                : ProviderPredicateComparison.Exact);
    }
}
