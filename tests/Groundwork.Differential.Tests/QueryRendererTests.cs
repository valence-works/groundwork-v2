using System.Collections.Immutable;
using Groundwork.Kernel;
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
    public void All_four_renderers_use_a_native_reduction_after_distinct_and_input_paging()
    {
        var request = new QueryRequest(
            Table,
            new Predicate.Equal(Name, QueryConstant.Of(Name, "Alice")),
            [new OrderTerm(Amount, OrderDirection.Ascending, NullOrder.Last)],
            Projection.ColumnsOnly(Amount),
            Paging.OffsetLimit(0, 2),
            new ResultShape.Sum(Amount),
            distinct: true);

        var relational = new RelationalQueryCommand[]
        {
            new SqliteQueryRenderer().Render(request),
            new PostgreSqlQueryRenderer().Render(request),
            new SqlServerQueryRenderer().Render(request)
        };
        Assert.All(relational, command =>
        {
            Assert.Contains("SUM", command.CommandText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("COUNT", command.CommandText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("ROW_NUMBER", command.CommandText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("amount", command.CommandText, StringComparison.OrdinalIgnoreCase);
        });

        var mongo = new MongoQueryRenderer().Render(request);
        Assert.NotEmpty(mongo.Pipeline);
        Assert.Contains("$group", mongo.Pipeline.ToString(), StringComparison.Ordinal);
        Assert.Contains("$facet", mongo.Pipeline.ToString(), StringComparison.Ordinal);
        Assert.Contains("$sum", mongo.Pipeline.ToString(), StringComparison.Ordinal);
        Assert.Contains("$limit", mongo.Pipeline.ToString(), StringComparison.Ordinal);
    }

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
        Assert.Contains("__groundwork_total_count", count.CommandText, StringComparison.Ordinal);
        Assert.Contains("LEFT JOIN __groundwork_page", count.CommandText, StringComparison.Ordinal);

        var sqlServerCount = new SqlServerQueryRenderer().Render(
            Request(new Predicate.Equal(Id, QueryConstant.Of(Id, 1L)), [], Paging.None, ResultShape.TotalCount.Instance));
        Assert.Contains("COUNT_BIG(*)", sqlServerCount.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("OFFSET 0 ROWS", sqlServerCount.CommandText, StringComparison.Ordinal);
        var orderedSqlServerCount = new SqlServerQueryRenderer().Render(Request(
            Predicate.AlwaysTrue.Instance,
            [new OrderTerm(Id, OrderDirection.Ascending, NullOrder.First)],
            Paging.None, ResultShape.TotalCount.Instance));
        Assert.Contains("ORDER BY", orderedSqlServerCount.CommandText, StringComparison.Ordinal);
        Assert.Contains("OFFSET 0 ROWS", orderedSqlServerCount.CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public void Sql_server_string_ranges_use_strict_comparison_and_length_tie_breaks()
    {
        var request = Request(new Predicate.Range(Name,
                Bound.Inclusive(QueryConstant.Of(Name, "a")),
                Bound.Inclusive(QueryConstant.Of(Name, "a "))),
            [], Paging.None, ResultShape.Rows.Instance);
        var command = new SqlServerQueryRenderer().Render(request);
        Assert.Contains("> @p", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("< @p", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("DATALENGTH", command.CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public void Counted_columns_only_projection_does_not_reapply_predicate_to_the_base_cte()
    {
        var request = Request(
            new Predicate.Equal(Name, QueryConstant.Of(Name, "Alice")),
            [new OrderTerm(Id, OrderDirection.Ascending, NullOrder.First)],
            Paging.OffsetLimit(1, 2),
            ResultShape.TotalCount.Instance,
            projection: Projection.ColumnsOnly(Id));

        var command = new SqliteQueryRenderer().Render(request);

        Assert.Contains("WHERE", command.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("__groundwork_base WHERE ([name]", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("__groundwork_count_only", command.CommandText, StringComparison.Ordinal);
        Assert.Equal(3, command.Parameters.Length);
    }

    [Fact]
    public void Empty_in_is_match_none_but_keeps_a_declared_sql_server_and_mongo_hint()
    {
        var request = Request(new Predicate.In(Id, ImmutableArray<QueryConstant>.Empty), [], Paging.None, ResultShape.Rows.Instance);
        var options = new QueryRenderOptions([
            new QueryIndexDeclaration("ix_customers_id", [new QueryIndexColumn("id", true, QueryType.Int64)], QueryIndexPinning.Pinned, includesNulls: false)]);

        var sql = new SqlServerQueryRenderer().Render(request, options);
        var mongo = new MongoQueryRenderer().Render(request, options);

        Assert.True(sql.IsMatchNone);
        Assert.True(sql.IndexHintApplied);
        Assert.Contains("INDEX([ix_customers_id])", sql.CommandText, StringComparison.Ordinal);
        Assert.Contains("[id] IS NOT NULL", sql.CommandText, StringComparison.Ordinal);
        Assert.True(mongo.IsMatchNone);
        Assert.Equal("ix_customers_id", mongo.Hint);
        Assert.Contains("_groundwork_match_none", mongo.Filter.ToString(), StringComparison.Ordinal);
        Assert.Contains("$type", mongo.Filter.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Pinned_logical_index_resolves_to_the_provider_physical_name()
    {
        var options = new QueryRenderOptions([
            new QueryIndexDeclaration("ix_customers_id", ["id"], QueryIndexPinning.Pinned)])
        {
            PhysicalIndexNames = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ix_customers_id"] = "__groundwork_ix_customers_ix_customers_id"
            }
        };
        var command = new SqlServerQueryRenderer().Render(
            Request(new Predicate.Equal(Id, QueryConstant.Of(Id, 7L)), [], Paging.None, ResultShape.Rows.Instance), options);

        Assert.True(command.IndexHintApplied);
        Assert.Contains("INDEX([__groundwork_ix_customers_ix_customers_id])", command.CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public void Mongo_provider_default_expectation_keeps_the_optimizer_in_control()
    {
        var options = new QueryRenderOptions(
            [new QueryIndexDeclaration("ix_customers_id", ["id"], QueryIndexPinning.ProviderDefault)],
            selectedIndex: "ix_customers_id")
        {
            PhysicalIndexNames = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ix_customers_id"] = "__groundwork_ix_customers_ix_customers_id"
            }
        };

        var command = new MongoQueryRenderer().Render(
            Request(new Predicate.Equal(Id, QueryConstant.Of(Id, 7L)), [], Paging.None, ResultShape.Rows.Instance), options);

        Assert.Null(command.Hint);
        Assert.Equal("ix_customers_id", command.ExpectedIndex);
    }

    [Fact]
    public void SqlServer_search_key_ranges_bind_as_the_physical_ansi_type()
    {
        var folded = new ColumnRef(
            Table, "name", QueryType.String, true, 100,
            stringComparison: QueryStringComparisonPolicy.UnicodeOrdinalIgnoreCase);
        var options = QueryRenderOptions.Default with
        {
            SearchKeyColumns = new Dictionary<string, QuerySearchKeyColumn>(StringComparer.Ordinal)
            {
                ["name"] = new(
                    "name",
                    SearchKeyProjection.ColumnName("name"),
                    QuerySearchKeyPolicy.UnicodeOrdinalIgnoreCase,
                    700)
            }
        };

        var command = new SqlServerQueryRenderer().Render(
            Request(new Predicate.StartsWith(folded, "I"), [], Paging.None, ResultShape.Rows.Instance),
            options);

        Assert.Contains("CAST(@p0 AS varchar(700))", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("CAST(@p1 AS varchar(700))", command.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("DATALENGTH([__groundwork_search_name]", command.CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public void Mongo_folded_search_key_ranges_remain_native_index_bounds()
    {
        var folded = new ColumnRef(
            Table, "name", QueryType.String, true, 100,
            stringComparison: QueryStringComparisonPolicy.UnicodeOrdinalIgnoreCase);
        var hidden = SearchKeyProjection.ColumnName("name");
        var options = QueryRenderOptions.Default with
        {
            SearchKeyColumns = new Dictionary<string, QuerySearchKeyColumn>(StringComparer.Ordinal)
            {
                ["name"] = new(
                    "name",
                    hidden,
                    QuerySearchKeyPolicy.UnicodeOrdinalIgnoreCase,
                    700)
            }
        };

        var command = new MongoQueryRenderer().Render(
            Request(new Predicate.StartsWith(folded, "I"), [], Paging.None, ResultShape.Rows.Instance),
            options);

        var bounds = Assert.IsType<BsonDocument>(command.Filter[hidden]);
        Assert.Equal("|000049", bounds["$gte"].AsString);
        Assert.Equal("|00004A", bounds["$lt"].AsString);
        Assert.DoesNotContain("$expr", command.Filter.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("$function", command.Filter.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Mongo_set_mutation_update_filter_remains_native_index_bounds()
    {
        var folded = new ColumnRef(
            Table, "name", QueryType.String, true, 100,
            stringComparison: QueryStringComparisonPolicy.UnicodeOrdinalIgnoreCase);
        var hidden = SearchKeyProjection.ColumnName("name");
        var options = QueryRenderOptions.Default with
        {
            SearchKeyColumns = new Dictionary<string, QuerySearchKeyColumn>(StringComparer.Ordinal)
            {
                ["name"] = new(
                    "name",
                    hidden,
                    QuerySearchKeyPolicy.UnicodeOrdinalIgnoreCase,
                    700)
            }
        };
        var predicate = new Predicate.StartsWith(folded, "I");

        var filter = new MongoQueryRenderer().RenderAggregationSourcePredicate(predicate, Table.Value, options);

        var bounds = Assert.IsType<BsonDocument>(filter[hidden]);
        Assert.Equal("|000049", bounds["$gte"].AsString);
        Assert.Equal("|00004A", bounds["$lt"].AsString);
        Assert.DoesNotContain("$expr", filter.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("$function", filter.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Mongo_set_mutation_delete_filter_rewrites_a_logical_folded_prefix()
    {
        var filter = RenderMongoMutationFilter();

        Assert.Contains(SearchKeyProjection.ColumnName("name"), filter.Names);
        Assert.DoesNotContain("$expr", filter.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("$function", filter.ToString(), StringComparison.Ordinal);
    }

    private static BsonDocument RenderMongoMutationFilter()
    {
        var folded = new ColumnRef(
            Table, "name", QueryType.String, true, 100,
            stringComparison: QueryStringComparisonPolicy.UnicodeOrdinalIgnoreCase);
        var options = QueryRenderOptions.Default with
        {
            SearchKeyColumns = new Dictionary<string, QuerySearchKeyColumn>(StringComparer.Ordinal)
            {
                ["name"] = new(
                    "name",
                    SearchKeyProjection.ColumnName("name"),
                    QuerySearchKeyPolicy.UnicodeOrdinalIgnoreCase,
                    700)
            }
        };

        return new MongoQueryRenderer().RenderAggregationSourcePredicate(
            new Predicate.StartsWith(folded, "I"), Table.Value, options);
    }

    [Fact]
    public void All_four_renderers_refuse_a_forged_prefix_comparison_policy()
    {
        var forged = new ColumnRef(Table, "name", QueryType.String, true, 100,
            stringComparison: QueryStringComparisonPolicy.Ordinal);
        var request = Request(new Predicate.StartsWith(forged, "I"), [], Paging.None, ResultShape.Rows.Instance);
        var options = QueryRenderOptions.Default with
        {
            SearchKeyColumns = new Dictionary<string, QuerySearchKeyColumn>(StringComparer.Ordinal)
            {
                ["name"] = new("name", SearchKeyProjection.ColumnName("name"), QuerySearchKeyPolicy.AsciiIgnoreCase, 500)
            }
        };

        foreach (var renderer in new object[]
        {
            new SqliteQueryRenderer(),
            new PostgreSqlQueryRenderer(),
            new SqlServerQueryRenderer(),
            new MongoQueryRenderer()
        })
        {
            var failure = Assert.Throws<QueryRenderException>(() => Render(renderer, request, options));
            Assert.Equal("GW-QUERY-031", failure.Code);
            Assert.Contains("matching comparison policy", failure.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void All_four_renderers_lower_ordinal_prefix_to_an_exact_base_column_range()
    {
        var request = Request(new Predicate.StartsWith(Name, "ab"), [], Paging.None, ResultShape.Rows.Instance);
        var relational = new[]
        {
            new SqliteQueryRenderer().Render(request),
            new PostgreSqlQueryRenderer().Render(request),
            new SqlServerQueryRenderer().Render(request)
        };

        foreach (var command in relational)
        {
            Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, "ab"));
            Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, "ac"));
            Assert.DoesNotContain("LIKE", command.CommandText, StringComparison.OrdinalIgnoreCase);
        }

        var mongo = new MongoQueryRenderer().Render(request);
        Assert.Contains("$gte", mongo.Filter.ToString(), StringComparison.Ordinal);
        Assert.Contains("$lt", mongo.Filter.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("$regex", mongo.Filter.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Mongo_partial_match_none_requires_exact_index_column_types()
    {
        var request = Request(new Predicate.In(Id, ImmutableArray<QueryConstant>.Empty), [], Paging.None, ResultShape.Rows.Instance);
        var options = new QueryRenderOptions([
            new QueryIndexDeclaration("ix_customers_id", ["id"], QueryIndexPinning.Pinned, includesNulls: false)]);

        var failure = Assert.Throws<QueryRenderException>(() => new MongoQueryRenderer().Render(request, options));
        Assert.Equal("GW-QUERY-009", failure.Code);
        Assert.Contains("exact QueryIndexColumn types", failure.Message, StringComparison.Ordinal);
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
        var tokenRequest = Request(
            Predicate.AlwaysTrue.Instance,
            [new OrderTerm(Amount, OrderDirection.Ascending, NullOrder.First)],
            Paging.Keyset(5),
            ResultShape.Rows.Instance);
        var options = new QueryRenderOptions(tieBreakColumns: [Id]);
        var token = QueryContinuationToken.Encode(tokenRequest, options,
            [QueryConstant.Of(Amount, null), QueryConstant.Of(Id, 42L)]);
        var request = Request(
            Predicate.AlwaysTrue.Instance,
            [new OrderTerm(Amount, OrderDirection.Ascending, NullOrder.First)],
            Paging.Continuation(token, 5),
            ResultShape.Rows.Instance);

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
    public void Continuation_tokens_bind_invocation_values_and_reject_unbound_legacy_tokens()
    {
        var first = Request(new Predicate.Equal(Name, QueryConstant.Of(Name, "alice")),
            [new OrderTerm(Id, OrderDirection.Ascending, NullOrder.First)], Paging.Keyset(1), ResultShape.Rows.Instance);
        var options = new QueryRenderOptions();
        var token = QueryContinuationToken.Encode(first, options, [QueryConstant.Of(Id, 7L)]);
        var other = Request(new Predicate.Equal(Name, QueryConstant.Of(Name, "bob")),
            [new OrderTerm(Id, OrderDirection.Ascending, NullOrder.First)], Paging.Continuation(token, 1), ResultShape.Rows.Instance);
        Assert.Throws<FormatException>(() => QueryContinuationToken.Decode(token, other, options));
        Assert.Throws<FormatException>(() => QueryContinuationToken.Decode(
            QueryContinuationToken.Encode([QueryConstant.Of(Id, 7L)]), first, options));
    }

    [Fact]
    public void Scoped_continuations_bind_anonymous_scope_discriminators()
    {
        var request = Request(Predicate.AlwaysTrue.Instance,
            [new OrderTerm(Id, OrderDirection.Ascending, NullOrder.First)], Paging.Keyset(1), ResultShape.Rows.Instance);
        var scopeColumn = new ColumnRef(Table, "__groundwork_scope", QueryType.String, false);
        var scopedA = QueryRequestExecution.WithProviderPredicate(request,
            new Predicate.And([request.Where, new Predicate.Equal(scopeColumn, QueryConstant.Of(scopeColumn, "scope-a"))]),
            QueryRequestExecution.ScopeBindingDiscriminator("scope-a"));
        var scopedB = QueryRequestExecution.WithProviderPredicate(request,
            new Predicate.And([request.Where, new Predicate.Equal(scopeColumn, QueryConstant.Of(scopeColumn, "scope-b"))]),
            QueryRequestExecution.ScopeBindingDiscriminator("scope-b"));
        var token = QueryContinuationToken.Encode(scopedA, QueryRenderOptions.Default, [QueryConstant.Of(Id, 1L)]);
        Assert.DoesNotContain("scope-a", token, StringComparison.Ordinal);
        Assert.Throws<FormatException>(() => QueryContinuationToken.Decode(token, scopedB, QueryRenderOptions.Default));
    }

    [Fact]
    public void Search_key_rewrites_preserve_privileged_continuation_binding()
    {
        var status = new ColumnRef(Table, "status", QueryType.String, true, 32,
            stringComparison: QueryStringComparisonPolicy.AsciiIgnoreCase);
        var options = new QueryRenderOptions(tieBreakColumns: [Id])
        {
            SearchKeyColumns = new Dictionary<string, QuerySearchKeyColumn>
            {
                [status.Name] = new(status.Name, "__groundwork_search_status",
                    QuerySearchKeyPolicy.AsciiIgnoreCase, 160)
            }
        };
        var first = Request(new Predicate.StartsWith(status, "Op"), [], Paging.Keyset(1), ResultShape.Rows.Instance);
        first = QueryRequestExecution.WithProviderPredicate(first, first.Where, "privileged-audit-binding");
        var token = QueryContinuationToken.Encode(first, options, [QueryConstant.Of(Id, 7L)]);
        var next = Request(first.Where, [], Paging.Continuation(token, 1), ResultShape.Rows.Instance);
        next = QueryRequestExecution.WithProviderPredicate(next, next.Where, "privileged-audit-binding");

        var relational = new SqliteQueryRenderer().Render(next, options);
        var mongo = new MongoQueryRenderer().Render(next, options);

        Assert.Contains("__groundwork_search_status", relational.CommandText, StringComparison.Ordinal);
        Assert.Contains("__groundwork_search_status", mongo.Filter.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Provider_cursor_and_json_array_renderers_preserve_contract_guards()
    {
        var guid = new ColumnRef(Table, "guid", QueryType.Guid, false);
        var request = Request(Predicate.AlwaysTrue.Instance,
            [new OrderTerm(guid, OrderDirection.Ascending, NullOrder.First)], Paging.Keyset(1), ResultShape.Rows.Instance);
        var options = new QueryRenderOptions();
        var token = QueryContinuationToken.Encode(request, options, [QueryConstant.Of(guid, Guid.Empty)]);
        request = Request(Predicate.AlwaysTrue.Instance,
            [new OrderTerm(guid, OrderDirection.Ascending, NullOrder.First)], Paging.Continuation(token, 1), ResultShape.Rows.Instance);
        var sql = new SqlServerQueryRenderer().Render(request, options);
        Assert.Contains("CONVERT(char(36)", sql.CommandText, StringComparison.Ordinal);

        var set = new ElementSetRef("tags", QueryType.String);
        var elementRequest = Request(new Predicate.ElementOf(set,
            [QueryConstant.Of(Name, "x")], SetQuantifier.All), [], Paging.None, ResultShape.Rows.Instance);
        Assert.Contains("json_type", new SqliteQueryRenderer().Render(elementRequest).CommandText, StringComparison.Ordinal);
        Assert.Contains("LEFT(LTRIM", new SqlServerQueryRenderer().Render(elementRequest).CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public void Mongo_latest_per_key_uses_portable_string_key_for_timestamp_ties()
    {
        var timestamp = new ColumnRef(Table, "createdAt", QueryType.DateTimeOffset, false);
        var tie = new ColumnRef(Table, "tie", QueryType.String, false);
        var request = new QueryRequest(
            Table,
            Predicate.AlwaysTrue.Instance,
            new[] { new OrderTerm(Name, OrderDirection.Ascending, NullOrder.First) }.ToImmutableArray(),
            Projection.ColumnsOnly(Name, tie, timestamp),
            Paging.None,
            result: ResultShape.Rows.Instance,
            latestPerKey: new LatestPerKey(Name, timestamp));
        var command = new MongoQueryRenderer().Render(request,
            new QueryRenderOptions(tieBreakColumns: [tie]));
        var pipeline = string.Join("\n", command.Pipeline.Select(stage => stage.ToString()));
        Assert.Contains("_groundwork_latest_tie_key_0", pipeline, StringComparison.Ordinal);
        Assert.Contains("charCodeAt", pipeline, StringComparison.Ordinal);
    }

    [Fact]
    public void Mongo_string_ranges_use_the_portable_ordinal_key()
    {
        var request = Request(new Predicate.Range(Name,
                Bound.Inclusive(QueryConstant.Of(Name, "\U00010000")),
                Bound.Exclusive(QueryConstant.Of(Name, "\uE000"))),
            [], Paging.None, ResultShape.Rows.Instance);
        var command = new MongoQueryRenderer().Render(request);
        Assert.Contains("charCodeAt", command.Filter.ToString(), StringComparison.Ordinal);
        Assert.Contains("$gte", command.Filter.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Mongo_unpaged_count_streams_without_a_facet_array()
    {
        var command = new MongoQueryRenderer().Render(Request(
            Predicate.AlwaysTrue.Instance, [], Paging.None, ResultShape.TotalCount.Instance));
        var pipeline = string.Join("\n", command.Pipeline.Select(stage => stage.ToString()));
        Assert.DoesNotContain("$facet", pipeline, StringComparison.Ordinal);
        Assert.Contains("$setWindowFields", pipeline, StringComparison.Ordinal);
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
        var options = new QueryRenderOptions(
            [new QueryIndexDeclaration("ix_id", ["id"], QueryIndexPinning.ProviderDefault)],
            selectedIndex: "ix_id");

        var sql = new SqlServerQueryRenderer().Render(request, options);
        var postgres = new PostgreSqlQueryRenderer().Render(request, options with { });

        Assert.Equal("ix_id", sql.SelectedIndex);
        Assert.False(sql.IndexHintApplied);
        Assert.DoesNotContain("INDEX", sql.CommandText, StringComparison.Ordinal);
        Assert.Equal("ix_id", postgres.SelectedIndex);
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
        Assert.Contains("$unionWith", totalPipeline, StringComparison.Ordinal);
        Assert.DoesNotContain("$facet", totalPipeline, StringComparison.Ordinal);
        var scopedTotal = new MongoQueryRenderer().Render(Request(
            Predicate.AlwaysTrue.Instance, [], Paging.Keyset(5), ResultShape.TotalCount.Instance),
            physicalCollectionName: "customers__scope__anonymous");
        Assert.Contains("customers__scope__anonymous", string.Join("\n", scopedTotal.Pipeline.Select(stage => stage.ToString())), StringComparison.Ordinal);
    }

    [Fact]
    public void All_four_renderers_emit_q2_search_and_element_set_leaves_without_case_folding()
    {
        var contains = Request(new Predicate.Substring(Name, "ice", Anchor.Contains), [], Paging.None, ResultShape.Rows.Instance);
        var endsWith = Request(new Predicate.Substring(Name, "ce", Anchor.EndsWith), [], Paging.None, ResultShape.Rows.Instance);
        var elementOf = Request(
            new Predicate.ElementOf(new ElementSetRef("tags", QueryType.String), [QueryConstant.Of("Alice")], SetQuantifier.Any),
            [], Paging.None, ResultShape.Rows.Instance);

        var renderers = new object[]
        {
            new SqliteQueryRenderer(),
            new PostgreSqlQueryRenderer(),
            new SqlServerQueryRenderer(),
            new MongoQueryRenderer()
        };
        foreach (var renderer in renderers)
        {
            Assert.Null(Record.Exception(() => Render(renderer, contains)));
            Assert.Null(Record.Exception(() => Render(renderer, endsWith)));
            Assert.Null(Record.Exception(() => Render(renderer, elementOf)));
        }

        var sql = new SqlServerQueryRenderer().Render(contains);
        Assert.DoesNotContain("LOWER", sql.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPPER", sql.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DATALENGTH", sql.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("LEN(@", sql.CommandText, StringComparison.Ordinal);
        var mongo = new MongoQueryRenderer().Render(contains);
        Assert.DoesNotContain("i]", mongo.Filter.ToString(), StringComparison.Ordinal);

        var emptyAll = Request(
            new Predicate.ElementOf(new ElementSetRef("tags", QueryType.String), [], SetQuantifier.All),
            [], Paging.None, ResultShape.Rows.Instance);
        var emptyAllSqlite = new SqliteQueryRenderer().Render(emptyAll);
        var emptyAllPostgres = new PostgreSqlQueryRenderer().Render(emptyAll);
        var emptyAllSqlServer = new SqlServerQueryRenderer().Render(emptyAll);
        var emptyAllMongo = new MongoQueryRenderer().Render(emptyAll);
        Assert.Contains("json_type", emptyAllSqlite.CommandText, StringComparison.Ordinal);
        Assert.Contains("jsonb_typeof", emptyAllPostgres.CommandText, StringComparison.Ordinal);
        Assert.Contains("ISJSON", emptyAllSqlServer.CommandText, StringComparison.Ordinal);
        Assert.Contains("array", emptyAllMongo.Filter.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Relational_not_nodes_use_total_complements_and_column_not_equal_rejects_nulls()
    {
        var notEqual = Request(
            new Predicate.Not(new Predicate.Equal(Name, QueryConstant.Of(Name, "Alice"))),
            [], Paging.None, ResultShape.Rows.Instance);
        var notRange = Request(
            new Predicate.Not(new Predicate.Range(Amount, Bound.Inclusive(QueryConstant.Of(Amount, 2)), null)),
            [], Paging.None, ResultShape.Rows.Instance);
        var notContains = Request(
            new Predicate.Not(new Predicate.Substring(Name, "ice", Anchor.Contains)),
            [], Paging.None, ResultShape.Rows.Instance);
        var otherAmount = new ColumnRef(Table, "otherAmount", QueryType.Int32, isNullable: true);
        var columnNotEqual = Request(
            new Predicate.ColumnCompare(Amount, CompareOp.NotEqual, otherAmount),
            [], Paging.None, ResultShape.Rows.Instance);

        foreach (var command in new[]
        {
            new SqliteQueryRenderer().Render(notEqual),
            new PostgreSqlQueryRenderer().Render(notEqual),
            new SqlServerQueryRenderer().Render(notEqual)
        })
            Assert.Contains("CASE WHEN", command.CommandText, StringComparison.Ordinal);

        foreach (var renderer in new object[]
        {
            new SqliteQueryRenderer(),
            new PostgreSqlQueryRenderer(),
            new SqlServerQueryRenderer()
        })
        {
            // Q2 deliberately refuses these negations; the renderer must preserve that
            // refusal rather than emit a three-valued SQL predicate.
            Assert.Equal("GW-SEM-NOT-001", Assert.Throws<QueryRenderException>(() => Render(renderer, notRange)).Code);
            Assert.Equal("GW-SEM-NOT-001", Assert.Throws<QueryRenderException>(() => Render(renderer, notContains)).Code);
        }

        foreach (var command in new[]
        {
            new SqliteQueryRenderer().Render(columnNotEqual),
            new PostgreSqlQueryRenderer().Render(columnNotEqual),
            new SqlServerQueryRenderer().Render(columnNotEqual)
        })
        {
            Assert.Contains("IS NOT NULL", command.CommandText, StringComparison.Ordinal);
            Assert.DoesNotContain("IS NULL OR", command.CommandText, StringComparison.Ordinal);
        }

        var mongo = new MongoQueryRenderer().Render(columnNotEqual);
        Assert.Contains("$and", mongo.Filter.ToString(), StringComparison.Ordinal);
        Assert.Contains("$ne", mongo.Filter.ToString(), StringComparison.Ordinal);
    }

    private static object Render(object renderer, QueryRequest request) => renderer switch
    {
        SqliteQueryRenderer sqlite => sqlite.Render(request),
        PostgreSqlQueryRenderer postgres => postgres.Render(request),
        SqlServerQueryRenderer sqlServer => sqlServer.Render(request),
        MongoQueryRenderer mongo => mongo.Render(request),
        _ => throw new ArgumentOutOfRangeException(nameof(renderer))
    };

    private static object Render(object renderer, QueryRequest request, QueryRenderOptions options) => renderer switch
    {
        SqliteQueryRenderer sqlite => sqlite.Render(request, options),
        PostgreSqlQueryRenderer postgres => postgres.Render(request, options),
        SqlServerQueryRenderer sqlServer => sqlServer.Render(request, options),
        MongoQueryRenderer mongo => mongo.Render(request, options),
        _ => throw new ArgumentOutOfRangeException(nameof(renderer))
    };

    private static QueryRequest Request(Predicate predicate, IEnumerable<OrderTerm> order, Paging paging, ResultShape result, Projection? projection = null) =>
        new(Table, predicate, order.ToImmutableArray(), projection ?? Projection.All, paging, result);
}
