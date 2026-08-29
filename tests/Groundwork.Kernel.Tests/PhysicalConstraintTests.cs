using Groundwork.Kernel.Schema;
using Xunit;

namespace Groundwork.Kernel.Tests;

public sealed class PhysicalConstraintTests
{
    private static readonly ProviderIdentity Provider = new("relational-test", "1.0");

    [Fact]
    public void Physical_references_and_checks_are_snapshotted_as_explicit_declarations()
    {
        var customer = Customer();
        var order = StorageUnit.Declare("order", "orders")
            .Guid("id", column => column.Required())
            .Guid("customer_id", column => column.Required())
            .Decimal("total", 18, 2, column => column.Required())
            .Key("id")
            .Index("by_customer", "customer_id")
            .PhysicalReference("fk_orders_customer", customer, "customer_id")
            .Check("ck_orders_total", "total", CheckConstraintOperator.GreaterThanOrEqual, 0m)
            .Build();

        var reference = Assert.Single(order.References);
        Assert.Equal(ReferenceEnforcement.Physical, reference.Enforcement);
        Assert.Equal(customer.Name, reference.TargetName);
        Assert.Equal(customer.Key.Columns, reference.TargetKeyColumns);
        Assert.False(reference.TargetKeyHasProviderSequence);

        var check = Assert.Single(order.CheckConstraints);
        Assert.Equal("ck_orders_total", check.Name);
        Assert.Equal("total", check.Column);
        Assert.Equal(CheckConstraintOperator.GreaterThanOrEqual, check.Operator);
        Assert.Equal(0m, check.Value.Value);
    }

    [Fact]
    public void Physical_constraints_are_additive_plan_operations_and_survive_history()
    {
        var order = StorageUnit.Declare("order", "orders")
            .Guid("id", column => column.Required())
            .Guid("customer_id", column => column.Required())
            .Decimal("total", 18, 2, column => column.Required())
            .Key("id")
            .Index("by_customer", "customer_id")
            .PhysicalReference("fk_orders_customer", Customer(), "customer_id")
            .Check("ck_orders_total", "total", CheckConstraintOperator.GreaterThanOrEqual, 0m)
            .Build();
        var target = new PhysicalSchemaTarget(new SchemaSubject(order), Provider);

        var plan = PhysicalSchemaDiffPlanner.Plan(target, PhysicalSchemaHistoryState.Empty, DateTimeOffset.UnixEpoch);

        var foreignKey = Assert.Single(plan.Operations.OfType<CreatePhysicalForeignKeyOperation>());
        Assert.Equal("fk_orders_customer", foreignKey.Reference.Name);
        var check = Assert.Single(plan.Operations.OfType<CreatePhysicalCheckConstraintOperation>());
        Assert.Equal("ck_orders_total", check.Constraint.Name);
        Assert.True(Array.IndexOf(plan.Operations.ToArray(), foreignKey) < Array.IndexOf(plan.Operations.ToArray(), check));

        var applied = plan.Complete(
            [.. plan.Operations
                .Where(operation => operation.Kind != PhysicalSchemaOperationKind.PublishAppliedState)
                .Select(operation => new PhysicalSchemaOperationAcknowledgement(
                    operation.Identity,
                    operation.Fingerprint,
                    DateTimeOffset.UnixEpoch))],
            DateTimeOffset.UnixEpoch);
        var roundTrip = PhysicalSchemaAppliedStateSerializer.Deserialize(
            PhysicalSchemaAppliedStateSerializer.Serialize(applied));
        var restart = PhysicalSchemaDiffPlanner.Plan(
            target,
            PhysicalSchemaHistoryState.FromApplied(roundTrip),
            DateTimeOffset.UnixEpoch.AddMinutes(1));

        Assert.Empty(restart.Operations);
        Assert.Single(roundTrip.Snapshot.Subject.References, reference =>
            reference.Enforcement == ReferenceEnforcement.Physical);
        Assert.Single(roundTrip.Snapshot.Subject.CheckConstraints);
        Assert.Contains(roundTrip.Snapshot.SemanticOperations, operation =>
            operation.Kind == PhysicalSchemaOperationKind.CreatePhysicalForeignKey);
        Assert.Contains(roundTrip.Snapshot.SemanticOperations, operation =>
            operation.Kind == PhysicalSchemaOperationKind.CreatePhysicalCheckConstraint);
    }

