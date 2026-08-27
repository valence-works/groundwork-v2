using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using System.Text.Json;
using Xunit;

namespace Groundwork.Kernel.Tests;

public sealed class K5SchemaEvolutionTests
{
    private static readonly ProviderIdentity Provider = new("test-provider", "1.0");
    private static readonly DateTimeOffset PlannedAt = new(2026, 8, 15, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Canonical_index_payload_refuses_malformed_or_noncanonical_fields()
    {
        var malformed = new[]
        {
            SchemaFingerprint.Canonicalize(["by-name", "not-a-bool", "Included", "0", "name:Ascending"]),
            SchemaFingerprint.Canonicalize(["by-name", "False", "Unknown", "0", "name:Ascending"]),
            SchemaFingerprint.Canonicalize(["by-name", "False", "Included", "not-an-int", "name:Ascending"]),
            SchemaFingerprint.Canonicalize(["by-name", "False", "Included", "0", "name:Sideways"]),
            SchemaFingerprint.Canonicalize(["by-name", "False", "Included", "0", " :Ascending"]),
            SchemaFingerprint.Canonicalize(["by-name", "False", "Included", "00", "name:Ascending"]),
            SchemaFingerprint.Canonicalize(["by-name", "False", "Included", "0"])
        };

        foreach (var canonical in malformed)
            Assert.False(CanonicalIndexPayload.TryParse(canonical, out _));
    }

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
        var index = Assert.Single(operations.OfType<CreatePhysicalIndexOperation>(), operation => operation.Index.Name == "by_priority");

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
        Assert.Contains(plan.Operations, operation => operation is CreatePhysicalIndexOperation index && index.Index.Name == "by_priority");
        Assert.DoesNotContain(plan.Operations, operation => operation is CreatePrimaryStorageOperation);
    }

