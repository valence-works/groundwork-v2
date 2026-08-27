using System.Collections.Immutable;

namespace Groundwork.Kernel.Schema;

/// <summary>A refusal that prevents a schema plan from being applied.</summary>
public sealed record SchemaRefusal(string Code, string Message, string Path);

public static class PhysicalSchemaDiffPlanner
{
    public static PhysicalSchemaDiffPlan Plan(
        PhysicalSchemaTarget target,
        PhysicalSchemaHistoryState history,
        DateTimeOffset plannedAt,
        LegacyPhysicalSchemaHistoryPolicy legacyHistoryPolicy = LegacyPhysicalSchemaHistoryPolicy.RejectEntriesWithoutAppliedSnapshot)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(history);
        if (legacyHistoryPolicy != LegacyPhysicalSchemaHistoryPolicy.RejectEntriesWithoutAppliedSnapshot)
            throw new ArgumentOutOfRangeException(nameof(legacyHistoryPolicy), legacyHistoryPolicy, null);

        if (history.HasLegacyHistory && history.AppliedState is null)
        {
            return PhysicalSchemaDiffPlan.Invalid(
                target,
                plannedAt,
                [new SchemaRefusal(
                    "GW-SCHEMA-001",
                    "Legacy schema history has no typed applied snapshot; remove it rather than infer an adopted schema.",
                    "schemaHistory")]);
        }

        var applied = history.AppliedState;
        if (applied is not null &&
            (applied.TargetIdentity != target.Identity ||
             !string.Equals(applied.Provider.Name, target.Provider.Name, StringComparison.Ordinal)))
        {
            return PhysicalSchemaDiffPlan.Invalid(
                target,
                plannedAt,
                [new SchemaRefusal(
                    "GW-SCHEMA-002",
                    $"Applied state '{applied.TargetIdentity}' does not match target '{target.Identity}'.",
                    "schemaHistory.identity")]);
        }

        var desired = DeriveSemanticOperations(target);
        var appliedIdentities = applied?.Snapshot.SemanticOperations
            .Select(operation => operation.Identity)
            .ToHashSet(StringComparer.Ordinal) ?? [];
        var appliedBySlot = applied?.Snapshot.SemanticOperations
            .ToDictionary(operation => operation.SlotIdentity, StringComparer.Ordinal) ?? [];
        var additiveRefusals = ValidateAdditiveDiff(desired, applied);
        if (additiveRefusals.Length != 0)
            return PhysicalSchemaDiffPlan.Invalid(
                target,
                plannedAt,
                additiveRefusals,
                CreateSnapshot(target, desired),
                applied?.TargetFingerprint,
                applied?.AppliedOperations);

        var realizedDesired = desired
            .Select(operation => Realize(operation, appliedBySlot))
            .ToImmutableArray();
        var snapshot = CreateSnapshot(target, realizedDesired);
        var pending = realizedDesired
            .Where(operation => !IsAlreadyApplied(operation, appliedIdentities, appliedBySlot))
            .ToList();

        if (applied?.TargetFingerprint == target.Fingerprint && pending.Count == 0)
            return PhysicalSchemaDiffPlan.Valid(
                target,
                plannedAt,
                snapshot,
                [],
                applied.TargetFingerprint,
                applied.AppliedOperations);

