using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.LiveDatabases;
using Groundwork.Query.Model;
using Groundwork.SqlServer;
using Groundwork.Store;
using Groundwork.Substrate.Relational;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Groundwork.SqlServer.Tests;

/// <summary>Live public-session evidence for SQL Server's caller-owned parameter ceiling.</summary>
[Collection(SqlServerLiveDatabase.Name)]
public sealed class SqlServerParameterBudgetTests(SqlServerFixture fixture)
{
    [Fact]
    public void Renderer_and_connection_advertise_one_effective_caller_budget_for_queries_and_batches()
    {
        using var connection = new SqlServerProviderConnection("Server=unused");

        Assert.Equal(2_098, SqlServerQueryRenderer.ParameterBudget);
        Assert.Equal(SqlServerQueryRenderer.ParameterBudget, connection.QueryAdmission.MaximumParameters);
        Assert.Equal(SqlServerQueryRenderer.ParameterBudget, connection.QueryAdmission.MaximumBatchReadKeys);
    }

    [Fact]
    public void Relational_parameter_binding_keeps_long_search_needles_as_nvarchar_max()
    {
        var needle = new string('x', 10_000);
        var command = new RelationalQueryCommand(
            "SELECT @p0",
            [new QueryRenderParameter("p0", QueryType.String, needle)],
            includesTotalCount: false,
            isMatchNone: false,
            selectedIndex: null,
            indexHintApplied: false,
            appliedOrder: []);
        using var native = new SqlCommand();

        RelationalQueryResultReader.AddParameters(native, command);

        var parameter = Assert.IsType<SqlParameter>(Assert.Single(native.Parameters));
        Assert.Equal(-1, parameter.Size);
        Assert.Equal(needle.Length, Assert.IsType<string>(parameter.Value).Length);
    }

    [SkippableFact]
    public void Public_session_executes_2098_parameters_and_refuses_2099_before_provider_io()
    {
        var connectionString = fixture.Reset();
        using var connection = new SqlServerProviderFactory().Create(connectionString);
        var name = "sqlserver_batch_boundary_" + Guid.NewGuid().ToString("N");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId(name),
            Name = name,
            Columns = [new ColumnDefinition { Name = "id", Type = PortableType.Int32, IsNullable = false }],
            Key = new KeyDefinition { Columns = ["id"] }
        };

        Assert.True(connection.Schema.Apply(unit).Applied);
        var observer = new RecordingObserver();
        var session = connection.OpenSession(unit, StorageAccess.Global, observer);
        Assert.Equal(WriteOutcomeStatus.Inserted, session.Insert(new StorageValues(
            new Dictionary<string, object?> { ["id"] = 1234 })).Status);

        var table = new TableId(name);
        var id = new ColumnRef(table, "id", QueryType.Int32, isNullable: false);
        var options = new QueryRenderOptions
        {
            InValueLimit = SqlServerQueryRenderer.ParameterBudget + 1
        };
        var accepted = new QueryRequest(
            table,
            new Predicate.In(id, Enumerable.Range(0, SqlServerQueryRenderer.ParameterBudget)
                .Select(value => QueryConstant.Of(id, value))),
            [],
            Projection.All,
            Paging.None);

        var result = session.Query(accepted, options);
        Assert.Equal(1234, Assert.Single(result.Rows)["id"]);
        var queryCommands = observer.Commands.Count(command => command.Operation == "sqlserver.query");
        Assert.Equal(1, queryCommands);
        var commandsBeforeRefusal = observer.Commands.Count;

        var overBudget = new QueryRequest(
            table,
            new Predicate.In(id, Enumerable.Range(0, SqlServerQueryRenderer.ParameterBudget + 1)
                .Select(value => QueryConstant.Of(id, value))),
            [],
            Projection.All,
            Paging.None);
        var refusal = Assert.Throws<QueryRenderException>(() => session.Query(overBudget, options));

        Assert.Equal("GW-QUERY-015", refusal.Code);
        Assert.Equal(commandsBeforeRefusal, observer.Commands.Count);
        Assert.Equal(queryCommands, observer.Commands.Count(command => command.Operation == "sqlserver.query"));
    }

    private sealed class RecordingObserver : IProviderCommandObserver
    {
        public List<ProviderCommandEvent> Commands { get; } = [];

        public void Observe(ProviderCommandEvent command) => Commands.Add(command);
    }
}
