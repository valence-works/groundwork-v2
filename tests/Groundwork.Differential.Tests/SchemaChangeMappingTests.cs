using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Store;
using Xunit;

namespace Groundwork.Differential.Tests;

/// <summary>
/// The public <see cref="SchemaDiff"/> vocabulary is what the hosting startup gate reads to decide
/// whether pending work is additive enough to apply unattended. A removal that describes itself as
/// an addition is therefore not a cosmetic reporting bug.
/// </summary>
public sealed class SchemaChangeMappingTests
{
    private static readonly ProviderIdentity Provider = new("test-provider", "1.0");

    [Fact]
    public void Every_evolution_operation_describes_itself_as_what_it_does()
    {
        var executor = new RecordingExecutor();
        PhysicalSchemaApplication.Apply(Target(Orders(includeLegacy: true)), executor, DateTimeOffset.UnixEpoch);

        var plan = PhysicalSchemaDiffPlanner.Plan(
            Target(Orders(includeLegacy: false) with { Name = "purchase_orders", Indexes = [] }),
            PhysicalSchemaHistoryState.FromApplied(executor.AppliedState!),
            DateTimeOffset.UnixEpoch);
        var kinds = SchemaChangeMapping.Describe(plan.Operations).Select(change => change.Kind).ToArray();

        Assert.Contains(SchemaChangeKind.RenameStorageUnit, kinds);
        Assert.Contains(SchemaChangeKind.DropColumn, kinds);
        Assert.Contains(SchemaChangeKind.DropIndex, kinds);
        // The bucket every one of these silently fell into before the mapping was made total.
        Assert.DoesNotContain(SchemaChangeKind.AddDerivedColumn, kinds);
        Assert.DoesNotContain(SchemaChangeKind.AddColumn, kinds);
    }

    [Fact]
    public void A_retirement_describes_itself_as_dropping_the_storage_unit()
    {
        var executor = new RecordingExecutor();
        PhysicalSchemaApplication.Apply(Target(Orders(includeLegacy: false)), executor, DateTimeOffset.UnixEpoch);
        var retired = new PhysicalSchemaTarget(
            new SchemaSubject(Orders(includeLegacy: false), new SchemaEvolutionMetadata(retiresPrimaryStorage: true)),
            Provider);

        var plan = PhysicalSchemaDiffPlanner.Plan(
            retired,
            PhysicalSchemaHistoryState.FromApplied(executor.AppliedState!),
            DateTimeOffset.UnixEpoch);

        Assert.Equal(
            SchemaChangeKind.DropStorageUnit,
            Assert.Single(SchemaChangeMapping.Describe(plan.Operations)).Kind);
    }

    private static PhysicalSchemaTarget Target(StorageUnit unit) => new(new SchemaSubject(unit), Provider);

    private static StorageUnit Orders(bool includeLegacy) => new()
    {
        Id = new StorageUnitId("orders"),
        Name = "orders",
        Columns =
        [
            new() { Name = "id", Type = PortableType.String, MaxLength = 64, IsNullable = false },
            new() { Name = "customer", Type = PortableType.String, MaxLength = 64 },
            ..(includeLegacy
                ? new[] { new ColumnDefinition { Name = "legacy_total", Type = PortableType.Decimal, Precision = 18, Scale = 4 } }
                : [])
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        Indexes = includeLegacy
            ? [new IndexDefinition { Name = "by_customer", Columns = [new IndexColumn("customer")] }]
            : []
    };

    private sealed class RecordingExecutor : IPhysicalSchemaExecutor
    {
        public PhysicalSchemaAppliedState? AppliedState { get; private set; }

        public IPhysicalSchemaApplicationLock AcquireApplicationLock(PhysicalSchemaTargetIdentity target) =>
            new Lease(target);

        public PhysicalSchemaHistoryState ReadHistory(
            PhysicalSchemaTargetIdentity target,
            IPhysicalSchemaApplicationLock applicationLock) =>
            AppliedState is null
                ? PhysicalSchemaHistoryState.Empty
                : PhysicalSchemaHistoryState.FromApplied(AppliedState);

        public PhysicalSchemaOperationAcknowledgement ApplyOperation(
            PhysicalSchemaTargetIdentity target,
            PhysicalSchemaOperation operation,
            IPhysicalSchemaApplicationLock applicationLock) =>
            new(operation.Identity, operation.Fingerprint, DateTimeOffset.UnixEpoch);

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
