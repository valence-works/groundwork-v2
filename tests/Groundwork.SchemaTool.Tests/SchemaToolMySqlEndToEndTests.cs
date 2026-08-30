using Groundwork.LiveDatabases;
using Groundwork.MySql;
using Groundwork.Testing;
using MySqlConnector;
using Xunit;

namespace Groundwork.SchemaTool.Tests;

public sealed class SchemaToolMySqlEndToEndTests : IDisposable
{
    [SkippableFact]
    public async Task Authorized_interop_view_converts_ticks_to_datetime6_and_is_not_a_base_table()
    {
        using var database = LiveMySqlDatabase.OpenOrSkip();
        const string table = "interop_orders";
        const string view = "reporting_orders";
        var schema = harness.Temp(
            "mysql-interop-view.json",
            """
            {"tables":[{"name":"interop_orders","columns":[{"name":"id","type":"String","nullable":false,"length":64,"precision":null,"scale":null,"folding":"None","generation":"Supplied"},{"name":"occurred_at","type":"DateTimeOffset","nullable":false,"length":null,"precision":null,"scale":null,"folding":"None","generation":"Supplied"}],"key":["id"],"indexes":[],"scope":"Scoped","interopView":"reporting_orders"}]}
            """);

        var safeOnly = await harness.RunAsync(
            ["apply", "--schema", schema, "--safe"],
            database.ConnectionString);
        Assert.True(SchemaToolExitCodes.AuthorizationRequired == safeOnly.ExitCode, safeOnly.Reason);
        var apply = await harness.ApplyAuthorizedAsync(schema, database.ConnectionString);
        Assert.True(SchemaToolExitCodes.Success == apply.ExitCode, apply.Reason);

        var timestamp = DateTimeOffset.UnixEpoch.AddTicks(-1);
        using var connection = new MySqlConnection(database.ConnectionString);
        connection.Open();
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = $"INSERT INTO `{table}` (`id`,`occurred_at`,`__groundwork_scope`) VALUES ('o-1',@ticks,'tenant-a');";
            insert.Parameters.AddWithValue("ticks", timestamp.Ticks);
            insert.ExecuteNonQuery();
        }
        using (var read = connection.CreateCommand())
        {
            read.CommandText = $"SELECT occurred_at,`__groundwork_scope` FROM `{view}` WHERE id='o-1';";
            using var reader = read.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(DateTimeOffset.UnixEpoch.AddTicks(-10).UtcTicks, reader.GetDateTime(0).Ticks);
            Assert.Equal("tenant-a", reader.GetString(1));
        }
        Assert.False(new MySqlDialect().TableExists(connection, transaction: null, view));
        var status = await harness.RunAsync(["status", "--schema", schema], database.ConnectionString);
        Assert.Equal(SchemaToolExitCodes.Success, status.ExitCode);