    [Fact]
    public void Scoped_physical_references_include_the_scope_key_on_both_sides()
    {
        var customer = Customer() with { Scope = ScopePolicy.Scoped };
        var order = StorageUnit.Declare("order", "orders")
            .Guid("id", column => column.Required())
            .Guid("customer_id", column => column.Required())
            .Key("id")
            .Index("by_customer", "customer_id")
            .Scoped()
            .PhysicalReference("fk_orders_customer", customer, "customer_id")
            .Build();

        var physical = ProviderOwnedColumns.Physicalize(order, new ProviderOwnedColumnPolicy
        {
            ProviderName = "test"
        });

        var reference = Assert.Single(physical.References);
        Assert.Equal([ProviderOwnedColumns.Scope, "customer_id"], reference.Columns);
        Assert.Equal([ProviderOwnedColumns.Scope, "id"], reference.TargetKeyColumns);
    }

    [Fact]
    public void Changing_an_applied_constraint_is_refused_without_a_portable_replace_operation()
    {
        var original = OrderWithMinimum(CheckConstraintOperator.GreaterThanOrEqual, 0m);
        var originalTarget = new PhysicalSchemaTarget(new SchemaSubject(original), Provider);
        var originalPlan = PhysicalSchemaDiffPlanner.Plan(
            originalTarget,
            PhysicalSchemaHistoryState.Empty,
            DateTimeOffset.UnixEpoch);
        var applied = originalPlan.Complete(
            [.. originalPlan.Operations
                .Where(operation => operation.Kind != PhysicalSchemaOperationKind.PublishAppliedState)
                .Select(operation => new PhysicalSchemaOperationAcknowledgement(
                    operation.Identity,
                    operation.Fingerprint,
                    DateTimeOffset.UnixEpoch))],
            DateTimeOffset.UnixEpoch);

        var changed = new PhysicalSchemaTarget(
            new SchemaSubject(OrderWithMinimum(CheckConstraintOperator.GreaterThan, 1m)),
            Provider);
        var plan = PhysicalSchemaDiffPlanner.Plan(
            changed,
            PhysicalSchemaHistoryState.FromApplied(applied),
            DateTimeOffset.UnixEpoch.AddMinutes(1));

        Assert.False(plan.IsApplicable);
        Assert.Contains(plan.Refusals, refusal =>
            refusal.Code == "GW-SCHEMA-004" && refusal.Path == "schema.operations.ck_orders_total");
    }

    [Fact]
    public void Physical_constraint_names_obey_the_portable_identifier_boundary()
    {
        var name = new string('x', PortabilityValidator.MaximumPortableIdentifierLength + 1);
        var exception = Assert.Throws<DeclarationBuildException>(() => StorageUnit.Declare("order", "orders")
            .Guid("id", column => column.Required())
            .Int32("total", column => column.Required())
            .Key("id")
            .Check(name, "total", CheckConstraintOperator.GreaterThanOrEqual, 0)
            .Build());

        var refusal = Assert.Single(exception.Findings, item =>
            item.Path == $"checkConstraints.{name}.name");
        Assert.Equal("GW-PORT-010", refusal.Code);
    }

    [Fact]
    public void Ordered_checks_refuse_types_without_portable_cross_provider_ordering()
    {
        var exception = Assert.Throws<ArgumentException>(() => StorageUnit.Declare("order", "orders")
            .Guid("id", column => column.Required())
            .Boolean("active", column => column.Required())
            .Key("id")
            .Check("ck_orders_active", "active", CheckConstraintOperator.GreaterThan, false)
            .Build());

        Assert.Contains("only equality operators", exception.Message, StringComparison.Ordinal);
    }

    private static StorageUnit OrderWithMinimum(CheckConstraintOperator @operator, decimal minimum) =>
        StorageUnit.Declare("order", "orders")
            .Guid("id", column => column.Required())
            .Decimal("total", 18, 2, column => column.Required())
            .Key("id")
            .Check("ck_orders_total", "total", @operator, minimum)
            .Build();

    private static StorageUnit Customer() => StorageUnit.Declare("customer", "customers")
        .Guid("id", column => column.Required())
        .Key("id")
        .Build();
}
