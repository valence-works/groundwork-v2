using System.Collections.Immutable;
using Groundwork.Kernel;
using Groundwork.MongoDb;
using Groundwork.Query.Model;
using Groundwork.Store;
using Groundwork.Testing;
using Xunit;

namespace Groundwork.Differential.Tests;

public sealed class SelectedOrdinalIdentityProviderTests
{
    [Fact]
    public void In_memory_executes_selected_ordinal_identity_equality_and_distinct_with_public_values()
    {
        var unit = DeclareUnit();
        using var connection = new InMemoryProviderFactory().Create("memory://selected-ordinal-provider");
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        session.Insert(Values(1, "Ada"));
        session.Insert(Values(2, "Ada"));
        session.Insert(Values(3, "Grace"));

        var table = new TableId(unit.Name);
        var name = new ColumnRef(table, "name", QueryType.String, isNullable: false, maxLength: 64);
        var request = new QueryRequest(
            table,
            Predicate.AlwaysTrue.Instance,
            [new OrderTerm(name, OrderDirection.Ascending, NullOrder.First)],
            Projection.ColumnsOnly(name),
            Paging.Keyset(1),
            distinct: true);

        var result = session.Query(request, unit.CreateQueryRenderOptions("by_name"));

        var row = Assert.Single(result.Rows);
        Assert.Equal("Ada", row["name"]);
        Assert.DoesNotContain("__groundwork_ordinal_name", row.Keys);
        var continuation = Assert.IsType<string>(result.NextContinuationToken);
        var next = session.Query(new QueryRequest(
            request.Table,
            request.Where,
            request.Order,
            request.Projection,
            Paging.Continuation(continuation, 1),
            distinct: true), unit.CreateQueryRenderOptions("by_name"));
        Assert.Equal("Grace", Assert.Single(next.Rows)["name"]);
    }

    [Fact]
    public void Mongo_selected_ordinal_identity_filters_physically_but_deduplicates_the_public_value()
    {
        var logical = DeclareUnit();
        var physical = SearchKeyProjection.Expand(logical);
        var supplied = logical.CreateQueryRenderOptions("by_name");
        var options = supplied with
        {
            Indexes = SearchKeyQueryMappings.RetargetIndexes(physical, supplied.Indexes).ToImmutableArray(),
            SearchKeyColumns = SearchKeyQueryMappings.For(physical, "by_name")
        };
        var table = new TableId(logical.Name);
        var name = new ColumnRef(table, "name", QueryType.String, isNullable: false, maxLength: 64);
        var publicRequest = new QueryRequest(
            table,
            new Predicate.Equal(name, QueryConstant.Of(name, "Ada")),
            [new OrderTerm(name, OrderDirection.Ascending, NullOrder.First)],
            Projection.ColumnsOnly(name),
            Paging.None,
            distinct: true);
        var execution = QueryRequestExecution.ForProviderPage(publicRequest, options);

        var command = new MongoQueryRenderer().Render(execution, options);

        Assert.True(command.Filter.Contains("__groundwork_ordinal_name"));
        Assert.Equal(
            PortableStringComparison.CreateOrdinal("Ada"),
            command.Filter["__groundwork_ordinal_name"].AsString);
        Assert.False(command.Filter.Contains("name"));
        var group = command.Pipeline.Single(stage => stage.Contains("$group"))["$group"].AsBsonDocument;
        var distinctKey = group["_id"].AsBsonDocument;
        Assert.True(distinctKey.Contains("name"));
        Assert.False(distinctKey.Contains("__groundwork_ordinal_name"));
        Assert.Equal("by_name", command.ExpectedIndex);
    }

    private static StorageUnit DeclareUnit() =>
        StorageUnit.Declare("selected-ordinal-provider", "selected_ordinal_provider")
            .Int32("id", column => column.Required())
            .String("name", 64, column => column.Required().OrdinalIdentity("__groundwork_ordinal_name"))
            .Key("id")
            .Index("by_name", index => index.UseOrdinalIdentities().Column("name"))
            .Build();

    private static StorageValues Values(int id, string name) =>
        new(new Dictionary<string, object?>
        {
            ["id"] = id,
            ["name"] = name
        });
}
