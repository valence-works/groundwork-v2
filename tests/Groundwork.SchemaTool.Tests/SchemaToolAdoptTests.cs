using System.Text.Json;
using Groundwork.Substrate.Relational;
using Groundwork.Testing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Groundwork.SchemaTool.Tests;

/// <summary>
/// <c>groundwork adopt</c> end to end against a real file database, through the discovered SQLite
/// plug-in. The catalog is produced by a normal apply and Groundwork's history row is then deleted,
/// which is the situation adoption is for: a real catalog the tool has no record of applying.
/// </summary>
public sealed class SchemaToolAdoptTests : IDisposable
{
    [Fact]
    public async Task Adopt_records_an_existing_catalog_under_the_same_authorization_apply_uses()
    {
        var schema = harness.Temp("schema.json", SchemaToolCliHarness.InitialSchema());
        var database = Path.Combine(harness.Root, "adopt.db");
        var connection = $"Data Source={database}";

        var apply = await harness.RunAsync(["apply", "--schema", schema, "--safe"], connection);
        Assert.True(SchemaToolExitCodes.Success == apply.ExitCode, apply.Reason);
        var appliedOperations = Operations(apply, "appliedOperations");
        ForgetHistory(connection);

        // Authorization is the apply flow's, not a weaker one of adoption's own.
        var unauthorized = await harness.RunAsync(["adopt", "--schema", schema], connection);
        Assert.True(SchemaToolExitCodes.InvalidInvocation == unauthorized.ExitCode, unauthorized.Reason);
        Assert.Contains("--safe", unauthorized.Report.RootElement
            .GetProperty("diagnostics")[0].GetProperty("message").GetString()!, StringComparison.Ordinal);

        var staleAuthorization = await harness.RunAsync(
            ["adopt", "--schema", schema, "--expected-plan", "not-the-current-plan"], connection);
        Assert.True(SchemaToolExitCodes.AuthorizationRequired == staleAuthorization.ExitCode, staleAuthorization.Reason);
        Assert.Equal(
            "authorization-required",
            staleAuthorization.Report.RootElement.GetProperty("outcome").GetString());
        Assert.Null(ReadHistoryJson(connection));

        var adopt = await harness.RunAsync(["adopt", "--schema", schema, "--safe"], connection);
        Assert.True(SchemaToolExitCodes.Success == adopt.ExitCode, adopt.Reason);
        Assert.Equal("adopted", adopt.Report.RootElement.GetProperty("outcome").GetString());
        Assert.True(adopt.Report.RootElement.GetProperty("targetMutated").GetBoolean());
        Assert.Equal(0, adopt.Report.RootElement.GetProperty("pendingOperations").GetArrayLength());

        // The ledger it published is the one the apply published, operation for operation.
        Assert.Equal(appliedOperations, Operations(adopt, "appliedOperations"));
        Assert.Equal(
            apply.Report.RootElement.GetProperty("appliedTargetFingerprint").GetString(),
            adopt.Report.RootElement.GetProperty("appliedTargetFingerprint").GetString());

        var status = await harness.RunAsync(["status", "--schema", schema], connection);
        Assert.True(SchemaToolExitCodes.Success == status.ExitCode, status.Reason);
        Assert.Equal("ready", status.Report.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(0, status.Report.RootElement.GetProperty("pendingOperations").GetArrayLength());

        // Adopting what is already recorded says so instead of claiming it adopted again.
        var again = await harness.RunAsync(["adopt", "--schema", schema, "--safe"], connection);
        Assert.True(SchemaToolExitCodes.Success == again.ExitCode, again.Reason);
        Assert.Equal("ready", again.Report.RootElement.GetProperty("outcome").GetString());
        Assert.False(again.Report.RootElement.GetProperty("targetMutated").GetBoolean());
    }

    [Fact]
    public async Task Adopt_refuses_a_catalog_that_differs_and_names_what_differs()
    {
        var declared = harness.Temp("declared.json", SchemaToolCliHarness.InitialSchema());
        var evolved = harness.Temp("evolved.json", SchemaToolCliHarness.EvolvedSchema());
        var database = Path.Combine(harness.Root, "drift.db");
        var connection = $"Data Source={database}";

        Assert.True(
            SchemaToolExitCodes.Success ==
            (await harness.RunAsync(["apply", "--schema", declared, "--safe"], connection)).ExitCode);
        ForgetHistory(connection);

        // The catalog is the one the initial schema describes, so adopting the evolved schema
        // would be claiming a column and an index that are not there.
        var drift = await harness.RunAsync(["adopt", "--schema", evolved, "--safe"], connection);
        Assert.True(SchemaToolExitCodes.ValidationFailed == drift.ExitCode, drift.Reason);
        Assert.Equal("blocked", drift.Report.RootElement.GetProperty("outcome").GetString());
        Assert.False(drift.Report.RootElement.GetProperty("targetMutated").GetBoolean());
        var reported = Diagnostics(drift);
        Assert.Contains("error GW-RUNTIME-001 columns.priority", reported);
        Assert.Contains("error GW-RUNTIME-002 indexes.by_priority", reported);
        Assert.Null(ReadHistoryJson(connection));

        // Once history exists, adoption refuses rather than overwriting it — including for a
        // declaration that has pending changes, which is apply's job and not adoption's.
        Assert.True(
            SchemaToolExitCodes.Success ==
            (await harness.RunAsync(["adopt", "--schema", declared, "--safe"], connection)).ExitCode);
        var recorded = await harness.RunAsync(["adopt", "--schema", evolved, "--safe"], connection);
        Assert.True(SchemaToolExitCodes.ValidationFailed == recorded.ExitCode, recorded.Reason);
        Assert.Contains("error GW-SCHEMA-011 schemaHistory", Diagnostics(recorded));
    }

    private static string[] Diagnostics(SchemaToolCliRun run) =>
        [.. run.Report.RootElement.GetProperty("diagnostics").EnumerateArray()
            .Select(diagnostic =>
                $"{diagnostic.GetProperty("severity").GetString()} " +
                $"{diagnostic.GetProperty("code").GetString()} " +
                $"{diagnostic.GetProperty("target").GetString()}")];

    private static string[] Operations(SchemaToolCliRun run, string property) =>
        [.. run.Report.RootElement.GetProperty(property).EnumerateArray()
            .Select(operation =>
                $"{operation.GetProperty("identity").GetString()}|{operation.GetProperty("fingerprint").GetString()}")
            .Order(StringComparer.Ordinal)];

    /// <summary>Deletes Groundwork's record of the apply, leaving the catalog exactly as it was.</summary>
    private static void ForgetHistory(string connectionString) =>
        Execute(connectionString, $"DELETE FROM \"{RelationalDialect.SchemaHistoryTable}\";");

    private static string? ReadHistoryJson(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT \"state_json\" FROM \"{RelationalDialect.SchemaHistoryTable}\";";
        var value = command.ExecuteScalar() as string;
        SqliteConnection.ClearPool(connection);
        return value;
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

    private readonly SchemaToolCliHarness harness = new(
        static (arguments, output, error) => GroundworkSchemaCli.RunAsync(arguments, output, error),
        "sqlite",
        Path.Combine(AppContext.BaseDirectory, "Groundwork.Sqlite.dll"));

    public void Dispose() => harness.Dispose();
}
