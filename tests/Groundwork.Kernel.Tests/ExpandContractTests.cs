using System.Collections.Immutable;
using System.Reflection;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Xunit;

namespace Groundwork.Kernel.Tests;

/// <summary>
/// Expand–contract as a first-class workflow: one declaration yields an additive expand plan and a
/// later destructive contract plan, and the contract plan refuses until durable state establishes
/// that it may run.
/// </summary>
public sealed class ExpandContractTests
{
    private const string MigrationId = "2026-08-widen-total";
    private static readonly ProviderIdentity Provider = new("test-provider", "1.0");
    private static readonly DateTimeOffset T0 = new(2026, 8, 27, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromHours(24);

    // ------------------------------------------------------------------ the split

    [Fact]
    public void One_declaration_yields_an_additive_expand_plan_and_a_destructive_contract_plan()
    {
        var executor = Expanded();
        var target = SupersedingTarget();

        var expand = Plan(target, executor, SchemaEvolutionPhase.Expand);
        var contract = Plan(target, executor, SchemaEvolutionPhase.Contract, Ready(target, executor));

        Assert.Equal(SchemaEvolutionPhase.Expand, expand.Phase);
        Assert.Equal(SchemaEvolutionPhase.Contract, contract.Phase);
        // The expand plan is already applied, so it has nothing left to do; the contract plan is
        // exactly the removal the expand deliberately withheld, plus the marker that records it.
        Assert.Empty(expand.Operations);
        Assert.Equal(
            new[] { "DropColumn:total", "ColumnSupersession:total", "ValidatePhysicalSchema:target", "PublishAppliedState:target" },
            contract.Operations.Select(operation => $"{operation.Kind}:{operation.SubjectIdentity}").ToArray());
        Assert.Equal("total", Assert.Single(contract.Operations.OfType<DropColumnOperation>()).Column.Name);
    }

    [Fact]
    public void The_expand_plan_adds_the_replacement_and_names_the_superseded_column_only_to_retain_it()
    {
        var executor = Applied(BeforeTarget());
        var target = SupersedingTarget();

        var expand = Plan(target, executor, SchemaEvolutionPhase.Expand);

        Assert.Equal(
            new[] { "AddColumn:total_amount", "ColumnSupersession:total", "ValidatePhysicalSchema:target", "PublishAppliedState:target" },
            expand.Operations.Select(operation => $"{operation.Kind}:{operation.SubjectIdentity}").ToArray());
        // The dual-presence guarantee, stated as a property of the plan: nothing the expand half
        // does can be observed by an application version that still owns the superseded column.
        // Only the marker names it, and the marker performs no physical work.
        Assert.Empty(expand.Operations.Where(operation =>
            operation.Kind is not PhysicalSchemaOperationKind.ColumnSupersession &&
            operation.SubjectIdentity == "total"));
        var marker = Assert.Single(expand.Operations.OfType<ColumnSupersessionOperation>());
        Assert.Equal(ColumnSupersessionState.Retained, marker.State);
        Assert.Equal("total_amount", marker.Supersession.ReplacementColumn);
    }

    [Fact]
    public void The_contract_plan_and_the_expand_plan_of_one_declaration_have_distinct_fingerprints()
    {
        var executor = Expanded();
        var target = SupersedingTarget();

        var expand = Plan(target, executor, SchemaEvolutionPhase.Expand);
        var contract = Plan(target, executor, SchemaEvolutionPhase.Contract, Ready(target, executor));

        var expandOperations = expand.Operations.Select(operation => operation.Fingerprint).ToArray();
        var contractOperations = contract.Operations.Select(operation => operation.Fingerprint).ToArray();
        Assert.NotEqual(expandOperations, contractOperations);
        // The marker is the one operation both phases derive, and it fingerprints differently in
        // each: recording "retained" and recording "contracted" are different durable facts.
        var retained = new ColumnSupersessionOperation(
            target.Subject, Supersession, ColumnSupersessionState.Retained);
        var contracted = Assert.Single(contract.Operations.OfType<ColumnSupersessionOperation>());
        Assert.NotEqual(retained.Fingerprint, contracted.Fingerprint);
        Assert.Equal(retained.SlotIdentity, contracted.SlotIdentity);
    }

    // ------------------------------------------------------------------ the gate

    [Fact]
    public void A_contract_plan_without_established_readiness_is_refused()
    {
        var executor = Expanded();
        var target = SupersedingTarget();

        var plan = Plan(target, executor, SchemaEvolutionPhase.Contract);

        var refusal = Assert.Single(plan.Refusals);
        Assert.Equal("GW-EXPAND-004", refusal.Code);
        Assert.Equal(
            "A contract plan for 'test-provider:orders' requires contract readiness established from the " +
            "applied schema ledger and the data-migration ledger; none was supplied.",
            refusal.Message);
    }

    [Fact]
    public void Readiness_cannot_be_asserted_by_a_caller()
    {
        // The only way to obtain readiness is AssessContractReadiness, which reads durable state.
        // The type has no public constructor, so an application or provider assembly cannot write
        // "ready, refusals: none" — the same discipline DataMigrationExhaustion enforces.
        Assert.Empty(typeof(ContractReadinessAssessment)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance));
    }

