using System.Text.Json;
using Groundwork.Sqlite;
using Xunit;

namespace Groundwork.SchemaTool.Tests;

public sealed class SchemaToolSqliteEndToEndTests : IDisposable
{
    private const string InitialSchema = """
        {"tables":[{"name":"tickets","columns":[{"name":"id","type":"String","nullable":false,"length":64,"precision":null,"scale":null,"folding":"None","generation":"Supplied"}],"key":["id"],"indexes":[]}]}
        """;

    private const string EvolvedSchema = """
        {"tables":[{"name":"tickets","columns":[{"name":"id","type":"String","nullable":false,"length":64,"precision":null,"scale":null,"folding":"None","generation":"Supplied"},{"name":"priority","type":"Int32","nullable":true,"length":null,"precision":null,"scale":null,"folding":"None","generation":"Supplied"}],"key":["id"],"indexes":[{"name":"by_priority","columns":[{"name":"priority","descending":false}],"includeNulls":true,"unique":false}]}]}
        """;

    [Fact]
    public async Task Discovered_sqlite_factory_plans_applies_and_reports_status_against_a_real_file_database()
    {
        var schema = Temp("schema.json", InitialSchema);
        var database = Path.Combine(directory, "store.db");
        var connection = $"Data Source={database}";

        var plan = await RunJsonAsync(["plan", "--schema", schema], connection);
        Assert.Equal(SchemaToolExitCodes.PendingChanges, plan.Exit);
        Assert.Equal("1", plan.Report.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal("pending", plan.Report.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("SQLite", plan.Report.RootElement.GetProperty("provider").GetProperty("name").GetString());
        Assert.Equal("1.0", plan.Report.RootElement.GetProperty("provider").GetProperty("version").GetString());
        Assert.True(plan.Report.RootElement.GetProperty("pendingOperations").GetArrayLength() > 0);
        Assert.False(plan.Report.RootElement.GetProperty("targetMutated").GetBoolean());

        var apply = await RunJsonAsync(["apply", "--schema", schema, "--safe"], connection);
        Assert.Equal(SchemaToolExitCodes.Success, apply.Exit);
        Assert.Equal("applied", apply.Report.RootElement.GetProperty("outcome").GetString());
        Assert.True(apply.Report.RootElement.GetProperty("targetMutated").GetBoolean());
        Assert.True(File.Exists(database));
        Assert.True(File.Exists(database + ".schema.lock"));

        var status = await RunJsonAsync(["status", "--schema", schema], connection);
        Assert.Equal(SchemaToolExitCodes.Success, status.Exit);
        Assert.Equal("ready", status.Report.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(0, status.Report.RootElement.GetProperty("pendingOperations").GetArrayLength());
        Assert.True(status.Report.RootElement.GetProperty("appliedOperations").GetArrayLength() > 0);

        var evolved = Temp("evolved.json", EvolvedSchema);
        var evolvedPlan = await RunJsonAsync(["plan", "--schema", evolved], connection);
        Assert.Equal(SchemaToolExitCodes.PendingChanges, evolvedPlan.Exit);
        Assert.Equal("pending", evolvedPlan.Report.RootElement.GetProperty("outcome").GetString());
        Assert.False(evolvedPlan.Report.RootElement
            .GetProperty("authorization").GetProperty("destructiveRequired").GetBoolean());
        var fingerprint = evolvedPlan.Report.RootElement.GetProperty("planFingerprint").GetString()!;

        var authorized = await RunJsonAsync(
            ["apply", "--schema", evolved, "--expected-plan", fingerprint], connection);
        Assert.Equal(SchemaToolExitCodes.Success, authorized.Exit);
        Assert.Equal("applied", authorized.Report.RootElement.GetProperty("outcome").GetString());

        var settled = await RunJsonAsync(["status", "--schema", evolved], connection);
        Assert.Equal(SchemaToolExitCodes.Success, settled.Exit);
        Assert.Equal("ready", settled.Report.RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Sqlite_factory_honors_the_store_schema_lock_held_by_another_process()
    {
        var schema = Temp("locked-schema.json", InitialSchema);
        var database = Path.Combine(directory, "locked.db");
        using var holder = new SqliteProviderFactory().Create($"Data Source={database}");

        var plan = await RunJsonAsync(["plan", "--schema", schema], $"Data Source={database}");
        Assert.Equal(SchemaToolExitCodes.ExecutionFailed, plan.Exit);
        Assert.Contains("GW-CLI-010", plan.Output, StringComparison.Ordinal);
    }

    private async Task<(int Exit, JsonDocument Report, string Output)> RunJsonAsync(
        string[] arguments,
        string connection)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await GroundworkSchemaCli.RunAsync(
            [
                .. arguments,
                "--provider", "sqlite",
                "--connection", connection,
                "--provider-assembly", Path.Combine(AppContext.BaseDirectory, "Groundwork.Sqlite.dll"),
                "--output", "json"
            ],
            output,
            error);
        Assert.Equal(string.Empty, error.ToString());
        var text = output.ToString();
        return (exit, JsonDocument.Parse(text), text);
    }

    private readonly string directory = Path.Combine(
        Path.GetTempPath(), "groundwork-schema-tool-sqlite-" + Guid.NewGuid().ToString("N"));

    public SchemaToolSqliteEndToEndTests() => Directory.CreateDirectory(directory);

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private string Temp(string name, string contents)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, contents);
        return path;
    }
}