    [Fact]
    public void Applied_state_from_an_earlier_schema_boundary_names_the_discard_remedy()
    {
        var executor = new FakeExecutor();
        PhysicalSchemaApplication.Apply(CreateTarget(CreateUnit(includePriority: true)), executor, PlannedAt.AddMinutes(1));
        var recorded = PhysicalSchemaAppliedStateSerializer.Serialize(executor.AppliedState!);
        var stale = recorded.Replace(
            $"\"targetFingerprint\":\"{executor.AppliedState!.TargetFingerprint}\"",
            "\"targetFingerprint\":\"0000000000000000000000000000000000000000000000000000000000000000\"",
            StringComparison.Ordinal);

        var failure = Assert.Throws<GroundworkSchemaBoundaryException>(
            () => PhysicalSchemaAppliedStateSerializer.Deserialize(stale));

        Assert.Equal(CreateTarget(CreateUnit(includePriority: true)).Identity, failure.Target);
        Assert.StartsWith("GW-SCHEMA-006", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Discard that catalog", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Changed_provider_definition_replans_as_an_authorized_replacement()
    {
        static PhysicalSchemaTarget Target(StorageUnit unit, string definition) => new(
            new SchemaSubject(unit),
            Provider,
            [new ProviderPhysicalSchemaDefinition(Provider.Name, unit.Id, "batch-type", "customer_batch", definition)]);

        var executor = new FakeExecutor();
        PhysicalSchemaApplication.Apply(
            Target(CreateUnit(includePriority: false), "[\"id\",\"name\"]"), executor, PlannedAt.AddMinutes(1));

        var plan = PhysicalSchemaDiffPlanner.Plan(
            Target(CreateUnit(includePriority: true), "[\"id\",\"name\",\"priority\"]"),
            PhysicalSchemaHistoryState.FromApplied(executor.AppliedState!),
            PlannedAt.AddMinutes(2));

        Assert.True(plan.IsApplicable, string.Join("; ", plan.Refusals.Select(refusal => refusal.Message)));
        var replacement = Assert.Single(plan.Operations
            .OfType<ApplyProviderPhysicalSchemaDefinitionOperation>());
        Assert.True(replacement.RequiresAuthorization);
    }

    [Fact]
    public void Changed_declared_index_rebuilds_even_beside_a_provider_definition()
    {
        static PhysicalSchemaTarget Target(StorageUnit unit) => new(
            new SchemaSubject(unit),
            Provider,
            [new ProviderPhysicalSchemaDefinition(Provider.Name, unit.Id, "batch-type", "customer_batch", "[]")]);

        var executor = new FakeExecutor();
        PhysicalSchemaApplication.Apply(Target(CreateUnit(includePriority: true)), executor, PlannedAt.AddMinutes(1));
        var retyped = CreateUnit(includePriority: true) with
        {
            Indexes = [new IndexDefinition
            {
                Name = "by_priority",
                Columns = [new IndexColumn("priority", SortDirection.Descending)]
            }]
        };

        var plan = PhysicalSchemaDiffPlanner.Plan(
            Target(retyped),
            PhysicalSchemaHistoryState.FromApplied(executor.AppliedState!),
            PlannedAt.AddMinutes(2));

        Assert.True(plan.IsApplicable, string.Join("; ", plan.Refusals.Select(refusal => refusal.Message)));
        var rebuild = Assert.Single(plan.Operations.OfType<RebuildPhysicalIndexOperation>());
        Assert.Equal("by_priority", rebuild.Index.Name);
        Assert.Equal(SortDirection.Descending, Assert.Single(rebuild.Index.Columns).Direction);
        Assert.Contains(rebuild.Identity, PhysicalSchemaPlanProtection.Inspect(plan.Operations).DestructiveOperationIdentities);
    }

    [Fact]
    public void Required_column_without_a_backfill_source_is_refused_for_existing_rows()
    {
        var initial = CreateTarget(CreateUnit(includePriority: false));
        var executor = new FakeExecutor();
        PhysicalSchemaApplication.Apply(initial, executor, PlannedAt.AddMinutes(1));
        var unsafeUnit = CreateUnit(includePriority: true) with
        {
            Columns = CreateUnit(includePriority: true).Columns
                .Select(column => column.Name == "priority" ? column with { Default = null } : column)
                .ToArray()
        };

        var plan = PhysicalSchemaDiffPlanner.Plan(
            CreateTarget(unsafeUnit),
            PhysicalSchemaHistoryState.FromApplied(executor.AppliedState!),
            PlannedAt.AddMinutes(2));

        Assert.False(plan.IsApplicable);
        Assert.Contains(plan.Refusals, refusal => refusal.Code == "GW-SCHEMA-005");
    }

    [Fact]
    public void Widening_an_applied_column_plans_an_authorized_alter()
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

        Assert.True(plan.IsApplicable, string.Join("; ", plan.Refusals.Select(refusal => refusal.Message)));
        var alter = Assert.Single(plan.Operations.OfType<AlterColumnOperation>());
        Assert.Equal(ColumnAlterationKind.Widening, alter.Alteration);
        Assert.Equal(100, alter.From.MaxLength);
        Assert.Equal(200, alter.Column.MaxLength);
        // A widening keeps every stored value, so it is authorized as a semantic migration rather
        // than as a destructive one — and the alter is not re-run as an add.
        Assert.Empty(plan.Operations.OfType<AddColumnOperation>());
        var protection = PhysicalSchemaPlanProtection.Inspect(plan.Operations);
        Assert.Empty(protection.DestructiveOperations);
        Assert.Contains("alter-column:customer.name", protection.SemanticMigrationIdentities);
    }

    [Fact]
    public void Narrowing_an_applied_column_plans_a_destructive_alter()
    {
        var initial = CreateTarget(CreateUnit(includePriority: false));
        var executor = new FakeExecutor();
        PhysicalSchemaApplication.Apply(initial, executor, PlannedAt.AddMinutes(1));
        var narrowed = CreateUnit(includePriority: false) with
        {
            Columns =
            [
                new() { Name = "id", Type = PortableType.Guid, IsNullable = false },
                new() { Name = "name", Type = PortableType.String, IsNullable = false, MaxLength = 40 }
            ]
        };

        var plan = PhysicalSchemaDiffPlanner.Plan(
            CreateTarget(narrowed),
            PhysicalSchemaHistoryState.FromApplied(executor.AppliedState!),
            PlannedAt.AddMinutes(2));

        Assert.True(plan.IsApplicable, string.Join("; ", plan.Refusals.Select(refusal => refusal.Message)));
        var alter = Assert.Single(plan.Operations.OfType<AlterColumnOperation>());
        Assert.Equal(ColumnAlterationKind.Narrowing, alter.Alteration);
        var protection = PhysicalSchemaPlanProtection.Inspect(plan.Operations);
        Assert.Equal("alter-column:customer.name", Assert.Single(protection.DestructiveOperations).Address);
        Assert.Empty(protection.SemanticMigrationIdentities);
    }

    [Theory]
    [InlineData(18, 4, 28, 4, ColumnAlterationKind.Widening)]
    [InlineData(28, 4, 18, 4, ColumnAlterationKind.Narrowing)]
    [InlineData(18, 4, 18, 2, ColumnAlterationKind.Narrowing)]
    [InlineData(18, 4, 20, 6, ColumnAlterationKind.Narrowing)]
    public void Decimal_precision_widens_only_at_an_unchanged_scale(
        int appliedPrecision,
        int appliedScale,
        int declaredPrecision,
        int declaredScale,
        ColumnAlterationKind expected)
    {
        var executor = new FakeExecutor();
        PhysicalSchemaApplication.Apply(
            CreateTarget(CreateDecimalUnit(appliedPrecision, appliedScale)),
            executor,
            PlannedAt.AddMinutes(1));

        var plan = PhysicalSchemaDiffPlanner.Plan(
            CreateTarget(CreateDecimalUnit(declaredPrecision, declaredScale)),
            PhysicalSchemaHistoryState.FromApplied(executor.AppliedState!),
            PlannedAt.AddMinutes(2));

        Assert.True(plan.IsApplicable, string.Join("; ", plan.Refusals.Select(refusal => refusal.Message)));
        Assert.Equal(expected, Assert.Single(plan.Operations.OfType<AlterColumnOperation>()).Alteration);
    }

    [Fact]
    public void Widening_nullable_index_missing_values_is_a_protected_rebuild()
    {
        var initial = CreateTarget(CreateNullableIndexUnit(MissingValueBehavior.Excluded));
        var executor = new FakeExecutor();
        PhysicalSchemaApplication.Apply(initial, executor, PlannedAt.AddMinutes(1));

        var widened = CreateTarget(CreateNullableIndexUnit(MissingValueBehavior.Included));
        var plan = PhysicalSchemaDiffPlanner.Plan(
            widened,
            PhysicalSchemaHistoryState.FromApplied(executor.AppliedState!),
            PlannedAt.AddMinutes(2));

        Assert.True(plan.IsApplicable, string.Join("; ", plan.Refusals.Select(refusal => refusal.Message)));
        var rebuild = Assert.Single(plan.Operations.OfType<RebuildPhysicalIndexOperation>());
        Assert.Equal("by_category", rebuild.SubjectIdentity);
        Assert.Contains(rebuild.Identity, PhysicalSchemaPlanProtection.Inspect(plan.Operations).DestructiveOperationIdentities);
    }

    [Fact]
    public void Widening_index_without_nullable_keys_is_still_a_rebuild()
    {
        var initial = CreateTarget(CreateNonNullableIndexUnit(MissingValueBehavior.Excluded));
        var executor = new FakeExecutor();
        PhysicalSchemaApplication.Apply(initial, executor, PlannedAt.AddMinutes(1));

        var widened = CreateTarget(CreateNonNullableIndexUnit(MissingValueBehavior.Included));
        var plan = PhysicalSchemaDiffPlanner.Plan(
            widened,
            PhysicalSchemaHistoryState.FromApplied(executor.AppliedState!),
            PlannedAt.AddMinutes(2));

        // Re-issuing the create would be a silent no-op on every provider, because their create is
        // idempotent by name and would leave the deployed index on its old partial-index filter.
        Assert.True(plan.IsApplicable, string.Join("; ", plan.Refusals.Select(refusal => refusal.Message)));
        Assert.Empty(plan.Operations.OfType<CreatePhysicalIndexOperation>());
        Assert.Single(plan.Operations.OfType<RebuildPhysicalIndexOperation>());
    }

    [Fact]
    public void Applied_index_widening_replans_as_idempotent_after_rebuild()
    {
        var initial = CreateTarget(CreateNullableIndexUnit(MissingValueBehavior.Excluded));
        var executor = new FakeExecutor();
        PhysicalSchemaApplication.Apply(initial, executor, PlannedAt.AddMinutes(1));

        var widened = CreateTarget(CreateNullableIndexUnit(MissingValueBehavior.Included));
        var applied = PhysicalSchemaApplication.Apply(widened, executor, PlannedAt.AddMinutes(2));

        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, applied.Outcome);
        Assert.Contains(applied.AppliedState!.Snapshot.SemanticOperations,
            operation => operation.Kind == PhysicalSchemaOperationKind.RebuildPhysicalIndex);
        var restart = PhysicalSchemaDiffPlanner.Plan(
            widened,
            PhysicalSchemaHistoryState.FromApplied(applied.AppliedState),
            PlannedAt.AddMinutes(3));
        Assert.Empty(restart.Operations);

        var additive = CreateTarget(widened.Subject.Definition with
        {
            Columns = [.. widened.Subject.Columns, new ColumnDefinition { Name = "later", Type = PortableType.String }]
        });
        var additiveResult = PhysicalSchemaApplication.Apply(additive, executor, PlannedAt.AddMinutes(4));
        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, additiveResult.Outcome);
        Assert.Empty(PhysicalSchemaDiffPlanner.Plan(
                additive,
                PhysicalSchemaHistoryState.FromApplied(additiveResult.AppliedState!),
                PlannedAt.AddMinutes(5))
            .Operations);
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
        Assert.Contains(result.Refusals, diagnostic => diagnostic.Code == "GW-SCHEMA-007");
        Assert.Null(executor.AppliedState);
    }

    [Fact]
    public void Derived_search_key_backfill_requires_authorization_for_startup_auto_apply()
    {
        var baseUnit = CreateUnit(includePriority: false);
        var logical = baseUnit with
        {
            Columns = [.. baseUnit.Columns.Select(column =>
                column.Name == "name"
                    ? column with { Collation = PortableCollation.OrdinalIgnoreCase }
                    : column)]
        };
        var target = CreateTarget(SearchKeyProjection.Expand(logical));
        var executor = new FakeExecutor();

        var result = GroundworkRuntimeSchemaAdmission.InspectRuntimeAdmission(
            executor,
            target,
            new GroundworkRuntimeSchemaAdmissionOptions { AutoApplyOnStartup = true });

        Assert.False(result.IsReady);
        Assert.Equal(PhysicalSchemaApplicationOutcome.AuthorizationRequired, result.Application!.Outcome);
        Assert.Contains(result.Plan.Operations, operation => operation is BackfillColumnOperation backfill && backfill.Derived is not null);
        Assert.Contains(result.Refusals, diagnostic => diagnostic.Code == "GW-SCHEMA-007" &&
            diagnostic.Message.Contains("backfill-column", StringComparison.Ordinal));
        Assert.Null(executor.AppliedState);
    }

    [Fact]
    public void Adding_folding_rebuilds_an_existing_logical_index_after_backfill()
    {
        var initialUnit = CreateUnit(includePriority: false) with
        {
            Indexes = [new IndexDefinition
            {
                Name = "by_name",
                Columns = [new IndexColumn("name")]
            }]
        };
        var executor = new FakeExecutor();
        PhysicalSchemaApplication.Apply(CreateTarget(initialUnit), executor, PlannedAt.AddMinutes(1));

        var folded = initialUnit with
        {
            Columns = [.. initialUnit.Columns.Select(column =>
                column.Name == "name"
                    ? column with { Collation = PortableCollation.OrdinalIgnoreCase }
                    : column)]
        };
        var plan = PhysicalSchemaDiffPlanner.Plan(
            CreateTarget(SearchKeyProjection.Expand(folded)),
            PhysicalSchemaHistoryState.FromApplied(executor.AppliedState!),
            PlannedAt.AddMinutes(2));

        Assert.True(plan.IsApplicable, string.Join("; ", plan.Refusals.Select(refusal => refusal.Message)));
        var backfill = Assert.Single(plan.Operations.OfType<BackfillColumnOperation>(), operation => operation.Derived is not null);
        var rebuild = Assert.Single(plan.Operations.OfType<RebuildPhysicalIndexOperation>(), operation => operation.Index.Name == "by_name");
        Assert.True(Array.IndexOf(plan.Operations.ToArray(), backfill) < Array.IndexOf(plan.Operations.ToArray(), rebuild));
        Assert.DoesNotContain(plan.Refusals, refusal => refusal.Code == "GW-SCHEMA-003");
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
    public void Index_and_aggregation_declaration_order_does_not_change_subject_identity()
    {
        static StorageUnit Declare(bool alphabetical)
        {
            var builder = StorageUnit.Declare("orders", "orders")
                .String("id", 64, column => column.Required())
                .String("status", 16, column => column.Required())
                .Key("id");
            builder = alphabetical
                ? builder
                    .Index("a_by_status", "status")
                    .Index("z_by_id", "id")
                    .Aggregate("a_totals", aggregate => aggregate.GroupBy("status").Count("n"))
                    .Aggregate("z_latest", aggregate => aggregate.GroupBy("status").Max("newest", "id"))
                : builder
                    .Index("z_by_id", "id")
                    .Index("a_by_status", "status")
                    .Aggregate("z_latest", aggregate => aggregate.GroupBy("status").Max("newest", "id"))
                    .Aggregate("a_totals", aggregate => aggregate.GroupBy("status").Count("n"));
            return builder.Build();
        }

        var declared = Declare(alphabetical: false);
        Assert.Equal(["z_by_id", "a_by_status"], declared.Indexes.Select(index => index.Name));
        Assert.Equal(
            new SchemaSubject(Declare(alphabetical: true)).Fingerprint,
            new SchemaSubject(declared).Fingerprint);
    }

    [Fact]
    public void Subject_snapshots_nested_defaults_and_fingerprints_their_content()
    {
        var binary = new byte[] { 1, 2 };
        var json = new Dictionary<string, object?>
        {
            ["items"] = new List<object?> { 1, new Dictionary<string, object?> { ["active"] = true } }
        };
        var first = new SchemaSubject(CreateDefaultsUnit(binary, json));
        var different = new SchemaSubject(CreateDefaultsUnit(new byte[] { 1, 3 }, json));

        binary[0] = 9;
        ((List<object?>)json["items"]!)[0] = 99;

        Assert.Equal(new byte[] { 1, 2 }, first.Columns[1].Default!.Value);
        var storedJson = Assert.IsType<Dictionary<string, object?>>(first.Columns[2].Default!.Value);
        Assert.Equal(1, Assert.IsType<List<object?>>(storedJson["items"])[0]);
        Assert.NotEqual(first.Fingerprint, different.Fingerprint);
    }

    [Fact]
    public void Aggregation_profile_fingerprint_is_injective_for_delimited_identifiers()
    {
        var baseUnit = new StorageUnit
        {
            Id = new StorageUnitId("aggregation-canonical-collision"),
            Name = "AggregationCanonicalCollision",
            Columns =
            [
                new() { Name = "id", Type = PortableType.Int32, IsNullable = false },
                new() { Name = "group", Type = PortableType.String },
                new() { Name = "ab", Type = PortableType.String },
                new() { Name = "bc", Type = PortableType.String },
                new() { Name = "a", Type = PortableType.String },
                new() { Name = "c", Type = PortableType.String }
            ],
            Key = new KeyDefinition { Columns = ["id"] }
        };
        var first = new SchemaSubject(baseUnit with
        {
            AggregationProfiles =
            [
                new AggregationProfile
                {
                    Name = "summary",
                    GroupByColumns = ["group"],
                    Aggregates = [new Aggregate.Min("a", "bc")]
                }
            ]
        });
        var second = new SchemaSubject(baseUnit with
        {
            AggregationProfiles =
            [
                new AggregationProfile
                {
                    Name = "summary",
                    GroupByColumns = ["group"],
                    Aggregates = [new Aggregate.Min("ab", "c")]
                }
            ]
        });

        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void Applied_state_serialization_preserves_typed_defaults()
    {
        var target = CreateTarget(CreateDefaultsUnit(
            new byte[] { 1, 2 },
            new Dictionary<string, object?> { ["answer"] = 42 }));
        var executor = new FakeExecutor();
        var applied = PhysicalSchemaApplication.Apply(target, executor, PlannedAt.AddMinutes(1)).AppliedState!;

        var restored = PhysicalSchemaAppliedStateSerializer.Deserialize(
            PhysicalSchemaAppliedStateSerializer.Serialize(applied));

        Assert.Equal(new byte[] { 1, 2 }, restored.Snapshot.Subject.Columns[1].Default!.Value);
        var restoredJson = Assert.IsType<Dictionary<string, object?>>(restored.Snapshot.Subject.Columns[2].Default!.Value);
        Assert.Equal(42, restoredJson["answer"]);
        Assert.Equal(applied.TargetFingerprint, restored.TargetFingerprint);
    }

    [Fact]
    public void Json_default_numeric_format_is_stable_across_serialization()
    {
        var target = CreateTarget(CreateDefaultsUnit(
            new byte[] { 1 },
            new Dictionary<string, object?> { ["value"] = 42.0d }));
        var executor = new FakeExecutor();
        var applied = PhysicalSchemaApplication.Apply(target, executor, PlannedAt.AddMinutes(1)).AppliedState!;

        var restored = PhysicalSchemaAppliedStateSerializer.Deserialize(
            PhysicalSchemaAppliedStateSerializer.Serialize(applied));

        Assert.Equal(applied.TargetFingerprint, restored.TargetFingerprint);
        Assert.Equal(applied.Snapshot.Subject.Fingerprint, restored.Snapshot.Subject.Fingerprint);
    }

    [Fact]
    public void Json_element_defaults_are_snapshotted_without_retaining_the_document()
    {
        using var document = JsonDocument.Parse("{\"b\":2,\"a\":[true,null]}");
        var subject = new SchemaSubject(new StorageUnit
        {
            Id = new StorageUnitId("json-element"),
            Name = "JsonElement",
            Columns =
            [
                new() { Name = "id", Type = PortableType.Int32, IsNullable = false },
                new() { Name = "payload", Type = PortableType.Json, Default = new PortableDefault(document.RootElement) }
            ],
            Key = new KeyDefinition { Columns = ["id"] }
        });

        document.Dispose();
        Assert.IsType<Dictionary<string, object?>>(subject.Columns[1].Default!.Value);
        Assert.Equal("JsonElement", subject.Name);
    }

    [Fact]
    public void Applied_history_rejects_an_identity_that_does_not_match_its_payload()
    {
        var target = CreateTarget(CreateUnit(includePriority: false));
        var plan = PhysicalSchemaDiffPlanner.Plan(target, PhysicalSchemaHistoryState.Empty, PlannedAt);
        var operation = plan.Snapshot.SemanticOperations[0];
        var corrupted = operation with { Identity = "forged" };

        Assert.Throws<InvalidOperationException>(() =>
            new PhysicalSchemaAppliedSnapshot(target.Subject, [corrupted], []));
    }

    [Fact]
    public void Applied_history_rejects_an_extra_ledger_operation()
    {
        var target = CreateTarget(CreateUnit(includePriority: false));
        var executor = new FakeExecutor();
        var applied = PhysicalSchemaApplication.Apply(target, executor, PlannedAt.AddMinutes(1)).AppliedState!;
        var rogue = new AddColumnOperation(target.Subject,
            new ColumnDefinition { Name = "rogue", Type = PortableType.String });
        var forgedLedger = applied.AppliedOperations.Append(new PhysicalSchemaAppliedOperation(
            rogue.Identity,
            rogue.Fingerprint,
            rogue.Kind,
            rogue.SubjectId,
            rogue.SubjectIdentity,
            rogue.SlotIdentity,
            PlannedAt,
            rogue.CanonicalPayload));

        Assert.Throws<InvalidOperationException>(() => new PhysicalSchemaAppliedState(
            target,
            applied.PlannedAt,
            applied.AppliedAt,
            applied.Snapshot,
            forgedLedger));
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
                ? new[]
                {
                    new ColumnDefinition
                    {
                        Name = "priority",
                        Type = PortableType.Int32,
                        IsNullable = false,
                        Default = new PortableDefault(0)
                    }
                }
                : Array.Empty<ColumnDefinition>())
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        Indexes = includePriority
            ? [new IndexDefinition { Name = "by_priority", Columns = [new IndexColumn("priority")] }]
            : []
    };

    private static StorageUnit CreateDefaultsUnit(byte[] binary, Dictionary<string, object?> json) => new()
    {
        Id = new StorageUnitId("defaults"),
        Name = "Defaults",
        Columns =
        [
            new() { Name = "id", Type = PortableType.Int32, IsNullable = false },
            new() { Name = "binary", Type = PortableType.Binary, MaxLength = 10, Default = new PortableDefault(binary) },
            new() { Name = "json", Type = PortableType.Json, Default = new PortableDefault(json) }
        ],
        Key = new KeyDefinition { Columns = ["id"] }
    };

    private static StorageUnit CreateNullableIndexUnit(MissingValueBehavior missingValues) => new()
    {
        Id = new StorageUnitId("catalog"),
        Name = "Catalog",
        Columns =
        [
            new() { Name = "id", Type = PortableType.Guid, IsNullable = false },
            new() { Name = "category", Type = PortableType.String, IsNullable = true, MaxLength = 50 }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        Indexes =
        [
            new IndexDefinition
            {
                Name = "by_category",
                Columns = [new IndexColumn("category")],
                MissingValues = missingValues
            }
        ]
    };

    private static StorageUnit CreateNonNullableIndexUnit(MissingValueBehavior missingValues) => new()
    {
        Id = new StorageUnitId("catalog"),
        Name = "Catalog",
        Columns =
        [
            new() { Name = "id", Type = PortableType.Guid, IsNullable = false }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        Indexes =
        [
            new IndexDefinition
            {
                Name = "by_id",
                Columns = [new IndexColumn("id")],
                MissingValues = missingValues
            }
        ]
    };

    /// <summary>
    /// Rename support must not restate every deployed catalog. A declaration that renames nothing
    /// records no logical id at all, so its applied state stays exactly the document — and the
    /// fingerprint — an earlier build wrote.
    /// </summary>
    [Fact]
    public void A_declaration_that_renames_nothing_records_no_logical_id()
    {
        var executor = new FakeExecutor();
        PhysicalSchemaApplication.Apply(CreateTarget(CreateUnit(includePriority: true)), executor, PlannedAt.AddMinutes(1));

        var recorded = PhysicalSchemaAppliedStateSerializer.Serialize(executor.AppliedState!);

        // The unit id serializes as an object; a column id would be a bare string.
        Assert.DoesNotContain("\"id\":\"", recorded, StringComparison.Ordinal);
        Assert.Equal(
            executor.AppliedState!.TargetFingerprint,
            PhysicalSchemaAppliedStateSerializer.Deserialize(recorded).TargetFingerprint);
    }

    [Fact]
    public void A_renamed_column_records_the_logical_id_it_is_planned_under()
    {
        var executor = new FakeExecutor();
        PhysicalSchemaApplication.Apply(CreateTarget(CreateUnit(includePriority: false)), executor, PlannedAt.AddMinutes(1));
        var renamed = CreateTarget(RenameColumn(CreateUnit(includePriority: false), "name", "full_name"));

        var applied = PhysicalSchemaApplication.Apply(
            renamed, executor, PlannedAt.AddMinutes(2), _ => PhysicalSchemaPlanAuthorization.Allow);
        var recorded = PhysicalSchemaAppliedStateSerializer.Serialize(applied.AppliedState!);

        Assert.Contains("\"id\":\"name\"", recorded, StringComparison.Ordinal);
        Assert.Equal(
            applied.AppliedState!.TargetFingerprint,
            PhysicalSchemaAppliedStateSerializer.Deserialize(recorded).TargetFingerprint);
    }

    [Fact]
    public void Altering_an_indexed_column_takes_its_index_out_of_the_way_and_puts_it_back()
    {
        var executor = new FakeExecutor();
        PhysicalSchemaApplication.Apply(CreateTarget(CreateUnit(includePriority: true)), executor, PlannedAt.AddMinutes(1));
        var widened = CreateUnit(includePriority: true) with
        {
            Columns = [.. CreateUnit(includePriority: true).Columns.Select(column => column.Name == "priority"
                ? column with { Type = PortableType.Int64 }
                : column)]
        };

        var plan = PhysicalSchemaDiffPlanner.Plan(
            CreateTarget(widened),
            PhysicalSchemaHistoryState.FromApplied(executor.AppliedState!),
            PlannedAt.AddMinutes(2));

        Assert.True(plan.IsApplicable, string.Join("; ", plan.Refusals.Select(refusal => refusal.Message)));
        var operations = plan.Operations.ToArray();
        var dropIndex = Assert.Single(plan.Operations.OfType<DropPhysicalIndexOperation>());
        var alter = Assert.Single(plan.Operations.OfType<AlterColumnOperation>());
        var createIndex = Assert.Single(plan.Operations.OfType<CreatePhysicalIndexOperation>());
        Assert.Equal("by_priority", dropIndex.Index.Name);
        Assert.Equal("by_priority", createIndex.Index.Name);
        Assert.True(Array.IndexOf(operations, dropIndex) < Array.IndexOf(operations, alter));
        Assert.True(Array.IndexOf(operations, alter) < Array.IndexOf(operations, createIndex));

        var applied = PhysicalSchemaApplication.Apply(
            CreateTarget(widened),
            executor,
            PlannedAt.AddMinutes(3),
            _ => PhysicalSchemaPlanAuthorization.Allow);
        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, applied.Outcome);
        Assert.Empty(PhysicalSchemaDiffPlanner.Plan(
                CreateTarget(widened),
                PhysicalSchemaHistoryState.FromApplied(applied.AppliedState!),
                PlannedAt.AddMinutes(4))
            .Operations);
    }

    [Fact]
    public void Renaming_a_column_plans_a_rename_rather_than_a_drop_and_an_add()
    {
        var executor = new FakeExecutor();
        PhysicalSchemaApplication.Apply(CreateTarget(CreateUnit(includePriority: false)), executor, PlannedAt.AddMinutes(1));

        var plan = PhysicalSchemaDiffPlanner.Plan(
            CreateTarget(RenameColumn(CreateUnit(includePriority: false), "name", "full_name")),
            PhysicalSchemaHistoryState.FromApplied(executor.AppliedState!),
            PlannedAt.AddMinutes(2));

        Assert.True(plan.IsApplicable, string.Join("; ", plan.Refusals.Select(refusal => refusal.Message)));
        var rename = Assert.Single(plan.Operations.OfType<RenameColumnOperation>());
        Assert.Equal("name", rename.FromName);
        Assert.Equal("full_name", rename.ToName);
        Assert.Empty(plan.Operations.OfType<DropColumnOperation>());
        Assert.Empty(plan.Operations.OfType<AddColumnOperation>());
        // A rename carries data, so it is authorized as semantic work rather than destructive.
        var protection = PhysicalSchemaPlanProtection.Inspect(plan.Operations);
        Assert.Empty(protection.DestructiveOperations);
        Assert.Contains("rename-column:customer.full_name", protection.SemanticMigrationIdentities);
    }

    /// <summary>
    /// A rename is only a rename because the declaration claims continuity. Renaming the column
    /// without keeping its logical id says the old column is gone and a new one has arrived, and the
    /// plan takes the declaration at its word — under destructive authorization.
    /// </summary>
    [Fact]
    public void Renaming_a_column_without_keeping_its_logical_id_plans_a_drop_and_an_add()
    {
        var executor = new FakeExecutor();
        PhysicalSchemaApplication.Apply(CreateTarget(CreateUnit(includePriority: false)), executor, PlannedAt.AddMinutes(1));
        var replaced = CreateUnit(includePriority: false) with
        {
            Columns = [.. CreateUnit(includePriority: false).Columns.Select(column => column.Name == "name"
                ? column with { Name = "full_name", IsNullable = true }
                : column)]
        };

        var plan = PhysicalSchemaDiffPlanner.Plan(
            CreateTarget(replaced),
            PhysicalSchemaHistoryState.FromApplied(executor.AppliedState!),
            PlannedAt.AddMinutes(2));

        Assert.True(plan.IsApplicable, string.Join("; ", plan.Refusals.Select(refusal => refusal.Message)));
        Assert.Empty(plan.Operations.OfType<RenameColumnOperation>());
        Assert.Equal("name", Assert.Single(plan.Operations.OfType<DropColumnOperation>()).Column.Name);
        Assert.Equal("full_name", Assert.Single(plan.Operations.OfType<AddColumnOperation>()).Column.Name);
        Assert.Contains(
            "drop-column:customer.name",
            PhysicalSchemaPlanProtection.Inspect(plan.Operations).DestructiveOperations.Select(item => item.Address));
    }

    [Fact]
    public void An_applied_rename_records_the_new_name_and_replans_as_a_no_op()
    {
        var executor = new FakeExecutor();
        PhysicalSchemaApplication.Apply(CreateTarget(CreateUnit(includePriority: false)), executor, PlannedAt.AddMinutes(1));
        var renamed = CreateTarget(RenameColumn(CreateUnit(includePriority: false), "name", "full_name"));

        var applied = PhysicalSchemaApplication.Apply(renamed, executor, PlannedAt.AddMinutes(2), _ => PhysicalSchemaPlanAuthorization.Allow);

        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, applied.Outcome);
        // The rename itself leaves no residue: the ledger simply names the column its new name.
        Assert.DoesNotContain(applied.AppliedState!.Snapshot.SemanticOperations,
            operation => operation.Kind == PhysicalSchemaOperationKind.RenameColumn);
        Assert.Contains(applied.AppliedState.Snapshot.SemanticOperations,
            operation => operation.Kind == PhysicalSchemaOperationKind.AddColumn && operation.SubjectIdentity == "full_name");
        Assert.DoesNotContain(applied.AppliedState.Snapshot.SemanticOperations,
            operation => operation.SubjectIdentity == "name");
        Assert.Empty(PhysicalSchemaDiffPlanner.Plan(
                renamed,
                PhysicalSchemaHistoryState.FromApplied(applied.AppliedState),
                PlannedAt.AddMinutes(3))
            .Operations);
    }

    [Fact]
    public void Renaming_primary_storage_plans_a_rename_that_carries_its_applied_indexes()
    {
        var executor = new FakeExecutor();
        PhysicalSchemaApplication.Apply(CreateTarget(CreateUnit(includePriority: true)), executor, PlannedAt.AddMinutes(1));

        var renamed = CreateTarget(CreateUnit(includePriority: true) with { Name = "CustomerAccount" });
        var plan = PhysicalSchemaDiffPlanner.Plan(
            renamed,
            PhysicalSchemaHistoryState.FromApplied(executor.AppliedState!),
            PlannedAt.AddMinutes(2));

        Assert.True(plan.IsApplicable, string.Join("; ", plan.Refusals.Select(refusal => refusal.Message)));
        var rename = Assert.Single(plan.Operations.OfType<RenamePrimaryStorageOperation>());
        Assert.Equal("Customer", rename.FromName);
        Assert.Equal("CustomerAccount", rename.ToName);
        Assert.Equal("by_priority", Assert.Single(rename.CarriedIndexes).Name);
        Assert.Empty(plan.Operations.OfType<CreatePrimaryStorageOperation>());
        Assert.Empty(plan.Operations.OfType<DropPrimaryStorageOperation>());
        // Column work is keyed on the storage unit id, so renaming the storage churns no columns.
        Assert.Empty(plan.Operations.OfType<AddColumnOperation>());
        Assert.Empty(plan.Operations.OfType<DropColumnOperation>());
    }

    [Fact]
    public void Dropping_a_column_plans_an_authorized_drop_and_shrinks_the_applied_ledger()
    {
        var executor = new FakeExecutor();
        PhysicalSchemaApplication.Apply(CreateTarget(CreateUnit(includePriority: true)), executor, PlannedAt.AddMinutes(1));
        var before = executor.AppliedState!.Snapshot.SemanticOperations.Length;
        var withoutPriority = CreateUnit(includePriority: true) with
        {
            Columns = [.. CreateUnit(includePriority: true).Columns.Where(column => column.Name != "priority")],
            Indexes = []
        };

        var plan = PhysicalSchemaDiffPlanner.Plan(
            CreateTarget(withoutPriority),
            PhysicalSchemaHistoryState.FromApplied(executor.AppliedState),
            PlannedAt.AddMinutes(2));

        Assert.True(plan.IsApplicable, string.Join("; ", plan.Refusals.Select(refusal => refusal.Message)));
        var drop = Assert.Single(plan.Operations.OfType<DropColumnOperation>());
        Assert.Equal("priority", drop.Column.Name);
        var dropIndex = Assert.Single(plan.Operations.OfType<DropPhysicalIndexOperation>());
        Assert.Equal("by_priority", dropIndex.Index.Name);
        // The index has to go before the column it keys on.
        Assert.True(Array.IndexOf(plan.Operations.ToArray(), dropIndex) < Array.IndexOf(plan.Operations.ToArray(), drop));

        var applied = PhysicalSchemaApplication.Apply(
            CreateTarget(withoutPriority),
            executor,
            PlannedAt.AddMinutes(3),
            _ => PhysicalSchemaPlanAuthorization.Allow);

        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, applied.Outcome);
        Assert.True(applied.AppliedState!.Snapshot.SemanticOperations.Length < before);
        Assert.DoesNotContain(applied.AppliedState.Snapshot.SemanticOperations,
            operation => operation.SubjectIdentity is "priority" or "by_priority");
        Assert.DoesNotContain(applied.AppliedState.AppliedOperations,
            operation => operation.SubjectIdentity is "priority" or "by_priority");
        Assert.Empty(PhysicalSchemaDiffPlanner.Plan(
                CreateTarget(withoutPriority),
                PhysicalSchemaHistoryState.FromApplied(applied.AppliedState),
                PlannedAt.AddMinutes(4))
            .Operations);
    }

    [Fact]
    public void A_dropped_column_is_authorized_by_its_readable_address()
    {
        var executor = new FakeExecutor();
        var unit = CreateOrdersUnit(includeLegacyTotal: true);
        PhysicalSchemaApplication.Apply(CreateTarget(unit), executor, PlannedAt.AddMinutes(1));

        var plan = PhysicalSchemaDiffPlanner.Plan(
            CreateTarget(CreateOrdersUnit(includeLegacyTotal: false)),
            PhysicalSchemaHistoryState.FromApplied(executor.AppliedState!),
            PlannedAt.AddMinutes(2));

        var protection = PhysicalSchemaPlanProtection.Inspect(plan.Operations);
        var drop = Assert.Single(protection.DestructiveOperations);
        Assert.Equal("drop-column:orders.legacy_total", drop.Address);
        Assert.True(drop.IsAuthorizedBy(new HashSet<string>(StringComparer.Ordinal) { "drop-column:orders.legacy_total" }));
        Assert.True(drop.IsAuthorizedBy(new HashSet<string>(StringComparer.Ordinal) { drop.Identity }));
        Assert.False(drop.IsAuthorizedBy(new HashSet<string>(StringComparer.Ordinal) { "drop-column:orders.total" }));
    }

    [Fact]
    public void An_address_two_operations_answer_to_authorizes_neither()
    {
        static PhysicalSchemaTarget Target(StorageUnit unit, string definition) => new(
            new SchemaSubject(unit),
            Provider,
            [
                new ProviderPhysicalSchemaDefinition(Provider.Name, unit.Id, "batch-type", "customer_batch", definition),
                new ProviderPhysicalSchemaDefinition(Provider.Name, unit.Id, "stream-type", "customer_batch", definition)
            ]);

        var executor = new FakeExecutor();
        PhysicalSchemaApplication.Apply(Target(CreateUnit(includePriority: false), "[\"id\"]"), executor, PlannedAt.AddMinutes(1));

        var plan = PhysicalSchemaDiffPlanner.Plan(
            Target(CreateUnit(includePriority: false), "[\"id\",\"name\"]"),
            PhysicalSchemaHistoryState.FromApplied(executor.AppliedState!),
            PlannedAt.AddMinutes(2));

        var protection = PhysicalSchemaPlanProtection.Inspect(plan.Operations);
        Assert.Equal(2, protection.DestructiveOperations.Length);
        Assert.All(protection.DestructiveOperations, operation => Assert.Null(operation.Address));
        Assert.All(
            protection.DestructiveOperations,
            operation => Assert.False(operation.IsAuthorizedBy(
                new HashSet<string>(StringComparer.Ordinal) { "apply-provider-definition:customer.customer_batch" })));
    }

    [Fact]
    public void Retiring_primary_storage_plans_one_authorized_drop_and_empties_the_ledger()
    {
        var executor = new FakeExecutor();
        PhysicalSchemaApplication.Apply(CreateTarget(CreateUnit(includePriority: true)), executor, PlannedAt.AddMinutes(1));

        var retired = new PhysicalSchemaTarget(
            new SchemaSubject(CreateUnit(includePriority: true), new SchemaEvolutionMetadata(retiresPrimaryStorage: true)),
            Provider);
        var plan = PhysicalSchemaDiffPlanner.Plan(
            retired,
            PhysicalSchemaHistoryState.FromApplied(executor.AppliedState!),
            PlannedAt.AddMinutes(2));

        Assert.True(plan.IsApplicable, string.Join("; ", plan.Refusals.Select(refusal => refusal.Message)));
        var drop = Assert.Single(plan.Operations.OfType<DropPrimaryStorageOperation>());
        Assert.Equal("Customer", drop.Name);
        Assert.Equal("drop-primary-storage:customer.Customer", Assert.Single(
            PhysicalSchemaPlanProtection.Inspect(plan.Operations).DestructiveOperations).Address);

        var applied = PhysicalSchemaApplication.Apply(retired, executor, PlannedAt.AddMinutes(3), _ => PhysicalSchemaPlanAuthorization.Allow);

        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, applied.Outcome);
        Assert.Empty(applied.AppliedState!.Snapshot.SemanticOperations);
        Assert.Empty(PhysicalSchemaDiffPlanner.Plan(
                retired,
                PhysicalSchemaHistoryState.FromApplied(applied.AppliedState),
                PlannedAt.AddMinutes(4))
            .Operations);
    }

    [Fact]
    public void Dropping_a_key_column_is_refused_as_an_invalid_evolution()
    {
        var executor = new FakeExecutor();
        var unit = CreateOrdersUnit(includeLegacyTotal: true);
        PhysicalSchemaApplication.Apply(CreateTarget(unit), executor, PlannedAt.AddMinutes(1));
        var keyless = unit with
        {
            Columns = [.. unit.Columns.Where(column => column.Name != "id"), new ColumnDefinition
            {
                Name = "surrogate",
                Type = PortableType.Guid,
                IsNullable = false
            }],
            Key = new KeyDefinition { Columns = ["surrogate"] }
        };

        var plan = PhysicalSchemaDiffPlanner.Plan(
            CreateTarget(keyless),
            PhysicalSchemaHistoryState.FromApplied(executor.AppliedState!),
            PlannedAt.AddMinutes(2));

        Assert.False(plan.IsApplicable);
        Assert.Contains(plan.Refusals, refusal => refusal.Code == "GW-SCHEMA-004" && refusal.Message.Contains("'id'", StringComparison.Ordinal));
        Assert.Empty(plan.Operations);
    }

    [Fact]
    public void Changing_a_key_columns_portable_type_is_refused_as_an_invalid_evolution()
    {
        var executor = new FakeExecutor();
        var unit = CreateOrdersUnit(includeLegacyTotal: false);
        PhysicalSchemaApplication.Apply(CreateTarget(unit), executor, PlannedAt.AddMinutes(1));
        var retyped = unit with
        {
            Columns = [.. unit.Columns.Select(column => column.Name == "id"
                ? column with { Type = PortableType.String, MaxLength = 64 }
                : column)]
        };

        var plan = PhysicalSchemaDiffPlanner.Plan(
            CreateTarget(retyped),
            PhysicalSchemaHistoryState.FromApplied(executor.AppliedState!),
            PlannedAt.AddMinutes(2));

        Assert.False(plan.IsApplicable);
        Assert.Contains(plan.Refusals, refusal => refusal.Code == "GW-SCHEMA-003");
    }

    [Fact]
    public void A_rename_onto_a_name_another_applied_column_still_holds_is_refused()
    {
        var executor = new FakeExecutor();
        var unit = CreateOrdersUnit(includeLegacyTotal: true);
        PhysicalSchemaApplication.Apply(CreateTarget(unit), executor, PlannedAt.AddMinutes(1));
        // Swap the two physical names. The declaration is valid — both names stay unique — but
        // neither rename can run first without colliding with the other.
        var colliding = unit with
        {
            Columns = [.. unit.Columns.Select(column => column.Name switch
            {
                "legacy_total" => column with { Name = "total", Id = "legacy_total" },
                "total" => column with { Name = "legacy_total", Id = "total" },
                _ => column
            })]
        };

        var plan = PhysicalSchemaDiffPlanner.Plan(
            CreateTarget(colliding),
            PhysicalSchemaHistoryState.FromApplied(executor.AppliedState!),
            PlannedAt.AddMinutes(2));

        Assert.False(plan.IsApplicable);
        Assert.Contains(plan.Refusals, refusal => refusal.Code == "GW-SCHEMA-003" &&
            refusal.Message.Contains("two applies", StringComparison.Ordinal));
    }

    private static StorageUnit RenameColumn(StorageUnit unit, string from, string to) => unit with
    {
        Columns = [.. unit.Columns.Select(column => column.Name == from
            ? column with { Name = to, Id = column.LogicalId }
            : column)],
        Key = new KeyDefinition
        {
            Columns = [.. unit.Key.Columns.Select(column => column == from ? to : column)]
        },
        Indexes = [.. unit.Indexes.Select(index => new IndexDefinition
        {
            Name = index.Name,
            Columns = [.. index.Columns.Select(column => column.Column == from ? new IndexColumn(to, column.Direction) : column)],
            IsUnique = index.IsUnique,
            MissingValues = index.MissingValues,
            SchemaVersion = index.SchemaVersion
        })]
    };

    private static StorageUnit CreateOrdersUnit(bool includeLegacyTotal) => new()
    {
        Id = new StorageUnitId("orders"),
        Name = "orders",
        Columns =
        [
            new() { Name = "id", Type = PortableType.Guid, IsNullable = false },
            new() { Name = "total", Type = PortableType.Decimal, Precision = 18, Scale = 4 },
            ..(includeLegacyTotal
                ? new[] { new ColumnDefinition { Name = "legacy_total", Type = PortableType.Decimal, Precision = 18, Scale = 4 } }
                : [])
        ],
        Key = new KeyDefinition { Columns = ["id"] }
    };

    private static StorageUnit CreateDecimalUnit(int precision, int scale) => new()
    {
        Id = new StorageUnitId("ledger"),
        Name = "Ledger",
        Columns =
        [
            new() { Name = "id", Type = PortableType.Guid, IsNullable = false },
            new() { Name = "total", Type = PortableType.Decimal, Precision = precision, Scale = scale }
        ],
        Key = new KeyDefinition { Columns = ["id"] }
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
