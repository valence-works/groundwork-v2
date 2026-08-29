using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Xunit;

namespace Groundwork.Kernel.Tests;

public sealed class ReferenceDeclarationTests
{
    [Fact]
    public void Builder_declares_a_logical_composite_reference_in_target_key_order()
    {
        var target = StorageUnit.Declare("customer", "customers")
            .String("tenant", 64, column => column.Required())
            .Guid("id", column => column.Required())
            .Key("tenant", "id")
            .Build();

        var source = StorageUnit.Declare("order", "orders")
            .Guid("order_id", column => column.Required())
            .String("customer_tenant", 64, column => column.Required())
            .Guid("customer_id", column => column.Required())
            .Key("order_id")
            .Index("by_customer", "customer_tenant", "customer_id")
            .Reference("customer", target, "customer_tenant", "customer_id")
            .Build();

        var reference = Assert.Single(source.References);
        Assert.Equal("customer", reference.Name);
        Assert.Equal(["customer_tenant", "customer_id"], reference.Columns);
        Assert.Equal(target.Id, reference.TargetUnitId);
    }

    [Fact]
    public void Referencing_columns_must_be_declared_once()
    {
        var target = GuidTarget();

        var exception = Assert.Throws<DeclarationBuildException>(() => StorageUnit.Declare("order", "orders")
            .Guid("id", column => column.Required())
            .Key("id")
            .Reference("customer", target, "missing", "missing")
            .Build());

        Assert.Contains(exception.Findings, finding => finding.Code == "GW-DECL-REF-001");
    }

    [Fact]
    public void Referencing_shape_must_match_the_target_key()
    {
        var target = StorageUnit.Declare("customer", "customers")
            .String("tenant", 64, column => column.Required())
            .Guid("id", column => column.Required())
            .Key("tenant", "id")
            .Build();

        var exception = Assert.Throws<DeclarationBuildException>(() => StorageUnit.Declare("order", "orders")
            .Guid("id", column => column.Required())
            .Guid("customer_id", column => column.Required())
            .Key("id")
            .Index("by_customer", "customer_id")
            .Reference("customer", target, "customer_id")
            .Build());

        Assert.Contains(exception.Findings, finding => finding.Code == "GW-DECL-REF-002");
    }

    [Fact]
    public void Referencing_and_target_units_must_share_the_same_scope_policy()
    {
        var target = StorageUnit.Declare("customer", "customers")
            .Guid("id", column => column.Required())
            .Key("id")
            .Scoped()
            .Build();

        var exception = Assert.Throws<DeclarationBuildException>(() => SourceBuilder()
            .Reference("customer", target, "customer_id")
            .Build());

        Assert.Contains(exception.Findings, finding => finding.Code == "GW-DECL-REF-003");
    }

