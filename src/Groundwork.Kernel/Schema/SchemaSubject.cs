using System.Collections.Immutable;
using System.Globalization;

namespace Groundwork.Kernel.Schema;

/// <summary>
/// Optional metadata describing why a schema change requires operator authorization.
/// </summary>
public sealed record SchemaEvolutionMetadata
{
    public SchemaEvolutionMetadata(bool isDestructive = false, string? semanticMigrationId = null)
    {
        if (semanticMigrationId is not null && string.IsNullOrWhiteSpace(semanticMigrationId))
            throw new ArgumentException("A semantic migration id cannot be empty.", nameof(semanticMigrationId));

        IsDestructive = isDestructive;
        SemanticMigrationId = semanticMigrationId;
    }

    public bool IsDestructive { get; }

    public string? SemanticMigrationId { get; }
}

/// <summary>
/// A first-class provider-neutral schema subject. It describes one typed storage unit and has no
/// dependency on a route, contract family, serialization format, or provider runtime.
/// </summary>
public sealed class SchemaSubject
{
    private readonly StorageUnit definition;

    public SchemaSubject(StorageUnit definition, SchemaEvolutionMetadata? evolution = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Validate(definition);
        this.definition = Snapshot(definition);
        Evolution = evolution ?? new SchemaEvolutionMetadata();
        Fingerprint = SchemaFingerprint.Create(
            [
                Id.Value,
                Name,
                SchemaVersion.ToString(CultureInfo.InvariantCulture),
                Scope.ToString(),
                Concurrency.ToString(),
                Timestamps.ToString(),
                CanonicalRetention(definition.Retention),
                .. Columns.Select(CanonicalColumn),
                .. Key.Columns.Select(column => $"key:{column}"),
                .. DerivedColumns.Select(CanonicalDerivedColumn),
                .. Indexes.Select(CanonicalIndex),
                .. (definition.AggregationProfiles ?? []).Select(CanonicalAggregationProfile),
                Evolution.IsDestructive ? "destructive" : "safe",
                Evolution.SemanticMigrationId
            ]);
    }

    public StorageUnitId Id => definition.Id;

    public string Name => definition.Name;

    public ImmutableArray<ColumnDefinition> Columns => definition.Columns.ToImmutableArray();

    public KeyDefinition Key => new() { Columns = definition.Key.Columns.ToImmutableArray() };

    public ImmutableArray<DerivedColumnDefinition> DerivedColumns => definition.DerivedColumns.ToImmutableArray();

    public ImmutableArray<IndexDefinition> Indexes => definition.Indexes.ToImmutableArray();

    public ImmutableArray<AggregationProfile> AggregationProfiles => definition.AggregationProfiles.ToImmutableArray();

    public ScopePolicy Scope => definition.Scope;

    public ConcurrencyDeclaration Concurrency => definition.Concurrency;

    public TimestampDeclaration Timestamps => definition.Timestamps;

    public RetentionDeclaration? Retention => definition.Retention;

    public int SchemaVersion => definition.SchemaVersion;

    public SchemaEvolutionMetadata Evolution { get; }

    public string Fingerprint { get; }

    /// <summary>Returns a fresh immutable snapshot suitable for provider mapping.</summary>
    public StorageUnit Definition => Snapshot(definition);

    public override string ToString() => Id.Value;

