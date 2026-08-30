using System.Text.Json;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Testing;
using Xunit;

namespace Groundwork.SchemaTool.Tests;

/// <summary>
/// The deployment tool's half of the expand–contract workflow: <c>--phase</c> selects which plan is
/// reported and applied, and the contract phase reports the gate rather than deciding it.
/// </summary>
public sealed class SchemaToolExpandContractTests : IDisposable
{
    private const string MigrationId = "2026-08-slugify";

    private const string BeforeSchema =
        """
        {"tables":[{"name":"tickets","columns":[{"name":"id","type":"String","nullable":false,"length":64,"precision":null,"scale":null,"folding":"None","generation":"Supplied"},{"name":"slug","type":"String","nullable":true,"length":64,"precision":null,"scale":null,"folding":"None","generation":"Supplied"}],"key":["id"],"indexes":[]}]}
        """;

    private const string AfterSchema =
        """
        {"tables":[{"name":"tickets","columns":[{"name":"id","type":"String","nullable":false,"length":64,"precision":null,"scale":null,"folding":"None","generation":"Supplied"},{"name":"slug_v2","type":"String","nullable":true,"length":128,"precision":null,"scale":null,"folding":"None","generation":"Supplied"}],"key":["id"],"indexes":[],"evolution":{"isDestructive":false,"semanticMigrationId":"2026-08-slugify","retiresPrimaryStorage":false,"supersessions":[{"supersededColumn":{"name":"slug","type":"String","nullable":true,"length":64,"precision":null,"scale":null,"folding":"None","generation":"Supplied"},"replacementColumn":"slug_v2"}],"dualPresenceWindowTicks":36000000000}}]}
        """;