    [Fact]
    public void Contracting_before_the_expand_is_applied_is_refused()
    {
        var executor = Applied(BeforeTarget());
        var target = SupersedingTarget();

        var readiness = ExpandContractWorkflow.AssessContractReadiness(
            target, History(executor), CompletedLedger(target, T0), T0);

        Assert.False(readiness.IsReady);
        var refusal = Assert.Single(readiness.Refusals);
        Assert.Equal("GW-EXPAND-001", refusal.Code);
        Assert.Equal(
            "Column 'total' cannot be contracted: the applied ledger does not record it as retained " +
            "beside replacement column 'total_amount'. Apply the expand plan first.",
            refusal.Message);
        Assert.Equal("schema.supersessions.total", refusal.Path);
    }

    [Fact]
    public void Contracting_before_the_backfill_is_recorded_complete_is_refused()
    {
        var executor = Expanded();
        var target = SupersedingTarget();

        var never = ExpandContractWorkflow.AssessContractReadiness(target, History(executor), [], T0.AddDays(9));
        // A provider with no data-migration execution at all is a different fact from an empty
        // ledger, and the refusal says which one it is.
        var unrecordable = ExpandContractWorkflow.AssessContractReadiness(
            target, History(executor), dataMigrations: null, T0.AddDays(9));
        var running = ExpandContractWorkflow.AssessContractReadiness(
            target, History(executor), [RunningLedger(target)], T0.AddDays(9));

        Assert.Equal("GW-EXPAND-002", Assert.Single(never.Refusals).Code);
        Assert.Equal(
            "Column 'total' cannot be contracted until data migration '2026-08-widen-total' is recorded " +
            "complete; the ledger records it as not started.",
            Assert.Single(never.Refusals).Message);
        Assert.Equal(
            "Column 'total' cannot be contracted until data migration '2026-08-widen-total' is recorded " +
            "complete; the ledger records it as running.",
            Assert.Single(running.Refusals).Message);
        Assert.Equal(
            "Column 'total' cannot be contracted until data migration '2026-08-widen-total' is recorded " +
            "complete; this provider records no data migrations, so nothing can establish that it finished.",
            Assert.Single(unrecordable.Refusals).Message);
    }