    private static void Validate(StorageUnit unit)
    {
        ConcurrencyDeclaration.ValidateDeclaration(unit);
        if (string.IsNullOrWhiteSpace(unit.Id.Value))
            throw new ArgumentException("A schema subject requires a non-empty storage-unit id.", nameof(unit));
        if (string.IsNullOrWhiteSpace(unit.Name))
            throw new ArgumentException("A schema subject requires a non-empty name.", nameof(unit));

        var columns = unit.Columns ?? throw new ArgumentException("A schema subject requires columns.", nameof(unit));
        var columnNames = columns.Select(column => column.Name).ToArray();
        if (columnNames.Any(string.IsNullOrWhiteSpace) || columnNames.Distinct(StringComparer.Ordinal).Count() != columnNames.Length)
            throw new ArgumentException("Schema subject columns must have unique non-empty names.", nameof(unit));

        var columnSet = columnNames.ToHashSet(StringComparer.Ordinal);
        if (unit.Key.Columns is null || unit.Key.Columns.Count == 0 ||
            unit.Key.Columns.Any(column => !columnSet.Contains(column)))
        {
            throw new ArgumentException("A schema subject key must name one or more declared columns.", nameof(unit));
        }

        var concurrency = unit.Concurrency ?? throw new ArgumentException(
            "A schema subject requires a concurrency declaration.", nameof(unit));
        if (concurrency.IsNone && concurrency.TokenColumn is not null)
            throw new ArgumentException("A non-optimistic concurrency declaration cannot name a token column.", nameof(unit));
        if (concurrency.IsOptimistic)
        {
            if (string.IsNullOrWhiteSpace(concurrency.TokenColumn))
                throw new ArgumentException("An optimistic concurrency declaration requires a token column name.", nameof(unit));
            var token = columns.FirstOrDefault(column => column.Name == concurrency.TokenColumn);
            if (token is not null && (token.Type != PortableType.Int64 || token.IsNullable ||
                                      token.Default?.Value is not long defaultValue || defaultValue != 0))
            {
                throw new ArgumentException(
                    $"Optimistic token column '{concurrency.TokenColumn}' must be a non-null Int64 with default 0.", nameof(unit));
            }
        }

        var indexes = unit.Indexes ?? [];
        if (indexes.GroupBy(index => index.Name, StringComparer.Ordinal).Any(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() != 1))
            throw new ArgumentException("Schema subject indexes must have unique non-empty names.", nameof(unit));
        if (indexes.Any(index => index.Columns is null || index.Columns.Count == 0 ||
                                 index.Columns.Any(column => !columnSet.Contains(column.Column))))
        {
            throw new ArgumentException("Schema subject indexes must name declared columns.", nameof(unit));
        }

        AggregationProfileValidator.ValidateUnit(unit);
        // SchemaSubject validates only retention here. The full portability pass belongs to
        // provider/build seams and would reject pre-existing declarations that remain valid in
        // this schema model (for example, an unbounded string used only by a legacy index).
        var portability = PortabilityValidator.ValidateRetention(unit);
        if (!portability.IsPortable)
        {
            var refusal = portability.Refusals[0];
            throw new ArgumentException(
                $"{refusal.Code} at {refusal.Path}: {refusal.Message}", nameof(unit));
        }
    }

    private static StorageUnit Snapshot(StorageUnit source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        Columns = source.Columns.Select(Snapshot).ToImmutableArray(),
        Key = new KeyDefinition { Columns = source.Key.Columns.ToImmutableArray() },
        DerivedColumns = (source.DerivedColumns ?? []).Select(derived => new DerivedColumnDefinition
        {
            Name = derived.Name,
            SourceColumn = derived.SourceColumn,
            Projection = derived.Projection,
            AlgorithmId = derived.AlgorithmId
        }).ToImmutableArray(),
        Indexes = (source.Indexes ?? []).Select(index => new IndexDefinition
        {
            Name = index.Name,
            Columns = index.Columns.Select(column => new IndexColumn(column.Column, column.Direction)).ToImmutableArray(),
            IsUnique = index.IsUnique,
            MissingValues = index.MissingValues,
            SchemaVersion = index.SchemaVersion
        }).ToImmutableArray(),
        AggregationProfiles = (source.AggregationProfiles ?? []).Select(Snapshot).ToImmutableArray(),
        Scope = source.Scope,
        Concurrency = source.Concurrency,
        Timestamps = source.Timestamps,
        Retention = source.Retention is null ? null : new RetentionDeclaration
        {
            KeepNewest = source.Retention.KeepNewest,
            OrderColumn = source.Retention.OrderColumn,
            PartitionColumns = source.Retention.PartitionColumns.ToImmutableArray(),
            Trigger = source.Retention.Trigger
        },
        SchemaVersion = source.SchemaVersion
    };