        pending.Add(new ValidatePhysicalSchemaOperation(target));
        pending.Add(new PublishAppliedStateOperation(target));
        return PhysicalSchemaDiffPlan.Valid(
            target,
            plannedAt,
            snapshot,
            pending,
            applied?.TargetFingerprint,
            applied?.AppliedOperations);
    }

    private static ImmutableArray<PhysicalSchemaOperation> DeriveSemanticOperations(PhysicalSchemaTarget target)
    {
        var operations = new List<PhysicalSchemaOperation>
        {
            new CreatePrimaryStorageOperation(target.Subject)
        };

        foreach (var column in target.Subject.Columns.OrderBy(column => column.Name, StringComparer.Ordinal))
        {
            operations.Add(new AddColumnOperation(target.Subject, column));
            var derived = target.Subject.DerivedColumns.FirstOrDefault(item => item.Name == column.Name);
            if (derived is not null)
            {
                operations.Add(new BackfillColumnOperation(target.Subject, column, derived));
                if (!column.IsNullable)
                    operations.Add(new FinalizeColumnOperation(target.Subject, column));
            }
            else if (!column.IsNullable)
            {
                operations.Add(new BackfillColumnOperation(target.Subject, column));
                operations.Add(new FinalizeColumnOperation(target.Subject, column));
            }
        }

        operations.AddRange(target.Subject.Indexes
            .OrderBy(index => index.Name, StringComparer.Ordinal)
            .Select(index => (PhysicalSchemaOperation)new CreatePhysicalIndexOperation(target.Subject, index)));
        operations.AddRange(target.ProviderDefinitions.Select(definition =>
            (PhysicalSchemaOperation)new ApplyProviderPhysicalSchemaDefinitionOperation(definition)));

        return operations
            .GroupBy(operation => operation.Identity, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(OperationOrder)
            .ThenBy(operation => operation.SubjectIdentity, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static ImmutableArray<SchemaRefusal> ValidateAdditiveDiff(
        IReadOnlyList<PhysicalSchemaOperation> desired,
        PhysicalSchemaAppliedState? applied)
    {
        if (applied is null)
            return [];

        var desiredByIdentity = desired.ToDictionary(operation => operation.Identity, StringComparer.Ordinal);
        var desiredBySlot = desired.ToDictionary(operation => operation.SlotIdentity, StringComparer.Ordinal);
        var appliedIdentities = applied.Snapshot.SemanticOperations
            .Select(operation => operation.Identity)
            .ToHashSet(StringComparer.Ordinal);
        var appliedSlots = applied.Snapshot.SemanticOperations
            .Select(operation => operation.SlotIdentity)
            .ToHashSet(StringComparer.Ordinal);
        var refusals = new List<SchemaRefusal>();
        refusals.AddRange(desired
            .OfType<AddColumnOperation>()
            .Where(operation =>
                !appliedSlots.Contains(operation.SlotIdentity) &&
                !operation.Column.IsNullable &&
                operation.Column.Default is null &&
                operation.Column.Generation == ColumnGeneration.Supplied &&
                !desired.OfType<BackfillColumnOperation>().Any(backfill =>
                    backfill.Derived is not null &&
                    string.Equals(backfill.Column.Name, operation.Column.Name, StringComparison.Ordinal)) &&
                string.IsNullOrWhiteSpace(operation.SemanticMigrationId))
            .Select(operation => new SchemaRefusal(
                "GW-SCHEMA-005",
                $"Non-nullable column '{operation.Column.Name}' has no portable default or semantic migration for existing rows.",
                $"schema.columns.{operation.Column.Name}.default")));
        var reportedSubjects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var current in applied.Snapshot.SemanticOperations)
        {
            if (desiredByIdentity.TryGetValue(current.Identity, out _) ||
                desired.Any(operation => IsAlreadyApplied(operation, current)))
                continue;

            if (desiredBySlot.TryGetValue(current.SlotIdentity, out var replacement))
            {
                if (IsIndexWidening(current, replacement) || IsSearchKeyRetarget(current, replacement) ||
                    IsProviderDefinitionReplacement(current, replacement))
                {
                    continue;
                }
                if (!reportedSubjects.Add($"{current.SubjectId?.Value}:{current.SubjectIdentity}"))
                    continue;
                refusals.Add(new SchemaRefusal(
                    "GW-SCHEMA-003",
                    $"Applied operation '{current.Identity}' conflicts with changed definition '{replacement.Identity}'. Schema evolution is additive-only; authorize a deliberate replacement separately.",
                    $"schema.operations.{replacement.SubjectIdentity}"));
                continue;
            }

            if (!reportedSubjects.Add($"{current.SubjectId?.Value}:{current.SubjectIdentity}"))
                continue;
            refusals.Add(new SchemaRefusal(
                "GW-SCHEMA-004",
                $"Applied operation '{current.Identity}' is absent from the desired target. Removing physical schema is not an additive evolution.",
                $"schema.operations.{current.SubjectIdentity}"));
        }

        return refusals.ToImmutableArray();
    }

    /// <summary>
    /// A provider-owned definition is derived from the declaration rather than declared, so a
    /// changed payload in an existing slot re-applies under authorization instead of refusing.
    /// Additive-only continues to protect declared columns, indexes, and primary storage.
    /// </summary>
    private static bool IsProviderDefinitionReplacement(
        PhysicalSchemaAppliedOperation applied,
        PhysicalSchemaOperation desired) =>
        applied.Kind == PhysicalSchemaOperationKind.ApplyProviderDefinition &&
        desired is ApplyProviderPhysicalSchemaDefinitionOperation;

    private static PhysicalSchemaOperation Realize(
        PhysicalSchemaOperation operation,
        IReadOnlyDictionary<string, PhysicalSchemaAppliedOperation> appliedBySlot)
    {
        if (appliedBySlot.TryGetValue(operation.SlotIdentity, out var appliedDefinition) &&
            IsProviderDefinitionReplacement(appliedDefinition, operation) &&
            !string.Equals(appliedDefinition.Identity, operation.Identity, StringComparison.Ordinal))
        {
            operation.RequiresAuthorization = true;
            return operation;
        }

        if (operation is not CreatePhysicalIndexOperation create ||
            !appliedBySlot.TryGetValue(create.SlotIdentity, out var applied) ||
            !create.Index.Columns.Any(indexColumn =>
                create.Subject.Columns.Any(column =>
                    column.Name == indexColumn.Column && column.IsNullable)) &&
            !IsSearchKeyRetarget(applied, operation))
        {
            return operation;
        }

        if (IsIndexWidening(applied, operation) || IsSearchKeyRetarget(applied, operation))
        {
            return new RebuildPhysicalIndexOperation(
                create.Subject,
                create.Index,
                applied.Fingerprint);
        }

        if (TryGetAppliedRebuild(applied, operation, out var supersededFingerprint))
        {
            return new RebuildPhysicalIndexOperation(
                create.Subject,
                create.Index,
                supersededFingerprint);
        }

        return operation;
    }

    private static bool TryGetAppliedRebuild(
        PhysicalSchemaAppliedOperation applied,
        PhysicalSchemaOperation desired,
        out string supersededFingerprint)
    {
        supersededFingerprint = string.Empty;
        if (applied.Kind != PhysicalSchemaOperationKind.RebuildPhysicalIndex ||
            desired is not CreatePhysicalIndexOperation ||
            !SchemaFingerprint.TryParseCanonical(applied.CanonicalPayload, out var appliedParts) ||
            !SchemaFingerprint.TryParseCanonical(desired.CanonicalPayload, out var desiredParts) ||
            appliedParts.Length < 6 || desiredParts.Length < 5 ||
            !string.Equals(appliedParts[4], desiredParts[4], StringComparison.Ordinal))
        {
            return false;
        }

        supersededFingerprint = appliedParts[5]!;
        return true;
    }

    private static bool IsAlreadyApplied(
        PhysicalSchemaOperation desired,
        IReadOnlySet<string> appliedIdentities,
        IReadOnlyDictionary<string, PhysicalSchemaAppliedOperation> appliedBySlot)
    {
        if (appliedIdentities.Contains(desired.Identity))
            return true;
        if (desired is not CreatePhysicalIndexOperation create ||
            !appliedBySlot.TryGetValue(create.SlotIdentity, out var applied))
        {
            return false;
        }

        return IsAlreadyApplied(desired, applied);
    }

    private static bool IsAlreadyApplied(
        PhysicalSchemaOperation desired,
        PhysicalSchemaAppliedOperation applied)
    {
        if (applied.Identity == desired.Identity)
            return true;
        if (desired is not CreatePhysicalIndexOperation ||
            applied.Kind != PhysicalSchemaOperationKind.RebuildPhysicalIndex ||
            !SchemaFingerprint.TryParseCanonical(applied.CanonicalPayload, out var appliedParts) ||
            !SchemaFingerprint.TryParseCanonical(desired.CanonicalPayload, out var desiredParts) ||
            appliedParts.Length < 5 || desiredParts.Length < 5)
        {
            return false;
        }

        return string.Equals(appliedParts[4], desiredParts[4], StringComparison.Ordinal);
    }

    private static bool IsIndexWidening(
        PhysicalSchemaAppliedOperation applied,
        PhysicalSchemaOperation desired)
    {
        if (applied.Kind != PhysicalSchemaOperationKind.CreatePhysicalIndex ||
            desired is not CreatePhysicalIndexOperation create ||
            !CanonicalIndexPayload.TryParseOperation(applied.CanonicalPayload, out var current) ||
            current.MissingValues != MissingValueBehavior.Excluded ||
            create.Index.MissingValues != MissingValueBehavior.Included)
        {
            return false;
        }

        var desiredPayload = CanonicalIndexPayload.From(create.Index);
        return string.Equals(
            (current with { MissingValues = MissingValueBehavior.Included }).Canonical,
            desiredPayload.Canonical,
            StringComparison.Ordinal);
    }

    private static bool IsSearchKeyRetarget(
        PhysicalSchemaAppliedOperation applied,
        PhysicalSchemaOperation desired)
    {
        if (applied.Kind != PhysicalSchemaOperationKind.CreatePhysicalIndex ||
            desired is not CreatePhysicalIndexOperation create ||
            !CanonicalIndexPayload.TryParseOperation(applied.CanonicalPayload, out var current))
        {
            return false;
        }
        return SearchKeyProjection.IsIndexRetarget(current.ToDefinition(), create.Index, create.Subject.DerivedColumns);
    }

    private static PhysicalSchemaAppliedSnapshot CreateSnapshot(
        PhysicalSchemaTarget target,
        IReadOnlyList<PhysicalSchemaOperation> semanticOperations)
    {
        var operations = semanticOperations
            .Where(operation => operation.Kind is not PhysicalSchemaOperationKind.ValidatePhysicalSchema and
                                not PhysicalSchemaOperationKind.PublishAppliedState)
            .Select(operation => new PhysicalSchemaAppliedOperation(
                operation.Identity,
                operation.Fingerprint,
                operation.Kind,
                operation.SubjectId,
                operation.SubjectIdentity,
                operation.SlotIdentity,
                default,
                operation.CanonicalPayload))
            .ToArray();
        return new PhysicalSchemaAppliedSnapshot(target.Subject, operations, target.ProviderDefinitions);
    }

    private static int OperationOrder(PhysicalSchemaOperation operation) => operation.Kind switch
    {
        PhysicalSchemaOperationKind.CreatePrimaryStorage => 0,
        PhysicalSchemaOperationKind.AddColumn => 1,
        PhysicalSchemaOperationKind.BackfillColumn => 2,
        PhysicalSchemaOperationKind.FinalizeColumn => 3,
        PhysicalSchemaOperationKind.CreatePhysicalIndex => 4,
        PhysicalSchemaOperationKind.RebuildPhysicalIndex => 5,
        PhysicalSchemaOperationKind.ApplyProviderDefinition => 6,
        PhysicalSchemaOperationKind.ValidatePhysicalSchema => 100,
        PhysicalSchemaOperationKind.PublishAppliedState => 101,
        _ => 100
    };
}

public sealed class PhysicalSchemaDiffPlan
{
    private PhysicalSchemaDiffPlan(
        PhysicalSchemaTarget target,
        DateTimeOffset plannedAt,
        PhysicalSchemaAppliedSnapshot snapshot,
        IEnumerable<PhysicalSchemaOperation> operations,
        IEnumerable<SchemaRefusal> refusals,
        string? expectedAppliedTargetFingerprint,
        IEnumerable<PhysicalSchemaAppliedOperation>? previousAppliedOperations)
    {
        Target = target;
        PlannedAt = plannedAt;
        Snapshot = snapshot;
        Operations = operations.ToImmutableArray();
        Refusals = refusals.ToImmutableArray();
        ExpectedAppliedTargetFingerprint = expectedAppliedTargetFingerprint;
        PreviousAppliedOperations = previousAppliedOperations?.ToImmutableArray() ?? [];
    }

    public PhysicalSchemaTarget Target { get; }

    public DateTimeOffset PlannedAt { get; }

    public ImmutableArray<PhysicalSchemaOperation> Operations { get; }

    public ImmutableArray<SchemaRefusal> Refusals { get; }

    public string? ExpectedAppliedTargetFingerprint { get; }

    private ImmutableArray<PhysicalSchemaAppliedOperation> PreviousAppliedOperations { get; }

    public bool IsApplicable => Refusals.Length == 0;

    internal PhysicalSchemaAppliedSnapshot Snapshot { get; }

    public PhysicalSchemaAppliedState Complete(
        IReadOnlyList<PhysicalSchemaOperationAcknowledgement> acknowledgements,
        DateTimeOffset appliedAt)
    {
        if (!IsApplicable)
            throw new InvalidOperationException("Cannot complete an inapplicable schema plan.");
        if (Operations.Length == 0)
            throw new InvalidOperationException("A no-change schema plan has no new applied state to record.");

        var expected = Operations
            .Where(operation => operation.Kind != PhysicalSchemaOperationKind.PublishAppliedState)
            .ToArray();
        var supplied = acknowledgements?.ToArray() ?? throw new ArgumentNullException(nameof(acknowledgements));
        if (supplied.Length != expected.Length)
            throw new InvalidOperationException($"Expected {expected.Length} operation acknowledgements but received {supplied.Length}.");

        var byIdentity = supplied
            .GroupBy(acknowledgement => acknowledgement.Identity, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        foreach (var operation in expected)
        {
            if (!byIdentity.TryGetValue(operation.Identity, out var matches) || matches.Length != 1)
                throw new InvalidOperationException($"Operation '{operation.Identity}' was not acknowledged exactly once.");
            if (!string.Equals(matches[0].Fingerprint, operation.Fingerprint, StringComparison.Ordinal))
                throw new PhysicalSchemaFingerprintConflictException(operation.Identity, operation.Fingerprint, matches[0].Fingerprint);
        }

        var currentOperations = Operations
            .Where(operation => operation.Kind is not PhysicalSchemaOperationKind.ValidatePhysicalSchema and
                                not PhysicalSchemaOperationKind.PublishAppliedState)
            .ToArray();
        var carriedOperations = PreviousAppliedOperations
            .Where(operation => operation.Kind is not PhysicalSchemaOperationKind.ValidatePhysicalSchema and
                                not PhysicalSchemaOperationKind.PublishAppliedState)
            .Where(previous => currentOperations.All(operation => operation.SlotIdentity != previous.SlotIdentity));
        var currentAppliedOperations = Operations.Select(operation =>
        {
            var acknowledgement = supplied.SingleOrDefault(item => item.Identity == operation.Identity);
            return new PhysicalSchemaAppliedOperation(
                operation.Identity,
                operation.Fingerprint,
                operation.Kind,
                operation.SubjectId,
                operation.SubjectIdentity,
                operation.SlotIdentity,
                acknowledgement?.AppliedAt ?? appliedAt,
                operation.CanonicalPayload);
        });
        var appliedOperations = carriedOperations.Concat(currentAppliedOperations).ToArray();
        return new PhysicalSchemaAppliedState(Target, PlannedAt, appliedAt, Snapshot, appliedOperations);
    }

    internal static PhysicalSchemaDiffPlan Valid(
        PhysicalSchemaTarget target,
        DateTimeOffset plannedAt,
        PhysicalSchemaAppliedSnapshot snapshot,
        IEnumerable<PhysicalSchemaOperation> operations,
        string? expectedAppliedTargetFingerprint,
        IEnumerable<PhysicalSchemaAppliedOperation>? previousAppliedOperations = null) =>
        new(target, plannedAt, snapshot, operations, [], expectedAppliedTargetFingerprint, previousAppliedOperations);

    internal static PhysicalSchemaDiffPlan Invalid(
        PhysicalSchemaTarget target,
        DateTimeOffset plannedAt,
        IEnumerable<SchemaRefusal> refusals,
        PhysicalSchemaAppliedSnapshot? snapshot = null,
        string? expectedAppliedTargetFingerprint = null,
        IEnumerable<PhysicalSchemaAppliedOperation>? previousAppliedOperations = null) =>
        new(target, plannedAt, snapshot ?? new PhysicalSchemaAppliedSnapshot(target.Subject, [], target.ProviderDefinitions), [], refusals, expectedAppliedTargetFingerprint, previousAppliedOperations);
}

/// <summary>Identifies operations that startup auto-apply must not execute without authorization.</summary>
public sealed record PhysicalSchemaPlanProtection(
    ImmutableArray<string> DestructiveOperationIdentities,
    ImmutableArray<string> SemanticMigrationIdentities)
{
    public bool IsSafe => DestructiveOperationIdentities.Length == 0 && SemanticMigrationIdentities.Length == 0;

    public static PhysicalSchemaPlanProtection Inspect(IReadOnlyList<PhysicalSchemaOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        return new(
            operations.Where(operation => operation.RequiresAuthorization)
                .Select(operation => operation.Identity)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(identity => identity, StringComparer.Ordinal)
                .ToImmutableArray(),
            operations.Where(operation => !string.IsNullOrWhiteSpace(operation.SemanticMigrationId))
                .Select(operation => operation.SemanticMigrationId!)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(identity => identity, StringComparer.Ordinal)
                .ToImmutableArray());
    }
}
