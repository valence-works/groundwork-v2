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
        var snapshot = CreateSnapshot(target, desired);
        var additiveRefusals = ValidateAdditiveDiff(desired, applied);
        if (additiveRefusals.Length != 0)
            return PhysicalSchemaDiffPlan.Invalid(target, plannedAt, additiveRefusals, snapshot, applied?.TargetFingerprint);

        var appliedIdentities = applied?.Snapshot.SemanticOperations
            .Select(operation => operation.Identity)
            .ToHashSet(StringComparer.Ordinal) ?? [];
        var appliedBySlot = applied?.Snapshot.SemanticOperations
            .ToDictionary(operation => operation.SlotIdentity, StringComparer.Ordinal) ?? [];
        var pending = desired
            .Where(operation => !appliedIdentities.Contains(operation.Identity))
            .Select(operation => Realize(operation, appliedBySlot))
            .ToList();

        if (applied?.TargetFingerprint == target.Fingerprint && pending.Count == 0)
            return PhysicalSchemaDiffPlan.Valid(target, plannedAt, snapshot, [], applied.TargetFingerprint);

        pending.Add(new ValidatePhysicalSchemaOperation(target));
        pending.Add(new PublishAppliedStateOperation(target));
        return PhysicalSchemaDiffPlan.Valid(target, plannedAt, snapshot, pending, applied?.TargetFingerprint);
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
            if (!column.IsNullable)
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
        var refusals = new List<SchemaRefusal>();
        var reportedSubjects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var current in applied.Snapshot.SemanticOperations)
        {
            if (desiredByIdentity.ContainsKey(current.Identity))
                continue;

            if (desiredBySlot.TryGetValue(current.SlotIdentity, out var replacement))
            {
                if (IsIndexWidening(current, replacement))
                    continue;
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

    private static PhysicalSchemaOperation Realize(
        PhysicalSchemaOperation operation,
        IReadOnlyDictionary<string, PhysicalSchemaAppliedOperation> appliedBySlot)
    {
        if (operation is not CreatePhysicalIndexOperation create ||
            !appliedBySlot.TryGetValue(create.SlotIdentity, out var applied) ||
            !IsIndexWidening(applied, operation) ||
            !create.Index.Columns.Any(indexColumn =>
                create.Subject.Columns.Any(column =>
                    column.Name == indexColumn.Column && column.IsNullable)))
        {
            return operation;
        }

        return new RebuildPhysicalIndexOperation(
            create.Subject,
            create.Index,
            applied.Fingerprint);
    }

    private static bool IsIndexWidening(
        PhysicalSchemaAppliedOperation applied,
        PhysicalSchemaOperation desired)
    {
        if (applied.Kind != PhysicalSchemaOperationKind.CreatePhysicalIndex ||
            desired is not CreatePhysicalIndexOperation create ||
            !SchemaFingerprint.TryParseCanonical(applied.CanonicalPayload, out var operationParts) ||
            operationParts.Length < 5 ||
            !SchemaFingerprint.TryParseCanonical(operationParts[4]!, out var currentIndexParts) ||
            !SchemaFingerprint.TryParseCanonical(CreatePhysicalIndexOperation.CanonicalIndex(create.Index), out var desiredIndexParts) ||
            currentIndexParts.Length != desiredIndexParts.Length ||
            currentIndexParts.Length < 4 ||
            currentIndexParts[2] != MissingValueBehavior.Excluded.ToString() ||
            desiredIndexParts[2] != MissingValueBehavior.Included.ToString())
        {
            return false;
        }

        for (var index = 0; index < currentIndexParts.Length; index++)
        {
            if (index == 2)
                continue;
            if (!string.Equals(currentIndexParts[index], desiredIndexParts[index], StringComparison.Ordinal))
                return false;
        }

        return true;
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
        string? expectedAppliedTargetFingerprint)
    {
        Target = target;
        PlannedAt = plannedAt;
        Snapshot = snapshot;
        Operations = operations.ToImmutableArray();
        Refusals = refusals.ToImmutableArray();
        ExpectedAppliedTargetFingerprint = expectedAppliedTargetFingerprint;
    }

    public PhysicalSchemaTarget Target { get; }

    public DateTimeOffset PlannedAt { get; }

    public ImmutableArray<PhysicalSchemaOperation> Operations { get; }

    public ImmutableArray<SchemaRefusal> Refusals { get; }

    public string? ExpectedAppliedTargetFingerprint { get; }

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

        var appliedOperations = Operations.Select(operation =>
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
        }).ToArray();
        return new PhysicalSchemaAppliedState(Target, PlannedAt, appliedAt, Snapshot, appliedOperations);
    }

    internal static PhysicalSchemaDiffPlan Valid(
        PhysicalSchemaTarget target,
        DateTimeOffset plannedAt,
        PhysicalSchemaAppliedSnapshot snapshot,
        IEnumerable<PhysicalSchemaOperation> operations,
        string? expectedAppliedTargetFingerprint) =>
        new(target, plannedAt, snapshot, operations, [], expectedAppliedTargetFingerprint);

    internal static PhysicalSchemaDiffPlan Invalid(
        PhysicalSchemaTarget target,
        DateTimeOffset plannedAt,
        IEnumerable<SchemaRefusal> refusals,
        PhysicalSchemaAppliedSnapshot? snapshot = null,
        string? expectedAppliedTargetFingerprint = null) =>
        new(target, plannedAt, snapshot ?? new PhysicalSchemaAppliedSnapshot(target.Subject, [], target.ProviderDefinitions), [], refusals, expectedAppliedTargetFingerprint);
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