    private static ColumnDefinition Snapshot(ColumnDefinition source) => new()
    {
        Name = source.Name,
        Type = source.Type,
        IsNullable = source.IsNullable,
        MaxLength = source.MaxLength,
        Precision = source.Precision,
        Scale = source.Scale,
        Collation = source.Collation,
        LogicalCollation = source.LogicalCollation,
        Default = source.Default is null ? null : new PortableDefault(SchemaValue.Snapshot(source.Default.Value, source.Type)),
        Generation = source.Generation
    };

    private static string CanonicalColumn(ColumnDefinition column) =>
        SchemaFingerprint.Canonicalize(
        [
            column.Name,
            column.Type.ToString(),
            column.IsNullable.ToString(CultureInfo.InvariantCulture),
            column.MaxLength?.ToString(CultureInfo.InvariantCulture),
            column.Precision?.ToString(CultureInfo.InvariantCulture),
            column.Scale?.ToString(CultureInfo.InvariantCulture),
            column.Collation?.ToString(),
            column.LogicalCollation?.ToString(),
            column.Generation.ToString(),
            column.Default is null ? null : SchemaValue.Canonicalize(column.Default.Value, column.Type)
        ]);

    private static string CanonicalDerivedColumn(DerivedColumnDefinition column) =>
        SchemaFingerprint.Canonicalize([column.Name, column.SourceColumn, column.Projection.ToString(), column.AlgorithmId]);

    private static string CanonicalIndex(IndexDefinition index) => CanonicalIndexPayload.From(index).Canonical;

    private static AggregationProfile Snapshot(AggregationProfile profile) =>
        AggregationProfileSnapshot.Capture(profile);

    private static string CanonicalAggregationProfile(AggregationProfile profile) =>
        AggregationProfileCanonicalization.Canonicalize(profile);

    private static string CanonicalRetention(RetentionDeclaration? retention) => retention is null
        ? "retention:none"
        : SchemaFingerprint.Canonicalize([
            "retention",
            retention.KeepNewest.ToString(CultureInfo.InvariantCulture),
            retention.OrderColumn,
            retention.Trigger.ToString(),
            .. retention.PartitionColumns]);
}

/// <summary>Provider-owned schema materialization metadata carried through the neutral plan.</summary>
public sealed class ProviderPhysicalSchemaDefinition : IEquatable<ProviderPhysicalSchemaDefinition>
{
    public ProviderPhysicalSchemaDefinition(
        string providerName,
        StorageUnitId subjectId,
        string kind,
        string subjectIdentity,
        string canonicalDefinition)
    {
        ProviderName = string.IsNullOrWhiteSpace(providerName)
            ? throw new ArgumentException("A provider name is required.", nameof(providerName))
            : providerName;
        SubjectId = string.IsNullOrWhiteSpace(subjectId.Value)
            ? throw new ArgumentException("A subject id is required.", nameof(subjectId))
            : subjectId;
        Kind = string.IsNullOrWhiteSpace(kind)
            ? throw new ArgumentException("A definition kind is required.", nameof(kind))
            : kind;
        SubjectIdentity = string.IsNullOrWhiteSpace(subjectIdentity)
            ? throw new ArgumentException("A subject identity is required.", nameof(subjectIdentity))
            : subjectIdentity;
        CanonicalDefinition = string.IsNullOrWhiteSpace(canonicalDefinition)
            ? throw new ArgumentException("Canonical definition content is required.", nameof(canonicalDefinition))
            : canonicalDefinition;
        Fingerprint = SchemaFingerprint.Create([ProviderName, SubjectId.Value, Kind, SubjectIdentity, CanonicalDefinition]);
    }

    public string ProviderName { get; }

    public StorageUnitId SubjectId { get; }

    public string Kind { get; }

    public string SubjectIdentity { get; }

    public string CanonicalDefinition { get; }

    public string Fingerprint { get; }

    public bool Equals(ProviderPhysicalSchemaDefinition? other) =>
        other is not null &&
        ProviderName == other.ProviderName &&
        SubjectId == other.SubjectId &&
        Kind == other.Kind &&
        SubjectIdentity == other.SubjectIdentity &&
        CanonicalDefinition == other.CanonicalDefinition;

    public override bool Equals(object? obj) => Equals(obj as ProviderPhysicalSchemaDefinition);

