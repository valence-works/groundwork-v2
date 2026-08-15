using System.Collections.Immutable;
using System.Globalization;

namespace Groundwork.Kernel.Schema;

public enum PhysicalSchemaOperationKind
{
    CreatePrimaryStorage,
    AddColumn,
    BackfillColumn,
    FinalizeColumn,
    CreatePhysicalIndex,
    RebuildPhysicalIndex,
    ApplyProviderDefinition,
    ValidatePhysicalSchema,
    PublishAppliedState
}

/// <summary>One immutable semantic schema operation with deterministic identity and fingerprint.</summary>
public abstract class PhysicalSchemaOperation
{
    protected PhysicalSchemaOperation(
        PhysicalSchemaOperationKind kind,
        StorageUnitId? subjectId,
        string subjectIdentity,
        string? slotIdentity = null,
        params string?[] semanticParts)
    {
        if (string.IsNullOrWhiteSpace(subjectIdentity))
            throw new ArgumentException("A schema operation requires a subject identity.", nameof(subjectIdentity));

        Kind = kind;
        SubjectId = subjectId;
        SubjectIdentity = subjectIdentity;
        SlotIdentity = slotIdentity ?? CreateSlotIdentity(kind, subjectId, subjectIdentity);
        CanonicalPayload = SchemaFingerprint.Canonicalize(
            [kind.ToString(), subjectId?.Value, subjectIdentity, SlotIdentity, .. semanticParts]);
        Fingerprint = SchemaFingerprint.CreateCanonical(CanonicalPayload);
        Identity = $"{ToKebabCase(kind.ToString())}:{subjectId?.Value ?? "subject"}:{subjectIdentity}:{Fingerprint[..16]}";
    }

    public PhysicalSchemaOperationKind Kind { get; }

    public StorageUnitId? SubjectId { get; }

    public string SubjectIdentity { get; }

    public string Identity { get; }

    public string SlotIdentity { get; }

    public string Fingerprint { get; }

    public string CanonicalPayload { get; }

    /// <summary>Whether startup auto-apply must obtain explicit operator authorization.</summary>
    public bool RequiresAuthorization { get; internal set; }

    /// <summary>Optional semantic migration marker that startup auto-apply never infers.</summary>
    public string? SemanticMigrationId { get; internal set; }

    internal static string CreateSlotIdentity(
        PhysicalSchemaOperationKind kind,
        StorageUnitId? subjectId,
        string subjectIdentity,
        params string?[] discriminators) =>
        $"{ToKebabCase(kind.ToString())}:{SchemaFingerprint.Create(
            [kind.ToString(), subjectId?.Value, subjectIdentity, .. discriminators])}";

    private static string ToKebabCase(string value)
    {
        var result = new System.Text.StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            if (index != 0 && char.IsUpper(value[index]))
                result.Append('-');
            result.Append(char.ToLowerInvariant(value[index]));
        }
        return result.ToString();
    }
}

public sealed class CreatePrimaryStorageOperation : PhysicalSchemaOperation
{
    internal CreatePrimaryStorageOperation(SchemaSubject subject)
        : base(PhysicalSchemaOperationKind.CreatePrimaryStorage, subject.Id, subject.Name)
    {
        Subject = subject;
        RequiresAuthorization = subject.Evolution.IsDestructive;
        SemanticMigrationId = subject.Evolution.SemanticMigrationId;
    }

    public SchemaSubject Subject { get; }
}

public sealed class AddColumnOperation : PhysicalSchemaOperation
{
    internal AddColumnOperation(SchemaSubject subject, ColumnDefinition column)
        : base(
            PhysicalSchemaOperationKind.AddColumn,
            subject.Id,
            column.Name,
            null,
            CanonicalColumn(column))
    {
        Subject = subject;
        Column = Snapshot(column);
        RequiresAuthorization = subject.Evolution.IsDestructive;
        SemanticMigrationId = subject.Evolution.SemanticMigrationId;
    }

    public SchemaSubject Subject { get; }

    public ColumnDefinition Column { get; }