    [Fact]
    public void Contracting_before_the_dual_presence_window_elapses_is_refused()
    {
        var executor = Expanded();
        var target = SupersedingTarget();
        var completedAt = T0.AddHours(2);

        var early = ExpandContractWorkflow.AssessContractReadiness(
            target, History(executor), CompletedLedger(target, completedAt), completedAt.AddHours(23));
        var late = ExpandContractWorkflow.AssessContractReadiness(
            target, History(executor), CompletedLedger(target, completedAt), completedAt + Window);

        Assert.Equal("GW-EXPAND-003", Assert.Single(early.Refusals).Code);
        Assert.Equal(
            "Column 'total' cannot be contracted until its dual-presence window elapses at " +
            "2026-08-28T11:00:00.0000000+00:00; 01:00:00 of 1.00:00:00 remains.",
            Assert.Single(early.Refusals).Message);
        Assert.True(late.IsReady);
        // The window opens at the later of the retention being recorded and the backfill completing,
        // so a backfill that finishes two hours after the expand moves the gate by two hours.
        Assert.Equal(completedAt + Window, Assert.Single(late.Supersessions).ContractableAt);
    }

    [Fact]
    public void Readiness_established_against_another_applied_state_does_not_admit_this_one()
    {
        var executor = Expanded();
        var target = SupersedingTarget();
        var stale = Ready(target, executor);

        // The applied state moves between the assessment and the plan: another expand adds a column.
        ApplyWithMigration(SupersedingTargetWithNote(), executor, T0.AddDays(2));
        var plan = Plan(target, executor, SchemaEvolutionPhase.Contract, stale);

        var refusal = Assert.Single(plan.Refusals);
        Assert.Equal("GW-EXPAND-005", refusal.Code);
        Assert.StartsWith("Contract readiness was established for 'test-provider:orders'", refusal.Message);
    }

    [Fact]
    public void Readiness_will_not_reuse_a_marker_recorded_for_another_replacement_column()
    {
        var executor = Expanded();
        // The declaration is re-pointed at a column that is already applied, so the replacement
        // exists — but the retention recorded in the ledger is the one it abandoned.
        var repointed = new PhysicalSchemaTarget(
            new SchemaSubject(
                After(),
                new SchemaEvolutionMetadata(
                    semanticMigrationId: MigrationId,
                    supersessions:
                    [
                        new ColumnSupersession(
                            new ColumnDefinition { Name = "total", Type = PortableType.Decimal, Precision = 10, Scale = 2 },
                            "name")
                    ],
                    dualPresenceWindow: Window)),
            Provider);

        var readiness = ExpandContractWorkflow.AssessContractReadiness(
            repointed, History(executor), executor.Ledger, T0.AddDays(3));

        Assert.Equal("GW-EXPAND-001", Assert.Single(readiness.Refusals).Code);
        Assert.Equal(
            "Column 'total' cannot be contracted: the applied ledger does not record it as retained " +
            "beside replacement column 'name'. Apply the expand plan first.",
            Assert.Single(readiness.Refusals).Message);
    }

    // ------------------------------------------------------------------ the terminal state

    [Fact]
    public void A_contracted_column_is_never_re_added_by_either_phase()
    {
        var executor = Expanded();
        var target = SupersedingTarget();
        var contracted = ApplyWithMigration(
            target, executor, T0.AddDays(3), SchemaEvolutionPhase.Contract);
        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, contracted.Outcome);

        var expand = Plan(target, executor, SchemaEvolutionPhase.Expand);
        var contractAgain = ApplyWithMigration(
            target, executor, T0.AddDays(4), SchemaEvolutionPhase.Contract);

