using Groundwork.Kernel;
using Groundwork.LiveDatabases;
using Groundwork.SchemaTool;
using Groundwork.Store;
using Groundwork.Testing;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Groundwork.SqlServer.Tests;

[Collection(SqlServerLiveDatabase.Name)]
public sealed class SqlServerSchemaTargetParityTests : IDisposable
{
    [SkippableFact]
    public async Task Cli_applied_history_carries_the_fingerprint_the_runtime_expects()
    {
        var connection = LiveSqlServer.ConnectionString;
        Skip.If(string.IsNullOrWhiteSpace(connection),
            "Set GROUNDWORK_SQLSERVER_CONNECTION to run SQL Server integration tests.");
        var table = "parity_orders_" + Guid.NewGuid().ToString("N");
        try
        {
            var declared = harness.Temp("parity.json", SchemaToolCliHarness.ParitySchema(table));
            var emitted = Path.Combine(harness.Root, "emitted.json");

            var emit = await harness.EmitAsync(declared, emitted);
            Assert.True(SchemaToolExitCodes.Success == emit.ExitCode, emit.Reason);

            var apply = await harness.ApplyAuthorizedAsync(emitted, connection!);
            Assert.True(SchemaToolExitCodes.Success == apply.ExitCode, apply.Reason);
            Assert.Equal("applied", apply.Report.RootElement.GetProperty("outcome").GetString());

            var unit = SchemaToolCliHarness.ParityDeclaration(table);
            Assert.Equal(
                SqlServerSchemaCoordinator.Target(SqlServerSchemaCoordinator.Prepare(unit)).Fingerprint,
                apply.Report.RootElement.GetProperty("appliedTargetFingerprint").GetString());

            using var store = new SqlServerProviderFactory().Create(connection!);
            var session = store.OpenSession(unit, StorageAccess.Scoped(new StorageScope("tenant-a")));
            Assert.Empty(store.Schema.Diff(unit).Changes);
            Assert.Equal(WriteOutcomeStatus.Inserted, session.Insert(new StorageValues(new Dictionary<string, object?>
            {
                ["id"] = "one",
                ["customer"] = "Ada",
                ["status"] = "pending"
            })).Status);
            Assert.NotNull(session.Read(new StorageKey(new Dictionary<string, object?> { ["id"] = "one" })));
        }
        finally
        {
            Cleanup(connection!, table);
        }
    }

    private static void Cleanup(string connectionString, string table)
    {
        using var connection = new SqlConnection(connectionString);
        try
        {
            connection.Open();
        }
        catch (SqlException)
        {
            return;
        }
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            DROP TABLE IF EXISTS [{table}];
            IF OBJECT_ID(N'[__groundwork_schema_history]', N'U') IS NOT NULL
                DELETE FROM [__groundwork_schema_history] WHERE subject_id=@id;
            IF OBJECT_ID(N'[__groundwork_schema_fences]', N'U') IS NOT NULL
                DELETE FROM [__groundwork_schema_fences] WHERE subject_id=@id;
            """;
        command.Parameters.AddWithValue("@id", table);
        command.ExecuteNonQuery();
    }

    private readonly SchemaToolCliHarness harness = new(
        static (arguments, output, error) => GroundworkSchemaCli.RunAsync(arguments, output, error),
        "sqlserver",
        Path.Combine(AppContext.BaseDirectory, "Groundwork.SqlServer.dll"));

    public void Dispose() => harness.Dispose();
}
