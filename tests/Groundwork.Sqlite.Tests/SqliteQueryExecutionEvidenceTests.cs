using Groundwork.Query.Model;
using Groundwork.Sqlite;
using Groundwork.Store;
using Groundwork.Substrate.Relational;
using Xunit;

namespace Groundwork.Sqlite.Tests;

public sealed class SqliteQueryExecutionEvidenceTests
{
    [Fact]
    public void Computed_scope_token_projection_withholds_shape_without_changing_the_command()
    {
        var table = new TableId("records");
        var id = new ColumnRef(table, "id", QueryType.String, isNullable: false);
        var scope = new ColumnRef(table, CrossScopeQueryMaterializer.ScopeTokenColumn,
            QueryType.String, isNullable: false);
        var request = new QueryRequest(table, Predicate.AlwaysTrue.Instance,
            [new OrderTerm(id, nullOrder: NullOrder.First)],
            Projection.ColumnsOnly(id, scope), Paging.Keyset(2));
        var renderer = new SqliteQueryRenderer();

        var rendered = renderer.RenderForExecution(request, QueryRenderOptions.Default, hasLookahead: false);

        Assert.Contains("groundwork_scope_token(", rendered.Command.CommandText, StringComparison.Ordinal);
        Assert.Equal(renderer.Render(request, QueryRenderOptions.Default).CommandText, rendered.Command.CommandText);
        Assert.Null(rendered.Shape);

        var ordinary = new QueryRequest(table, Predicate.AlwaysTrue.Instance,
            [new OrderTerm(id, nullOrder: NullOrder.First)], Projection.ColumnsOnly(id), Paging.Keyset(2));
        Assert.NotNull(renderer.RenderForExecution(ordinary, QueryRenderOptions.Default, hasLookahead: false).Shape);
    }
}
