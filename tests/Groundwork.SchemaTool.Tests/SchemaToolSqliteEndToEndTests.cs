using System.Text.Json;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
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
        Assert.True(SchemaToolExitCodes.ExecutionFailed == missing.ExitCode, missing.Reason);
        Assert.Contains("does not exist", missing.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(database));
        Assert.False(File.Exists(database + ".schema.lock"));

        var refused = await harness.RunAsync(
            ["apply", "--schema", schema, "--expected-plan", "not-the-current-plan"], connection);
        Assert.True(SchemaToolExitCodes.AuthorizationRequired == refused.ExitCode, refused.Reason);
        Assert.Equal("authorization-required", refused.Report.RootElement.GetProperty("outcome").GetString());
        Assert.False(File.Exists(database));
        Assert.False(File.Exists(database + ".schema.lock"));

        File.Create(database).Dispose();
        var plan = await harness.RunAsync(["plan", "--schema", schema], connection);
        Assert.True(SchemaToolExitCodes.PendingChanges == plan.ExitCode, plan.Reason);
        Assert.Equal("pending", plan.Report.RootElement.GetProperty("outcome").GetString());
        Assert.False(plan.Report.RootElement.GetProperty("targetMutated").GetBoolean());
        Assert.Equal(0, CountTables(connection));

        var apply = await harness.RunAsync(["apply", "--schema", schema, "--safe"], connection);
        Assert.True(SchemaToolExitCodes.Success == apply.ExitCode, apply.Reason);
        Assert.Equal("1", apply.Report.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal("applied", apply.Report.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("SQLite", apply.Report.RootElement.GetProperty("provider").GetProperty("name").GetString());
        Assert.Equal("1.0", apply.Report.RootElement.GetProperty("provider").GetProperty("version").GetString());
        Assert.True(apply.Report.RootElement.GetProperty("targetMutated").GetBoolean());
        Assert.True(File.Exists(database + ".schema.lock"));

        var status = await harness.RunAsync(["status", "--schema", schema], connection);
        Assert.True(SchemaToolExitCodes.Success == status.ExitCode, status.Reason);
        Assert.Equal("ready", status.Report.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(0, status.Report.RootElement.GetProperty("pendingOperations").GetArrayLength());
        Assert.True(status.Report.RootElement.GetProperty("appliedOperations").GetArrayLength() > 0);

        var evolved = harness.Temp("evolved.json", SchemaToolCliHarness.EvolvedSchema());
        var evolvedPlan = await harness.RunAsync(["plan", "--schema", evolved], connection);
        Assert.True(SchemaToolExitCodes.PendingChanges == evolvedPlan.ExitCode, evolvedPlan.Reason);
        Assert.True(evolvedPlan.Report.RootElement.GetProperty("pendingOperations").GetArrayLength() > 0);
        Assert.False(evolvedPlan.Report.RootElement
            .GetProperty("authorization").GetProperty("destructiveRequired").GetBoolean());
        var fingerprint = evolvedPlan.Report.RootElement.GetProperty("planFingerprint").GetString()!;

        var authorized = await harness.RunAsync(
            ["apply", "--schema", evolved, "--expected-plan", fingerprint], connection);
        Assert.True(SchemaToolExitCodes.Success == authorized.ExitCode, authorized.Reason);
        Assert.Equal("applied", authorized.Report.RootElement.GetProperty("outcome").GetString());

        var settled = await harness.RunAsync(["status", "--schema", evolved], connection);
        Assert.True(SchemaToolExitCodes.Success == settled.ExitCode, settled.Reason);
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
            Assert.True(SchemaToolExitCodes.ExecutionFailed == plan.ExitCode, plan.Reason);
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
        Assert.True(SchemaToolExitCodes.InvalidInvocation == memory.ExitCode, memory.Reason);
        Assert.Contains("GW-CLI-001", memory.Output, StringComparison.Ordinal);
        Assert.Contains("in-memory", memory.Output, StringComparison.Ordinal);

        var uriMemory = await harness.RunAsync(
            ["apply", "--schema", schema, "--safe"], "Data Source=file:refusal?mode=memory&cache=shared");
        Assert.True(SchemaToolExitCodes.InvalidInvocation == uriMemory.ExitCode, uriMemory.Reason);
        Assert.Contains("in-memory", uriMemory.Output, StringComparison.Ordinal);

        var unconnected = await harness.RunAsync(["plan", "--schema", schema]);
        Assert.True(SchemaToolExitCodes.InvalidInvocation == unconnected.ExitCode, unconnected.Reason);
        Assert.Contains("GW-CLI-001", unconnected.Output, StringComparison.Ordinal);
        Assert.Contains("--connection or --database", unconnected.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_catalog_from_an_earlier_schema_boundary_is_refused_by_name_not_by_exit_ten()
    {
        var schema = harness.Temp("boundary-schema.json", SchemaToolCliHarness.InitialSchema());
        var database = Path.Combine(harness.Root, "boundary.db");
        var connection = $"Data Source={database}";
        Assert.True(SchemaToolExitCodes.Success ==
            (await harness.RunAsync(["apply", "--schema", schema, "--safe"], connection)).ExitCode);

        // Rewrite the fingerprint the snapshot recorded, which is what an earlier build's
        // fingerprint boundary leaves behind.
        Execute(connection,
            """
            UPDATE __groundwork_schema_history SET state_json = replace(
                state_json,
                '"targetFingerprint":"' || target_fingerprint || '"',
                '"targetFingerprint":"stale-boundary"');
            """);

        var status = await harness.RunAsync(["status", "--schema", schema], connection);
        Assert.True(SchemaToolExitCodes.ValidationFailed == status.ExitCode, status.Reason);
        Assert.Contains(GroundworkSchemaBoundaryException.Code, status.Output, StringComparison.Ordinal);
        Assert.Contains("Discard that catalog", status.Output, StringComparison.Ordinal);

        var unit = StorageUnit.Declare("tickets", "tickets")
            .String("id", 64, column => column.Required())
            .Key("id")
            .Build();
        using var store = new SqliteProviderFactory().Create(connection);
        var failure = Assert.Throws<GroundworkSchemaBoundaryException>(() => store.Schema.Diff(unit));
        Assert.Contains("Discard that catalog", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_multi_target_schema_applies_under_every_target_plan_fingerprint()
    {
        var schema = harness.Temp("multi.json", SchemaToolCliHarness.MultiTargetSchema);
        var database = Path.Combine(harness.Root, "multi.db");
        var connection = $"Data Source={database}";
        File.Create(database).Dispose();

        var plan = await harness.RunAsync(["plan", "--schema", schema], connection);
        Assert.True(SchemaToolExitCodes.PendingChanges == plan.ExitCode, plan.Reason);
        Assert.Equal(2, plan.Report.RootElement.GetProperty("targets").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, plan.Report.RootElement.GetProperty("planFingerprint").ValueKind);

        var apply = await harness.ApplyAuthorizedAsync(schema, connection);
        Assert.True(SchemaToolExitCodes.Success == apply.ExitCode, apply.Reason);
        Assert.Equal("applied", apply.Report.RootElement.GetProperty("outcome").GetString());

        var status = await harness.RunAsync(["status", "--schema", schema], connection);
        Assert.True(SchemaToolExitCodes.Success == status.ExitCode, status.Reason);
        Assert.Equal("ready", status.Report.RootElement.GetProperty("outcome").GetString());
    }

    private static void Execute(string connectionString, string sql)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
        SqliteConnection.ClearPool(connection);
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
