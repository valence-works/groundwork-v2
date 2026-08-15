using Groundwork.Query.Model;
using Groundwork.Query.Planning;
using Xunit;

namespace Groundwork.Runtime.Tests;

public sealed class RuntimeCoverageTests
{
#pragma warning disable GW_COVER_006
    [Fact]
    public void SuppressedScanStillRefused()
    {
        // An analyzer pragma can remove a diagnostic, but it cannot add this runtime AST value.
        var table = new TableId("tickets");
        var status = new ColumnRef(table, "status", QueryType.String);
        var request = new QueryRequest(
            table,
            new Predicate.Substring(status, "open", Anchor.Contains),
            [],
            Projection.All,
            Paging.None);

        var exception = Assert.Throws<QueryCoverageException>(() =>
            QueryCoverageEnforcer.EnsureCovered(
                request,
                [new CoverageIndex("ix_other", [new CoverageIndexColumn("other")])],
                new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero)));

        Assert.Equal("GW-COVER-016", exception.Code);
    }
#pragma warning restore GW_COVER_006
}
