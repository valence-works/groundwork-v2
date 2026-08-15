using System.Collections.Immutable;
using Groundwork.MongoDb;
using Groundwork.PostgreSql;
using Groundwork.Query.Model;
using Groundwork.SqlServer;
using Groundwork.Sqlite;
using Groundwork.Substrate.Relational;
using MongoDB.Bson;
using Xunit;

namespace Groundwork.Differential.Tests;

public sealed class QueryRendererTests
{
    private static readonly TableId Table = new("customers");
    private static readonly ColumnRef Id = new(Table, "id", QueryType.Int64, isNullable: false);
    private static readonly ColumnRef Name = new(Table, "name", QueryType.String, isNullable: true, maxLength: 100);
    private static readonly ColumnRef Amount = new(Table, "amount", QueryType.Int32, isNullable: true);

    [Fact]
    public void All_four_renderers_preserve_the_normalized_result_shape_and_order()
    {
        var request = Request(
            new Predicate.In(Name, [QueryConstant.Of(Name, "Alice"), QueryConstant.Of(Name, null)]),
            [new OrderTerm(Name, OrderDirection.Ascending, NullOrder.First)],
            Paging.OffsetLimit(0, 10),
            ResultShape.Rows.Instance);
        var options = new QueryRenderOptions(tieBreakColumns: [Id]);

        var sqlite = new SqliteQueryRenderer().Render(request, options);
        var postgres = new PostgreSqlQueryRenderer().Render(request, options);
        var sqlServer = new SqlServerQueryRenderer().Render(request, options);
        var mongo = new MongoQueryRenderer().Render(request, options);

        Assert.Equal(new[] { "name", "id" }, sqlite.AppliedOrder.ToArray());
        Assert.Equal(sqlite.AppliedOrder.ToArray(), postgres.AppliedOrder.ToArray());
        Assert.Equal(sqlite.AppliedOrder.ToArray(), sqlServer.AppliedOrder.ToArray());
        Assert.Equal(sqlite.AppliedOrder.ToArray(), mongo.AppliedOrder.ToArray());
        Assert.Equal(3, sqlite.Parameters.Length);
        Assert.Equal(sqlite.Parameters.Select(parameter => parameter.Type).ToArray(), postgres.Parameters.Select(parameter => parameter.Type).ToArray());
        Assert.DoesNotContain("LOWER", sqlite.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPPER", sqlite.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ILIKE", postgres.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("$regex", mongo.Filter.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Result_shape_controls_count_without_an_unconditional_count()
    {
        var rows = new SqliteQueryRenderer().Render(Request(new Predicate.Equal(Id, QueryConstant.Of(Id, 1L)), [], Paging.None, ResultShape.Rows.Instance));
        var count = new SqliteQueryRenderer().Render(Request(new Predicate.Equal(Id, QueryConstant.Of(Id, 1L)), [], Paging.None, ResultShape.TotalCount.Instance));

        Assert.False(rows.IncludesTotalCount);
        Assert.DoesNotContain("COUNT", rows.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.True(count.IncludesTotalCount);
        Assert.Contains("COUNT(*) OVER()", count.CommandText, StringComparison.Ordinal);

        var sqlServerCount = new SqlServerQueryRenderer().Render(
            Request(new Predicate.Equal(Id, QueryConstant.Of(Id, 1L)), [], Paging.None, ResultShape.TotalCount.Instance));
        Assert.Contains("COUNT_BIG(*) OVER()", sqlServerCount.CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_in_is_match_none_but_keeps_a_declared_sql_server_and_mongo_hint()
    {
        var request = Request(new Predicate.In(Id, ImmutableArray<QueryConstant>.Empty), [], Paging.None, ResultShape.Rows.Instance);
        var options = new QueryRenderOptions([
            new QueryIndexDeclaration("ix_customers_id", ["id"], QueryIndexPinning.Pinned, includesNulls: false)]);

        var sql = new SqlServerQueryRenderer().Render(request, options);
        var mongo = new MongoQueryRenderer().Render(request, options);

        Assert.True(sql.IsMatchNone);
        Assert.True(sql.IndexHintApplied);
        Assert.Contains("INDEX([ix_customers_id])", sql.CommandText, StringComparison.Ordinal);
        Assert.True(mongo.IsMatchNone);
        Assert.Equal("ix_customers_id", mongo.Hint);
        Assert.True(mongo.Filter.Contains("_groundwork_match_none"));
    }

    [Fact]
    public void Sparse_pinned_index_refuses_a_nullable_match_but_not_a_contradiction()
    {
        var options = new QueryRenderOptions([
            new QueryIndexDeclaration("ix_amount", ["amount"], QueryIndexPinning.Pinned, includesNulls: false)]);
        var request = Request(Predicate.AlwaysTrue.Instance, [], Paging.None, ResultShape.Rows.Instance);

        var sql = Assert.Throws<QueryRenderException>(() => new SqlServerQueryRenderer().Render(request, options));
        var mongo = Assert.Throws<QueryRenderException>(() => new MongoQueryRenderer().Render(request, options));
        Assert.Equal("GW-QUERY-009", sql.Code);
        Assert.Equal("GW-QUERY-009", mongo.Code);
    }

    [Fact]
    public void Nullable_keyset_continuation_is_typed_and_uses_explicit_null_rank_on_all_providers()
    {
        var token = QueryContinuationToken.Encode([QueryConstant.Of(Amount, null), QueryConstant.Of(Id, 42L)]);
        var request = Request(
            Predicate.AlwaysTrue.Instance,
            [new OrderTerm(Amount, OrderDirection.Ascending, NullOrder.First)],
            Paging.Continuation(token, 5),
            ResultShape.Rows.Instance);
        var options = new QueryRenderOptions(tieBreakColumns: [Id]);

        var sqlite = new SqliteQueryRenderer().Render(request, options);
        var postgres = new PostgreSqlQueryRenderer().Render(request, options);
        var sqlServer = new SqlServerQueryRenderer().Render(request, options);
        var mongo = new MongoQueryRenderer().Render(request, options);

        Assert.Contains("IS NOT NULL", sqlite.CommandText, StringComparison.Ordinal);
        Assert.Contains("IS NULL", sqlite.CommandText, StringComparison.Ordinal);
        Assert.Contains("IS NOT NULL", postgres.CommandText, StringComparison.Ordinal);
        Assert.Contains("IS NOT NULL", sqlServer.CommandText, StringComparison.Ordinal);
        Assert.Contains("$ne", mongo.Filter.ToString(), StringComparison.Ordinal);
        Assert.Contains("_groundwork_null_rank_0", string.Join("\n", mongo.Pipeline.Select(stage => stage.ToString())), StringComparison.Ordinal);
        Assert.Equal(new[] { "amount", "id" }, mongo.AppliedOrder.ToArray());
    }

    [Fact]
    public void In_cardinality_and_provider_parameter_budgets_fail_with_the_v1_fence_code()
    {
        var values = Enumerable.Range(0, 1_001).Select(value => QueryConstant.Of(Amount, value)).ToArray();
        var overIn = Request(new Predicate.In(Amount, values), [], Paging.None, ResultShape.Rows.Instance);
        var inFailure = Assert.Throws<QueryRenderException>(() => new SqliteQueryRenderer().Render(overIn));
        Assert.Equal("GW-QUERY-015", inFailure.Code);

        var budgetValues = Enumerable.Range(0, 2_101).Select(value => QueryConstant.Of(Amount, value)).ToArray();
        var budgetFailure = Assert.Throws<QueryRenderException>(() => new SqlServerQueryRenderer().Render(
            Request(new Predicate.In(Amount, budgetValues), [], Paging.None, ResultShape.Rows.Instance),
            new QueryRenderOptions { InValueLimit = 3_000 }));
        Assert.Equal("GW-QUERY-015", budgetFailure.Code);
    }

    [Fact]
    public void Default_index_policy_never_emits_a_hint_and_postgres_has_no_hint_syntax()
    {
        var request = Request(new Predicate.Equal(Id, QueryConstant.Of(Id, 7L)), [], Paging.None, ResultShape.Rows.Instance);
        var options = new QueryRenderOptions([new QueryIndexDeclaration("ix_id", ["id"], QueryIndexPinning.ProviderDefault)]);

        var sql = new SqlServerQueryRenderer().Render(request, options);
        var postgres = new PostgreSqlQueryRenderer().Render(request, options with { });

        Assert.False(sql.IndexHintApplied);
        Assert.DoesNotContain("INDEX", sql.CommandText, StringComparison.Ordinal);
        Assert.False(postgres.IndexHintApplied);
    }

    [Fact]
    public void Mongo_total_count_is_rendered_only_for_the_total_count_shape()
    {
        var rows = new MongoQueryRenderer().Render(Request(
            Predicate.AlwaysTrue.Instance,
            [new OrderTerm(Amount, OrderDirection.Descending, NullOrder.First)],
            Paging.Keyset(5),
            ResultShape.Rows.Instance));
        var total = new MongoQueryRenderer().Render(Request(
            Predicate.AlwaysTrue.Instance,
            [new OrderTerm(Amount, OrderDirection.Descending, NullOrder.First)],
            Paging.Keyset(5),
            ResultShape.TotalCount.Instance));

        var rowsPipeline = string.Join("\n", rows.Pipeline.Select(stage => stage.ToString()));
        var totalPipeline = string.Join("\n", total.Pipeline.Select(stage => stage.ToString()));
        Assert.DoesNotContain("__groundwork_total_count", rowsPipeline, StringComparison.Ordinal);
        Assert.Contains("__groundwork_total_count", totalPipeline, StringComparison.Ordinal);
    }

    private static QueryRequest Request(Predicate predicate, IEnumerable<OrderTerm> order, Paging paging, ResultShape result) =>
        new(Table, predicate, order.ToImmutableArray(), Projection.All, paging, result);
}
