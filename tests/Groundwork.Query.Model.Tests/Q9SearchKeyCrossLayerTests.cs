using Groundwork.Kernel;
using Xunit;

namespace Groundwork.Query.Model.Tests;

public sealed class Q9SearchKeyCrossLayerTests
{
    [Theory]
    [InlineData(PortableStringComparisonPolicy.AsciiIgnoreCase, "Turkish I")]
    [InlineData(PortableStringComparisonPolicy.AsciiIgnoreCase, "!AZ~")]
    [InlineData(PortableStringComparisonPolicy.UnicodeOrdinalIgnoreCase, "Turkish I")]
    [InlineData(PortableStringComparisonPolicy.UnicodeOrdinalIgnoreCase, "Straße")]
    [InlineData(PortableStringComparisonPolicy.UnicodeOrdinalIgnoreCase, "İıſ")]
    [InlineData(PortableStringComparisonPolicy.UnicodeOrdinalIgnoreCase, "𐐀𐐨")]
    [InlineData(PortableStringComparisonPolicy.UnicodeOrdinalIgnoreCase, "\uFFFF")]
    public void Query_bounds_and_kernel_writes_share_the_same_search_key_algorithm(
        PortableStringComparisonPolicy kernelPolicy,
        string value)
    {
        var queryPolicy = kernelPolicy switch
        {
            PortableStringComparisonPolicy.AsciiIgnoreCase => QuerySearchKeyPolicy.AsciiIgnoreCase,
            PortableStringComparisonPolicy.UnicodeOrdinalIgnoreCase => QuerySearchKeyPolicy.UnicodeOrdinalIgnoreCase,
            _ => throw new ArgumentOutOfRangeException(nameof(kernelPolicy), kernelPolicy, null)
        };

        Assert.Equal(
            PortableStringComparison.CreateSearchKey(value, kernelPolicy),
            QuerySearchKeys.Encode(value, queryPolicy));
    }

    [Theory]
    [InlineData(QuerySearchKeyPolicy.Ordinal, "a", "b")]
    [InlineData(QuerySearchKeyPolicy.Ordinal, "\uD7FF", "\uD800\uDC00")]
    [InlineData(QuerySearchKeyPolicy.Ordinal, "\uD83D\uDE00", "\uD83D\uDE01")]
    [InlineData(QuerySearchKeyPolicy.Ordinal, "\uD83D\uDFFF", "\uD83E\uDC00")]
    [InlineData(QuerySearchKeyPolicy.AsciiIgnoreCase, "|004F", "|0050")]
    [InlineData(QuerySearchKeyPolicy.UnicodeOrdinalIgnoreCase, "|00004F", "|000050")]
    public void Query_successors_match_the_shared_boundary_algorithm(
        QuerySearchKeyPolicy policy,
        string encoded,
        string expected)
    {
        Assert.Equal(expected, QuerySearchKeys.Successor(encoded, policy));
    }

    [Fact]
    public void Ordinal_successor_has_no_upper_bound_after_the_maximum_scalar()
    {
        Assert.Null(QuerySearchKeys.Successor("\uDBFF\uDFFF", QuerySearchKeyPolicy.Ordinal));
    }
}
