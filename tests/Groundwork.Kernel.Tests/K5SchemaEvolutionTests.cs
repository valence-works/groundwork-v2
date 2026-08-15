using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Xunit;

namespace Groundwork.Kernel.Tests;

public sealed class K5SchemaEvolutionTests
{
    private static readonly ProviderIdentity Provider = new("test-provider", "1.0");
    private static readonly DateTimeOffset PlannedAt = new(2026, 8, 15, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_columns_only_subject_plans_without_a_route_and_applies()
    {
        var target = CreateTarget(CreateUnit(includePriority: true));
        var plan = PhysicalSchemaDiffPlanner.Plan(target, PhysicalSchemaHistoryState.Empty, PlannedAt);

        Assert.True(plan.IsApplicable, string.Join("; ", plan.Refusals.Select(diagnostic => diagnostic.Message)));
        Assert.Contains(plan.Operations, operation => operation is CreatePrimaryStorageOperation);
        Assert.DoesNotContain(plan.Operations, operation => operation.GetType().Name.Contains("Route", StringComparison.Ordinal));

        var executor = new FakeExecutor();
        var applied = PhysicalSchemaApplication.Apply(target, executor, PlannedAt.AddMinutes(1));

        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, applied.Outcome);
        Assert.Equal(target.Fingerprint, executor.AppliedState!.TargetFingerprint);
        Assert.Equal(target.Subject.Id, executor.AppliedState.Snapshot.Subject.Id);
    }

    [Fact]
    public void Non_nullable_column_addition_is_add_backfill_finalize_before_index()
    {
        var target = CreateTarget(CreateUnit(includePriority: true));
        var operations = PhysicalSchemaDiffPlanner.Plan(target, PhysicalSchemaHistoryState.Empty, PlannedAt)
            .Operations
            .ToArray();
        var add = Assert.Single(operations.OfType<AddColumnOperation>(), operation => operation.Column.Name == "priority");
        var backfill = Assert.Single(operations.OfType<BackfillColumnOperation>(), operation => operation.Column.Name == "priority");
        var finalize = Assert.Single(operations.OfType<FinalizeColumnOperation>(), operation => operation.Column.Name == "priority");
        var index = Assert.Single(operations.OfType<CreatePhysicalIndexOperation>(), operation => operation.Index.Name == "by-priority");

        Assert.True(Array.IndexOf(operations, add) < Array.IndexOf(operations, backfill));
        Assert.True(Array.IndexOf(operations, backfill) < Array.IndexOf(operations, finalize));
        Assert.True(Array.IndexOf(operations, finalize) < Array.IndexOf(operations, index));
    }

    [Fact]
    public void Plan_apply_replan_is_fingerprint_stable_and_idempotent()
    {
        var target = CreateTarget(CreateUnit(includePriority: false));
        var executor = new FakeExecutor();

        var first = PhysicalSchemaApplication.Apply(target, executor, PlannedAt.AddMinutes(1));
        var restart = PhysicalSchemaDiffPlanner.Plan(
            target,
            PhysicalSchemaHistoryState.FromApplied(executor.AppliedState!),
            PlannedAt.AddMinutes(2));

        Assert.Equal(target.Fingerprint, first.AppliedState!.TargetFingerprint);
        Assert.Empty(restart.Operations);
        Assert.Equal(first.AppliedState.TargetFingerprint, restart.Target.Fingerprint);
        Assert.Equal(
            first.Plan.Operations
                .Where(operation => operation.Kind != PhysicalSchemaOperationKind.PublishAppliedState)
                .Select(operation => (operation.Identity, operation.Fingerprint)),
            executor.Acknowledgements
                .Where(acknowledgement => first.Plan.Operations.Any(operation => operation.Identity == acknowledgement.Identity))
            .Select(acknowledgement => (acknowledgement.Identity, acknowledgement.Fingerprint)));
    }

    [Fact]
    public void Adding_a_column_is_the_only_new_semantic_work()
    {
        var initial = CreateTarget(CreateUnit(includePriority: false));
        var executor = new FakeExecutor();
        PhysicalSchemaApplication.Apply(initial, executor, PlannedAt.AddMinutes(1));

        var changed = CreateTarget(CreateUnit(includePriority: true));
        var plan = PhysicalSchemaDiffPlanner.Plan(
            changed,
            PhysicalSchemaHistoryState.FromApplied(executor.AppliedState!),
            PlannedAt.AddMinutes(2));

        Assert.True(plan.IsApplicable, string.Join("; ", plan.Refusals.Select(refusal => refusal.Message)));
        Assert.Contains(plan.Operations, operation => operation is AddColumnOperation column && column.Column.Name == "priority");
        Assert.Contains(plan.Operations, operation => operation is CreatePhysicalIndexOperation index && index.Index.Name == "by-priority");
        Assert.DoesNotContain(plan.Operations, operation => operation is CreatePrimaryStorageOperation);
    }

