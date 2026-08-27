using Groundwork.Kernel;
using Groundwork.SchemaTool;
using Groundwork.Store;
using Groundwork.Testing;
using Xunit;

namespace Groundwork.Sqlite.Tests;

public sealed class SqliteSchemaTargetParityTests : IDisposable
{
    [Fact]
    public async Task Cli_applied_history_carries_the_fingerprint_the_runtime_expects()
    {
        var declared = harness.Temp("parity.json", SchemaToolCliHarness.ParitySchema());
        var emitted = Path.Combine(harness.Root, "emitted.json");
        var database = Path.Combine(harness.Root, "parity.db");
        var connection = $"Data Source={database}";

        var emit = await harness.EmitAsync(declared, emitted);
        Assert.True(SchemaToolExitCodes.Success == emit.ExitCode, emit.Reason);

        File.Create(database).Dispose();
        var apply = await harness.ApplyAuthorizedAsync(emitted, connection);
        Assert.True(SchemaToolExitCodes.Success == apply.ExitCode, apply.Reason);
        Assert.Equal("applied", apply.Report.RootElement.GetProperty("outcome").GetString());

        var unit = SchemaToolCliHarness.ParityDeclaration();
        Assert.Equal(
            SqliteSchemaCoordinator.Target(SqliteSchemaCoordinator.Physicalize(unit)).Fingerprint,
            apply.Report.RootElement.GetProperty("appliedTargetFingerprint").GetString());

        using var store = new SqliteProviderFactory().Create(connection);
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

    private readonly SchemaToolCliHarness harness = new(
        static (arguments, output, error) => GroundworkSchemaCli.RunAsync(arguments, output, error),
        "sqlite",
        Path.Combine(AppContext.BaseDirectory, "Groundwork.Sqlite.dll"));

    public void Dispose() => harness.Dispose();
}
