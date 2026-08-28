using Groundwork.Kernel.Schema;
using Xunit;

namespace Groundwork.Kernel.Tests;

/// <summary>
/// The one decision about deployed columns a declaration does not describe. Every provider reports
/// the same facts into it, so what a foreign column means cannot drift between them, and the
/// opt-in cannot quietly become "ignore drift".
/// </summary>
public sealed class ForeignColumnAdmissionTests
{
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void Under_the_default_policy_every_foreign_column_is_drift(
        bool nullable,
        bool defaulted,
        bool generated)
    {
        var verdict = ForeignColumnAdmission.Classify(
            "orders",
            ForeignColumnPolicy.Refuse,
            [new ForeignPhysicalColumn("audit_id", nullable, defaulted, generated)]);

        Assert.Empty(verdict.Tolerated);
        var refusal = Assert.Single(verdict.Drift);
        Assert.Equal("GW-RUNTIME-001", refusal.Code);
        Assert.Equal("columns.audit_id", refusal.Path);
        Assert.Contains("'orders.audit_id' is not declared by this schema", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void The_opt_in_tolerates_only_a_foreign_column_the_database_fills_in(
        bool nullable,
        bool defaulted,
        bool generated)
    {
        var verdict = ForeignColumnAdmission.Classify(
            "orders",
            ForeignColumnPolicy.TolerateDatabaseSupplied,
            [new ForeignPhysicalColumn("audit_id", nullable, defaulted, generated)]);

        Assert.Empty(verdict.Drift);
        var warning = Assert.Single(verdict.Tolerated);
        Assert.Equal("GW-RUNTIME-003", warning.Code);
        Assert.Equal("columns.audit_id", warning.Path);
        Assert.Contains("Groundwork neither reads nor writes it", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_foreign_column_no_write_could_omit_stays_fatal_under_the_opt_in()
    {
        var verdict = ForeignColumnAdmission.Classify(
            "orders",
            ForeignColumnPolicy.TolerateDatabaseSupplied,
            [new ForeignPhysicalColumn("tenant_ref", IsNullable: false, HasDefault: false, IsDatabaseGenerated: false)]);

        Assert.Empty(verdict.Tolerated);
        var refusal = Assert.Single(verdict.Drift);
        Assert.Equal("GW-RUNTIME-001", refusal.Code);
        Assert.Contains(
            "the database supplies no value for it, so a write that omits it cannot succeed",
            refusal.Message,
            StringComparison.Ordinal);
        Assert.Contains("not nullable, not defaulted", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mixed_catalog_splits_by_column_rather_than_by_verdict()
    {
        var verdict = ForeignColumnAdmission.Classify(
            "orders",
            ForeignColumnPolicy.TolerateDatabaseSupplied,
            [
                new ForeignPhysicalColumn("tenant_ref", false, false, false),
                new ForeignPhysicalColumn("audit_id", true, false, false)
            ]);

        Assert.Equal("columns.audit_id", Assert.Single(verdict.Tolerated).Path);
        Assert.Equal("columns.tenant_ref", Assert.Single(verdict.Drift).Path);
    }

    [Fact]
    public void Nothing_foreign_is_nothing_to_report()
    {
        var verdict = ForeignColumnAdmission.Classify("orders", ForeignColumnPolicy.TolerateDatabaseSupplied, []);

        Assert.Empty(verdict.Drift);
        Assert.Empty(verdict.Tolerated);
    }

    [Fact]
    public void Foreign_columns_are_reported_in_a_stable_order()
    {
        var verdict = ForeignColumnAdmission.Classify(
            "orders",
            ForeignColumnPolicy.Refuse,
            [
                new ForeignPhysicalColumn("zulu", true, false, false),
                new ForeignPhysicalColumn("alpha", true, false, false)
            ]);

        Assert.Equal(
            new[] { "columns.alpha", "columns.zulu" },
            verdict.Drift.Select(refusal => refusal.Path).ToArray());
    }

    [Fact]
    public void The_policy_is_not_part_of_the_schema_fingerprint()
    {
        // Tolerance changes what an undeclared column means, not the shape of the declared one.
        // Folding it into the fingerprint would make turning it on look like a schema change, and
        // would split the deployment tool's compiled target from the host's.
        var strict = new SchemaSubject(Orders(ForeignColumnPolicy.Refuse));
        var tolerant = new SchemaSubject(Orders(ForeignColumnPolicy.TolerateDatabaseSupplied));

        Assert.Equal(strict.Fingerprint, tolerant.Fingerprint);
        Assert.Equal(ForeignColumnPolicy.TolerateDatabaseSupplied, tolerant.ForeignColumns);
        Assert.Equal(ForeignColumnPolicy.Refuse, strict.ForeignColumns);
    }

    [Fact]
    public void The_policy_is_not_recorded_in_persisted_applied_state()
    {
        // Applied state records what was applied, and tolerating a column applies nothing. Writing
        // it there would also change the canonical JSON of every row written before this existed,
        // which the serializer's canonical-form check rejects on read.
        var target = new PhysicalSchemaTarget(
            new SchemaSubject(Orders(ForeignColumnPolicy.TolerateDatabaseSupplied)),
            new ProviderIdentity("Fake", "1.0"));
        var plan = PhysicalSchemaDiffPlanner.Plan(target, PhysicalSchemaHistoryState.Empty, DateTimeOffset.UnixEpoch);
        var state = plan.Complete(
            [.. plan.Operations
                .Where(operation => operation.Kind != PhysicalSchemaOperationKind.PublishAppliedState)
                .Select(operation => new PhysicalSchemaOperationAcknowledgement(
                    operation.Identity, operation.Fingerprint, DateTimeOffset.UnixEpoch))],
            DateTimeOffset.UnixEpoch);

        var json = PhysicalSchemaAppliedStateSerializer.Serialize(state);

        Assert.DoesNotContain("foreignColumns", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TolerateDatabaseSupplied", json, StringComparison.Ordinal);
        Assert.Equal(
            ForeignColumnPolicy.Refuse,
            PhysicalSchemaAppliedStateSerializer.Deserialize(json).Snapshot.Subject.ForeignColumns);
    }

    private static StorageUnit Orders(ForeignColumnPolicy policy) => new()
    {
        Id = new StorageUnitId("orders"),
        Name = "orders",
        Columns = [new() { Name = "id", Type = PortableType.String, MaxLength = 64, IsNullable = false }],
        Key = new KeyDefinition { Columns = ["id"] },
        ForeignColumns = policy
    };
}
