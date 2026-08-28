using System.Collections.Immutable;
using Groundwork.Kernel.Schema;
using Xunit;

namespace Groundwork.Kernel.Tests;

/// <summary>
/// Adoption records that a catalog Groundwork never applied is exactly what applying the compiled
/// target would have produced. The claim it writes is the dangerous part, so most of what is
/// asserted here is that the published row is the same value a real apply publishes, and that
/// nothing is published at all when the proof fails.
/// </summary>
public sealed class SchemaAdoptionTests
{
    private static readonly DateTimeOffset AdoptedAt = new(2026, 8, 27, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_adopted_row_is_the_row_a_real_apply_would_have_published()
    {
        var target = Target();

        var applier = new FakeExecutor();
        var applied = PhysicalSchemaApplication.Apply(target, applier, AdoptedAt).AppliedState!;

        var adopter = new FakeExecutor();
        var adoption = PhysicalSchemaAdoption.Adopt(target, adopter, AdoptedAt);

        Assert.Equal(PhysicalSchemaAdoptionOutcome.Adopted, adoption.Outcome);
        var adopted = adoption.AppliedState!;
        Assert.Same(adopted, adopter.AppliedState);

        // The two rows are the same value: same target, same snapshot, same ledger, operation for
        // operation. Anything less and the next diff against this target would disagree with itself
        // depending on how the catalog came to exist.
        Assert.Equal(applied.TargetFingerprint, adopted.TargetFingerprint);
        Assert.Equal(applied.Snapshot.Fingerprint, adopted.Snapshot.Fingerprint);
        Assert.Equal(applied.Snapshot.CanonicalPayload, adopted.Snapshot.CanonicalPayload);
        Assert.Equal(Ledger(applied), Ledger(adopted));

        // Pinned rather than derived: the ledger records the create, every column, the index, the
        // validation and the publication, under the identities the planner assigns them.
        Assert.Equal(
            new[]
            {
                "AddColumn:id",
                "AddColumn:total",
                "BackfillColumn:id",
                "CreatePhysicalIndex:by_total",
                "CreatePrimaryStorage:orders",
                "FinalizeColumn:id",
                "PublishAppliedState:target",
                "ValidatePhysicalSchema:target"
            },
            adopted.AppliedOperations
                .Select(operation => $"{operation.Kind}:{operation.SubjectIdentity}")
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(AdoptedAt, adopted.AppliedAt);

        // And the point of all of it: planning the same target again finds nothing left to do.
        var replan = PhysicalSchemaDiffPlanner.Plan(
            target, PhysicalSchemaHistoryState.FromApplied(adopted), AdoptedAt);
        Assert.True(replan.IsApplicable);
        Assert.Empty(replan.Operations);
    }

    [Fact]
    public void A_catalog_that_differs_is_refused_by_name_and_publishes_nothing()
    {
        var executor = new FakeExecutor
        {
            CatalogDrift =
            [
                new SchemaRefusal(
                    "GW-RUNTIME-001",
                    "Relational schema column 'orders.total' differs: nullability True != False.",
                    "columns.total")
            ]
        };

        var adoption = PhysicalSchemaAdoption.Adopt(Target(), executor, AdoptedAt);

        Assert.Equal(PhysicalSchemaAdoptionOutcome.Refused, adoption.Outcome);
        Assert.Null(adoption.AppliedState);
        Assert.Null(executor.AppliedState);
        var refusal = Assert.Single(adoption.Refusals);
        Assert.Equal("GW-RUNTIME-001", refusal.Code);
        Assert.Contains("nullability True != False", refusal.Message, StringComparison.Ordinal);
        Assert.Equal("columns.total", refusal.Path);
    }

    [Fact]
    public void Index_drift_refuses_adoption_even_though_it_only_degrades_a_running_host()
    {
        var executor = new FakeExecutor
        {
            CatalogIndexDrift =
            [
                new SchemaRefusal("GW-RUNTIME-002", "Index 'by_total' does not match its declaration.", "indexes.by_total")
            ]
        };

        var adoption = PhysicalSchemaAdoption.Adopt(Target(), executor, AdoptedAt);

        Assert.Equal(PhysicalSchemaAdoptionOutcome.Refused, adoption.Outcome);
        Assert.Null(executor.AppliedState);
        Assert.Equal("GW-RUNTIME-002", Assert.Single(adoption.Refusals).Code);
    }

    [Fact]
    public void A_catalog_the_provider_calls_invalid_without_naming_anything_is_still_refused()
    {
        var executor = new FakeExecutor { CatalogIsValid = false };

        var adoption = PhysicalSchemaAdoption.Adopt(Target(), executor, AdoptedAt);

        Assert.Equal(PhysicalSchemaAdoptionOutcome.Refused, adoption.Outcome);
        Assert.Null(executor.AppliedState);
        Assert.Equal("GW-SCHEMA-013", Assert.Single(adoption.Refusals).Code);
    }

    [Fact]
    public void A_target_that_already_has_history_is_refused_rather_than_overwritten()
    {
        var executor = new FakeExecutor();
        PhysicalSchemaApplication.Apply(Target(), executor, AdoptedAt);
        var published = executor.AppliedState;

        // A second declaration of the same subject, one column wider, so history exists and the
        // plan is not empty — the case where overwriting would replace evidence with a claim.
        var adoption = PhysicalSchemaAdoption.Adopt(Target(extraColumn: true), executor, AdoptedAt);

        Assert.Equal(PhysicalSchemaAdoptionOutcome.Refused, adoption.Outcome);
        Assert.Same(published, executor.AppliedState);
        var refusal = Assert.Single(adoption.Refusals);
        Assert.Equal("GW-SCHEMA-011", refusal.Code);
        Assert.Contains(published!.TargetFingerprint, refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Adopting_a_target_history_already_records_exactly_reports_it_and_republishes_nothing()
    {
        var target = Target();
        var executor = new FakeExecutor();
        PhysicalSchemaApplication.Apply(target, executor, AdoptedAt);
        var published = executor.AppliedState;

        var adoption = PhysicalSchemaAdoption.Adopt(target, executor, AdoptedAt);

        Assert.Equal(PhysicalSchemaAdoptionOutcome.AlreadyAdopted, adoption.Outcome);
        Assert.Same(published, adoption.AppliedState);
        Assert.Same(published, executor.AppliedState);
        Assert.Empty(adoption.Refusals);
        Assert.Equal(0, executor.CatalogInspections);
    }

    [Fact]
    public void A_retired_subject_has_no_catalog_to_verify_so_it_cannot_be_adopted()
    {
        var target = new PhysicalSchemaTarget(
            new SchemaSubject(Orders(false), new SchemaEvolutionMetadata(retiresPrimaryStorage: true)),
            new ProviderIdentity("Fake", "1.0"));
        var executor = new FakeExecutor();

        var adoption = PhysicalSchemaAdoption.Adopt(target, executor, AdoptedAt);

        Assert.Equal(PhysicalSchemaAdoptionOutcome.Refused, adoption.Outcome);
        Assert.Null(executor.AppliedState);
        Assert.Equal(0, executor.CatalogInspections);
        Assert.Equal("GW-SCHEMA-012", Assert.Single(adoption.Refusals).Code);
    }

    [Fact]
    public void Legacy_history_still_refuses_rather_than_being_adopted_over()
    {
        var executor = new FakeExecutor { Legacy = true };

        var adoption = PhysicalSchemaAdoption.Adopt(Target(), executor, AdoptedAt);

        Assert.Equal(PhysicalSchemaAdoptionOutcome.Refused, adoption.Outcome);
        Assert.Null(executor.AppliedState);
        Assert.Equal(0, executor.CatalogInspections);
        Assert.Equal("GW-SCHEMA-001", Assert.Single(adoption.Refusals).Code);
    }

    [Fact]
    public void An_unauthorized_adoption_inspects_nothing_and_publishes_nothing()
    {
        var executor = new FakeExecutor();

        var adoption = PhysicalSchemaAdoption.Adopt(
            Target(),
            executor,
            AdoptedAt,
            planAuthorization: _ => PhysicalSchemaPlanAuthorization.Deny(
                [new SchemaRefusal("GW-CLI-007", "Schema changes require explicit --safe authorization.", "authorization.safe")]));

        Assert.Equal(PhysicalSchemaAdoptionOutcome.AuthorizationRequired, adoption.Outcome);
        Assert.Null(executor.AppliedState);
        Assert.Equal(0, executor.CatalogInspections);
        Assert.Equal("GW-CLI-007", Assert.Single(adoption.Refusals).Code);
    }

    [Fact]
    public void The_catalog_is_proved_under_the_same_lock_that_publishes_the_claim()
    {
        var executor = new FakeExecutor();

        PhysicalSchemaAdoption.Adopt(Target(), executor, AdoptedAt);

        Assert.NotNull(executor.InspectedUnder);
        Assert.Same(executor.PublishedUnder, executor.InspectedUnder);
    }

    [Fact]
    public void A_provider_that_cannot_compare_a_catalog_to_a_target_cannot_be_adopted_through()
    {
        var executor = new HistoryOnlyExecutor();

        Assert.Throws<ArgumentException>(() => PhysicalSchemaAdoption.Adopt(Target(), executor, AdoptedAt));
    }

    /// <summary>
    /// One string per ledger row carrying every field that is not a timestamp, joined into one
    /// value so the comparison is an ordinary string equality. Comparing the two
    /// <see cref="ImmutableArray{T}"/> values directly binds xUnit's <c>Equal&lt;T&gt;(T, T)</c>
    /// overload, which compares the underlying array by reference and passes on anything.
    /// </summary>
    private static string Ledger(PhysicalSchemaAppliedState state) => string.Join(
        Environment.NewLine,
        state.AppliedOperations
            .Select(operation =>
                $"{operation.Identity}|{operation.Fingerprint}|{operation.Kind}|{operation.SubjectIdentity}|" +
                $"{operation.SlotIdentity}|{operation.CanonicalPayload}")
            .Order(StringComparer.Ordinal));

    private static PhysicalSchemaTarget Target(bool extraColumn = false) => new(
        new SchemaSubject(Orders(extraColumn)),
        new ProviderIdentity("Fake", "1.0"));

    private static StorageUnit Orders(bool extraColumn) => new()
    {
        Id = new StorageUnitId("orders"),
        Name = "orders",
        Columns =
        [
            new() { Name = "id", Type = PortableType.String, MaxLength = 64, IsNullable = false },
            new() { Name = "total", Type = PortableType.Decimal, Precision = 18, Scale = 4 },
            .. extraColumn
                ? new[] { new ColumnDefinition { Name = "note", Type = PortableType.String, MaxLength = 128 } }
                : []
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        Indexes = [new IndexDefinition { Name = "by_total", Columns = [new IndexColumn("total")] }]
    };

    private sealed class HistoryOnlyExecutor : IPhysicalSchemaExecutor
    {
        public IPhysicalSchemaApplicationLock AcquireApplicationLock(PhysicalSchemaTargetIdentity target) =>
            throw new InvalidOperationException("The lock must not be taken before the provider is rejected.");

        public PhysicalSchemaHistoryState ReadHistory(
            PhysicalSchemaTargetIdentity target,
            IPhysicalSchemaApplicationLock applicationLock) => PhysicalSchemaHistoryState.Empty;

        public PhysicalSchemaOperationAcknowledgement ApplyOperation(
            PhysicalSchemaTargetIdentity target,
            PhysicalSchemaOperation operation,
            IPhysicalSchemaApplicationLock applicationLock) => throw new NotSupportedException();

        public void PublishAppliedState(
            PhysicalSchemaAppliedState state,
            string? expectedAppliedTargetFingerprint,
            IPhysicalSchemaApplicationLock applicationLock) => throw new NotSupportedException();
    }

    private sealed class FakeExecutor : IPhysicalSchemaExecutor, IPhysicalSchemaCatalogInspector
    {
        public PhysicalSchemaAppliedState? AppliedState { get; private set; }

        public bool Legacy { get; init; }

        public bool CatalogIsValid { get; init; } = true;

        public ImmutableArray<SchemaRefusal> CatalogDrift { get; init; } = [];

        public ImmutableArray<SchemaRefusal> CatalogIndexDrift { get; init; } = [];

        public int CatalogInspections { get; private set; }

        public IPhysicalSchemaApplicationLock? InspectedUnder { get; private set; }

        public IPhysicalSchemaApplicationLock? PublishedUnder { get; private set; }

        public IPhysicalSchemaApplicationLock AcquireApplicationLock(PhysicalSchemaTargetIdentity target) =>
            new Lease(target);

        public PhysicalSchemaHistoryState ReadHistory(
            PhysicalSchemaTargetIdentity target,
            IPhysicalSchemaApplicationLock applicationLock) =>
            Legacy
                ? PhysicalSchemaHistoryState.LegacyHistoryDetected
                : AppliedState is null
                    ? PhysicalSchemaHistoryState.Empty
                    : PhysicalSchemaHistoryState.FromApplied(AppliedState);

        public PhysicalSchemaInspectionResult InspectDeployedCatalog(
            PhysicalSchemaTarget target,
            IPhysicalSchemaApplicationLock applicationLock)
        {
            CatalogInspections++;
            InspectedUnder = applicationLock;
            return new PhysicalSchemaInspectionResult(
                PhysicalSchemaHistoryState.Empty,
                CatalogIsValid && CatalogDrift.Length == 0,
                CatalogDrift,
                CatalogIndexDrift);
        }

        public PhysicalSchemaOperationAcknowledgement ApplyOperation(
            PhysicalSchemaTargetIdentity target,
            PhysicalSchemaOperation operation,
            IPhysicalSchemaApplicationLock applicationLock) =>
            new(operation.Identity, operation.Fingerprint, AdoptedAt);

        public void PublishAppliedState(
            PhysicalSchemaAppliedState state,
            string? expectedAppliedTargetFingerprint,
            IPhysicalSchemaApplicationLock applicationLock)
        {
            if (!string.Equals(AppliedState?.TargetFingerprint, expectedAppliedTargetFingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException("CAS conflict.");
            PublishedUnder = applicationLock;
            AppliedState = state;
        }

        private sealed class Lease(PhysicalSchemaTargetIdentity target) : IPhysicalSchemaApplicationLock
        {
            public PhysicalSchemaTargetIdentity Target { get; } = target;

            public void Dispose()
            {
            }
        }
    }
}
