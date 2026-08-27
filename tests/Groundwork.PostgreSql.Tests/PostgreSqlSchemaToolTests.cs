using Groundwork.SchemaTool;
using Groundwork.Testing;
using Npgsql;
using Xunit;

namespace Groundwork.PostgreSql.Tests;

public sealed class PostgreSqlSchemaToolTests : IDisposable
{
    [SkippableFact]
    public async Task Discovered_postgresql_factory_plans_applies_and_reports_status_against_a_live_database()
    {
        using var database = PostgreSqlFixture.OpenOrSkip();
        var schema = harness.Temp("schema.json", SchemaToolCliHarness.InitialSchema());

        var plan = await harness.RunAsync(["plan", "--schema", schema], database.ConnectionString);
        Assert.True(SchemaToolExitCodes.PendingChanges == plan.ExitCode, plan.Reason);
        Assert.Equal("pending", plan.Report.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("PostgreSQL", plan.Report.RootElement.GetProperty("provider").GetProperty("name").GetString());
        Assert.True(plan.Report.RootElement.GetProperty("pendingOperations").GetArrayLength() > 0);
        Assert.False(HistoryTableExists(database.ConnectionString));

        var apply = await harness.RunAsync(["apply", "--schema", schema, "--safe"], database.ConnectionString);
        Assert.True(SchemaToolExitCodes.Success == apply.ExitCode, apply.Reason);
        Assert.Equal("applied", apply.Report.RootElement.GetProperty("outcome").GetString());
        Assert.True(apply.Report.RootElement.GetProperty("targetMutated").GetBoolean());

        var status = await harness.RunAsync(["status", "--schema", schema], database.ConnectionString);
        Assert.True(SchemaToolExitCodes.Success == status.ExitCode, status.Reason);
        Assert.Equal("ready", status.Report.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(0, status.Report.RootElement.GetProperty("pendingOperations").GetArrayLength());

        var evolved = harness.Temp("evolved.json", SchemaToolCliHarness.EvolvedSchema());
        var evolvedPlan = await harness.RunAsync(["plan", "--schema", evolved], database.ConnectionString);
        Assert.True(SchemaToolExitCodes.PendingChanges == evolvedPlan.ExitCode, evolvedPlan.Reason);
        var fingerprint = evolvedPlan.Report.RootElement.GetProperty("planFingerprint").GetString()!;

        var authorized = await harness.RunAsync(
            ["apply", "--schema", evolved, "--expected-plan", fingerprint], database.ConnectionString);
        Assert.True(SchemaToolExitCodes.Success == authorized.ExitCode, authorized.Reason);
        Assert.Equal("applied", authorized.Report.RootElement.GetProperty("outcome").GetString());

        var settled = await harness.RunAsync(["status", "--schema", evolved], database.ConnectionString);
        Assert.True(SchemaToolExitCodes.Success == settled.ExitCode, settled.Reason);
        Assert.Equal("ready", settled.Report.RootElement.GetProperty("outcome").GetString());
    }

    private static bool HistoryTableExists(string connectionString)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT to_regclass('__groundwork_schema_history') IS NOT NULL;";
        return (bool)command.ExecuteScalar()!;
    }

    private readonly SchemaToolCliHarness harness = new(
        static (arguments, output, error) => GroundworkSchemaCli.RunAsync(arguments, output, error),
        "postgresql",
        Path.Combine(AppContext.BaseDirectory, "Groundwork.PostgreSql.dll"));

    public void Dispose() => harness.Dispose();
}
