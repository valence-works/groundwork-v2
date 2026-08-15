using System.Collections.Immutable;

namespace Groundwork.Kernel.Schema;

public sealed record PhysicalSchemaAppliedOperation(
    string Identity,
    string Fingerprint,
    PhysicalSchemaOperationKind Kind,
    StorageUnitId? SubjectId,
    string SubjectIdentity,
    string SlotIdentity,
    DateTimeOffset AppliedAt,
    string CanonicalPayload);

/// <summary>Immutable applied semantic schema snapshot used for restart comparison and inspection.</summary>
public sealed class PhysicalSchemaAppliedSnapshot
{
    internal PhysicalSchemaAppliedSnapshot(
        SchemaSubject subject,
        IEnumerable<PhysicalSchemaAppliedOperation> semanticOperations,
        IEnumerable<ProviderPhysicalSchemaDefinition>? providerDefinitions)
    {
        Subject = subject ?? throw new ArgumentNullException(nameof(subject));
        SemanticOperations = semanticOperations
            .Where(operation => operation.Kind is not PhysicalSchemaOperationKind.ValidatePhysicalSchema and
                                not PhysicalSchemaOperationKind.PublishAppliedState)
            .OrderBy(operation => operation.Identity, StringComparer.Ordinal)
            .ToImmutableArray();
        ProviderDefinitions = (providerDefinitions ?? [])
            .OrderBy(definition => definition.ProviderName, StringComparer.Ordinal)
            .ThenBy(definition => definition.Kind, StringComparer.Ordinal)
            .ThenBy(definition => definition.SubjectIdentity, StringComparer.Ordinal)
            .ToImmutableArray();
        foreach (var operation in SemanticOperations)
            PhysicalSchemaOperationIntegrity.Validate(operation);

        CanonicalPayload = SchemaFingerprint.Canonicalize(
        [
            Subject.Fingerprint,
            .. SemanticOperations.Select(operation => operation.CanonicalPayload),
            .. ProviderDefinitions.Select(definition => definition.Fingerprint)
        ]);
        Fingerprint = SchemaFingerprint.CreateCanonical(CanonicalPayload);
    }

    public SchemaSubject Subject { get; }

    public ImmutableArray<PhysicalSchemaAppliedOperation> SemanticOperations { get; }

    public ImmutableArray<ProviderPhysicalSchemaDefinition> ProviderDefinitions { get; }

    public string CanonicalPayload { get; }

    public string Fingerprint { get; }
}

/// <summary>Durable evidence that one complete provider target was applied.</summary>
public sealed class PhysicalSchemaAppliedState
{
    internal PhysicalSchemaAppliedState(
        PhysicalSchemaTarget target,
        DateTimeOffset plannedAt,
        DateTimeOffset appliedAt,
        PhysicalSchemaAppliedSnapshot snapshot,
        IEnumerable<PhysicalSchemaAppliedOperation> appliedOperations)
    {
        TargetIdentity = target.Identity;
        Provider = target.Provider;
        TargetFingerprint = target.Fingerprint;
        PlannedAt = plannedAt;
        AppliedAt = appliedAt;
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        AppliedOperations = appliedOperations.ToImmutableArray();
        foreach (var operation in AppliedOperations)
            PhysicalSchemaOperationIntegrity.Validate(operation);

        ValidateOperationLedger(target);

        var expected = SchemaFingerprint.Create(
        [
            Snapshot.Subject.Fingerprint,
            Snapshot.Subject.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Provider.Name,
            Provider.Version,
            .. Snapshot.ProviderDefinitions.Select(definition => definition.Fingerprint)
        ]);
        if (!string.Equals(expected, TargetFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("Applied schema target fingerprint does not match its snapshot.");
    }

    private void ValidateOperationLedger(PhysicalSchemaTarget target)
    {
        var expected = Snapshot.SemanticOperations
            .Select(operation => operation.Identity)
            .Concat([
                new ValidatePhysicalSchemaOperation(target).Identity,
                new PublishAppliedStateOperation(target).Identity
            ])
            .ToArray();
        var actual = AppliedOperations.Select(operation => operation.Identity).ToArray();
        if (actual.Length != actual.Distinct(StringComparer.Ordinal).Count() ||
            !actual.OrderBy(identity => identity, StringComparer.Ordinal)
                .SequenceEqual(expected.OrderBy(identity => identity, StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Applied schema operation ledger does not match its semantic snapshot.");
        }

        foreach (var operation in Snapshot.SemanticOperations)
            EnsureLedgerMatches(operation, AppliedOperations.Single(item => item.Identity == operation.Identity));

        EnsureLedgerMatches(
            new ValidatePhysicalSchemaOperation(target),
            AppliedOperations.Single(item => item.Kind == PhysicalSchemaOperationKind.ValidatePhysicalSchema));
        EnsureLedgerMatches(
            new PublishAppliedStateOperation(target),
            AppliedOperations.Single(item => item.Kind == PhysicalSchemaOperationKind.PublishAppliedState));
    }

    private static void EnsureLedgerMatches(
        PhysicalSchemaAppliedOperation expected,
        PhysicalSchemaAppliedOperation actual)
    {
        if (actual.Fingerprint != expected.Fingerprint ||
            actual.Kind != expected.Kind ||
            actual.SubjectId != expected.SubjectId ||
            actual.SubjectIdentity != expected.SubjectIdentity ||
            actual.SlotIdentity != expected.SlotIdentity ||
            actual.CanonicalPayload != expected.CanonicalPayload)
        {
            throw new InvalidOperationException(
                $"Applied schema operation '{actual.Identity}' does not match its planned operation.");
        }
    }

    private static void EnsureLedgerMatches(
        PhysicalSchemaOperation expected,
        PhysicalSchemaAppliedOperation actual) =>
        EnsureLedgerMatches(
            new PhysicalSchemaAppliedOperation(
                expected.Identity,
                expected.Fingerprint,
                expected.Kind,
                expected.SubjectId,
                expected.SubjectIdentity,
                expected.SlotIdentity,
                actual.AppliedAt,
                expected.CanonicalPayload),
            actual);

    public PhysicalSchemaTargetIdentity TargetIdentity { get; }

    public ProviderIdentity Provider { get; }

    public string TargetFingerprint { get; }

    public DateTimeOffset PlannedAt { get; }

    public DateTimeOffset AppliedAt { get; }

    public PhysicalSchemaAppliedSnapshot Snapshot { get; }

    public ImmutableArray<PhysicalSchemaAppliedOperation> AppliedOperations { get; }
}

/// <summary>Provider history read by the schema planner.</summary>
public sealed class PhysicalSchemaHistoryState
{
    private PhysicalSchemaHistoryState(PhysicalSchemaAppliedState? appliedState, bool hasLegacyHistory)
    {
        AppliedState = appliedState;
        HasLegacyHistory = hasLegacyHistory;
    }

    public static PhysicalSchemaHistoryState Empty { get; } = new(null, false);

    public static PhysicalSchemaHistoryState LegacyHistoryDetected { get; } = new(null, true);

    public PhysicalSchemaAppliedState? AppliedState { get; }

    public bool HasLegacyHistory { get; }

    public static PhysicalSchemaHistoryState FromApplied(PhysicalSchemaAppliedState appliedState) =>
        new(appliedState ?? throw new ArgumentNullException(nameof(appliedState)), false);
}

public enum LegacyPhysicalSchemaHistoryPolicy
{
    RejectEntriesWithoutAppliedSnapshot
}

public sealed record PhysicalSchemaOperationAcknowledgement(
    string Identity,
    string Fingerprint,
    DateTimeOffset AppliedAt);