    [Fact]
    public void Referencing_columns_must_have_the_target_key_portable_types()
    {
        var target = GuidTarget();

        var exception = Assert.Throws<DeclarationBuildException>(() => StorageUnit.Declare("order", "orders")
            .Guid("id", column => column.Required())
            .Int64("customer_id", column => column.Required())
            .Key("id")
            .Index("by_customer", "customer_id")
            .Reference("customer", target, "customer_id")
            .Build());

        var finding = Assert.Single(exception.Findings, finding => finding.Code == "GW-DECL-REF-004");
        Assert.Contains("Int64", finding.Message, StringComparison.Ordinal);
        Assert.Contains("Guid", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reference_requires_a_covering_key_or_index()
    {
        var target = GuidTarget();

        var exception = Assert.Throws<DeclarationBuildException>(() => StorageUnit.Declare("order", "orders")
            .Guid("id", column => column.Required())
            .Guid("customer_id", column => column.Required())
            .Key("id")
            .Reference("customer", target, "customer_id")
            .Build());

        Assert.Contains(exception.Findings, finding => finding.Code == "GW-DECL-REF-005");
    }

    [Fact]
    public void Storage_key_is_a_covering_index_for_a_reference()
    {
        var target = GuidTarget();

        var source = StorageUnit.Declare("customer_alias", "customer_aliases")
            .Guid("customer_id", column => column.Required())
            .Key("customer_id")
            .Reference("customer", target, "customer_id")
            .Build();

        Assert.Single(source.References);
        Assert.Empty(source.Indexes);
    }

    [Fact]
    public void Canonical_subject_snapshots_reference_columns_and_physicalization_keeps_them_logical()
    {
        var target = GuidTarget();
        var source = SourceBuilder().Reference("customer", target, "customer_id").Build();
        var subject = new SchemaSubject(source);

        var physical = ProviderOwnedColumns.Physicalize(subject.Definition, new ProviderOwnedColumnPolicy
        {
            ProviderName = "test"
        });

        AssertReference(subject.Definition.References);
        AssertReference(physical.References);
        Assert.DoesNotContain(physical.Indexes, index => index.Name.Contains("reference", StringComparison.OrdinalIgnoreCase));

        void AssertReference(IReadOnlyList<ReferenceDefinition> references)
        {
            var reference = Assert.Single(references);
            Assert.Equal("customer", reference.Name);
            Assert.Equal(["customer_id"], reference.Columns);
            Assert.Equal(target.Id, reference.TargetUnitId);
        }
    }

    [Fact]
    public void Reference_identity_participates_in_the_schema_subject_fingerprint()
    {
        var customers = new SchemaSubject(SourceBuilder()
            .Reference("customer", new StorageUnitId("customer"), "customer_id")
            .Build());
        var accounts = new SchemaSubject(SourceBuilder()
            .Reference("customer", new StorageUnitId("account"), "customer_id")
            .Build());

        Assert.NotEqual(customers.Fingerprint, accounts.Fingerprint);
    }

    [Fact]
    public void Reference_declaration_order_does_not_change_the_schema_subject_fingerprint()
    {
        var customerThenAccount = new SchemaSubject(SourceBuilder()
            .Reference("customer", new StorageUnitId("customer"), "customer_id")
            .Reference("account", new StorageUnitId("account"), "customer_id")
            .Build());
        var accountThenCustomer = new SchemaSubject(SourceBuilder()
            .Reference("account", new StorageUnitId("account"), "customer_id")
            .Reference("customer", new StorageUnitId("customer"), "customer_id")
            .Build());

        Assert.Equal(customerThenAccount.Fingerprint, accountThenCustomer.Fingerprint);
    }

    [Fact]
    public void Applied_state_preserves_references_without_rewriting_legacy_empty_state()
    {
        var withoutReferences = AppliedState(SourceBuilder().Build());
        var legacyCompatibleJson = PhysicalSchemaAppliedStateSerializer.Serialize(withoutReferences);

        Assert.DoesNotContain("\"references\"", legacyCompatibleJson, StringComparison.Ordinal);
        Assert.Equal(
            legacyCompatibleJson,
            PhysicalSchemaAppliedStateSerializer.Serialize(
                PhysicalSchemaAppliedStateSerializer.Deserialize(legacyCompatibleJson)));

        var withReferences = AppliedState(SourceBuilder()
            .Reference("customer", new StorageUnitId("customer"), "customer_id")
            .Build());
        var json = PhysicalSchemaAppliedStateSerializer.Serialize(withReferences);
        var restored = PhysicalSchemaAppliedStateSerializer.Deserialize(json);

        Assert.Contains("\"references\":[", json, StringComparison.Ordinal);
        Assert.Equal("customer", Assert.Single(restored.Snapshot.Subject.References).Name);
    }

    [Fact]
    public void Complete_manifest_refuses_an_undeclared_reference_target()
    {
        var source = SourceBuilder()
            .Reference("customer", new StorageUnitId("missing"), "customer_id")
            .Build();

        var exception = Assert.Throws<ArgumentException>(() => SchemaSubject.ValidateManifest([source]));

        Assert.Contains("GW-DECL-REF-002", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Records_wrapper_preserves_reference_diagnostics()
    {
        var target = GuidTarget();

        var exception = Assert.Throws<Groundwork.Records.StorageDeclarationException>(() =>
            Groundwork.Records.StorageUnit.Declare("order", "orders")
                .Guid("id", column => column.Required())
                .Guid("customer_id", column => column.Required())
                .Key("id")
                .Reference("customer", target, "customer_id")
                .Build());

        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "GW-DECL-REF-005");
    }

    private static StorageUnit GuidTarget() => StorageUnit.Declare("customer", "customers")
        .Guid("id", column => column.Required())
        .Key("id")
        .Build();

    private static StorageDeclarationBuilder SourceBuilder() => StorageUnit.Declare("order", "orders")
        .Guid("id", column => column.Required())
        .Guid("customer_id", column => column.Required())
        .Key("id")
        .Index("by_customer", "customer_id");

    private static PhysicalSchemaAppliedState AppliedState(StorageUnit unit)
    {
        var target = new PhysicalSchemaTarget(
            new SchemaSubject(unit),
            new ProviderIdentity("Fake", "1.0"));
        var plan = PhysicalSchemaDiffPlanner.Plan(target, PhysicalSchemaHistoryState.Empty, DateTimeOffset.UnixEpoch);
        return plan.Complete(
            [.. plan.Operations
                .Where(operation => operation.Kind != PhysicalSchemaOperationKind.PublishAppliedState)
                .Select(operation => new PhysicalSchemaOperationAcknowledgement(
                    operation.Identity, operation.Fingerprint, DateTimeOffset.UnixEpoch))],
            DateTimeOffset.UnixEpoch);
    }
}
