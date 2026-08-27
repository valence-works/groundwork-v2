using Groundwork.Sqlite;
using Groundwork.Testing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Groundwork.SchemaTool.Tests;

public sealed class SchemaToolSqliteEndToEndTests : IDisposable
{
    [Fact]
    public async Task Discovered_sqlite_factory_plans_applies_and_reports_status_against_a_real_file_database()
    {
        var schema = harness.Temp("schema.json", SchemaToolCliHarness.InitialSchema());
        var database = Path.Combine(harness.Root, "store.db");
        var connection = $"Data Source={database}";

        var missing = await harness.RunAsync(["plan", "--schema", schema], connection);
        Assert.Equal(SchemaToolExitCodes.ExecutionFailed, missing.ExitCode);
        Assert.Contains("does not exist", missing.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(database));
        Assert.False(File.Exists(database + ".schema.lock"));

        var refused = await harness.RunAsync(
            ["apply", "--schema", schema, "--expected-plan", "not-the-current-plan"], connection);
        Assert.Equal(SchemaToolExitCodes.AuthorizationRequired, refused.ExitCode);
        Assert.Equal("authorization-required", refused.Report.RootElement.GetProperty("outcome").GetString());
        Assert.False(File.Exists(database));
        Assert.False(File.Exists(database + ".schema.lock"));

        File.Create(database).Dispose();
        var plan = await harness.RunAsync(["plan", "--schema", schema], connection);
        Assert.Equal(SchemaToolExitCodes.PendingChanges, plan.ExitCode);
        Assert.Equal("pending", plan.Report.RootElement.GetProperty("outcome").GetString());
        Assert.False(plan.Report.RootElement.GetProperty("targetMutated").GetBoolean());
        Assert.Equal(0, CountTables(connection));

        var apply = await harness.RunAsync(["apply", "--schema", schema, "--safe"], connection);
        Assert.Equal(SchemaToolExitCodes.Success, apply.ExitCode);
        Assert.Equal("1", apply.Report.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal("applied", apply.Report.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("SQLite", apply.Report.RootElement.GetProperty("provider").GetProperty("name").GetString());
        Assert.Equal("1.0", apply.Report.RootElement.GetProperty("provider").GetProperty("version").GetString());
        Assert.True(apply.Report.RootElement.GetProperty("targetMutated").GetBoolean());
        Assert.True(File.Exists(database + ".schema.lock"));

        var status = await harness.RunAsync(["status", "--schema", schema], connection);
        Assert.Equal(SchemaToolExitCodes.Success, status.ExitCode);
        Assert.Equal("ready", status.Report.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(0, status.Report.RootElement.GetProperty("pendingOperations").GetArrayLength());
        Assert.True(status.Report.RootElement.GetProperty("appliedOperations").GetArrayLength() > 0);

        var evolved = harness.Temp("evolved.json", SchemaToolCliHarness.EvolvedSchema());
        var evolvedPlan = await harness.RunAsync(["plan", "--schema", evolved], connection);
        Assert.Equal(SchemaToolExitCodes.PendingChanges, evolvedPlan.ExitCode);
        Assert.True(evolvedPlan.Report.RootElement.GetProperty("pendingOperations").GetArrayLength() > 0);
        Assert.False(evolvedPlan.Report.RootElement
            .GetProperty("authorization").GetProperty("destructiveRequired").GetBoolean());
        var fingerprint = evolvedPlan.Report.RootElement.GetProperty("planFingerprint").GetString()!;

        var authorized = await harness.RunAsync(
            ["apply", "--schema", evolved, "--expected-plan", fingerprint], connection);
        Assert.Equal(SchemaToolExitCodes.Success, authorized.ExitCode);
        Assert.Equal("applied", authorized.Report.RootElement.GetProperty("outcome").GetString());

        var settled = await harness.RunAsync(["status", "--schema", evolved], connection);
        Assert.Equal(SchemaToolExitCodes.Success, settled.ExitCode);
        Assert.Equal("ready", settled.Report.RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Sqlite_factory_honors_the_store_schema_lock_held_by_another_process()
    {
        var schema = harness.Temp("locked-schema.json", SchemaToolCliHarness.InitialSchema());
        var database = Path.Combine(harness.Root, "locked.db");
        var connection = $"Data Source={database}";
        var holder = new SqliteProviderFactory().Create(connection);
        try
        {
            var plan = await harness.RunAsync(["plan", "--schema", schema], connection);
            Assert.Equal(SchemaToolExitCodes.ExecutionFailed, plan.ExitCode);
            Assert.Contains("GW-CLI-010", plan.Output, StringComparison.Ordinal);
            Assert.Contains("GW-SQLITE-LIFETIME-001", plan.Output, StringComparison.Ordinal);
        }
        finally
        {
            holder.Dispose();
            using var pooled = new SqliteConnection(connection);
            SqliteConnection.ClearPool(pooled);
        }
    }

    [Fact]
    public async Task Sqlite_factory_refuses_memory_data_sources_and_missing_connections_as_invocation_errors()
    {
        var schema = harness.Temp("refusal-schema.json", SchemaToolCliHarness.InitialSchema());

        var memory = await harness.RunAsync(["plan", "--schema", schema], "Data Source=:memory:");
        Assert.Equal(SchemaToolExitCodes.InvalidInvocation, memory.ExitCode);
        Assert.Contains("GW-CLI-001", memory.Output, StringComparison.Ordinal);
        Assert.Contains("in-memory", memory.Output, StringComparison.Ordinal);

        var uriMemory = await harness.RunAsync(
            ["apply", "--schema", schema, "--safe"], "Data Source=file:refusal?mode=memory&cache=shared");
        Assert.Equal(SchemaToolExitCodes.InvalidInvocation, uriMemory.ExitCode);
        Assert.Contains("in-memory", uriMemory.Output, StringComparison.Ordinal);

        var unconnected = await harness.RunAsync(["plan", "--schema", schema]);
        Assert.Equal(SchemaToolExitCodes.InvalidInvocation, unconnected.ExitCode);
        Assert.Contains("GW-CLI-001", unconnected.Output, StringComparison.Ordinal);
        Assert.Contains("--connection or --database", unconnected.Output, StringComparison.Ordinal);
    }

    private static int CountTables(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table';";
        var count = Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        SqliteConnection.ClearPool(connection);
        return count;
    }

    private readonly SchemaToolCliHarness harness = new(
        static (arguments, output, error) => GroundworkSchemaCli.RunAsync(arguments, output, error),
        "sqlite",
        Path.Combine(AppContext.BaseDirectory, "Groundwork.Sqlite.dll"));

    public void Dispose() => harness.Dispose();
}
