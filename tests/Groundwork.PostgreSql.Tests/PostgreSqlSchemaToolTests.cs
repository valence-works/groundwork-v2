using System.Text.Json;
using Groundwork.SchemaTool;
using Npgsql;
using Xunit;

namespace Groundwork.PostgreSql.Tests;

public sealed class PostgreSqlSchemaToolTests : IDisposable
{
    private const string InitialSchema = """
        {"tables":[{"name":"tickets","columns":[{"name":"id","type":"String","nullable":false,"length":64,"precision":null,"scale":null,"folding":"None","generation":"Supplied"}],"key":["id"],"indexes":[]}]}
        """;

    private const string EvolvedSchema = """
        {"tables":[{"name":"tickets","columns":[{"name":"id","type":"String","nullable":false,"length":64,"precision":null,"scale":null,"folding":"None","generation":"Supplied"},{"name":"priority","type":"Int32","nullable":true,"length":null,"precision":null,"scale":null,"folding":"None","generation":"Supplied"}],"key":["id"],"indexes":[{"name":"by_priority","columns":[{"name":"priority","descending":false}],"includeNulls":true,"unique":false}]}]}
        """;

    [SkippableFact]
    public async Task Discovered_postgresql_factory_plans_applies_and_reports_status_against_a_live_database()
    {
        using var database = PostgreSqlSchemaToolFixture.OpenOrSkip();
        var schema = Temp("schema.json", InitialSchema);

        var plan = await RunJsonAsync(["plan", "--schema", schema], database.ConnectionString);
        Assert.Equal(SchemaToolExitCodes.PendingChanges, plan.Exit);
        Assert.Equal("pending", plan.Report.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("PostgreSQL", plan.Report.RootElement.GetProperty("provider").GetProperty("name").GetString());
        Assert.True(plan.Report.RootElement.GetProperty("pendingOperations").GetArrayLength() > 0);

        var apply = await RunJsonAsync(["apply", "--schema", schema, "--safe"], database.ConnectionString);
        Assert.Equal(SchemaToolExitCodes.Success, apply.Exit);
        Assert.Equal("applied", apply.Report.RootElement.GetProperty("outcome").GetString());
        Assert.True(apply.Report.RootElement.GetProperty("targetMutated").GetBoolean());

        var status = await RunJsonAsync(["status", "--schema", schema], database.ConnectionString);
        Assert.Equal(SchemaToolExitCodes.Success, status.Exit);
        Assert.Equal("ready", status.Report.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(0, status.Report.RootElement.GetProperty("pendingOperations").GetArrayLength());

        var evolved = Temp("evolved.json", EvolvedSchema);
        var evolvedPlan = await RunJsonAsync(["plan", "--schema", evolved], database.ConnectionString);
        Assert.Equal(SchemaToolExitCodes.PendingChanges, evolvedPlan.Exit);
        var fingerprint = evolvedPlan.Report.RootElement.GetProperty("planFingerprint").GetString()!;

        var authorized = await RunJsonAsync(
            ["apply", "--schema", evolved, "--expected-plan", fingerprint], database.ConnectionString);
        Assert.Equal(SchemaToolExitCodes.Success, authorized.Exit);
        Assert.Equal("applied", authorized.Report.RootElement.GetProperty("outcome").GetString());

        var settled = await RunJsonAsync(["status", "--schema", evolved], database.ConnectionString);
        Assert.Equal(SchemaToolExitCodes.Success, settled.Exit);
        Assert.Equal("ready", settled.Report.RootElement.GetProperty("outcome").GetString());
    }

    private static async Task<(int Exit, JsonDocument Report)> RunJsonAsync(
        string[] arguments,
        string connection)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await GroundworkSchemaCli.RunAsync(
            [
                .. arguments,
                "--provider", "postgresql",
                "--connection", connection,
                "--provider-assembly", Path.Combine(AppContext.BaseDirectory, "Groundwork.PostgreSql.dll"),
                "--output", "json"
            ],
            output,
            error);
        Assert.Equal(string.Empty, error.ToString());
        return (exit, JsonDocument.Parse(output.ToString()));
    }

    private readonly string directory = Path.Combine(
        Path.GetTempPath(), "groundwork-schema-tool-pg-" + Guid.NewGuid().ToString("N"));

    public PostgreSqlSchemaToolTests() => Directory.CreateDirectory(directory);

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private string Temp(string name, string contents)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, contents);
        return path;
    }

    private sealed class PostgreSqlSchemaToolFixture : IDisposable
    {
        private readonly string adminConnectionString;
        private readonly string schema;

        private PostgreSqlSchemaToolFixture(string adminConnectionString, string schema, string connectionString)
        {
            this.adminConnectionString = adminConnectionString;
            this.schema = schema;
            ConnectionString = connectionString;
        }

        public string ConnectionString { get; }

        public static PostgreSqlSchemaToolFixture OpenOrSkip()
        {
            var baseConnection = Environment.GetEnvironmentVariable("GROUNDWORK_POSTGRES_CONNECTION");
            Skip.If(string.IsNullOrWhiteSpace(baseConnection),
                "Set GROUNDWORK_POSTGRES_CONNECTION to run PostgreSQL integration tests.");
            var schema = "cli_" + Guid.NewGuid().ToString("N");
            using var admin = new NpgsqlConnection(baseConnection);
            try
            {
                admin.Open();
            }
            catch (Exception exception)
            {
                Skip.If(true, $"PostgreSQL is unavailable: {exception.Message}");
                throw;
            }
            using (var command = admin.CreateCommand())
            {
                command.CommandText = $"CREATE SCHEMA \"{schema}\";";
                command.ExecuteNonQuery();
            }
            var builder = new NpgsqlConnectionStringBuilder(baseConnection) { SearchPath = schema };
            return new PostgreSqlSchemaToolFixture(baseConnection, schema, builder.ConnectionString);
        }

        public void Dispose()
        {
            using (var pooled = new NpgsqlConnection(ConnectionString))
                NpgsqlConnection.ClearPool(pooled);

            using var admin = new NpgsqlConnection(adminConnectionString);
            admin.Open();
            using var command = admin.CreateCommand();
            command.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;";
            command.ExecuteNonQuery();
        }
    }
}
