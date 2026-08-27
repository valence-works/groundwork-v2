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

    /// <summary>
    /// The documented destructive flow, end to end: plan, pin the plan, and name the one operation
    /// being authorized by the address the documentation uses.
    /// </summary>
    [SkippableFact]
    public async Task Documented_drop_column_authorization_applies_and_leaves_the_remaining_rows()
    {
        using var database = PostgreSqlFixture.OpenOrSkip();
        var schema = harness.Temp("orders.json", SchemaToolCliHarness.OrdersSchema());
        Assert.Equal(
            SchemaToolExitCodes.Success,
            (await harness.RunAsync(["apply", "--schema", schema, "--safe"], database.ConnectionString)).ExitCode);
        Execute(database.ConnectionString, "INSERT INTO orders (id, customer, legacy_total) VALUES ('o-1', 'ada', 7);");

        var dropped = harness.Temp("orders-dropped.json", SchemaToolCliHarness.OrdersSchema(includeLegacyTotal: false));
        var plan = await harness.RunAsync(["plan", "--schema", dropped], database.ConnectionString);
        Assert.True(SchemaToolExitCodes.PendingChanges == plan.ExitCode, plan.Reason);
        var authorization = plan.Report.RootElement.GetProperty("authorization");
        Assert.True(authorization.GetProperty("destructiveRequired").GetBoolean());
        Assert.Equal(
            "drop-column:orders.legacy_total",
            Assert.Single(authorization.GetProperty("destructiveOperationsRequired").EnumerateArray()).GetString());
        var fingerprint = plan.Report.RootElement.GetProperty("planFingerprint").GetString()!;

        // Pinning the plan without naming the operation is still refused.
        var unnamed = await harness.RunAsync(
            ["apply", "--schema", dropped, "--expected-plan", fingerprint], database.ConnectionString);
        Assert.Equal(SchemaToolExitCodes.AuthorizationRequired, unnamed.ExitCode);

        var applied = await harness.RunAsync(
            [
                "apply", "--schema", dropped,
                "--expected-plan", fingerprint,
                "--allow-destructive", "drop-column:orders.legacy_total"
            ],
            database.ConnectionString);
        Assert.True(SchemaToolExitCodes.Success == applied.ExitCode, applied.Reason);
        Assert.Equal("applied", applied.Report.RootElement.GetProperty("outcome").GetString());

        Assert.Equal("ada", Scalar(database.ConnectionString, "SELECT customer FROM orders WHERE id='o-1';"));
        Assert.Equal(0L, Scalar(
            database.ConnectionString,
            "SELECT count(*) FROM information_schema.columns " +
            "WHERE table_name='orders' AND column_name='legacy_total';"));
        Assert.Equal(
            SchemaToolExitCodes.Success,
            (await harness.RunAsync(["status", "--schema", dropped], database.ConnectionString)).ExitCode);
    }

    /// <summary>
    /// A renamed column keeps its logical id, so the deployment renames the column in place and the
    /// rows that were in it are still there afterwards.
    /// </summary>
    [SkippableFact]
    public async Task A_renamed_column_keeps_its_rows_through_semantic_authorization()
    {
        using var database = PostgreSqlFixture.OpenOrSkip();
        var schema = harness.Temp("orders.json", SchemaToolCliHarness.OrdersSchema(includeLegacyTotal: false));
        Assert.Equal(
            SchemaToolExitCodes.Success,
            (await harness.RunAsync(["apply", "--schema", schema, "--safe"], database.ConnectionString)).ExitCode);
        Execute(database.ConnectionString, "INSERT INTO orders (id, customer) VALUES ('o-1', 'ada');");

        var renamed = harness.Temp(
            "orders-renamed.json",
            SchemaToolCliHarness.OrdersSchema(includeLegacyTotal: false, customerColumn: "buyer"));
        var plan = await harness.RunAsync(["plan", "--schema", renamed], database.ConnectionString);
        Assert.True(SchemaToolExitCodes.PendingChanges == plan.ExitCode, plan.Reason);
        Assert.Contains(
            plan.Report.RootElement.GetProperty("pendingOperations").EnumerateArray(),
            operation => operation.GetProperty("kind").GetString() == "RenameColumn");
        Assert.DoesNotContain(
            plan.Report.RootElement.GetProperty("pendingOperations").EnumerateArray(),
            operation => operation.GetProperty("kind").GetString() is "DropColumn" or "AddColumn");
        var fingerprint = plan.Report.RootElement.GetProperty("planFingerprint").GetString()!;

        var applied = await harness.RunAsync(
            [
                "apply", "--schema", renamed,
                "--expected-plan", fingerprint,
                "--allow-semantic", "rename-column:orders.buyer"
            ],
            database.ConnectionString);
        Assert.True(SchemaToolExitCodes.Success == applied.ExitCode, applied.Reason);
        Assert.Equal("ada", Scalar(database.ConnectionString, "SELECT buyer FROM orders WHERE id='o-1';"));
    }

    private static void Execute(string connectionString, string sql)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(string connectionString, string sql)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value == DBNull.Value ? null : value;
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
