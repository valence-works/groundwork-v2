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
