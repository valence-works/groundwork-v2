using System.Text.Json;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Testing;
using Xunit;

namespace Groundwork.SchemaTool.Tests;

/// <summary>
/// `status` reports pending versus applied data migrations per target from provider-owned state
/// alone: the tool cannot see host transforms, only the ledger and what the subject declares.
/// </summary>
public sealed class SchemaToolDataMigrationStatusTests : IDisposable
{
    private const string MigrationId = "2026-08-slugify";

    [Fact]
    public async Task A_running_ledger_entry_makes_an_applied_target_pending()
    {
        var session = new LedgerSession();
        var schema = Temp("schema.json", SchemaToolCliHarness.InitialSchema());
        Assert.Equal(SchemaToolExitCodes.Success, await RunAsync(
            ["apply", "--schema", schema, "--provider", "ledger", "--safe", "--output", "json"], session));

        session.Ledger.Add(new DataMigrationLedgerEntry(
            session.Target!, MigrationId, "tickets", "fingerprint",
            DataMigrationRunState.Running, cursor: "5:sid-42;",
            rowsScanned: 12, rowsChanged: 12, batches: 3,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, completedAt: null));

        var status = await RunAsync(["status", "--schema", schema, "--provider", "ledger", "--output", "json"], session);

        Assert.Equal(SchemaToolExitCodes.PendingChanges, status);
        var report = JsonDocument.Parse(output.ToString());
        Assert.Equal("pending", report.RootElement.GetProperty("outcome").GetString());
        var migration = Assert.Single(report.RootElement.GetProperty("dataMigrations").EnumerateArray());
        Assert.Equal(MigrationId, migration.GetProperty("identity").GetString());
        Assert.Equal("pending", migration.GetProperty("state").GetString());
        Assert.Equal("tickets", migration.GetProperty("unit").GetString());
        Assert.Equal(12, migration.GetProperty("rowsScanned").GetInt64());
        Assert.Equal(3, migration.GetProperty("batches").GetInt32());
        Assert.Equal("5:sid-42;", migration.GetProperty("resumeCursor").GetString());
        Assert.Equal(0, report.RootElement.GetProperty("pendingOperations").GetArrayLength());
    }

    [Fact]
    public async Task A_completed_ledger_entry_leaves_the_target_ready()
    {
        var session = new LedgerSession();
        var schema = Temp("schema.json", SchemaToolCliHarness.InitialSchema());
        await RunAsync(["apply", "--schema", schema, "--provider", "ledger", "--safe", "--output", "json"], session);
        session.Ledger.Add(new DataMigrationLedgerEntry(
            session.Target!, MigrationId, "tickets", "fingerprint",
            DataMigrationRunState.Completed, cursor: null,
            rowsScanned: 12, rowsChanged: 12, batches: 3,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));

        var status = await RunAsync(["status", "--schema", schema, "--provider", "ledger", "--output", "json"], session);

        Assert.Equal(SchemaToolExitCodes.Success, status);
        var report = JsonDocument.Parse(output.ToString());
        Assert.Equal("ready", report.RootElement.GetProperty("outcome").GetString());
        var migration = Assert.Single(report.RootElement.GetProperty("dataMigrations").EnumerateArray());
        Assert.Equal("applied", migration.GetProperty("state").GetString());
        Assert.Equal("1970-01-01T00:00:00.0000000+00:00", migration.GetProperty("completedAt").GetString());
    }

    [Fact]
    public async Task A_declared_semantic_migration_the_ledger_never_saw_is_named_not_recorded()
    {
        var session = new LedgerSession { SemanticMigrationId = MigrationId };
        var schema = Temp("schema.json", SchemaToolCliHarness.InitialSchema());

        var plan = await RunAsync(["plan", "--schema", schema, "--provider", "ledger", "--output", "json"], session);

        // The tool cannot see host transforms, so it names the gap without deciding the outcome:
        // the target is pending only because its schema operations are.
        Assert.Equal(SchemaToolExitCodes.PendingChanges, plan);
        var migration = Assert.Single(
            JsonDocument.Parse(output.ToString()).RootElement.GetProperty("dataMigrations").EnumerateArray());
        Assert.Equal("not-recorded", migration.GetProperty("state").GetString());
        Assert.Equal(MigrationId, migration.GetProperty("identity").GetString());
    }

    [Fact]
    public async Task A_provider_without_data_migrations_reports_none()
    {
        var session = new LedgerSession { OffersDataMigrations = false, SemanticMigrationId = MigrationId };
        var schema = Temp("schema.json", SchemaToolCliHarness.InitialSchema());

        await RunAsync(["plan", "--schema", schema, "--provider", "ledger", "--output", "json"], session);

        Assert.Equal(0, JsonDocument.Parse(output.ToString())
            .RootElement.GetProperty("dataMigrations").GetArrayLength());
    }

    // ------------------------------------------------------------------ fixtures

    private readonly string directory = Path.Combine(Path.GetTempPath(), "groundwork-migration-status-" + Guid.NewGuid().ToString("N"));
    private readonly StringWriter output = new();
    private readonly StringWriter error = new();

    public SchemaToolDataMigrationStatusTests() => Directory.CreateDirectory(directory);

    public void Dispose() { try { Directory.Delete(directory, recursive: true); } catch { } }

