using System.Text;
using Groundwork.Query.Model;
using Xunit;

namespace Groundwork.Query.Model.Tests;

public sealed class Q9SearchKeyQueryTests
{
    private static readonly TableId Table = new("tickets");
    private static readonly ColumnRef Status = new(Table, "status", QueryType.String, true, 32, stringComparison: QueryStringComparisonPolicy.AsciiIgnoreCase);

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
    public void Ordinal_prefix_uses_base_column_without_a_derived_key()
    {
        var request = new QueryRequest(Table, new Predicate.StartsWith(
            new ColumnRef(Table, "status", QueryType.String), "ab"), [], Projection.All, Paging.None);
        var rewritten = QuerySearchKeyRewriter.Rewrite(request, new Dictionary<string, QuerySearchKeyColumn>
        {
            ["status"] = new("status", "status", QuerySearchKeyPolicy.Ordinal, 32)
        });
        var range = Assert.IsType<Predicate.Range>(rewritten.Where);
        Assert.Equal("status", range.Column.Name);
        Assert.Equal("ab", range.Lower!.Value.Value);
        Assert.Equal("ac", range.Upper!.Value.Value);
    }

    [Theory]
    [InlineData("\uD7FF", "\uD800\uDC00")]
    [InlineData("\uD83D\uDFFF", "\uD83E\uDC00")]
    [InlineData("\uDBFF\uDFFF", null)]
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
}
