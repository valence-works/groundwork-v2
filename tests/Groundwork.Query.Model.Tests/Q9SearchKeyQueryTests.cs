using System.Text;
using Groundwork.Query.Model;
using Xunit;

namespace Groundwork.Query.Model.Tests;

public sealed class Q9SearchKeyQueryTests
{
    private static readonly TableId Table = new("tickets");
    private static readonly ColumnRef Status = new(Table, "status", QueryType.String, true, 32, stringComparison: QueryStringComparisonPolicy.AsciiIgnoreCase);

    [Fact]
    public void Locale_mapping_retargets_order_and_continuation_to_the_hidden_text_key()
    {
        var name = new ColumnRef(Table, "name", QueryType.String, false, 32);
        var request = new QueryRequest(
            Table,
            Predicate.AlwaysTrue.Instance,
            [new OrderTerm(name, OrderDirection.Descending, NullOrder.Last)],
            Projection.ColumnsOnly(name),
            Paging.OffsetLimit(0, 1),
            distinct: true);
        var mapping = new QuerySearchKeyColumn(
            "name",
            "__groundwork_search_name",
            QuerySearchKeyPolicy.Ordinal,
            384,
            orderByPhysicalColumn: true,
            supportsPrefixPredicates: false);
        var options = QueryRenderOptions.Default with
        {
            SearchKeyColumns = new Dictionary<string, QuerySearchKeyColumn> { ["name"] = mapping }
        };

        var rewritten = QuerySearchKeyRewriter.Rewrite(request, options.SearchKeyColumns);
        var term = Assert.Single(rewritten.Order);
        Assert.Equal("__groundwork_search_name", term.Column.Name);
        Assert.True(rewritten.Distinct);
        Assert.Equal(OrderDirection.Descending, term.Direction);
        Assert.Equal(NullOrder.Last, term.NullOrder);
        var execution = QueryRequestExecution.ForPage(request, options);
        Assert.Contains(execution.Projection.Columns, column => column.Name == "__groundwork_search_name");

        var result = QueryResultMaterializer.Materialize(request, options,
        [
            new Dictionary<string, object?>
            {
                ["name"] = "Åke",
                ["__groundwork_search_name"] = "|5D|77"
            },
            new Dictionary<string, object?>
            {
                ["name"] = "Ake",
                ["__groundwork_search_name"] = "|2A|3E"
            }
        ]);

        Assert.DoesNotContain("__groundwork_search_name", result.Rows.Single().Keys);
        var cursor = QueryContinuationToken.Decode(result.NextContinuationToken!, rewritten, options);
        Assert.Equal("|5D|77", cursor.Single().Value);
    }

    [Fact]
    public void Locale_order_mapping_does_not_retarget_ordinal_prefix_predicates()
    {
        var name = new ColumnRef(Table, "name", QueryType.String, false, 32);
        var request = new QueryRequest(
            Table,
            new Predicate.StartsWith(name, "Ak"),
            [],
            Projection.All,
            Paging.None);
        var mapping = new QuerySearchKeyColumn(
            "name",
            "__groundwork_search_name",
            QuerySearchKeyPolicy.Ordinal,
            384,
            orderByPhysicalColumn: true,
            supportsPrefixPredicates: false);

        var rewritten = QuerySearchKeyRewriter.Rewrite(request,
            new Dictionary<string, QuerySearchKeyColumn> { ["name"] = mapping });

        var range = Assert.IsType<Predicate.Range>(rewritten.Where);
        Assert.Equal("name", range.Column.Name);
        Assert.Equal("Ak", range.Lower!.Value.Value);
        Assert.Equal("Al", range.Upper!.Value.Value);
    }

