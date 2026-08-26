using System.Text.Json;
using Groundwork.SchemaTool;
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
        using var database = PostgreSqlFixture.OpenOrSkip();
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
}
