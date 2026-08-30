using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Xunit;

namespace Groundwork.Kernel.Tests;

public sealed class InteropViewSchemaContractTests
{
    private static readonly ProviderIdentity Provider = new("test-provider", "1.0");
    private static readonly DateTimeOffset PlannedAt = new(2026, 8, 15, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Interop_view_creation_is_protected_in_the_kernel_plan()
    {
        var target = Target(Unit(withView: true), "v1");
        var plan = PhysicalSchemaDiffPlanner.Plan(target, PhysicalSchemaHistoryState.Empty, PlannedAt);

        var apply = Assert.Single(plan.Operations.OfType<ApplyProviderPhysicalSchemaDefinitionOperation>());
        Assert.True(apply.RequiresAuthorization);

        var protection = PhysicalSchemaPlanProtection.Inspect(plan.Operations);
        var protectedOperation = Assert.Single(
            protection.DestructiveOperations,
            operation => operation.Identity == apply.Identity);
        Assert.Equal(apply.AuthorizationAddress, protectedOperation.Address);
    }

    [Fact]
    public void Runtime_auto_apply_refuses_an_interop_view_without_authorization()
    {
        var executor = new RecordingExecutor();
        var result = GroundworkRuntimeSchemaAdmission.InspectRuntimeAdmission(
            executor,
            Target(Unit(withView: true), "v1"),
            new GroundworkRuntimeSchemaAdmissionOptions { AutoApplyOnStartup = true });

        Assert.False(result.IsReady);
        Assert.Equal(GroundworkRuntimeSchemaAdmissionStatus.Blocked, result.Status);
        Assert.Equal(PhysicalSchemaApplicationOutcome.AuthorizationRequired, result.Application!.Outcome);
        Assert.Contains(result.Refusals, refusal => refusal.Code == "GW-SCHEMA-007");
        Assert.Null(executor.AppliedState);
        Assert.Empty(executor.AppliedOperations);
    }

    [Fact]
    public void Explicit_authorization_applies_an_interop_view_and_records_its_definition()
    {
        var executor = new RecordingExecutor();
        var target = Target(Unit(withView: true), "v1");

        var result = PhysicalSchemaApplication.Apply(
            target,
            executor,
            PlannedAt,
            _ => PhysicalSchemaPlanAuthorization.Allow);

        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, result.Outcome);
        Assert.Equal("v1", Assert.Single(executor.AppliedState!.Snapshot.ProviderDefinitions).CanonicalDefinition);
        Assert.Contains(
            executor.AppliedOperations,
            operation => operation is ApplyProviderPhysicalSchemaDefinitionOperation);
    }

