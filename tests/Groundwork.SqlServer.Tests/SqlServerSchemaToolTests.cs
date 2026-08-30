using Groundwork.LiveDatabases;
using Groundwork.SchemaTool;
using Groundwork.Testing;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Groundwork.SqlServer.Tests;

[Collection(SqlServerLiveDatabase.Name)]
public sealed class SqlServerSchemaToolTests : IDisposable
{
    [SkippableFact]
    public async Task Authorized_interop_view_preserves_native_datetimeoffset_and_is_not_a_base_table()
    {
        var connectionString = LiveSqlServer.ConnectionString;
        Skip.If(string.IsNullOrWhiteSpace(connectionString),
            "Set GROUNDWORK_SQLSERVER_CONNECTION to run SQL Server integration tests.");
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var table = "interop_orders_" + suffix;
        var view = "reporting_orders_" + suffix;
        try
        {
            var schema = harness.Temp(
                "sqlserver-interop-view.json",
                $$"""
                {"tables":[{"name":"{{table}}","columns":[{"name":"id","type":"String","nullable":false,"length":64,"precision":null,"scale":null,"folding":"None","generation":"Supplied"},{"name":"occurred_at","type":"DateTimeOffset","nullable":false,"length":null,"precision":null,"scale":null,"folding":"None","generation":"Supplied"}],"key":["id"],"indexes":[],"interopView":"{{view}}"}]}
                """);

            var safeOnly = await harness.RunAsync(["apply", "--schema", schema, "--safe"], connectionString!);
            Assert.True(SchemaToolExitCodes.AuthorizationRequired == safeOnly.ExitCode, safeOnly.Reason);
            var apply = await harness.ApplyAuthorizedAsync(schema, connectionString!);
            Assert.True(SchemaToolExitCodes.Success == apply.ExitCode, apply.Reason);

            var timestamp = new DateTimeOffset(2026, 8, 30, 10, 11, 12, TimeSpan.FromHours(2)).AddTicks(1_234_567);
            using var connection = new SqlConnection(connectionString);
            connection.Open();
            using (var insert = connection.CreateCommand())
            {
                insert.CommandText = $"INSERT INTO [{table}] ([id],[occurred_at]) VALUES ('o-1',@timestamp);";
                insert.Parameters.AddWithValue("@timestamp", timestamp);
                insert.ExecuteNonQuery();
            }
            using (var read = connection.CreateCommand())
            {
                read.CommandText = $"SELECT [occurred_at] FROM [{view}] WHERE [id]='o-1';";
                Assert.Equal(timestamp, (DateTimeOffset)read.ExecuteScalar()!);
            }
            Assert.False(new SqlServerDialect().TableExists(connection, transaction: null, view));
        }
        finally
        {
            Cleanup(connectionString!, table, view);
        }
    }

