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
        Identity = CreateIdentity(kind, subjectId, subjectIdentity, Fingerprint);
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

    internal static string CreateIdentity(
        PhysicalSchemaOperationKind kind,
        StorageUnitId? subjectId,
        string subjectIdentity,
        string fingerprint) =>
        $"{ToKebabCase(kind.ToString())}:{subjectId?.Value ?? "subject"}:{subjectIdentity}:{fingerprint[..16]}";

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
        column.Type == PortableType.String && (column.Collation is null or PortableCollation.Ordinal)
            ? PortableCollation.Ordinal.ToString()
            : column.Collation?.ToString(),
        column.Generation.ToString(),
        column.Default is null ? null : SchemaValue.Canonicalize(column.Default.Value, column.Type)
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
        LogicalCollation = column.LogicalCollation,
        Default = column.Default is null ? null : new PortableDefault(SchemaValue.Snapshot(column.Default.Value, column.Type)),
        Generation = column.Generation
    };

}

public sealed class BackfillColumnOperation : PhysicalSchemaOperation
{
    internal BackfillColumnOperation(
        SchemaSubject subject,
        ColumnDefinition column,
        DerivedColumnDefinition? derived = null)
        : base(
            PhysicalSchemaOperationKind.BackfillColumn,
            subject.Id,
            column.Name,
            null,
            AddColumnOperation.CanonicalColumn(column),
            derived is null ? null : SchemaFingerprint.Canonicalize([
                derived.Name,
                derived.SourceColumn,
                derived.Projection.ToString(),
                derived.AlgorithmId]))
    {
        Subject = subject;
        Column = AddColumnOperation.Snapshot(column);
        Derived = derived is null ? null : new DerivedColumnDefinition
        {
            Name = derived.Name,
            SourceColumn = derived.SourceColumn,
            Projection = derived.Projection,
            AlgorithmId = derived.AlgorithmId
        };
        RequiresAuthorization = subject.Evolution.IsDestructive || Derived is not null;
        SemanticMigrationId = subject.Evolution.SemanticMigrationId;
    }

    public SchemaSubject Subject { get; }

    public ColumnDefinition Column { get; }

    /// <summary>Provider-neutral source and algorithm metadata for a derived-column backfill.</summary>
    public DerivedColumnDefinition? Derived { get; }
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

    internal static string CanonicalIndex(IndexDefinition index) => CanonicalIndexPayload.From(index).Canonical;

    internal static IndexDefinition Snapshot(IndexDefinition index) => new()
    {
        Name = index.Name,
        Columns = index.Columns.Select(column => new IndexColumn(column.Column, column.Direction)).ToImmutableArray(),
        IsUnique = index.IsUnique,
        MissingValues = index.MissingValues,
        SchemaVersion = index.SchemaVersion
    };
}

internal sealed record CanonicalIndexPayload(
    string Name,
    bool IsUnique,
    MissingValueBehavior MissingValues,
    int SchemaVersion,
    ImmutableArray<IndexColumn> Columns)
{
    public string Canonical => SchemaFingerprint.Canonicalize(
    [
        Name,
        IsUnique.ToString(CultureInfo.InvariantCulture),
        MissingValues.ToString(),
        SchemaVersion.ToString(CultureInfo.InvariantCulture),
        .. Columns.Select(column => $"{column.Column}:{column.Direction}")
    ]);

    public static CanonicalIndexPayload From(IndexDefinition index) => new(
        index.Name,
        index.IsUnique,
        index.MissingValues,
        index.SchemaVersion,
        index.Columns.Select(column => new IndexColumn(column.Column, column.Direction)).ToImmutableArray());

    public IndexDefinition ToDefinition() => new()
    {
        Name = Name,
        IsUnique = IsUnique,
        MissingValues = MissingValues,
        SchemaVersion = SchemaVersion,
        Columns = Columns.Select(column => new IndexColumn(column.Column, column.Direction)).ToImmutableArray()
    };

    public static bool TryParseOperation(string canonicalOperation, out CanonicalIndexPayload payload)
    {
        payload = null!;
        return SchemaFingerprint.TryParseCanonical(canonicalOperation, out var operationParts) &&
            operationParts.Length == 5 &&
            operationParts[4] is { } canonicalIndex &&
            TryParse(canonicalIndex, out payload);
    }

    public static bool TryParse(string canonical, out CanonicalIndexPayload payload)
    {
        payload = null!;
        if (!SchemaFingerprint.TryParseCanonical(canonical, out var parts) ||
            parts.Length < 5 ||
            string.IsNullOrWhiteSpace(parts[0]) ||
            !bool.TryParse(parts[1], out var unique) ||
            !Enum.TryParse<MissingValueBehavior>(parts[2], ignoreCase: false, out var missingValues) ||
            !Enum.IsDefined(missingValues) ||
            !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var schemaVersion))
        {
            return false;
        }

        var columns = ImmutableArray.CreateBuilder<IndexColumn>(parts.Length - 4);
        for (var index = 4; index < parts.Length; index++)
        {
            var term = parts[index];
            var separator = term?.LastIndexOf(':') ?? -1;
            if (separator <= 0 ||
                string.IsNullOrWhiteSpace(term![..separator]) ||
                !Enum.TryParse<SortDirection>(term![(separator + 1)..], ignoreCase: false, out var direction) ||
                !Enum.IsDefined(direction))
            {
                return false;
            }

            columns.Add(new IndexColumn(term[..separator], direction));
        }

        var parsed = new CanonicalIndexPayload(parts[0]!, unique, missingValues, schemaVersion, columns.MoveToImmutable());
        if (!string.Equals(parsed.Canonical, canonical, StringComparison.Ordinal))
            return false;
        payload = parsed;
        return true;
    }
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
        if (!SchemaFingerprint.TryParseCanonical(operation.CanonicalPayload, out var parts) ||
            parts.Length < 4 ||
            parts[0] != operation.Kind.ToString() ||
            parts[1] != operation.SubjectId?.Value ||
            parts[2] != operation.SubjectIdentity ||
            parts[3] != operation.SlotIdentity)
        {
            throw Inconsistent(operation.Identity);
        }

        var expectedSlot = operation.Kind switch
        {
            PhysicalSchemaOperationKind.ApplyProviderDefinition when parts.Length >= 6 =>
                PhysicalSchemaOperation.CreateSlotIdentity(
                    operation.Kind,
                    operation.SubjectId,
                    operation.SubjectIdentity,
                    parts[4],
                    parts[5]),
            PhysicalSchemaOperationKind.RebuildPhysicalIndex =>
                PhysicalSchemaOperation.CreateSlotIdentity(
                    PhysicalSchemaOperationKind.CreatePhysicalIndex,
                    operation.SubjectId,
                    operation.SubjectIdentity),
            _ => PhysicalSchemaOperation.CreateSlotIdentity(
                operation.Kind,
                operation.SubjectId,
                operation.SubjectIdentity)
        };
        var expectedFingerprint = SchemaFingerprint.CreateCanonical(operation.CanonicalPayload);
        var expectedIdentity = PhysicalSchemaOperation.CreateIdentity(
            operation.Kind,
            operation.SubjectId,
            operation.SubjectIdentity,
            expectedFingerprint);
        if (expectedSlot != operation.SlotIdentity ||
            expectedFingerprint != operation.Fingerprint ||
            expectedIdentity != operation.Identity)
        {
            throw Inconsistent(operation.Identity);
        }
    }

    private static InvalidOperationException Inconsistent(string identity) =>
        new($"Applied schema operation '{identity}' is internally inconsistent.");
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
