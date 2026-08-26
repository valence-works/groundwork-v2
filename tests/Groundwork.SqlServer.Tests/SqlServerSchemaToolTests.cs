using System.Text.Json;
using Groundwork.SchemaTool;
using Xunit;

namespace Groundwork.SqlServer.Tests;

public sealed class SqlServerSchemaToolTests : IDisposable
{
    [SkippableFact]
    public async Task Discovered_sqlserver_factory_plans_applies_and_reports_status_against_a_live_database()
    {
        var connection = Environment.GetEnvironmentVariable("GROUNDWORK_SQLSERVER_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connection),
            "Set GROUNDWORK_SQLSERVER_CONNECTION to run SQL Server integration tests.");
        var table = "cli_tickets_" + Guid.NewGuid().ToString("N");
        var schema = Temp("schema.json", InitialSchema(table));

        var plan = await RunJsonAsync(["plan", "--schema", schema], connection!);
        Assert.Equal(SchemaToolExitCodes.PendingChanges, plan.Exit);
        Assert.Equal("pending", plan.Report.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("SQLServer", plan.Report.RootElement.GetProperty("provider").GetProperty("name").GetString());
        Assert.True(plan.Report.RootElement.GetProperty("pendingOperations").GetArrayLength() > 0);

        var apply = await RunJsonAsync(["apply", "--schema", schema, "--safe"], connection!);
        Assert.Equal(SchemaToolExitCodes.Success, apply.Exit);
        Assert.Equal("applied", apply.Report.RootElement.GetProperty("outcome").GetString());
        Assert.True(apply.Report.RootElement.GetProperty("targetMutated").GetBoolean());

        var status = await RunJsonAsync(["status", "--schema", schema], connection!);
        Assert.Equal(SchemaToolExitCodes.Success, status.Exit);
        Assert.Equal("ready", status.Report.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(0, status.Report.RootElement.GetProperty("pendingOperations").GetArrayLength());

        var evolved = Temp("evolved.json", EvolvedSchema(table));
        var evolvedPlan = await RunJsonAsync(["plan", "--schema", evolved], connection!);
        Assert.Equal(SchemaToolExitCodes.PendingChanges, evolvedPlan.Exit);
        var fingerprint = evolvedPlan.Report.RootElement.GetProperty("planFingerprint").GetString()!;

        var authorized = await RunJsonAsync(
            ["apply", "--schema", evolved, "--expected-plan", fingerprint], connection!);
        Assert.Equal(SchemaToolExitCodes.Success, authorized.Exit);
        Assert.Equal("applied", authorized.Report.RootElement.GetProperty("outcome").GetString());

        var settled = await RunJsonAsync(["status", "--schema", evolved], connection!);
        Assert.Equal(SchemaToolExitCodes.Success, settled.Exit);
        Assert.Equal("ready", settled.Report.RootElement.GetProperty("outcome").GetString());
    }

    private static string InitialSchema(string table) =>
        $$"""
        {"tables":[{"name":"{{table}}","columns":[{"name":"id","type":"String","nullable":false,"length":64,"precision":null,"scale":null,"folding":"None","generation":"Supplied"}],"key":["id"],"indexes":[]}]}
        """;

    private static string EvolvedSchema(string table) =>
        $$"""
        {"tables":[{"name":"{{table}}","columns":[{"name":"id","type":"String","nullable":false,"length":64,"precision":null,"scale":null,"folding":"None","generation":"Supplied"},{"name":"priority","type":"Int32","nullable":true,"length":null,"precision":null,"scale":null,"folding":"None","generation":"Supplied"}],"key":["id"],"indexes":[{"name":"by_priority","columns":[{"name":"priority","descending":false}],"includeNulls":true,"unique":false}]}]}
        """;

    private static async Task<(int Exit, JsonDocument Report)> RunJsonAsync(
        string[] arguments,
        string connection)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await GroundworkSchemaCli.RunAsync(
            [
                .. arguments,
                "--provider", "sqlserver",
                "--connection", connection,
                "--provider-assembly", Path.Combine(AppContext.BaseDirectory, "Groundwork.SqlServer.dll"),
                "--output", "json"
            ],
            output,
            error);
        Assert.Equal(string.Empty, error.ToString());
        return (exit, JsonDocument.Parse(output.ToString()));
    }

    private readonly string directory = Path.Combine(
        Path.GetTempPath(), "groundwork-schema-tool-sqlserver-" + Guid.NewGuid().ToString("N"));

    public SqlServerSchemaToolTests() => Directory.CreateDirectory(directory);

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private string Temp(string name, string contents)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, contents);
        return path;
    }
}