    [Theory]
    [InlineData(QueryStringComparisonPolicy.Ordinal, QuerySearchKeyPolicy.AsciiIgnoreCase)]
    [InlineData(QueryStringComparisonPolicy.CurrentCulture, QuerySearchKeyPolicy.AsciiIgnoreCase)]
    [InlineData(QueryStringComparisonPolicy.Icu, QuerySearchKeyPolicy.UnicodeOrdinalIgnoreCase)]
    [InlineData(QueryStringComparisonPolicy.UnicodeOrdinalIgnoreCase, QuerySearchKeyPolicy.AsciiIgnoreCase)]
    [InlineData(QueryStringComparisonPolicy.AsciiIgnoreCase, QuerySearchKeyPolicy.UnicodeOrdinalIgnoreCase)]
    public void Prefix_rewrite_refuses_a_column_policy_that_does_not_match_the_schema_mapping(
        QueryStringComparisonPolicy supplied,
        QuerySearchKeyPolicy declared)
    {
        var column = new ColumnRef(Table, "status", QueryType.String, true, 32, stringComparison: supplied);
        var request = new QueryRequest(Table, new Predicate.StartsWith(column, "Op"), [], Projection.All, Paging.None);

        var failure = Assert.Throws<QueryRenderException>(() => QuerySearchKeyRewriter.Rewrite(request,
            new Dictionary<string, QuerySearchKeyColumn>
            {
                ["status"] = new("status", "__groundwork_search_status", declared, 160)
            }));

        Assert.Equal("GW-QUERY-031", failure.Code);
        Assert.Contains("status", failure.Message, StringComparison.Ordinal);
        Assert.Contains(supplied.ToString(), failure.Message, StringComparison.Ordinal);
        Assert.Contains(declared.ToString(), failure.Message, StringComparison.Ordinal);
        Assert.Contains("matching", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Non_empty_prefix_rewrites_to_an_exact_hidden_range()
    {
        var request = new QueryRequest(Table, new Predicate.StartsWith(Status, "Op"), [], Projection.All, Paging.None);
        var rewritten = QuerySearchKeyRewriter.Rewrite(request, new Dictionary<string, QuerySearchKeyColumn>
        {
            ["status"] = new("status", "__groundwork_search_status", QuerySearchKeyPolicy.AsciiIgnoreCase, 160)
        });

        var range = Assert.IsType<Predicate.Range>(rewritten.Where);
        Assert.Equal("__groundwork_search_status", range.Column.Name);
        Assert.Equal("|006F|0070", range.Lower!.Value.Value);
        Assert.Equal("|006F|0071", range.Upper!.Value.Value);
        Assert.False(PortableQuerySemantics.Validate(rewritten).Refusals.Any());
    }

    [Fact]
    public void Folded_prefix_with_distinct_projection_remains_portable_after_rewrite()
    {
        var id = new ColumnRef(Table, "id", QueryType.Int64, false);
        var folded = new ColumnRef(Table, "folded", QueryType.String, true, 64,
            stringComparison: QueryStringComparisonPolicy.UnicodeOrdinalIgnoreCase);
        var request = new QueryRequest(
            Table,
            new Predicate.StartsWith(folded, "Op"),
            [new OrderTerm(id, OrderDirection.Ascending, NullOrder.Last)],
            Projection.ColumnsOnly(id, folded),
            Paging.OffsetLimit(0, 10),
            distinct: true);
        var options = QueryRenderOptions.Default with
        {
            SearchKeyColumns = new Dictionary<string, QuerySearchKeyColumn>
            {
                ["folded"] = new("folded", "__groundwork_search_folded", QuerySearchKeyPolicy.UnicodeOrdinalIgnoreCase, 384)
            }
        };

        var rewritten = QuerySearchKeyRewriter.Rewrite(request, options.SearchKeyColumns);

        Assert.True(rewritten.Distinct);
        Assert.True(PortableQuerySemantics.Validate(rewritten).IsPortable);
    }

    [Fact]
    public void Empty_prefix_rewrites_to_a_non_null_guard_and_max_boundary_has_no_upper_bound()
    {
        var request = new QueryRequest(Table, new Predicate.StartsWith(Status, ""), [], Projection.All, Paging.None);
        var rewritten = QuerySearchKeyRewriter.Rewrite(request, new Dictionary<string, QuerySearchKeyColumn>
        {
            ["status"] = new("status", "__groundwork_search_status", QuerySearchKeyPolicy.AsciiIgnoreCase, 160)
        });
        Assert.IsType<Predicate.Not>(rewritten.Where);

        Assert.Null(QuerySearchKeys.Successor("|FFFF", QuerySearchKeyPolicy.AsciiIgnoreCase));
    }

    [Fact]
    public void Ordinal_prefix_uses_base_column_without_a_mapping_or_derived_key()
    {
        var request = new QueryRequest(Table, new Predicate.StartsWith(
            new ColumnRef(Table, "status", QueryType.String), "ab"), [], Projection.All, Paging.None);
        var rewritten = QuerySearchKeyRewriter.Rewrite(request, new Dictionary<string, QuerySearchKeyColumn>());
        var range = Assert.IsType<Predicate.Range>(rewritten.Where);
        Assert.Equal("status", range.Column.Name);
        Assert.Equal("ab", range.Lower!.Value.Value);
        Assert.Equal("ac", range.Upper!.Value.Value);
    }

    [Theory]
    [InlineData("\uD7FF", "\uD800\uDC00")]
    [InlineData("\uD83D\uDFFF", "\uD83E\uDC00")]
    [InlineData("\uDBFF\uDFFF", "\uE000")]
    public void Ordinal_prefix_uses_a_well_formed_upper_bound_at_surrogate_boundaries(
        string prefix,
        string? expectedUpper)
    {
        var request = new QueryRequest(Table, new Predicate.StartsWith(
            new ColumnRef(Table, "status", QueryType.String), prefix), [], Projection.All, Paging.None);
        var rewritten = QuerySearchKeyRewriter.Rewrite(request, new Dictionary<string, QuerySearchKeyColumn>
        {
            ["status"] = new("status", "status", QuerySearchKeyPolicy.Ordinal, 32)
        });

        var range = Assert.IsType<Predicate.Range>(rewritten.Where);
        Assert.Equal(prefix, range.Lower!.Value.Value);
        Assert.Equal(expectedUpper, range.Upper?.Value.Value);
        if (expectedUpper is not null)
            Assert.NotEmpty(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetBytes(expectedUpper));
    }

    [Fact]
    public void Ordinal_d7ff_prefix_range_excludes_the_first_supplementary_scalar()
    {
        var request = new QueryRequest(Table, new Predicate.StartsWith(
            new ColumnRef(Table, "status", QueryType.String), "\uD7FF"), [], Projection.All, Paging.None);
        var rewritten = QuerySearchKeyRewriter.Rewrite(request, new Dictionary<string, QuerySearchKeyColumn>
        {
            ["status"] = new("status", "status", QuerySearchKeyPolicy.Ordinal, 32)
        });

        Assert.False(PortableQuerySemantics.Evaluate(rewritten.Where,
            new Dictionary<string, object?> { ["status"] = "\U00010000" }));
        Assert.True(PortableQuerySemantics.Evaluate(rewritten.Where,
            new Dictionary<string, object?> { ["status"] = "\uD7FFsuffix" }));
    }

    [Fact]
    public void Unicode_element_substring_rewrites_to_the_parallel_boundary_key_array()
    {
        var set = new ElementSetRef("workflowIds", QueryType.String);
        var request = new QueryRequest(
            Table,
            new Predicate.ElementSubstring(set, "Örn", Anchor.Contains,
                QueryStringComparisonPolicy.UnicodeOrdinalIgnoreCase),
            [],
            Projection.All,
            Paging.None);

        var rewritten = QueryElementSearchKeyRewriter.Rewrite(request,
            new Dictionary<string, QueryElementSearchKeyColumn>
            {
                ["workflowIds"] = new(
                    "workflowIds",
                    "__groundwork_search_workflowIds",
                    QuerySearchKeyPolicy.UnicodeOrdinalIgnoreCase,
                    450)
            });

        var predicate = Assert.IsType<Predicate.ElementSubstring>(rewritten.Where);
        Assert.Equal("__groundwork_search_workflowIds", predicate.Set.Name);
        Assert.Equal(QueryType.String, predicate.Set.Type);
        Assert.Equal(QueryStringComparisonPolicy.Ordinal, predicate.StringComparison);
        Assert.Equal("|0000D6|000052|00004E", predicate.Needle);
        Assert.True(PortableQuerySemantics.Validate(rewritten).IsPortable);
    }

    [Fact]
    public void Unicode_element_substring_without_a_mapping_remains_refused()
    {
        var request = new QueryRequest(
            Table,
            new Predicate.ElementSubstring(new ElementSetRef("workflowIds", QueryType.String), "Ö", Anchor.Contains,
                QueryStringComparisonPolicy.UnicodeOrdinalIgnoreCase),
            [],
            Projection.All,
            Paging.None);

        var unchanged = QueryElementSearchKeyRewriter.Rewrite(request,
            new Dictionary<string, QueryElementSearchKeyColumn>());

        Assert.Same(request.Where, unchanged.Where);
        var refusal = Assert.Single(PortableQuerySemantics.Validate(unchanged).Refusals);
        Assert.Equal("GW-SEM-TEXT-001", refusal.Code);
    }

    [Fact]
    public void Element_substring_longer_than_the_declared_element_bound_is_match_none()
    {
        var request = new QueryRequest(
            Table,
            new Predicate.ElementSubstring(
                new ElementSetRef("workflowIds", QueryType.String),
                new string('x', 451),
                Anchor.Contains),
            [],
            Projection.All,
            Paging.None);

        var rewritten = QueryElementSearchKeyRewriter.Rewrite(request,
            new Dictionary<string, QueryElementSearchKeyColumn>
            {
                ["workflowIds"] = new(
                    "workflowIds",
                    "__groundwork_search_workflowIds",
                    QuerySearchKeyPolicy.UnicodeOrdinalIgnoreCase,
                    450)
            });

        Assert.Same(Predicate.AlwaysFalse.Instance, rewritten.Where);
    }

    [Fact]
    public void Element_substring_rewrite_does_not_type_an_untyped_set()
    {
        var request = new QueryRequest(
            Table,
            new Predicate.ElementSubstring(new ElementSetRef("workflowIds"), "Ö", Anchor.Contains,
                QueryStringComparisonPolicy.UnicodeOrdinalIgnoreCase),
            [],
            Projection.All,
            Paging.None);

        var unchanged = QueryElementSearchKeyRewriter.Rewrite(request,
            new Dictionary<string, QueryElementSearchKeyColumn>
            {
                ["workflowIds"] = new("workflowIds", "__groundwork_search_workflowIds",
                    QuerySearchKeyPolicy.UnicodeOrdinalIgnoreCase)
            });

        Assert.Same(request.Where, unchanged.Where);
        Assert.Equal("GW-SEM-TYPE-007", Assert.Single(PortableQuerySemantics.Validate(unchanged).Refusals).Code);
    }
}