    [Fact]
    public void Removing_an_applied_interop_view_plans_an_authorized_drop()
    {
        var executor = new RecordingExecutor();
        PhysicalSchemaApplication.Apply(
            Target(Unit(withView: true), "v1"),
            executor,
            PlannedAt,
            _ => PhysicalSchemaPlanAuthorization.Allow);

        var target = Target(Unit(withView: false));
        var plan = PhysicalSchemaDiffPlanner.Plan(
            target,
            PhysicalSchemaHistoryState.FromApplied(executor.AppliedState!),
            PlannedAt.AddMinutes(1));

        var drop = Assert.Single(plan.Operations.OfType<DropProviderPhysicalSchemaDefinitionOperation>());
        Assert.True(drop.RequiresAuthorization);
        Assert.Equal("v1", drop.Definition.CanonicalDefinition);

        var result = PhysicalSchemaApplication.Apply(
            target,
            executor,
            PlannedAt.AddMinutes(2),
            _ => PhysicalSchemaPlanAuthorization.Allow);

        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, result.Outcome);
        Assert.Empty(executor.AppliedState!.Snapshot.ProviderDefinitions);
        Assert.Contains(executor.AppliedOperations, operation => operation is DropProviderPhysicalSchemaDefinitionOperation);
    }

    [Fact]
    public void Replacing_an_interop_view_drops_the_old_definition_before_applying_the_new_one()
    {
        var executor = new RecordingExecutor();
        PhysicalSchemaApplication.Apply(
            Target(Unit(withView: true), "v1"),
            executor,
            PlannedAt,
            _ => PhysicalSchemaPlanAuthorization.Allow);
        executor.AppliedOperations.Clear();

        var renamed = Unit(withView: true) with
        {
            Columns =
            [
                new() { Name = "id", Type = PortableType.Guid, IsNullable = false },
                new()
                {
                    Name = "amount",
                    Id = "total",
                    Type = PortableType.Decimal,
                    Precision = 18,
                    Scale = 4
                }
            ]
        };
        var target = Target(renamed, "v2");
        var plan = PhysicalSchemaDiffPlanner.Plan(
            target,
            PhysicalSchemaHistoryState.FromApplied(executor.AppliedState!),
            PlannedAt.AddMinutes(1));
        var operations = plan.Operations.ToArray();
        var drop = Assert.Single(operations.OfType<DropProviderPhysicalSchemaDefinitionOperation>());
        var apply = Assert.Single(operations.OfType<ApplyProviderPhysicalSchemaDefinitionOperation>());
        var rename = Assert.Single(operations.OfType<RenameColumnOperation>());

        Assert.True(drop.RequiresAuthorization);
        Assert.True(apply.RequiresAuthorization);
        Assert.True(Array.IndexOf(operations, drop) < Array.IndexOf(operations, rename));
        Assert.True(Array.IndexOf(operations, rename) < Array.IndexOf(operations, apply));

        var result = PhysicalSchemaApplication.Apply(
            target,
            executor,
            PlannedAt.AddMinutes(2),
            _ => PhysicalSchemaPlanAuthorization.Allow);

        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, result.Outcome);
        Assert.Equal("v2", Assert.Single(executor.AppliedState!.Snapshot.ProviderDefinitions).CanonicalDefinition);
    }

    [Fact]
    public void Applied_state_omits_an_absent_view_and_round_trips_a_declared_view()
    {
        var withoutView = new RecordingExecutor();
        PhysicalSchemaApplication.Apply(Target(Unit(withView: false)), withoutView, PlannedAt);
        var legacyCompatible = PhysicalSchemaAppliedStateSerializer.Serialize(withoutView.AppliedState!);
        Assert.DoesNotContain("interopView", legacyCompatible, StringComparison.Ordinal);

        var withView = new RecordingExecutor();
        PhysicalSchemaApplication.Apply(
            Target(Unit(withView: true), "v1"),
            withView,
            PlannedAt,
            _ => PhysicalSchemaPlanAuthorization.Allow);
        var json = PhysicalSchemaAppliedStateSerializer.Serialize(withView.AppliedState!);
        var restored = PhysicalSchemaAppliedStateSerializer.Deserialize(json);

        Assert.Contains("\"interopView\"", json, StringComparison.Ordinal);
        Assert.Equal("reporting_orders", restored.Snapshot.Subject.Definition.InteropView!.Name);
        Assert.Equal(json, PhysicalSchemaAppliedStateSerializer.Serialize(restored));
    }

    [Fact]
    public void Interop_view_removal_does_not_widen_removal_support_for_other_provider_definitions()
    {
        var unit = Unit(withView: false);
        var providerDefinition = new ProviderPhysicalSchemaDefinition(
            Provider.Name,
            unit.Id,
            "search-key-algorithm",
            "name_search_key",
            "algorithm-v1");
        var executor = new RecordingExecutor();
        PhysicalSchemaApplication.Apply(
            new PhysicalSchemaTarget(new SchemaSubject(unit), Provider, [providerDefinition]),
            executor,
            PlannedAt);

        var plan = PhysicalSchemaDiffPlanner.Plan(
            Target(unit),
            PhysicalSchemaHistoryState.FromApplied(executor.AppliedState!),
            PlannedAt.AddMinutes(1));

        Assert.False(plan.IsApplicable);
        Assert.Contains(plan.Refusals, refusal => refusal.Code == "GW-SCHEMA-004");
        Assert.DoesNotContain(plan.Operations, operation => operation is DropProviderPhysicalSchemaDefinitionOperation);
    }

    [Fact]
    public void Manifest_refuses_view_names_that_collide_with_another_view_or_table()
    {
        var orders = Unit(withView: true);
        var invoices = Unit(withView: true) with
        {
            Id = new StorageUnitId("invoices"),
            Name = "invoices"
        };
        var reportingTable = Unit(withView: false) with
        {
            Id = new StorageUnitId("reporting-table"),
            Name = "reporting_orders"
        };

        Assert.Contains(
            "collides",
            Assert.Throws<ArgumentException>(() => SchemaSubject.ValidateManifest([orders, invoices])).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "collides",
            Assert.Throws<ArgumentException>(() => SchemaSubject.ValidateManifest([orders, reportingTable])).Message,
            StringComparison.Ordinal);
    }

    private static PhysicalSchemaTarget Target(StorageUnit unit, string? definition = null) =>
        new(
            new SchemaSubject(unit),
            Provider,
            definition is null
                ? []
                : [new ProviderPhysicalSchemaDefinition(
                    Provider.Name,
                    unit.Id,
                    ProviderPhysicalSchemaDefinitionKinds.InteropView,
                    unit.InteropView!.Name,
                    definition)]);

    private static StorageUnit Unit(bool withView) => new()
    {
        Id = new StorageUnitId("orders"),
        Name = "orders",
        Columns =
        [
            new() { Name = "id", Type = PortableType.Guid, IsNullable = false },
            new() { Name = "total", Type = PortableType.Decimal, Precision = 18, Scale = 4 }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        InteropView = withView ? new InteropViewDeclaration("reporting_orders") : null
    };

    private sealed class RecordingExecutor : IPhysicalSchemaExecutor, IPhysicalSchemaHistoryInspector
    {
        public PhysicalSchemaAppliedState? AppliedState { get; private set; }

        public List<PhysicalSchemaOperation> AppliedOperations { get; } = [];

        public IPhysicalSchemaApplicationLock AcquireApplicationLock(PhysicalSchemaTargetIdentity target) =>
            new Lease(target);

        public PhysicalSchemaHistoryState ReadHistory(
            PhysicalSchemaTargetIdentity target,
            IPhysicalSchemaApplicationLock applicationLock) =>
            AppliedState is null
                ? PhysicalSchemaHistoryState.Empty
                : PhysicalSchemaHistoryState.FromApplied(AppliedState);

        public PhysicalSchemaInspectionResult InspectHistory(PhysicalSchemaTarget target) =>
            new(
                AppliedState is null
                    ? PhysicalSchemaHistoryState.Empty
                    : PhysicalSchemaHistoryState.FromApplied(AppliedState),
                IsAppliedSchemaValid: true);

        public PhysicalSchemaOperationAcknowledgement ApplyOperation(
            PhysicalSchemaTargetIdentity target,
            PhysicalSchemaOperation operation,
            IPhysicalSchemaApplicationLock applicationLock)
        {
            AppliedOperations.Add(operation);
            return new(operation.Identity, operation.Fingerprint, PlannedAt);
        }

        public void PublishAppliedState(
            PhysicalSchemaAppliedState state,
            string? expectedAppliedTargetFingerprint,
            IPhysicalSchemaApplicationLock applicationLock) => AppliedState = state;

        private sealed class Lease(PhysicalSchemaTargetIdentity target) : IPhysicalSchemaApplicationLock
        {
            public PhysicalSchemaTargetIdentity Target { get; } = target;

            public void Dispose()
            {
            }
        }
    }
}