    [SkippableFact]
    public async Task Discovered_sqlserver_factory_plans_applies_and_reports_status_against_a_live_database()
    {
        var connection = LiveSqlServer.ConnectionString;
        Skip.If(string.IsNullOrWhiteSpace(connection),
            "Set GROUNDWORK_SQLSERVER_CONNECTION to run SQL Server integration tests.");
        var table = "cli_tickets_" + Guid.NewGuid().ToString("N");
        try
        {
            var schema = harness.Temp("schema.json", SchemaToolCliHarness.InitialSchema(table));

            var plan = await harness.RunAsync(["plan", "--schema", schema], connection!);
            Assert.True(SchemaToolExitCodes.PendingChanges == plan.ExitCode, plan.Reason);
            Assert.Equal("pending", plan.Report.RootElement.GetProperty("outcome").GetString());
            Assert.Equal("SQLServer", plan.Report.RootElement.GetProperty("provider").GetProperty("name").GetString());
            Assert.True(plan.Report.RootElement.GetProperty("pendingOperations").GetArrayLength() > 0);

            var apply = await harness.RunAsync(["apply", "--schema", schema, "--safe"], connection!);
            Assert.True(SchemaToolExitCodes.Success == apply.ExitCode, apply.Reason);
            Assert.Equal("applied", apply.Report.RootElement.GetProperty("outcome").GetString());
            Assert.True(apply.Report.RootElement.GetProperty("targetMutated").GetBoolean());

            var status = await harness.RunAsync(["status", "--schema", schema], connection!);
            Assert.True(SchemaToolExitCodes.Success == status.ExitCode, status.Reason);
            Assert.Equal("ready", status.Report.RootElement.GetProperty("outcome").GetString());
            Assert.Equal(0, status.Report.RootElement.GetProperty("pendingOperations").GetArrayLength());

            var evolved = harness.Temp("evolved.json", SchemaToolCliHarness.EvolvedSchema(table));
            var evolvedPlan = await harness.RunAsync(["plan", "--schema", evolved], connection!);
            Assert.True(SchemaToolExitCodes.PendingChanges == evolvedPlan.ExitCode, evolvedPlan.Reason);
            // Widening the table rewrites its provider-owned batch type, which re-applies only
            // under explicit authorization.
            Assert.Contains(
                evolvedPlan.Report.RootElement.GetProperty("authorization")
                    .GetProperty("destructiveOperationsRequired").EnumerateArray(),
                identity => identity.GetString()!.StartsWith("apply-provider-definition:", StringComparison.Ordinal));

            var refused = await harness.RunAsync(["apply", "--schema", evolved, "--safe"], connection!);
            Assert.True(SchemaToolExitCodes.AuthorizationRequired == refused.ExitCode, refused.Reason);

            var authorized = await harness.ApplyAuthorizedAsync(evolved, connection!);
            Assert.True(SchemaToolExitCodes.Success == authorized.ExitCode, authorized.Reason);
            Assert.Equal("applied", authorized.Report.RootElement.GetProperty("outcome").GetString());

            var settled = await harness.RunAsync(["status", "--schema", evolved], connection!);
            Assert.True(SchemaToolExitCodes.Success == settled.ExitCode, settled.Reason);
            Assert.Equal("ready", settled.Report.RootElement.GetProperty("outcome").GetString());
        }
        finally
        {
            Cleanup(connection!, table);
        }
    }

    private static void Cleanup(string connectionString, string table, string? view = null)
    {
        using var connection = new SqlConnection(connectionString);
        try
        {
            connection.Open();
        }
        catch (SqlException)
        {
            return;
        }
        using var command = connection.CreateCommand();
        command.CommandText = (view is null ? string.Empty : $"DROP VIEW IF EXISTS [{view}];") + $"""
            DROP TABLE IF EXISTS [{table}];
            IF OBJECT_ID(N'[__groundwork_schema_history]', N'U') IS NOT NULL
                DELETE FROM [__groundwork_schema_history] WHERE subject_id=@id;
            IF OBJECT_ID(N'[__groundwork_schema_fences]', N'U') IS NOT NULL
                DELETE FROM [__groundwork_schema_fences] WHERE subject_id=@id;
            """;
        command.Parameters.AddWithValue("@id", table);
        command.ExecuteNonQuery();
    }

    private readonly SchemaToolCliHarness harness = new(
        static (arguments, output, error) => GroundworkSchemaCli.RunAsync(arguments, output, error),
        "sqlserver",
        Path.Combine(AppContext.BaseDirectory, "Groundwork.SqlServer.dll"));

    public void Dispose() => harness.Dispose();
}

/// <summary>
/// Serializes every suite that drives this process's one live SQL Server database. Their schema
/// catalogs are created on first use and the provider suite resets them wholesale, so two of these
/// running at once would each be tearing down the other's catalog. xUnit runs distinct collections
/// in parallel, so there is exactly one for the database rather than one per suite.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SqlServerLiveDatabase : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SQL Server live database";
}