        Assert.Empty(expand.Operations);
        Assert.Equal(PhysicalSchemaApplicationOutcome.NoChanges, contractAgain.Outcome);
        Assert.Empty(contractAgain.Plan.Operations);
        Assert.DoesNotContain(
            executor.AppliedState!.Snapshot.Subject.Columns,
            column => column.Name == "total");
        var marker = Assert.Single(executor.AppliedState.AppliedOperations
            .Where(operation => operation.Kind == PhysicalSchemaOperationKind.ColumnSupersession));
        Assert.True(ColumnSupersessionOperation.TryReadPayload(marker.CanonicalPayload, out var replacement, out var state));
        Assert.Equal(ColumnSupersessionState.Contracted, state);
        Assert.Equal("total_amount", replacement);
    }

    [Fact]
    public void Withdrawing_a_supersession_is_refused_while_retained_and_clean_once_contracted()
    {
        var executor = Expanded();
        var withdrawn = WithdrawnTarget();

        var whileRetained = Plan(withdrawn, executor, SchemaEvolutionPhase.Expand);

        var refusal = Assert.Single(whileRetained.Refusals);
        Assert.Equal("GW-EXPAND-006", refusal.Code);
        Assert.Equal(
            "Column 'total' is recorded as retained by an expand plan, and this declaration no longer " +
            "supersedes it. Contract it before withdrawing the supersession.",
            refusal.Message);

        ApplyWithMigration(
            SupersedingTarget(), executor, T0.AddDays(3), SchemaEvolutionPhase.Contract);
        var afterContract = Plan(withdrawn, executor, SchemaEvolutionPhase.Expand);

        Assert.Empty(afterContract.Refusals);
        Assert.Equal(
            new[] { "ValidatePhysicalSchema:target", "PublishAppliedState:target" },
            afterContract.Operations.Select(operation => $"{operation.Kind}:{operation.SubjectIdentity}").ToArray());
    }

    // ------------------------------------------------------------------ the declaration

    [Fact]
    public void A_declaration_cannot_both_declare_and_supersede_one_column()
    {
        var unit = Before();
        var thrown = Assert.Throws<ArgumentException>(() => new SchemaSubject(
            unit,
            new SchemaEvolutionMetadata(
                semanticMigrationId: MigrationId,
                supersessions: [new ColumnSupersession(
                    new ColumnDefinition { Name = "total", Type = PortableType.Decimal, Precision = 10, Scale = 2 },
                    "name")])));

        Assert.StartsWith(
            "Superseded column 'total' is still declared by 'orders'.",
            thrown.Message);
    }

    [Fact]
    public void A_supersession_requires_a_replacement_column_and_a_backfill_to_populate_it()
    {
        var missingReplacement = Assert.Throws<ArgumentException>(() => new SchemaSubject(
            After(),
            new SchemaEvolutionMetadata(
                semanticMigrationId: MigrationId,
                supersessions: [new ColumnSupersession(
                    new ColumnDefinition { Name = "total", Type = PortableType.Decimal, Precision = 10, Scale = 2 },
                    "absent")])));
        var missingMigration = Assert.Throws<ArgumentException>(() =>
            new SchemaEvolutionMetadata(supersessions: [Supersession]));

        Assert.StartsWith(
            "Replacement column 'absent' for superseded column 'total' is not declared by 'orders'.",
            missingReplacement.Message);
        Assert.StartsWith(
            "A declaration that supersedes a column requires a semantic migration id",
            missingMigration.Message);
    }

    [Fact]
    public void A_supersession_changes_the_subject_fingerprint_and_leaves_a_plain_subject_untouched()
    {
        var plain = new SchemaSubject(After());
        var superseding = SupersedingTarget().Subject;
        var widerWindow = new SchemaSubject(
            After(),
            new SchemaEvolutionMetadata(
                semanticMigrationId: MigrationId,
                supersessions: [Supersession],
                dualPresenceWindow: Window + TimeSpan.FromHours(1)));

        // Pinned: a subject that supersedes nothing fingerprints exactly as it did before the
        // workflow existed, so adding supersessions is not a persisted schema boundary for anyone.
        Assert.Equal("21d5f4cc6a41a9b6b6b20a9b11158203c98ddd8f663738d42b96da981b2696d5", plain.Fingerprint);
        Assert.Equal("5be8980c634680048b0e278f7c8103ef9e8930ecfeb0861bff7bc05d67f36aa6", superseding.Fingerprint);
        Assert.Equal("d983360ad9af288a4f9a3b944d38283ea8b21b4323d8dca2d2e8808c4c46ee75", widerWindow.Fingerprint);
        Assert.NotEqual(plain.Fingerprint, superseding.Fingerprint);
        Assert.NotEqual(superseding.Fingerprint, widerWindow.Fingerprint);
    }

    [Fact]
    public void An_invalid_transform_is_refused_before_expand_mutates_applied_state()
    {
        var executor = Applied(BeforeTarget());
        var appliedFingerprint = executor.AppliedState!.TargetFingerprint;
        var target = SupersedingTarget();
        var catalog = new DataMigrationCatalog(
        [
            new DataMigration(MigrationId, target.Subject.Id, new MissingSourceTransform())
        ]);

        var refusal = Assert.Throws<DataMigrationRefusedException>(() => PhysicalSchemaApplication.Apply(
            target,
            executor,
            T0.AddHours(1),
            dataMigrations: catalog,
            dataMigrationExecutor: executor));

        Assert.Equal(DataMigrationCodes.NotApplicable, refusal.Code);
        Assert.Equal(appliedFingerprint, executor.AppliedState.TargetFingerprint);
    }

    // ------------------------------------------------------------------ fixtures

    private static ColumnSupersession Supersession { get; } = new(
        new ColumnDefinition { Name = "total", Type = PortableType.Decimal, Precision = 10, Scale = 2 },
        "total_amount");

    private static StorageUnit Before() => new()
    {
        Id = new StorageUnitId("orders"),
        Name = "orders",
        Columns =
        [
            new ColumnDefinition { Name = "id", Type = PortableType.Int32, IsNullable = false },
            new ColumnDefinition { Name = "name", Type = PortableType.String, MaxLength = 64 },
            new ColumnDefinition { Name = "total", Type = PortableType.Decimal, Precision = 10, Scale = 2 }
        ],
        Key = new KeyDefinition { Columns = ["id"] }
    };

    private static StorageUnit After() => new()
    {
        Id = new StorageUnitId("orders"),
        Name = "orders",
        Columns =
        [
            new ColumnDefinition { Name = "id", Type = PortableType.Int32, IsNullable = false },
            new ColumnDefinition { Name = "name", Type = PortableType.String, MaxLength = 64 },
            new ColumnDefinition { Name = "total_amount", Type = PortableType.Decimal, Precision = 18, Scale = 2 }
        ],
        Key = new KeyDefinition { Columns = ["id"] }
    };

    private static PhysicalSchemaTarget BeforeTarget() =>
        new(new SchemaSubject(Before()), Provider);

    private static PhysicalSchemaTarget SupersedingTarget() =>
        new(
            new SchemaSubject(
                After(),
                new SchemaEvolutionMetadata(
                    semanticMigrationId: MigrationId,
                    supersessions: [Supersession],
                    dualPresenceWindow: Window)),
            Provider);

    /// <summary>The same declaration once the operator stops naming the supersession.</summary>
    private static PhysicalSchemaTarget WithdrawnTarget() =>
        new(new SchemaSubject(After(), new SchemaEvolutionMetadata(semanticMigrationId: MigrationId)), Provider);

    /// <summary>The same evolution plus one more added column, used to move the applied state on.</summary>
    private static PhysicalSchemaTarget SupersedingTargetWithNote()
    {
        var unit = After();
        return new PhysicalSchemaTarget(
            new SchemaSubject(
                unit with
                {
                    Columns = [.. unit.Columns, new ColumnDefinition { Name = "note", Type = PortableType.String, MaxLength = 32 }]
                },
                new SchemaEvolutionMetadata(
                    semanticMigrationId: MigrationId,
                    supersessions: [Supersession],
                    dualPresenceWindow: Window)),
            Provider);
    }

    private static FakeExecutor Applied(PhysicalSchemaTarget target)
    {
        var executor = new FakeExecutor();
        PhysicalSchemaApplication.Apply(target, executor, T0);
        return executor;
    }

    /// <summary>An executor whose applied state is the expand half, with its backfill complete.</summary>
    private static FakeExecutor Expanded()
    {
        var executor = Applied(BeforeTarget());
        var target = SupersedingTarget();
        ApplyWithMigration(target, executor, T0.AddHours(1));
        executor.Ledger = CompletedLedger(target, T0.AddHours(1));
        return executor;
    }

    private static PhysicalSchemaApplicationResult ApplyWithMigration(
        PhysicalSchemaTarget target,
        FakeExecutor executor,
        DateTimeOffset now,
        SchemaEvolutionPhase phase = SchemaEvolutionPhase.Expand) =>
        PhysicalSchemaApplication.Apply(
            target,
            executor,
            now,
            dataMigrations: new DataMigrationCatalog(
            [
                new DataMigration(MigrationId, target.Subject.Id, new CopyTotalTransform())
            ]),
            phase: phase,
            dataMigrationExecutor: executor);

    private static PhysicalSchemaHistoryState History(FakeExecutor executor) =>
        executor.AppliedState is null
            ? PhysicalSchemaHistoryState.Empty
            : PhysicalSchemaHistoryState.FromApplied(executor.AppliedState);

    private static ContractReadinessAssessment Ready(PhysicalSchemaTarget target, FakeExecutor executor) =>
        ExpandContractWorkflow.AssessContractReadiness(
            target, History(executor), executor.Ledger, T0.AddDays(3));

    private static PhysicalSchemaDiffPlan Plan(
        PhysicalSchemaTarget target,
        FakeExecutor executor,
        SchemaEvolutionPhase phase,
        ContractReadinessAssessment? readiness = null) =>
        PhysicalSchemaDiffPlanner.Plan(
            target,
            History(executor),
            T0.AddDays(3),
            phase: phase,
            readiness: readiness);

    private static IReadOnlyList<DataMigrationLedgerEntry> CompletedLedger(
        PhysicalSchemaTarget target,
        DateTimeOffset completedAt)
    {
        var entry = RunningLedger(target);
        return [entry.Complete(DataMigrationChunkOutcome.Exhausted(entry).Evidence, completedAt)];
    }

    private static DataMigrationLedgerEntry RunningLedger(PhysicalSchemaTarget target) =>
        DataMigrationLedgerEntry.Start(
            target.Identity,
            new DataMigration(MigrationId, target.Subject.Id, new CopyTotalTransform()),
            target.Subject.Definition,
            T0.AddHours(1));

    private sealed class CopyTotalTransform : IDataMigrationTransform
    {
        public string Identity => "copy-total";
        public string Version => "v1";
        public ImmutableArray<string> SourceColumns => [];
        public ImmutableArray<string> TargetColumns => ["total_amount"];
        public DataMigrationValues Transform(DataMigrationRow row) => DataMigrationValues.Unchanged;
    }

    private sealed class MissingSourceTransform : IDataMigrationTransform
    {
        public string Identity => "missing-source";
        public string Version => "v1";
        public ImmutableArray<string> SourceColumns => ["absent"];
        public ImmutableArray<string> TargetColumns => ["total_amount"];
        public DataMigrationValues Transform(DataMigrationRow row) => DataMigrationValues.Unchanged;
    }

    /// <summary>
    /// A durable schema executor that also serves the data-migration ledger, because the contract
    /// gate reads both and the two have to come from one provider.
    /// </summary>
    private sealed class FakeExecutor : IPhysicalSchemaExecutor, IPhysicalSchemaHistoryInspector, IDataMigrationExecutor
    {
        private readonly Dictionary<string, PhysicalSchemaOperationAcknowledgement> durable = new(StringComparer.Ordinal);
        private int applied;

        public PhysicalSchemaAppliedState? AppliedState { get; private set; }

        public IReadOnlyList<DataMigrationLedgerEntry> Ledger { get; set; } = [];

        public DataMigrationCapabilities Capabilities =>
            DataMigrationCapabilities.KeysetScan |
            DataMigrationCapabilities.AtomicChunkProgress |
            DataMigrationCapabilities.AppliedLedger |
            DataMigrationCapabilities.ExclusiveRunLease;

        public IPhysicalSchemaApplicationLock AcquireMigrationLock(PhysicalSchemaTargetIdentity target) =>
            new Lock(target);

        public IPhysicalSchemaApplicationLock AcquireApplicationLock(PhysicalSchemaTargetIdentity target) =>
            new Lock(target);

        public PhysicalSchemaHistoryState ReadHistory(
            PhysicalSchemaTargetIdentity target,
            IPhysicalSchemaApplicationLock applicationLock) =>
            AppliedState is null || AppliedState.TargetIdentity != target
                ? PhysicalSchemaHistoryState.Empty
                : PhysicalSchemaHistoryState.FromApplied(AppliedState);

        public PhysicalSchemaInspectionResult InspectHistory(PhysicalSchemaTarget target) =>
            new(ReadHistory(target.Identity, new Lock(target.Identity)), true);

        public PhysicalSchemaOperationAcknowledgement ApplyOperation(
            PhysicalSchemaTargetIdentity target,
            PhysicalSchemaOperation operation,
            IPhysicalSchemaApplicationLock applicationLock)
        {
            if (durable.TryGetValue(operation.Identity, out var existing))
                return existing;
            var acknowledgement = new PhysicalSchemaOperationAcknowledgement(
                operation.Identity, operation.Fingerprint, T0.AddHours(1).AddSeconds(applied++));
            durable[operation.Identity] = acknowledgement;
            return acknowledgement;
        }

        public void PublishAppliedState(
            PhysicalSchemaAppliedState state,
            string? expectedAppliedTargetFingerprint,
            IPhysicalSchemaApplicationLock applicationLock) => AppliedState = state;

        public DataMigrationLedgerEntry? ReadLedgerEntry(PhysicalSchemaTargetIdentity target, string migrationId) =>
            Ledger.FirstOrDefault(entry => entry.MigrationId == migrationId);

        public ValueTask<DataMigrationLedgerEntry?> ReadLedgerEntryAsync(
            PhysicalSchemaTargetIdentity target,
            string migrationId,
            CancellationToken cancellationToken = default) => new(ReadLedgerEntry(target, migrationId));

        public IReadOnlyList<DataMigrationLedgerEntry> ReadLedgerEntries(PhysicalSchemaTargetIdentity target) => Ledger;

        public ValueTask<IReadOnlyList<DataMigrationLedgerEntry>> ReadLedgerEntriesAsync(
            PhysicalSchemaTargetIdentity target,
            CancellationToken cancellationToken = default) => new(Ledger);

        public void WriteLedgerEntry(DataMigrationLedgerEntry entry) =>
            Ledger = [.. Ledger.Where(item => item.MigrationId != entry.MigrationId), entry];

        public ValueTask WriteLedgerEntryAsync(DataMigrationLedgerEntry entry, CancellationToken cancellationToken = default)
        {
            WriteLedgerEntry(entry);
            return default;
        }

        public DataMigrationChunkOutcome ExecuteChunk(DataMigrationChunkRequest request) =>
            DataMigrationChunkOutcome.Exhausted(request.Entry);

        public ValueTask<DataMigrationChunkOutcome> ExecuteChunkAsync(
            DataMigrationChunkRequest request,
            CancellationToken cancellationToken = default) => new(ExecuteChunk(request));

        private sealed class Lock(PhysicalSchemaTargetIdentity target) : IPhysicalSchemaApplicationLock
        {
            public PhysicalSchemaTargetIdentity Target { get; } = target;

            public void Dispose()
            {
            }
        }
    }
}