    internal static string CanonicalColumn(ColumnDefinition column) => SchemaFingerprint.Canonicalize(
    [
        column.Name,
        column.Type.ToString(),
        column.IsNullable.ToString(CultureInfo.InvariantCulture),
        column.MaxLength?.ToString(CultureInfo.InvariantCulture),
        column.Precision?.ToString(CultureInfo.InvariantCulture),
        column.Scale?.ToString(CultureInfo.InvariantCulture),
        column.Collation?.ToString(),
        column.Generation.ToString(),
        Convert.ToString(column.Default?.Value, CultureInfo.InvariantCulture)
    ]);

    internal static ColumnDefinition Snapshot(ColumnDefinition column) => new()
    {
        Name = column.Name,
        Type = column.Type,
        IsNullable = column.IsNullable,
        MaxLength = column.MaxLength,
        Precision = column.Precision,
        Scale = column.Scale,
        Collation = column.Collation,
        Default = column.Default is null ? null : new PortableDefault(CloneValue(column.Default.Value)),
        Generation = column.Generation
    };

    private static object? CloneValue(object? value) => value switch
    {
        byte[] bytes => bytes.ToArray(),
        Array array => array.Clone(),
        _ => value
    };
}

public sealed class BackfillColumnOperation : PhysicalSchemaOperation
{
    internal BackfillColumnOperation(SchemaSubject subject, ColumnDefinition column)
        : base(PhysicalSchemaOperationKind.BackfillColumn, subject.Id, column.Name, null, AddColumnOperation.CanonicalColumn(column))
    {
        Subject = subject;
        Column = AddColumnOperation.Snapshot(column);
        RequiresAuthorization = subject.Evolution.IsDestructive;
        SemanticMigrationId = subject.Evolution.SemanticMigrationId;
    }

    public SchemaSubject Subject { get; }

    public ColumnDefinition Column { get; }
}

public sealed class FinalizeColumnOperation : PhysicalSchemaOperation
{
    internal FinalizeColumnOperation(SchemaSubject subject, ColumnDefinition column)
        : base(PhysicalSchemaOperationKind.FinalizeColumn, subject.Id, column.Name, null, AddColumnOperation.CanonicalColumn(column))
    {
        Subject = subject;
        Column = AddColumnOperation.Snapshot(column);
        RequiresAuthorization = subject.Evolution.IsDestructive;
        SemanticMigrationId = subject.Evolution.SemanticMigrationId;
    }

    public SchemaSubject Subject { get; }

    public ColumnDefinition Column { get; }
}

public sealed class CreatePhysicalIndexOperation : PhysicalSchemaOperation
{
    internal CreatePhysicalIndexOperation(SchemaSubject subject, IndexDefinition index)
        : base(
            PhysicalSchemaOperationKind.CreatePhysicalIndex,
            subject.Id,
            index.Name,
            null,
            CanonicalIndex(index))
    {
        Subject = subject;
        Index = Snapshot(index);
        RequiresAuthorization = subject.Evolution.IsDestructive;
        SemanticMigrationId = subject.Evolution.SemanticMigrationId;
    }

    public SchemaSubject Subject { get; }

    public IndexDefinition Index { get; }

    internal static string CanonicalIndex(IndexDefinition index) => SchemaFingerprint.Canonicalize(
    [
        index.Name,
        index.IsUnique.ToString(CultureInfo.InvariantCulture),
        index.MissingValues.ToString(),
        index.SchemaVersion.ToString(CultureInfo.InvariantCulture),
        .. index.Columns.Select(column => $"{column.Column}:{column.Direction}")
    ]);

    internal static IndexDefinition Snapshot(IndexDefinition index) => new()
    {
        Name = index.Name,
        Columns = index.Columns.Select(column => new IndexColumn(column.Column, column.Direction)).ToImmutableArray(),
        IsUnique = index.IsUnique,
        MissingValues = index.MissingValues,
        SchemaVersion = index.SchemaVersion
    };
}