    [Fact]
    public async Task Expand_applies_additively_and_contract_reports_the_gate_before_it_opens()
    {
        var session = new SupersedingSession();
        var before = Temp("before.json", BeforeSchema);
        var after = Temp("after.json", AfterSchema);
        Assert.Equal(SchemaToolExitCodes.Success, await RunAsync(
            ["apply", "--schema", before, "--provider", "expanding", "--safe"], session));

        // The expand half carries the semantic migration that populates the replacement, so even
        // the additive plan is authorized against its exact fingerprint.
        Assert.Equal(SchemaToolExitCodes.Success, await ApplyExpandAsync(session, after));
        var expand = Report();

        Assert.Equal("expand", expand.GetProperty("phase").GetString());
        Assert.Equal("applied", expand.GetProperty("outcome").GetString());
        // Additive: the expand plan adds the replacement and records the retention, and removes
        // nothing at all.
        Assert.DoesNotContain("DropColumn", AppliedKinds(expand));
        Assert.Single(AppliedKinds(expand).Where(kind => kind == "ColumnSupersession"));

        // Nothing has been recorded for the backfill, so the contract half is gated shut.
        Assert.Equal(SchemaToolExitCodes.ValidationFailed, await RunAsync(
            ["plan", "--schema", after, "--provider", "expanding", "--phase", "contract", "--output", "json"],
            session));
        var blocked = Report();

        Assert.Equal("contract", blocked.GetProperty("phase").GetString());
        Assert.Equal("blocked", blocked.GetProperty("outcome").GetString());
        var diagnostic = Assert.Single(blocked.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("GW-EXPAND-002", diagnostic.GetProperty("code").GetString());
        var supersession = Assert.Single(blocked.GetProperty("supersessions").EnumerateArray());
        Assert.Equal("slug", supersession.GetProperty("supersededColumn").GetString());
        Assert.Equal("slug_v2", supersession.GetProperty("replacementColumn").GetString());
        Assert.False(supersession.GetProperty("isContractable").GetBoolean());
    }

    [Fact]
    public async Task A_completed_backfill_opens_the_gate_and_the_contract_plan_authorizes_its_own_fingerprint()
    {
        var session = new SupersedingSession();
        var before = Temp("before.json", BeforeSchema);
        var after = Temp("after.json", AfterSchema);
        await RunAsync(["apply", "--schema", before, "--provider", "expanding", "--safe"], session);
        await ApplyExpandAsync(session, after);
        Complete(session);

        Assert.Equal(SchemaToolExitCodes.PendingChanges, await RunAsync(
            ["plan", "--schema", after, "--provider", "expanding", "--phase", "contract", "--output", "json"],
            session));
        var contract = Report();
        var expandFingerprint = await PlanFingerprint(session, after, "expand");
        var contractFingerprint = contract.GetProperty("planFingerprint").GetString()!;

        Assert.True(Assert.Single(contract.GetProperty("supersessions").EnumerateArray())
            .GetProperty("isContractable").GetBoolean());
        // Distinct fingerprints for the two halves of one declaration, which is what an operator
        // passes to --expected-plan: authorizing the expand can never authorize the contract.
        Assert.NotEqual(expandFingerprint, contractFingerprint);
        Assert.Equal(
            new[] { "DropColumn:slug", "ColumnSupersession:slug" },
            contract.GetProperty("pendingOperations").EnumerateArray()
                .Select(operation => $"{operation.GetProperty("kind").GetString()}:{operation.GetProperty("subjectIdentity").GetString()}")
                .Where(entry => entry.StartsWith("DropColumn", StringComparison.Ordinal) ||
                                entry.StartsWith("ColumnSupersession", StringComparison.Ordinal))
                .ToArray());

        var applied = await RunAsync(
            ["apply", "--schema", after, "--provider", "expanding", "--phase", "contract",
             "--expected-plan", contractFingerprint,
             "--allow-destructive", "drop-column:tickets.slug",
             "--allow-semantic", MigrationId, "--output", "json"],
            session);

        Assert.Equal(SchemaToolExitCodes.Success, applied);
        Assert.Equal("applied", Report().GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task An_unknown_phase_is_an_invalid_invocation()
    {
        var session = new SupersedingSession();
        var schema = Temp("before.json", BeforeSchema);

        Assert.Equal(SchemaToolExitCodes.InvalidInvocation, await RunAsync(
            ["plan", "--schema", schema, "--provider", "expanding", "--phase", "shrink"], session));
        Assert.StartsWith("GW-CLI-001", error.ToString());
    }

    [Fact]
    public async Task Apply_refuses_a_document_migration_the_host_does_not_supply_before_publication()
    {
        var session = new SupersedingSession { OffersMigration = false };
        var before = Temp("before.json", BeforeSchema);
        var after = Temp("after.json", AfterSchema);
        Assert.Equal(SchemaToolExitCodes.Success, await RunAsync(
            ["apply", "--schema", before, "--provider", "expanding", "--safe"], session));

        var result = await ApplyExpandAsync(session, after);

        Assert.Equal(SchemaToolExitCodes.ValidationFailed, result);
        Assert.Contains(DataMigrationCodes.MissingTransform, output.ToString(), StringComparison.Ordinal);
        Assert.Contains(MigrationId, output.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, session.PublishCount);
    }

    // ------------------------------------------------------------------ fixtures

    private readonly string directory =
        Path.Combine(Path.GetTempPath(), "groundwork-expand-contract-cli-" + Guid.NewGuid().ToString("N"));
    private readonly StringWriter output = new();
    private readonly StringWriter error = new();

    public SchemaToolExpandContractTests() => Directory.CreateDirectory(directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // A leaked temporary directory is not worth failing a test over.
        }
    }

    private JsonElement Report() => JsonDocument.Parse(output.ToString()).RootElement;

    private static string[] AppliedKinds(JsonElement report) =>
        [.. report.GetProperty("appliedOperations").EnumerateArray()
            .Select(operation => operation.GetProperty("kind").GetString()!)];

    private async Task<int> ApplyExpandAsync(SupersedingSession session, string schema)
    {
        var fingerprint = await PlanFingerprint(session, schema, "expand");
        return await RunAsync(
            ["apply", "--schema", schema, "--provider", "expanding",
             "--expected-plan", fingerprint, "--allow-semantic", MigrationId, "--output", "json"],
            session);
    }

    private async Task<string> PlanFingerprint(SupersedingSession session, string schema, string phase)
    {
        await RunAsync(["plan", "--schema", schema, "--provider", "expanding", "--phase", phase, "--output", "json"], session);
        return Report().GetProperty("planFingerprint").GetString()!;
    }

    /// <summary>Records the backfill as durably finished, which is the only thing that opens the gate.</summary>
    private static void Complete(SupersedingSession session)
    {
        session.Ledger.Clear();
        session.Ledger.Add(new DataMigrationLedgerEntry(
            session.Target!, MigrationId, "tickets", session.Migration.RequestFingerprint(session.Definition!),
            DataMigrationRunState.Completed, cursor: null,
            rowsScanned: 4, rowsChanged: 4, batches: 1,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
    }

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

    /// <summary>
    /// A provider session whose ordinary compiler knows only physicalization. The canonical
    /// document supplies evolution through the interface's default evolution-aware overload.
    /// </summary>
    private sealed class SupersedingSession : ISchemaToolProviderSession, IDataMigrationExecutor
    {
        private PhysicalSchemaAppliedState? applied;

        public List<DataMigrationLedgerEntry> Ledger { get; } = [];

        public bool OffersMigration { get; init; } = true;

        public int PublishCount { get; private set; }

        public PhysicalSchemaTargetIdentity? Target { get; private set; }

        public StorageUnit? Definition { get; private set; }

        public DataMigration Migration { get; } =
            new(MigrationId, new StorageUnitId("tickets"), new CopySlugTransform());

        public ProviderIdentity Provider { get; } = new("expanding", "1");

        public IPhysicalSchemaTargetCompiler Targets => new Compiler(this);

        public IPhysicalSchemaExecutor Executor => new Executing(this);

        public IPhysicalSchemaHistoryInspector Inspector => new Executing(this);

        public IDataMigrationExecutor? DataMigrations => this;

        public DataMigrationCatalog DataMigrationCatalog => OffersMigration
            ? new DataMigrationCatalog([Migration])
            : DataMigrationCatalog.Empty;

        public DataMigrationCapabilities Capabilities => DataMigrationRunner.Required;

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

        public void Dispose()
        {
        }

        private sealed class Compiler(SupersedingSession owner) : IPhysicalSchemaTargetCompiler
        {
            public PhysicalSchemaTarget Compile(StorageUnit declaration)
            {
                owner.Definition = declaration;
                var target = new PhysicalSchemaTarget(
                    new SchemaSubject(SearchKeyProjection.Expand(declaration)),
                    owner.Provider);
                owner.Target = target.Identity;
                return target;
            }
        }

        private sealed class Executing(SupersedingSession owner) : IPhysicalSchemaExecutor, IPhysicalSchemaHistoryInspector
        {
            public IPhysicalSchemaApplicationLock AcquireApplicationLock(PhysicalSchemaTargetIdentity target) =>
                new Lease(target);

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
                IPhysicalSchemaApplicationLock applicationLock)
            {
                owner.applied = state;
                owner.PublishCount++;
            }

            private sealed class Lease(PhysicalSchemaTargetIdentity target) : IPhysicalSchemaApplicationLock
            {
                public PhysicalSchemaTargetIdentity Target { get; } = target;

                public void Dispose()
                {
                }
            }
        }

        private sealed class CopySlugTransform : IDataMigrationTransform
        {
            public string Identity => "copy-slug-v1";

            public System.Collections.Immutable.ImmutableArray<string> SourceColumns => ["slug"];

            public System.Collections.Immutable.ImmutableArray<string> TargetColumns => ["slug_v2"];

            public DataMigrationValues Transform(DataMigrationRow row) =>
                DataMigrationValues.Set(new Dictionary<string, object?> { ["slug_v2"] = row["slug"] });
        }
    }
}