    [Fact]
    public void Changing_an_applied_column_is_refused_as_non_additive()
    {
        var initial = CreateTarget(CreateUnit(includePriority: false));
        var executor = new FakeExecutor();
        PhysicalSchemaApplication.Apply(initial, executor, PlannedAt.AddMinutes(1));
        var changedUnit = CreateUnit(includePriority: false) with
        {
            Columns =
            [
                new() { Name = "id", Type = PortableType.Guid, IsNullable = false },
                new() { Name = "name", Type = PortableType.String, IsNullable = false, MaxLength = 200 }
            ]
        };

        var plan = PhysicalSchemaDiffPlanner.Plan(
            CreateTarget(changedUnit),
            PhysicalSchemaHistoryState.FromApplied(executor.AppliedState!),
            PlannedAt.AddMinutes(2));

        Assert.False(plan.IsApplicable);
        Assert.Equal("GW-SCHEMA-003", Assert.Single(plan.Refusals).Code);
        Assert.Empty(plan.Operations);
    }

    [Fact]
    public void Destructive_metadata_requires_authorization_for_startup_auto_apply()
    {
        var subject = new SchemaSubject(
            CreateUnit(includePriority: false),
            new SchemaEvolutionMetadata(isDestructive: true));
        var target = new PhysicalSchemaTarget(subject, Provider);
        var executor = new FakeExecutor();

        var result = GroundworkRuntimeSchemaAdmission.InspectRuntimeAdmission(
            executor,
            target,
            new GroundworkRuntimeSchemaAdmissionOptions { AutoApplyOnStartup = true });

        Assert.False(result.IsReady);
        Assert.Equal(PhysicalSchemaApplicationOutcome.AuthorizationRequired, result.Application!.Outcome);
        Assert.Contains(result.Refusals, diagnostic => diagnostic.Code == "GW-RUNTIME-002");
        Assert.Null(executor.AppliedState);
    }