public sealed class RebuildPhysicalIndexOperation : PhysicalSchemaOperation
{
    internal RebuildPhysicalIndexOperation(SchemaSubject subject, IndexDefinition index, string supersededFingerprint)
        : base(
            PhysicalSchemaOperationKind.RebuildPhysicalIndex,
            subject.Id,
            index.Name,
            CreateSlotIdentity(PhysicalSchemaOperationKind.CreatePhysicalIndex, subject.Id, index.Name),
            CreatePhysicalIndexOperation.CanonicalIndex(index),
            supersededFingerprint)
    {
        Subject = subject;
        Index = CreatePhysicalIndexOperation.Snapshot(index);
        SupersededFingerprint = supersededFingerprint;
        RequiresAuthorization = true;
        SemanticMigrationId = subject.Evolution.SemanticMigrationId;
    }

    public SchemaSubject Subject { get; }

    public IndexDefinition Index { get; }

    public string SupersededFingerprint { get; }
}

public sealed class ApplyProviderPhysicalSchemaDefinitionOperation : PhysicalSchemaOperation
{
    internal ApplyProviderPhysicalSchemaDefinitionOperation(ProviderPhysicalSchemaDefinition definition)
        : base(
            PhysicalSchemaOperationKind.ApplyProviderDefinition,
            definition.SubjectId,
            definition.SubjectIdentity,
            CreateSlotIdentity(
                PhysicalSchemaOperationKind.ApplyProviderDefinition,
                definition.SubjectId,
                definition.SubjectIdentity,
                definition.ProviderName,
                definition.Kind),
            definition.ProviderName,
            definition.Kind,
            definition.Fingerprint,
            definition.CanonicalDefinition) =>
        Definition = definition;

    public ProviderPhysicalSchemaDefinition Definition { get; }
}

public sealed class ValidatePhysicalSchemaOperation : PhysicalSchemaOperation
{
    private readonly PhysicalSchemaTarget target;

    internal ValidatePhysicalSchemaOperation(PhysicalSchemaTarget target)
        : base(
            PhysicalSchemaOperationKind.ValidatePhysicalSchema,
            target.Subject.Id,
            "target",
            null,
            [target.Fingerprint, .. target.ProviderDefinitions.Select(definition => definition.Fingerprint)])
    {
        this.target = target;
        TargetFingerprint = target.Fingerprint;
        Subject = target.Subject;
        ProviderDefinitions = target.ProviderDefinitions;
        RequiresAuthorization = target.Subject.Evolution.IsDestructive;
        SemanticMigrationId = target.Subject.Evolution.SemanticMigrationId;
    }

    public PhysicalSchemaTarget Target => target;

    public SchemaSubject Subject { get; }

    public string TargetFingerprint { get; }

    public ImmutableArray<ProviderPhysicalSchemaDefinition> ProviderDefinitions { get; }
}

public sealed class PublishAppliedStateOperation : PhysicalSchemaOperation
{
    internal PublishAppliedStateOperation(PhysicalSchemaTarget target)
        : base(PhysicalSchemaOperationKind.PublishAppliedState, target.Subject.Id, "target", null, target.Fingerprint)
    {
        TargetFingerprint = target.Fingerprint;
        RequiresAuthorization = target.Subject.Evolution.IsDestructive;
        SemanticMigrationId = target.Subject.Evolution.SemanticMigrationId;
    }

    public string TargetFingerprint { get; }
}

internal static class PhysicalSchemaOperationIntegrity
{
    public static void Validate(PhysicalSchemaAppliedOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var expected = SchemaFingerprint.CreateCanonical(operation.CanonicalPayload);
        if (!string.Equals(expected, operation.Fingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException($"Applied operation '{operation.Identity}' has an invalid fingerprint.");
    }
}

public sealed class PhysicalSchemaFingerprintConflictException(
    string operationIdentity,
    string expectedFingerprint,
    string actualFingerprint)
    : InvalidOperationException(
        $"Operation '{operationIdentity}' fingerprint conflict: expected '{expectedFingerprint}', received '{actualFingerprint}'.")
{
    public string OperationIdentity { get; } = operationIdentity;

    public string ExpectedFingerprint { get; } = expectedFingerprint;

    public string ActualFingerprint { get; } = actualFingerprint;
}
