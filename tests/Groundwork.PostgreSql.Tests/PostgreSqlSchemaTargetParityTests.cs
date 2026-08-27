using Groundwork.Kernel;
using Groundwork.SchemaTool;
using Groundwork.Store;
using Groundwork.Testing;
using Xunit;

namespace Groundwork.PostgreSql.Tests;

public sealed class PostgreSqlSchemaTargetParityTests : IDisposable
{
    [SkippableFact]
    public async Task Cli_applied_history_carries_the_fingerprint_the_runtime_expects()
    {
        using var database = PostgreSqlFixture.OpenOrSkip();
        var declared = harness.Temp("parity.json", SchemaToolCliHarness.ParitySchema());
        var emitted = Path.Combine(harness.Root, "emitted.json");

        var emit = await harness.EmitAsync(declared, emitted);
        Assert.True(SchemaToolExitCodes.Success == emit.ExitCode, emit.Reason);

        var apply = await harness.ApplyAuthorizedAsync(emitted, database.ConnectionString);
        Assert.True(SchemaToolExitCodes.Success == apply.ExitCode, apply.Reason);
        Assert.Equal("applied", apply.Report.RootElement.GetProperty("outcome").GetString());

        var unit = SchemaToolCliHarness.ParityDeclaration();
        Assert.Equal(
            PostgreSqlSchemaCoordinator.Target(PostgreSqlSchemaCoordinator.Physicalize(unit)).Fingerprint,
            apply.Report.RootElement.GetProperty("appliedTargetFingerprint").GetString());

        using var store = new PostgreSqlProviderFactory().Create(database.ConnectionString);
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
        "postgresql",
        Path.Combine(AppContext.BaseDirectory, "Groundwork.PostgreSql.dll"));

    public void Dispose() => harness.Dispose();
}