    [Fact]
    public void Explicit_authorization_can_apply_a_destructive_plan()
    {
        var target = new PhysicalSchemaTarget(
            new SchemaSubject(CreateUnit(includePriority: false), new SchemaEvolutionMetadata(isDestructive: true)),
            Provider);
        var executor = new FakeExecutor();

        var result = PhysicalSchemaApplication.Apply(
            target,
            executor,
            PlannedAt.AddMinutes(1),
            _ => PhysicalSchemaPlanAuthorization.Allow);

        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, result.Outcome);
        Assert.NotNull(executor.AppliedState);
    }

    [Fact]
    public void Runtime_admission_is_inspect_only_by_default()
    {
        var target = CreateTarget(CreateUnit(includePriority: false));
        var executor = new FakeExecutor();

        var result = GroundworkRuntimeSchemaAdmission.InspectRuntimeAdmission(executor, target);

        Assert.False(result.IsReady);
        Assert.Null(result.Application);
        Assert.NotEmpty(result.PendingOperations);
        Assert.Null(executor.AppliedState);
    }

    [Fact]
    public void Provider_owned_definitions_are_planned_and_snapshotted()
    {
        var definition = new ProviderPhysicalSchemaDefinition(
            Provider.Name,
            new StorageUnitId("customer"),
            "partial-index",
            "active-only",
            "where=isActive");
        var target = new PhysicalSchemaTarget(
            new SchemaSubject(CreateUnit(includePriority: false)),
            Provider,
            [definition]);
        var executor = new FakeExecutor();

        var result = PhysicalSchemaApplication.Apply(target, executor, PlannedAt.AddMinutes(1));

        Assert.Contains(result.Plan.Operations, operation => operation is ApplyProviderPhysicalSchemaDefinitionOperation);
        var applied = Assert.Single(result.AppliedState!.Snapshot.ProviderDefinitions);
        Assert.Equal(definition.Fingerprint, applied.Fingerprint);
        Assert.Equal(definition.CanonicalDefinition, applied.CanonicalDefinition);
    }

    [Fact]
    public void Applied_state_snapshot_is_not_aliased_to_subject_inputs()
    {
        var columns = new List<ColumnDefinition>
        {
            new() { Name = "id", Type = PortableType.Guid, IsNullable = false }
        };
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("customer"),
            Name = "Customer",
            Columns = columns,
            Key = new KeyDefinition { Columns = ["id"] }
        };
        var subject = new SchemaSubject(unit);
        var target = new PhysicalSchemaTarget(subject, Provider);

        columns.Add(new ColumnDefinition { Name = "mutated", Type = PortableType.String });
        var executor = new FakeExecutor();
        PhysicalSchemaApplication.Apply(target, executor, PlannedAt.AddMinutes(1));

        Assert.Single(executor.AppliedState!.Snapshot.Subject.Columns);
        Assert.DoesNotContain(executor.AppliedState.Snapshot.Subject.Columns, column => column.Name == "mutated");
    }

    [Fact]
    public void Applied_state_serialization_round_trips_the_subject_and_ledger()
    {
        var definition = new ProviderPhysicalSchemaDefinition(
            Provider.Name,
            new StorageUnitId("customer"),
            "partial-index",
            "active-only",
            "where=isActive");
        var target = new PhysicalSchemaTarget(
            new SchemaSubject(CreateUnit(includePriority: true)),
            Provider,
            [definition]);
        var executor = new FakeExecutor();
        var applied = PhysicalSchemaApplication.Apply(target, executor, PlannedAt.AddMinutes(1)).AppliedState!;

        var json = PhysicalSchemaAppliedStateSerializer.Serialize(applied);
        var restored = PhysicalSchemaAppliedStateSerializer.Deserialize(json);

        Assert.Equal(applied.TargetFingerprint, restored.TargetFingerprint);
        Assert.Equal(applied.Snapshot.Subject.Fingerprint, restored.Snapshot.Subject.Fingerprint);
        Assert.Equal(definition.Fingerprint, Assert.Single(restored.Snapshot.ProviderDefinitions).Fingerprint);
        Assert.Equal(
            applied.Snapshot.SemanticOperations.Select(operation => (operation.Identity, operation.CanonicalPayload)),
            restored.Snapshot.SemanticOperations.Select(operation => (operation.Identity, operation.CanonicalPayload)));
        Assert.Equal(
            applied.AppliedOperations.Select(operation => (operation.Identity, operation.CanonicalPayload, operation.AppliedAt)),
            restored.AppliedOperations.Select(operation => (operation.Identity, operation.CanonicalPayload, operation.AppliedAt)));
        Assert.Equal(json, PhysicalSchemaAppliedStateSerializer.Serialize(restored));
    }

    private static PhysicalSchemaTarget CreateTarget(StorageUnit unit) =>
        new(new SchemaSubject(unit), Provider);

    private static StorageUnit CreateUnit(bool includePriority) => new()
    {
        Id = new StorageUnitId("customer"),
        Name = "Customer",
        Columns =
        [
            new() { Name = "id", Type = PortableType.Guid, IsNullable = false },
            new() { Name = "name", Type = PortableType.String, IsNullable = false, MaxLength = 100 },
            ..(includePriority
                ? new[] { new ColumnDefinition { Name = "priority", Type = PortableType.Int32, IsNullable = false } }
                : Array.Empty<ColumnDefinition>())
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        Indexes = includePriority
            ? [new IndexDefinition { Name = "by-priority", Columns = [new IndexColumn("priority")] }]
            : []
    };

    private sealed class FakeExecutor : IPhysicalSchemaExecutor, IPhysicalSchemaHistoryInspector
    {
        private readonly Dictionary<string, PhysicalSchemaOperationAcknowledgement> durable = new(StringComparer.Ordinal);

        public PhysicalSchemaAppliedState? AppliedState { get; private set; }

        public List<PhysicalSchemaOperationAcknowledgement> Acknowledgements { get; } = [];

        public IPhysicalSchemaApplicationLock AcquireApplicationLock(PhysicalSchemaTargetIdentity target) =>
            new Lock(target);

        public PhysicalSchemaHistoryState ReadHistory(
            PhysicalSchemaTargetIdentity target,
            IPhysicalSchemaApplicationLock applicationLock) =>
            AppliedState is null ? PhysicalSchemaHistoryState.Empty : PhysicalSchemaHistoryState.FromApplied(AppliedState);

        public PhysicalSchemaInspectionResult InspectHistory(PhysicalSchemaTarget target) =>
            new(AppliedState is null ? PhysicalSchemaHistoryState.Empty : PhysicalSchemaHistoryState.FromApplied(AppliedState), true);

        public PhysicalSchemaOperationAcknowledgement ApplyOperation(
            PhysicalSchemaTargetIdentity target,
            PhysicalSchemaOperation operation,
            IPhysicalSchemaApplicationLock applicationLock)
        {
            if (durable.TryGetValue(operation.Identity, out var existing))
            {
                if (!string.Equals(existing.Fingerprint, operation.Fingerprint, StringComparison.Ordinal))
                    throw new PhysicalSchemaFingerprintConflictException(operation.Identity, operation.Fingerprint, existing.Fingerprint);
                Acknowledgements.Add(existing);
                return existing;
            }

            var acknowledgement = new PhysicalSchemaOperationAcknowledgement(
                operation.Identity,
                operation.Fingerprint,
                PlannedAt.AddMinutes(Acknowledgements.Count + 1));
            durable.Add(operation.Identity, acknowledgement);
            Acknowledgements.Add(acknowledgement);
            return acknowledgement;
        }

        public void PublishAppliedState(
            PhysicalSchemaAppliedState state,
            string? expectedAppliedTargetFingerprint,
            IPhysicalSchemaApplicationLock applicationLock)
        {
            if (AppliedState is not null && !string.Equals(AppliedState.TargetFingerprint, expectedAppliedTargetFingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException("CAS conflict.");
            AppliedState = state;
        }

        private sealed class Lock(PhysicalSchemaTargetIdentity target) : IPhysicalSchemaApplicationLock
        {
            public PhysicalSchemaTargetIdentity Target { get; } = target;

            public void Dispose()
            {
            }
        }
    }
}