        using (var simulateImplicitCommit = connection.CreateCommand())
        {
            simulateImplicitCommit.CommandText = $"DROP VIEW `{view}`;";
            simulateImplicitCommit.ExecuteNonQuery();
        }
        var drift = await harness.RunAsync(["status", "--schema", schema], database.ConnectionString);
        Assert.Equal(SchemaToolExitCodes.ValidationFailed, drift.ExitCode);
        var withoutView = harness.Temp(
            "mysql-interop-view-removed.json",
            File.ReadAllText(schema).Replace(",\"interopView\":\"reporting_orders\"", string.Empty, StringComparison.Ordinal));
        var recovery = await harness.ApplyAuthorizedAsync(withoutView, database.ConnectionString);
        Assert.Equal(SchemaToolExitCodes.Success, recovery.ExitCode);
        var recovered = await harness.RunAsync(["status", "--schema", withoutView], database.ConnectionString);
        Assert.Equal(SchemaToolExitCodes.Success, recovered.ExitCode);
    }

    [SkippableFact]
    public async Task Discovered_mysql_factory_plans_applies_and_reports_status_against_a_live_database()
    {
        using var database = LiveMySqlDatabase.OpenOrSkip();
        var schema = harness.Temp("mysql-schema.json", SchemaToolCliHarness.InitialSchema());

        var plan = await harness.RunAsync(["plan", "--schema", schema], database.ConnectionString);
        Assert.True(SchemaToolExitCodes.PendingChanges == plan.ExitCode, plan.Reason);
        Assert.Equal("pending", plan.Report.RootElement.GetProperty("outcome").GetString());
        Assert.False(plan.Report.RootElement.GetProperty("targetMutated").GetBoolean());
        Assert.False(TableExists(database.ConnectionString, "tickets"));

        var refused = await harness.RunAsync(
            ["apply", "--schema", schema, "--expected-plan", "not-the-current-plan"],
            database.ConnectionString);
        Assert.True(SchemaToolExitCodes.AuthorizationRequired == refused.ExitCode, refused.Reason);
        Assert.Equal("authorization-required", refused.Report.RootElement.GetProperty("outcome").GetString());
        Assert.False(TableExists(database.ConnectionString, "tickets"));

        var apply = await harness.RunAsync(
            ["apply", "--schema", schema, "--safe"],
            database.ConnectionString);
        Assert.True(SchemaToolExitCodes.Success == apply.ExitCode, apply.Reason);
        Assert.Equal("applied", apply.Report.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("MySQL/MariaDB", apply.Report.RootElement.GetProperty("provider").GetProperty("name").GetString());
        Assert.Equal("1.0", apply.Report.RootElement.GetProperty("provider").GetProperty("version").GetString());
        Assert.True(apply.Report.RootElement.GetProperty("targetMutated").GetBoolean());
        Assert.True(TableExists(database.ConnectionString, "tickets"));

        var status = await harness.RunAsync(["status", "--schema", schema], database.ConnectionString);
        Assert.True(SchemaToolExitCodes.Success == status.ExitCode, status.Reason);
        Assert.Equal("ready", status.Report.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(0, status.Report.RootElement.GetProperty("pendingOperations").GetArrayLength());
        Assert.True(status.Report.RootElement.GetProperty("appliedOperations").GetArrayLength() > 0);

        var validation = await harness.RunAsync(["validate", "--schema", schema], database.ConnectionString);
        Assert.True(SchemaToolExitCodes.Success == validation.ExitCode, validation.Reason);
        Assert.Equal("live", validation.Report.RootElement.GetProperty("inspectionMode").GetString());
        Assert.Equal("ready", validation.Report.RootElement.GetProperty("outcome").GetString());

        var evolved = harness.Temp("mysql-evolved.json", SchemaToolCliHarness.EvolvedSchema());
        var evolvedPlan = await harness.RunAsync(["plan", "--schema", evolved], database.ConnectionString);
        Assert.True(SchemaToolExitCodes.PendingChanges == evolvedPlan.ExitCode, evolvedPlan.Reason);
        var fingerprint = evolvedPlan.Report.RootElement.GetProperty("planFingerprint").GetString()!;

        var authorized = await harness.RunAsync(
            ["apply", "--schema", evolved, "--expected-plan", fingerprint],
            database.ConnectionString);
        Assert.True(SchemaToolExitCodes.Success == authorized.ExitCode, authorized.Reason);
        Assert.Equal("applied", authorized.Report.RootElement.GetProperty("outcome").GetString());

        var settled = await harness.RunAsync(["status", "--schema", evolved], database.ConnectionString);
        Assert.True(SchemaToolExitCodes.Success == settled.ExitCode, settled.Reason);
        Assert.Equal("ready", settled.Report.RootElement.GetProperty("outcome").GetString());
    }

    private static bool TableExists(string connectionString, string table)
    {
        using var connection = new MySqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name=@table;";
        command.Parameters.AddWithValue("@table", table);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private readonly SchemaToolCliHarness harness = new(
        static (arguments, output, error) => GroundworkSchemaCli.RunAsync(arguments, output, error),
        "mysql",
        Path.Combine(AppContext.BaseDirectory, "Groundwork.MySql.dll"));

    public void Dispose() => harness.Dispose();
}
