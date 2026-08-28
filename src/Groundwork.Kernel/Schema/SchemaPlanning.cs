using System.Collections.Immutable;

namespace Groundwork.Kernel.Schema;

/// <summary>A refusal that prevents a schema plan from being applied.</summary>
public sealed record SchemaRefusal(string Code, string Message, string Path);

public static class PhysicalSchemaDiffPlanner
{
    /// <summary>
    /// Plans one target. <paramref name="phase"/> selects which half of an expand–contract
    /// evolution the plan describes; it changes nothing for a declaration that supersedes no
    /// column, where both phases derive the same operations.
    /// </summary>
    public static PhysicalSchemaDiffPlan Plan(
        PhysicalSchemaTarget target,
        PhysicalSchemaHistoryState history,
        DateTimeOffset plannedAt,
        LegacyPhysicalSchemaHistoryPolicy legacyHistoryPolicy = LegacyPhysicalSchemaHistoryPolicy.RejectEntriesWithoutAppliedSnapshot,
        SchemaEvolutionPhase phase = SchemaEvolutionPhase.Expand,
        ContractReadinessAssessment? readiness = null)
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
                    "schemaHistory")],
                phase: phase);
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
                    "schemaHistory.identity")],
                phase: phase);
        }

        return target.Subject.Evolution.RetiresPrimaryStorage
            ? PlanRetirement(target, applied, plannedAt, phase)
            : PlanEvolution(target, applied, plannedAt, phase, readiness);
    }

    private static PhysicalSchemaDiffPlan PlanEvolution(
        PhysicalSchemaTarget target,
        PhysicalSchemaAppliedState? applied,
        DateTimeOffset plannedAt,
        SchemaEvolutionPhase phase,
        ContractReadinessAssessment? readiness)
    {
        var supersessions = ColumnSupersessionPlan.Resolve(target, applied);
        if (phase == SchemaEvolutionPhase.Contract &&
            supersessions.ValidateReadiness(target, applied, readiness) is { Length: > 0 } gate)
        {
            return PhysicalSchemaDiffPlan.Invalid(target, plannedAt, gate, phase: phase);
        }

        var desired = DeriveSemanticOperations(target, supersessions, phase);
        var evolution = SchemaEvolutionAnalysis.Analyze(target, applied, desired, supersessions);
        var refusals = evolution.Refusals
            .Concat(ValidateNewRequiredColumns(desired, evolution))
            .ToImmutableArray();
        if (refusals.Length != 0)
        {
            return PhysicalSchemaDiffPlan.Invalid(
                target,
                plannedAt,
                refusals,
                CreateSnapshot(target, desired),
                phase: phase,
                previousDefinition: applied?.Snapshot.Subject.Definition);
        }

        var appliedIdentities = applied?.Snapshot.SemanticOperations
            .Select(operation => operation.Identity)
            .ToHashSet(StringComparer.Ordinal) ?? [];
        var appliedBySlot = applied?.Snapshot.SemanticOperations
            .ToDictionary(operation => operation.SlotIdentity, StringComparer.Ordinal) ?? [];
        var realizedDesired = desired
            .Select(operation => Realize(operation, appliedBySlot, evolution))
            .ToImmutableArray();
        var snapshot = CreateSnapshot(target, realizedDesired);
        var pending = realizedDesired
            .Where(operation =>
                !evolution.SatisfiedIdentities.Contains(operation.Identity) &&
                (IsRebuiltByEvolution(operation, evolution) ||
                 !IsAlreadyApplied(operation, appliedIdentities, appliedBySlot)))
            .Concat(evolution.Operations)
            .OrderBy(OperationOrder)
            .ThenBy(operation => operation.SubjectIdentity, StringComparer.Ordinal)
            .ToList();

        if (applied?.TargetFingerprint == target.Fingerprint && pending.Count == 0)
            return PhysicalSchemaDiffPlan.Valid(
                target,
                plannedAt,
                snapshot,
                [],
                applied.TargetFingerprint,
                phase,
                applied.Snapshot.Subject.Definition);

        pending.Add(new ValidatePhysicalSchemaOperation(target));
        var publish = new PublishAppliedStateOperation(target);
        if (publish.SemanticMigrationId is null && HasAggregationProfileDrift(target, applied))
        {
            // Aggregation profiles are part of the declaration fingerprint but have no physical
            // operation of their own. Marking the publication keeps startup auto-apply from treating
            // a changed profile as an additive schema change and silently changing query semantics.
            publish.SemanticMigrationId = $"aggregation-profile:{target.Subject.Id.Value}";
        }
        pending.Add(publish);
        return PhysicalSchemaDiffPlan.Valid(
            target,
            plannedAt,
            snapshot,
            pending,
            applied?.TargetFingerprint,
            phase,
            applied?.Snapshot.Subject.Definition);
    }

    private static bool HasAggregationProfileDrift(
        PhysicalSchemaTarget target,
        PhysicalSchemaAppliedState? applied)
    {
        var deployed = applied?.Snapshot.Subject.AggregationProfiles ?? [];
        var desired = target.Subject.AggregationProfiles;
        if (deployed.Length != desired.Length)
            return true;

        var deployedByName = deployed.ToDictionary(profile => profile.Name, StringComparer.Ordinal);
        return desired.Any(profile =>
            !deployedByName.TryGetValue(profile.Name, out var prior) ||
            !string.Equals(
                AggregationProfileCanonicalization.Canonicalize(prior),
                AggregationProfileCanonicalization.Canonicalize(profile),
                StringComparison.Ordinal));
    }

    /// <summary>
    /// A retired subject plans exactly one authorized removal and records an empty ledger, so the
    /// evidence that the storage is gone is the ledger no longer describing any of it.
    /// </summary>
    private static PhysicalSchemaDiffPlan PlanRetirement(
        PhysicalSchemaTarget target,
        PhysicalSchemaAppliedState? applied,
        DateTimeOffset plannedAt,
        SchemaEvolutionPhase phase)
    {
        var snapshot = new PhysicalSchemaAppliedSnapshot(target.Subject, [], target.ProviderDefinitions);
        var pending = new List<PhysicalSchemaOperation>();
        if (applied?.Snapshot.SemanticOperations.Length > 0)
        {
            pending.Add(new DropPrimaryStorageOperation(
                target.Subject,
                applied.Snapshot.Subject.Name,
                applied.Snapshot.ProviderDefinitions));
        }
        if (applied?.TargetFingerprint == target.Fingerprint && pending.Count == 0)
            return PhysicalSchemaDiffPlan.Valid(
                target,
                plannedAt,
                snapshot,
                [],
                applied.TargetFingerprint,
                phase,
                applied.Snapshot.Subject.Definition);

        pending.Add(new ValidatePhysicalSchemaOperation(target));
        pending.Add(new PublishAppliedStateOperation(target));
        return PhysicalSchemaDiffPlan.Valid(
            target,
            plannedAt,
            snapshot,
            pending,
            applied?.TargetFingerprint,
            phase,
            applied?.Snapshot.Subject.Definition);
    }

    private static ImmutableArray<PhysicalSchemaOperation> DeriveSemanticOperations(
        PhysicalSchemaTarget target,
        ColumnSupersessionPlan supersessions,
        SchemaEvolutionPhase phase)
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
        operations.AddRange(supersessions.Operations(target.Subject, phase));

        return operations
            .GroupBy(operation => operation.Identity, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(OperationOrder)
            .ThenBy(operation => operation.SubjectIdentity, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    /// <summary>
    /// Adding a required column to storage that already holds rows needs something to put in it.
    /// This is a genuine invalidity rather than an authorization question: no operator approval can
    /// invent the missing values.
    /// </summary>
    private static IEnumerable<SchemaRefusal> ValidateNewRequiredColumns(
        IReadOnlyList<PhysicalSchemaOperation> desired,
        SchemaEvolutionAnalysis evolution)
    {
        if (evolution.AppliedByLogicalSlot.Count == 0)
            return [];

        var logicalColumnIds = desired
            .OfType<AddColumnOperation>()
            .ToDictionary(operation => operation.Column.Name, operation => operation.Column.LogicalId, StringComparer.Ordinal);
        return desired
            .OfType<AddColumnOperation>()
            .Where(operation =>
                !evolution.AppliedByLogicalSlot.ContainsKey(SchemaEvolutionAnalysis.LogicalSlot(
                    operation.Kind,
                    operation.SubjectId,
                    operation.SubjectIdentity,
                    operation.SlotIdentity,
                    logicalColumnIds)) &&
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
                $"schema.columns.{operation.Column.Name}.default"));
    }

    /// <summary>
    /// A provider-owned definition is derived from the declaration rather than declared, so a
    /// changed payload in an existing slot re-applies under authorization instead of refusing.
    /// </summary>
    private static bool IsProviderDefinitionReplacement(
        PhysicalSchemaAppliedOperation applied,
        PhysicalSchemaOperation desired) =>
        applied.Kind == PhysicalSchemaOperationKind.ApplyProviderDefinition &&
        desired is ApplyProviderPhysicalSchemaDefinitionOperation;

    private static PhysicalSchemaOperation Realize(
        PhysicalSchemaOperation operation,
        IReadOnlyDictionary<string, PhysicalSchemaAppliedOperation> appliedBySlot,
        SchemaEvolutionAnalysis evolution)
    {
        if (appliedBySlot.TryGetValue(operation.SlotIdentity, out var appliedDefinition) &&
            IsProviderDefinitionReplacement(appliedDefinition, operation) &&
            !string.Equals(appliedDefinition.Identity, operation.Identity, StringComparison.Ordinal))
        {
            operation.RequiresAuthorization = true;
            return operation;
        }

        // One rule for every index redefinition: an index whose declared shape no longer matches the
        // applied one is dropped and recreated under authorization, whatever changed about it.
        if (operation is not CreatePhysicalIndexOperation create ||
            !evolution.AppliedByLogicalSlot.TryGetValue(
                SchemaEvolutionAnalysis.LogicalSlot(
                    operation.Kind,
                    operation.SubjectId,
                    operation.SubjectIdentity,
                    operation.SlotIdentity,
                    new Dictionary<string, string>(StringComparer.Ordinal)),
                out var applied) ||
            IsAlreadyApplied(operation, applied))
        {
            return operation;
        }

        return new RebuildPhysicalIndexOperation(
            create.Subject,
            create.Index,
            applied.Kind == PhysicalSchemaOperationKind.RebuildPhysicalIndex &&
            SchemaFingerprint.TryParseCanonical(applied.CanonicalPayload, out var parts) &&
            parts.Length >= 6
                ? parts[5]!
                : applied.Fingerprint);
    }

    /// <summary>
    /// An index an alteration dropped out of the way is recreated even though the applied ledger
    /// still describes it: the ledger describes the index that used to be there.
    /// </summary>
    private static bool IsRebuiltByEvolution(
        PhysicalSchemaOperation operation,
        SchemaEvolutionAnalysis evolution) =>
        operation.Kind == PhysicalSchemaOperationKind.CreatePhysicalIndex &&
        evolution.RebuiltIndexNames.Contains(operation.SubjectIdentity);

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

    private static PhysicalSchemaAppliedSnapshot CreateSnapshot(
        PhysicalSchemaTarget target,
        IReadOnlyList<PhysicalSchemaOperation> semanticOperations)
    {
        var operations = semanticOperations
            .Where(operation => !PhysicalSchemaOperation.IsLedgerExcluded(operation.Kind))
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
        PhysicalSchemaOperationKind.RenamePrimaryStorage => 0,
        PhysicalSchemaOperationKind.CreatePrimaryStorage => 0,
        PhysicalSchemaOperationKind.RenameColumn => 1,
        PhysicalSchemaOperationKind.DropIndex => 2,
        PhysicalSchemaOperationKind.AddColumn => 3,
        PhysicalSchemaOperationKind.AlterColumn => 4,
        PhysicalSchemaOperationKind.BackfillColumn => 5,
        PhysicalSchemaOperationKind.FinalizeColumn => 6,
        PhysicalSchemaOperationKind.DropColumn => 7,
        // The marker records what the physical work above just did, so it lands after the removal
        // in the contract phase and after the replacement column is real in the expand phase.
        PhysicalSchemaOperationKind.ColumnSupersession => 8,
        PhysicalSchemaOperationKind.CreatePhysicalIndex => 9,
        PhysicalSchemaOperationKind.RebuildPhysicalIndex => 10,
        PhysicalSchemaOperationKind.ApplyProviderDefinition => 11,
        PhysicalSchemaOperationKind.DropPrimaryStorage => 12,
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
        SchemaEvolutionPhase phase,
        StorageUnit? previousDefinition)
    {
        Target = target;
        PlannedAt = plannedAt;
        Snapshot = snapshot;
        Operations = operations.ToImmutableArray();
        Refusals = refusals.ToImmutableArray();
        ExpectedAppliedTargetFingerprint = expectedAppliedTargetFingerprint;
        Phase = phase;
        PreviousDefinition = previousDefinition;
    }

    public PhysicalSchemaTarget Target { get; }

    /// <summary>Which half of an expand–contract evolution this plan describes.</summary>
    public SchemaEvolutionPhase Phase { get; }

    public DateTimeOffset PlannedAt { get; }

    public ImmutableArray<PhysicalSchemaOperation> Operations { get; }

    public ImmutableArray<SchemaRefusal> Refusals { get; }

    public string? ExpectedAppliedTargetFingerprint { get; }

    /// <summary>
    /// The exact declaration the plan was derived against. Provider coordinators use this
    /// immutable snapshot when describing declaration-only changes; they must not perform a second
    /// unlocked history read after planning.
    /// </summary>
    public StorageUnit? PreviousDefinition { get; }

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

        // The recorded ledger is exactly the snapshot the plan describes: operations executed this
        // round carry their acknowledged time and the rest carry the plan's. Nothing an evolution
        // removed can survive, which is what makes a drop verifiable in the ledger.
        var acknowledged = supplied.ToDictionary(item => item.Identity, StringComparer.Ordinal);
        var appliedOperations = Snapshot.SemanticOperations
            .Select(operation => operation with
            {
                AppliedAt = acknowledged.TryGetValue(operation.Identity, out var acknowledgement)
                    ? acknowledgement.AppliedAt
                    : appliedAt
            })
            .Concat(Operations
                .Where(operation => operation.Kind is PhysicalSchemaOperationKind.ValidatePhysicalSchema or
                    PhysicalSchemaOperationKind.PublishAppliedState)
                .Select(operation => new PhysicalSchemaAppliedOperation(
                    operation.Identity,
                    operation.Fingerprint,
                    operation.Kind,
                    operation.SubjectId,
                    operation.SubjectIdentity,
                    operation.SlotIdentity,
                    acknowledged.TryGetValue(operation.Identity, out var acknowledgement)
                        ? acknowledgement.AppliedAt
                        : appliedAt,
                    operation.CanonicalPayload)))
            .ToArray();
        return new PhysicalSchemaAppliedState(Target, PlannedAt, appliedAt, Snapshot, appliedOperations);
    }

    internal static PhysicalSchemaDiffPlan Valid(
        PhysicalSchemaTarget target,
        DateTimeOffset plannedAt,
        PhysicalSchemaAppliedSnapshot snapshot,
        IEnumerable<PhysicalSchemaOperation> operations,
        string? expectedAppliedTargetFingerprint,
        SchemaEvolutionPhase phase = SchemaEvolutionPhase.Expand,
        StorageUnit? previousDefinition = null) =>
        new(target, plannedAt, snapshot, operations, [], expectedAppliedTargetFingerprint, phase, previousDefinition);

    internal static PhysicalSchemaDiffPlan Invalid(
        PhysicalSchemaTarget target,
        DateTimeOffset plannedAt,
        IEnumerable<SchemaRefusal> refusals,
        PhysicalSchemaAppliedSnapshot? snapshot = null,
        string? expectedAppliedTargetFingerprint = null,
        SchemaEvolutionPhase phase = SchemaEvolutionPhase.Expand,
        StorageUnit? previousDefinition = null) =>
        new(
            target,
            plannedAt,
            snapshot ?? new PhysicalSchemaAppliedSnapshot(target.Subject, [], target.ProviderDefinitions),
            [],
            refusals,
            expectedAppliedTargetFingerprint,
            phase,
            previousDefinition);
}