    private string Temp(string name, string contents)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, contents);
        return path;
    }

    private Task<int> RunAsync(string[] arguments, ISchemaToolProviderSession session)
    {
        output.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        return GroundworkSchemaCli.RunAsync(arguments, output, error, _ => session);
    }

    private sealed class LedgerSession : ISchemaToolProviderSession, IDataMigrationExecutor
    {
        private PhysicalSchemaAppliedState? applied;

        public List<DataMigrationLedgerEntry> Ledger { get; } = [];

        public string? SemanticMigrationId { get; init; }

        public bool OffersDataMigrations { get; init; } = true;

        public PhysicalSchemaTargetIdentity? Target { get; private set; }

        public ProviderIdentity Provider { get; } = new("ledger", "1");

        public IPhysicalSchemaTargetCompiler Targets => new Compiler(this);

        public IPhysicalSchemaExecutor Executor => new Executing(this);

        public IPhysicalSchemaHistoryInspector Inspector => new Executing(this);

        public IDataMigrationExecutor? DataMigrations => OffersDataMigrations ? this : null;

        public DataMigrationCapabilities Capabilities => DataMigrationRunner.Required;

        public IPhysicalSchemaApplicationLock AcquireMigrationLock(PhysicalSchemaTargetIdentity target) =>
            new Lease(target);

        public DataMigrationLedgerEntry? ReadLedgerEntry(PhysicalSchemaTargetIdentity target, string migrationId) =>
            Ledger.FirstOrDefault(entry => entry.MigrationId == migrationId);

        public ValueTask<DataMigrationLedgerEntry?> ReadLedgerEntryAsync(
            PhysicalSchemaTargetIdentity target, string migrationId, CancellationToken cancellationToken = default) =>
            new(ReadLedgerEntry(target, migrationId));

        public IReadOnlyList<DataMigrationLedgerEntry> ReadLedgerEntries(PhysicalSchemaTargetIdentity target) =>
            Ledger.ToArray();

        public ValueTask<IReadOnlyList<DataMigrationLedgerEntry>> ReadLedgerEntriesAsync(
            PhysicalSchemaTargetIdentity target, CancellationToken cancellationToken = default) =>
            new(ReadLedgerEntries(target));

        public void WriteLedgerEntry(DataMigrationLedgerEntry entry) => Ledger.Add(entry);

        public ValueTask WriteLedgerEntryAsync(DataMigrationLedgerEntry entry, CancellationToken cancellationToken = default)
        {
            WriteLedgerEntry(entry);
            return default;
        }

        public DataMigrationChunkOutcome ExecuteChunk(DataMigrationChunkRequest request) =>
            DataMigrationChunkOutcome.Exhausted(request.Entry);

        public ValueTask<DataMigrationChunkOutcome> ExecuteChunkAsync(
            DataMigrationChunkRequest request, CancellationToken cancellationToken = default) =>
            new(ExecuteChunk(request));

        public void Dispose() { }

        private sealed class Lease(PhysicalSchemaTargetIdentity target) : IPhysicalSchemaApplicationLock
        {
            public PhysicalSchemaTargetIdentity Target { get; } = target;
            public void Dispose() { }
        }

        private sealed class Compiler(LedgerSession owner) : IPhysicalSchemaTargetCompiler
        {
            public PhysicalSchemaTarget Compile(StorageUnit declaration)
            {
                var target = new PhysicalSchemaTarget(
                    new SchemaSubject(
                        SearchKeyProjection.Expand(declaration),
                        new SchemaEvolutionMetadata(semanticMigrationId: owner.SemanticMigrationId)),
                    owner.Provider);
                owner.Target = target.Identity;
                return target;
            }
        }

        private sealed class Executing(LedgerSession owner) : IPhysicalSchemaExecutor, IPhysicalSchemaHistoryInspector
        {
            public IPhysicalSchemaApplicationLock AcquireApplicationLock(PhysicalSchemaTargetIdentity target) => new Lease(target);

            public PhysicalSchemaHistoryState ReadHistory(
                PhysicalSchemaTargetIdentity target, IPhysicalSchemaApplicationLock applicationLock) =>
                owner.applied is null
                    ? PhysicalSchemaHistoryState.Empty
                    : PhysicalSchemaHistoryState.FromApplied(owner.applied);

            public PhysicalSchemaInspectionResult InspectHistory(PhysicalSchemaTarget target)
            {
                owner.Target = target.Identity;
                return new(ReadHistory(target.Identity, new Lease(target.Identity)), IsAppliedSchemaValid: true);
            }

            public PhysicalSchemaOperationAcknowledgement ApplyOperation(
                PhysicalSchemaTargetIdentity target,
                PhysicalSchemaOperation operation,
                IPhysicalSchemaApplicationLock applicationLock) =>
                new(operation.Identity, operation.Fingerprint, DateTimeOffset.UnixEpoch);

            public void PublishAppliedState(
                PhysicalSchemaAppliedState state,
                string? expectedAppliedTargetFingerprint,
                IPhysicalSchemaApplicationLock applicationLock) => owner.applied = state;

            private sealed class Lease(PhysicalSchemaTargetIdentity target) : IPhysicalSchemaApplicationLock
            {
                public PhysicalSchemaTargetIdentity Target { get; } = target;
                public void Dispose() { }
            }
        }
    }
}