    public override int GetHashCode() => HashCode.Combine(ProviderName, SubjectId, Kind, SubjectIdentity, CanonicalDefinition);
}

/// <summary>Stable provider/subject identity used as the compare-and-swap history key.</summary>
public sealed record PhysicalSchemaTargetIdentity(StorageUnitId SubjectId, string ProviderName)
{
    public override string ToString() => $"{ProviderName}:{SubjectId.Value}";
}

/// <summary>A provider-neutral desired schema target for one first-class subject.</summary>
public sealed class PhysicalSchemaTarget
{
    public PhysicalSchemaTarget(
        SchemaSubject subject,
        ProviderIdentity provider,
        IEnumerable<ProviderPhysicalSchemaDefinition>? providerDefinitions = null)
    {
        Subject = subject ?? throw new ArgumentNullException(nameof(subject));
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        ProviderDefinitions = (providerDefinitions ?? [])
            .OrderBy(definition => definition.ProviderName, StringComparer.Ordinal)
            .ThenBy(definition => definition.Kind, StringComparer.Ordinal)
            .ThenBy(definition => definition.SubjectIdentity, StringComparer.Ordinal)
            .ToImmutableArray();
        if (ProviderDefinitions.Any(definition => !string.Equals(definition.ProviderName, Provider.Name, StringComparison.Ordinal)))
            throw new ArgumentException($"Every provider definition must belong to provider '{Provider.Name}'.", nameof(providerDefinitions));
        if (ProviderDefinitions.Any(definition => definition.SubjectId != Subject.Id))
            throw new ArgumentException("Every provider definition must belong to the target subject.", nameof(providerDefinitions));
        if (ProviderDefinitions.Select(definition => (definition.Kind, definition.SubjectIdentity)).Distinct().Count() != ProviderDefinitions.Length)
            throw new ArgumentException("Provider definitions must have unique kind and subject identities.", nameof(providerDefinitions));

        Identity = new PhysicalSchemaTargetIdentity(Subject.Id, Provider.Name);
        Fingerprint = SchemaFingerprint.Create(
        [
            Subject.Fingerprint,
            Subject.SchemaVersion.ToString(CultureInfo.InvariantCulture),
            Provider.Name,
            Provider.Version,
            .. ProviderDefinitions.Select(definition => definition.Fingerprint)
        ]);
    }

    public SchemaSubject Subject { get; }

    public PhysicalSchemaTargetIdentity Identity { get; }

    public ProviderIdentity Provider { get; }

    public ImmutableArray<ProviderPhysicalSchemaDefinition> ProviderDefinitions { get; }

    public string Fingerprint { get; }
}

/// <summary>Canonical, culture-independent schema fingerprint primitives.</summary>
public static class SchemaFingerprint
{
    public static string Create(IEnumerable<string?> parts) => CreateCanonical(Canonicalize(parts));

    public static string Canonicalize(IEnumerable<string?> parts) =>
        string.Concat(parts.Select(part =>
            $"{(part?.Length ?? -1).ToString(CultureInfo.InvariantCulture)}:{part ?? string.Empty};"));

    public static string CreateCanonical(string canonical) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(canonical)));

    internal static bool TryParseCanonical(string canonical, out ImmutableArray<string?> parts)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        var parsed = new List<string?>();
        var position = 0;
        while (position < canonical.Length)
        {
            var separator = canonical.IndexOf(':', position);
            if (separator < position ||
                !int.TryParse(canonical.AsSpan(position, separator - position), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var length) ||
                length < -1)
            {
                parts = [];
                return false;
            }

            position = separator + 1;
            if (length == -1)
            {
                if (position >= canonical.Length || canonical[position] != ';')
                {
                    parts = [];
                    return false;
                }
                parsed.Add(null);
                position++;
                continue;
            }

            if (canonical.Length - position <= length || canonical[position + length] != ';')
            {
                parts = [];
                return false;
            }
            parsed.Add(canonical.Substring(position, length));
            position += length + 1;
        }

        parts = parsed.ToImmutableArray();
        return string.Equals(Canonicalize(parts), canonical, StringComparison.Ordinal);
    }
}