/// <summary>One planned operation that startup auto-apply must not execute without authorization.</summary>
public sealed record PhysicalSchemaProtectedOperation(string Identity, string? Address)
{
    /// <summary>
    /// Whether the operator named exactly this operation. Both spellings address one operation in
    /// one plan; neither authorizes a class of operations, and the plan fingerprint is required
    /// alongside either.
    /// </summary>
    public bool IsAuthorizedBy(IReadOnlySet<string> authorizations)
    {
        ArgumentNullException.ThrowIfNull(authorizations);
        return authorizations.Contains(Identity) || (Address is not null && authorizations.Contains(Address));
    }
}

/// <summary>Identifies operations that startup auto-apply must not execute without authorization.</summary>
public sealed record PhysicalSchemaPlanProtection(
    ImmutableArray<PhysicalSchemaProtectedOperation> DestructiveOperations,
    ImmutableArray<string> SemanticMigrationIdentities)
{
    public bool IsSafe => DestructiveOperations.Length == 0 && SemanticMigrationIdentities.Length == 0;

    public ImmutableArray<string> DestructiveOperationIdentities =>
        [.. DestructiveOperations.Select(operation => operation.Identity)];

    /// <summary>
    /// The authorization an unauthenticated convenience apply uses: it performs everything the plan
    /// contains except work that destroys data it cannot reconstruct.
    /// </summary>
    /// <remarks>
    /// Rebuilding an index or recomputing a derived backfill is recoverable — re-applying the same
    /// declaration puts the result back from data that never left. Dropping a column or its storage,
    /// or narrowing a column past the values already in it, is not: nothing re-runs the loss away.
    /// Treating those as one category is what makes "this API was already destructive" sound like a
    /// reason to let it drop a column, and it is not one. Removals go through the deployment tool,
    /// where an operator names the exact operation against the exact plan.
    /// </remarks>
    public static PhysicalSchemaPlanAuthorization RefuseIrrecoverableWork(PhysicalSchemaDiffPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var refusals = plan.Operations
            .Where(IsIrrecoverable)
            .Select(operation => new SchemaRefusal(
                "GW-SCHEMA-010",
                $"'{operation.AuthorizationAddress}' destroys data that re-applying cannot restore, so it is " +
                "refused here. Apply it from the deployment tool, which authorizes the exact operation " +
                "against the exact plan.",
                $"schema.apply.{operation.Identity}"))
            .ToArray();
        return refusals.Length == 0
            ? PhysicalSchemaPlanAuthorization.Allow
            : PhysicalSchemaPlanAuthorization.Deny(refusals);
    }

    private static bool IsIrrecoverable(PhysicalSchemaOperation operation) => operation switch
    {
        AlterColumnOperation alter => alter.Alteration == ColumnAlterationKind.Narrowing,
        _ => operation.Kind is PhysicalSchemaOperationKind.DropColumn or
            PhysicalSchemaOperationKind.DropPrimaryStorage
    };

    public static PhysicalSchemaPlanProtection Inspect(IReadOnlyList<PhysicalSchemaOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        var destructive = operations.Where(operation => operation.RequiresAuthorization).ToArray();
        // A readable address only stands in for an identity while it names exactly one operation in
        // this plan. Where two operations would answer to it, the exact identity is the only
        // spelling that authorizes either.
        var ambiguous = destructive
            .GroupBy(operation => operation.AuthorizationAddress, StringComparer.Ordinal)
            .Where(group => group.Select(operation => operation.Identity).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        return new(
            [.. destructive
                .Select(operation => new PhysicalSchemaProtectedOperation(
                    operation.Identity,
                    ambiguous.Contains(operation.AuthorizationAddress) ? null : operation.AuthorizationAddress))
                .DistinctBy(operation => operation.Identity, StringComparer.Ordinal)
                .OrderBy(operation => operation.Identity, StringComparer.Ordinal)],
            [.. operations.Where(operation => !string.IsNullOrWhiteSpace(operation.SemanticMigrationId))
                .Select(operation => operation.SemanticMigrationId!)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(identity => identity, StringComparer.Ordinal)]);
    }
}
