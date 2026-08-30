using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Xunit;

namespace Groundwork.Kernel.Tests;

public sealed class StableDeclarationPlanningTests
{
    private static readonly ProviderIdentity Provider = new("test-provider", "1.0");
    private static readonly DateTimeOffset PlannedAt = new(2026, 8, 30, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Every_unplanned_schema_identity_change_is_a_named_kernel_refusal()
    {
        var initial = Unit();
        var applied = Apply(initial);
        var changes = new (StorageUnit Desired, string Path)[]
        {
            (initial with { Key = new KeyDefinition { Columns = ["alternate"] } }, "schema.key"),
            (initial with { Scope = ScopePolicy.Scoped }, "schema.scope"),
            (initial with { Concurrency = ConcurrencyDeclaration.Optimistic() }, "schema.concurrency"),
            (initial with { SchemaVersion = 2 }, "schema.schemaVersion"),
            (initial with { Retention = initial.Retention! with { KeepNewest = 9 } }, "schema.retention"),
            (initial with
            {
                AppendIdempotency = initial.AppendIdempotency! with { Window = TimeSpan.FromMinutes(11) }
            }, "schema.appendIdempotency"),
            (initial with
            {
                RetentionIdempotency = initial.RetentionIdempotency! with { Window = TimeSpan.FromMinutes(12) }
            }, "schema.retentionIdempotency")
        };

        foreach (var (desired, path) in changes)
        {
            var plan = Plan(desired, applied);

            Assert.False(plan.IsApplicable);
            Assert.Empty(plan.Operations);
            var refusal = Assert.Single(plan.Refusals);
            Assert.Equal(PhysicalSchemaDiffPlanner.StableDeclarationChangedCode, refusal.Code);
            Assert.Equal(path, refusal.Path);
            Assert.Contains("no portable in-place evolution", refusal.Message, StringComparison.Ordinal);
            if (path == "schema.key")
            {
                Assert.Contains("'id'", refusal.Message, StringComparison.Ordinal);
                Assert.Contains("'alternate'", refusal.Message, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Combined_stable_declaration_refusals_have_deterministic_order()
    {
        var initial = Unit();
        var desired = initial with
        {
            Key = new KeyDefinition { Columns = ["alternate"] },
            Scope = ScopePolicy.Scoped,
            Concurrency = ConcurrencyDeclaration.Optimistic(),
            SchemaVersion = 2,
            Retention = initial.Retention! with { KeepNewest = 9 },
            AppendIdempotency = initial.AppendIdempotency! with { Window = TimeSpan.FromMinutes(11) },
            RetentionIdempotency = initial.RetentionIdempotency! with { Window = TimeSpan.FromMinutes(12) }
        };

        var plan = Plan(desired, Apply(initial));

        Assert.Equal(
        [
            "schema.key",
            "schema.scope",
            "schema.concurrency",
            "schema.schemaVersion",
            "schema.retention",
            "schema.appendIdempotency",
            "schema.retentionIdempotency"
        ], plan.Refusals.Select(refusal => refusal.Path));
        Assert.All(plan.Refusals, refusal =>
            Assert.Equal(PhysicalSchemaDiffPlanner.StableDeclarationChangedCode, refusal.Code));
    }

    [Fact]
    public void Renaming_a_key_column_without_changing_its_logical_identity_remains_plannable()
    {
        var initial = Unit();
        var renamed = initial with
        {
            Columns = [.. initial.Columns.Select(column => column.Name == "id"
                ? column with { Id = "id", Name = "customer_id" }
                : column)],
            Key = new KeyDefinition { Columns = ["customer_id"] }
        };

        var plan = Plan(renamed, Apply(initial));

        Assert.True(plan.IsApplicable, string.Join("; ", plan.Refusals.Select(refusal => refusal.Message)));
        var operation = Assert.Single(plan.Operations.OfType<RenameColumnOperation>());
        Assert.Equal("id", operation.FromName);
        Assert.Equal("customer_id", operation.Column.Name);
    }

    [Fact]
    public void Replanning_the_identical_declaration_remains_an_empty_applicable_plan()
    {
        var unit = Unit();

        var plan = Plan(unit, Apply(unit));

        Assert.True(plan.IsApplicable);
        Assert.Empty(plan.Refusals);
        Assert.Empty(plan.Operations);
    }

    [Fact]
    public void Retirement_cannot_bypass_stable_declaration_validation()
    {
        var initial = Unit();
        var retired = new PhysicalSchemaTarget(
            new SchemaSubject(
                initial with { Scope = ScopePolicy.Scoped },
                new SchemaEvolutionMetadata(retiresPrimaryStorage: true)),
            Provider);

        var plan = PhysicalSchemaDiffPlanner.Plan(
            retired,
            PhysicalSchemaHistoryState.FromApplied(Apply(initial)),
            PlannedAt.AddMinutes(1));

        Assert.False(plan.IsApplicable);
        Assert.Empty(plan.Operations);
        Assert.Equal("schema.scope", Assert.Single(plan.Refusals).Path);
    }

    private static PhysicalSchemaDiffPlan Plan(StorageUnit unit, PhysicalSchemaAppliedState applied) =>
        PhysicalSchemaDiffPlanner.Plan(
            Target(unit),
            PhysicalSchemaHistoryState.FromApplied(applied),
            PlannedAt.AddMinutes(1));

    private static PhysicalSchemaAppliedState Apply(StorageUnit unit)
    {
        var plan = PhysicalSchemaDiffPlanner.Plan(Target(unit), PhysicalSchemaHistoryState.Empty, PlannedAt);
        var acknowledgements = plan.Operations
            .Where(operation => operation.Kind != PhysicalSchemaOperationKind.PublishAppliedState)
            .Select(operation => new PhysicalSchemaOperationAcknowledgement(
                operation.Identity,
                operation.Fingerprint,
                PlannedAt))
            .ToArray();
        return plan.Complete(acknowledgements, PlannedAt);
    }

    private static PhysicalSchemaTarget Target(StorageUnit unit) => new(new SchemaSubject(unit), Provider);

    private static StorageUnit Unit() => new()
    {
        Id = new StorageUnitId("stable-customer"),
        Name = "stable_customer",
        Columns =
        [
            new ColumnDefinition { Name = "id", Type = PortableType.Guid, IsNullable = false },
            new ColumnDefinition { Name = "alternate", Type = PortableType.Guid, IsNullable = false },
            new ColumnDefinition { Name = "sequence", Type = PortableType.Int64, IsNullable = false }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        Retention = new RetentionDeclaration
        {
            KeepNewest = 5,
            OrderColumn = "sequence",
            PartitionColumns = ["alternate"]
        },
        AppendIdempotency = new AppendIdempotencyDeclaration { Window = TimeSpan.FromMinutes(10) },
        RetentionIdempotency = new RetentionIdempotencyDeclaration { Window = TimeSpan.FromMinutes(10) }
    };
}
