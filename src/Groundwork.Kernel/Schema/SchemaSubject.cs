using System.Collections.Immutable;
using System.Globalization;

namespace Groundwork.Kernel.Schema;

/// <summary>
/// Optional metadata describing why a schema change requires operator authorization.
/// </summary>
public sealed record SchemaEvolutionMetadata
{
    public SchemaEvolutionMetadata(
        bool isDestructive = false,
        string? semanticMigrationId = null,
        bool retiresPrimaryStorage = false,
        ImmutableArray<ColumnSupersession> supersessions = default,
        TimeSpan dualPresenceWindow = default)
    {
        if (semanticMigrationId is not null && string.IsNullOrWhiteSpace(semanticMigrationId))
            throw new ArgumentException("A semantic migration id cannot be empty.", nameof(semanticMigrationId));
        if (dualPresenceWindow < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dualPresenceWindow), dualPresenceWindow, "A dual-presence window cannot run backwards.");
        }

        IsDestructive = isDestructive;
        SemanticMigrationId = semanticMigrationId;
        RetiresPrimaryStorage = retiresPrimaryStorage;
        // The parameter is an ImmutableArray rather than an IEnumerable so that its name and type
        // bind to the property of the same name: this record is round-tripped through
        // System.Text.Json by PhysicalSchemaAppliedStateSerializer, which refuses a constructor
        // parameter it cannot match to a property.
        Supersessions = supersessions.IsDefaultOrEmpty
            ? []
            : [.. supersessions
                .Select(supersession => supersession ?? throw new ArgumentException(
                    "A column supersession cannot be null.", nameof(supersessions)))
                .OrderBy(supersession => supersession.Name, StringComparer.Ordinal)];
        DualPresenceWindow = dualPresenceWindow;
        if (Supersessions.IsEmpty)
            return;

        if (Supersessions.Select(supersession => supersession.Name)
                .Distinct(StringComparer.Ordinal).Count() != Supersessions.Length)
        {
            throw new ArgumentException(
                "A column can be superseded only once in one declaration.", nameof(supersessions));
        }
        // Every supersession is completed by a backfill, and the readiness gate is that backfill's
        // recorded completion. A supersession with nothing to populate its replacement column is a
        // data-loss trap wearing the workflow's name, so it is refused rather than documented.
        if (string.IsNullOrWhiteSpace(semanticMigrationId))
        {
            throw new ArgumentException(
                "A declaration that supersedes a column requires a semantic migration id: the data migration " +
                "recorded under it is what populates the replacement column, and its recorded completion is " +
                "what opens the contract gate.",
                nameof(semanticMigrationId));
        }
        if (retiresPrimaryStorage)
        {
            throw new ArgumentException(
                "A retired subject drops its whole primary storage, so superseding one of its columns describes " +
                "work that cannot happen.",
                nameof(supersessions));
        }
    }

    public bool IsDestructive { get; }

    public string? SemanticMigrationId { get; }

    /// <summary>
    /// Declares that this subject's primary storage is retired. Planning then produces a single
    /// authorized <c>DropPrimaryStorage</c> operation instead of creating or evolving the unit,
    /// and the applied ledger shrinks to that removal. The declaration is kept and marked rather
    /// than deleted so the removal is a reviewable authorized plan instead of an inference drawn
    /// from an absent declaration.
    /// </summary>
    public bool RetiresPrimaryStorage { get; }

    /// <summary>
    /// Columns this declaration replaces across a dual-presence window, in superseded-column order.
    /// A superseded column is deliberately absent from <see cref="SchemaSubject.Columns"/>: the
    /// declaration that supersedes it cannot then read it, write it, alter it, or rename it, which
    /// is what makes the expand plan invisible to the application version that still owns it.
    /// </summary>
    public ImmutableArray<ColumnSupersession> Supersessions { get; }

    /// <summary>
    /// How long a superseded column must stay in place before the contract plan may remove it,
    /// measured from the later of the retention being recorded and its backfill being recorded
    /// complete. It bounds how long a pre-expand application version may still be writing.
    /// </summary>
    public TimeSpan DualPresenceWindow { get; }
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
                RetentionCanonicalization.Canonicalize(definition.Retention),
                AppendIdempotency is null
                    ? "idempotency:none"
                    : $"idempotency:{AppendIdempotency.Window.Ticks}:{AppendIdempotency.LedgerName}",
                RetentionIdempotency is null
                    ? "retention-idempotency:none"
                    : $"retention-idempotency:{RetentionIdempotency.Window.Ticks}:{RetentionIdempotency.LedgerName}",
                .. Columns.Select(CanonicalColumn),
                .. Key.Columns.Select(column => $"key:{column}"),
                .. DerivedColumns.Select(CanonicalDerivedColumn),
                // Indexes and aggregation profiles are sets, not sequences: naming the same ones
                // in a different order describes the same subject.
                .. Indexes.Select(CanonicalIndex).OrderBy(canonical => canonical, StringComparer.Ordinal),
                .. References.Select(CanonicalReference).OrderBy(canonical => canonical, StringComparer.Ordinal),
                .. (definition.CheckConstraints ?? []).Select(check => CanonicalCheckConstraint(definition, check))
                    .OrderBy(canonical => canonical, StringComparer.Ordinal),
                .. (definition.AggregationProfiles ?? []).Select(CanonicalAggregationProfile)
                    .OrderBy(canonical => canonical, StringComparer.Ordinal),
                Evolution.IsDestructive ? "destructive" : "safe",
                Evolution.SemanticMigrationId,
                // Appended only when set, so an already-deployed subject keeps the exact
                // fingerprint it was recorded under instead of hitting a persisted boundary.
                .. Evolution.RetiresPrimaryStorage ? (string?[])["retired"] : [],
                .. Evolution.Supersessions.IsEmpty
                    ? (string?[])[]
                    : [
                        .. Evolution.Supersessions.Select(supersession => "supersedes:" + supersession.Canonical),
                        "dual-presence:" + Evolution.DualPresenceWindow.Ticks.ToString(CultureInfo.InvariantCulture)
                    ]
            ]);
        ValidateSupersessions(definition, Evolution);
    }

    public StorageUnitId Id => definition.Id;

    public string Name => definition.Name;

    public ImmutableArray<ColumnDefinition> Columns => definition.Columns.ToImmutableArray();

    public KeyDefinition Key => new() { Columns = definition.Key.Columns.ToImmutableArray() };

    public ImmutableArray<DerivedColumnDefinition> DerivedColumns => definition.DerivedColumns.ToImmutableArray();

    public ImmutableArray<IndexDefinition> Indexes => definition.Indexes.ToImmutableArray();

    public ImmutableArray<ReferenceDefinition> References => definition.References.ToImmutableArray();

    public ImmutableArray<CheckConstraintDefinition> CheckConstraints => definition.CheckConstraints.ToImmutableArray();

    public ImmutableArray<AggregationProfile> AggregationProfiles => definition.AggregationProfiles.ToImmutableArray();

    public ScopePolicy Scope => definition.Scope;

    public ConcurrencyDeclaration Concurrency => definition.Concurrency;

    public TimestampDeclaration Timestamps => definition.Timestamps;

    public RetentionDeclaration? Retention => definition.Retention;

    public AppendIdempotencyDeclaration? AppendIdempotency => definition.AppendIdempotency;

    public RetentionIdempotencyDeclaration? RetentionIdempotency => definition.RetentionIdempotency;

    public int SchemaVersion => definition.SchemaVersion;

    /// <summary>
    /// How deployed columns this subject does not describe are treated. Read from the live
    /// declaration, never from persisted applied state, and deliberately not part of
    /// <see cref="Fingerprint"/> — see <see cref="StorageUnit.ForeignColumns"/>.
    /// </summary>
    public ForeignColumnPolicy ForeignColumns => definition.ForeignColumns;

    public SchemaEvolutionMetadata Evolution { get; }

    public string Fingerprint { get; }

    /// <summary>Returns a fresh immutable snapshot suitable for provider mapping.</summary>
    public StorageUnit Definition => Snapshot(definition);

    /// <summary>
    /// Validates a complete schema manifest before provider I/O. Provider identifiers are
    /// compared case-insensitively so a ledger cannot alias any declared unit on a provider
    /// with folded identifiers; the shared default ledger remains valid when spelled identically.
    /// </summary>
    public static void ValidateManifest(IEnumerable<StorageUnit> declarations)
    {
        var units = ValidateManifestDeclarations(declarations);

        var referenceFindings = StorageReferenceValidation.ValidateManifest(units);
        if (referenceFindings.Count != 0)
        {
            throw new ArgumentException(
                "The schema manifest has invalid logical references: " + string.Join(
                    "; ", referenceFindings.Select(finding => $"{finding.Code} at {finding.Path}: {finding.Message}")),
                nameof(declarations));
        }

        ValidateManifestIdentifiers(units, nameof(declarations));
    }

    internal static void ValidateManifestWithoutCrossUnitReferences(IEnumerable<StorageUnit> declarations)
    {
        var units = ValidateManifestDeclarations(declarations);
        ValidateManifestIdentifiers(units, nameof(declarations));
    }

    private static StorageUnit[] ValidateManifestDeclarations(IEnumerable<StorageUnit> declarations)
    {
        ArgumentNullException.ThrowIfNull(declarations);
        var units = declarations.ToArray();
        if (units.Any(unit => unit is null))
            throw new ArgumentException("A schema manifest cannot contain null storage units.", nameof(declarations));

        foreach (var unit in units)
            Validate(unit);
        return units;
    }

    private static void ValidateManifestIdentifiers(IReadOnlyList<StorageUnit> units, string parameterName)
    {
        var names = new Dictionary<string, StorageUnit>(StringComparer.OrdinalIgnoreCase);
        foreach (var unit in units)
        {
            if (!names.TryAdd(unit.Name, unit))
            {
                throw new ArgumentException(
                    $"Schema manifest storage unit names must be unique under provider identifier comparison: '{unit.Name}'.",
                    parameterName);
            }
        }

        var ledgers = new Dictionary<string, (string Name, StorageUnit Unit)>(StringComparer.OrdinalIgnoreCase);
        foreach (var unit in units)
        {
            foreach (var declaration in new[] { unit.AppendIdempotency?.LedgerName, unit.RetentionIdempotency?.LedgerName }
                         .Where(name => name is not null)
                         .Select(name => name!))
            {
                if (ledgers.TryGetValue(declaration, out var prior) &&
                    !string.Equals(prior.Name, declaration, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"Schema manifest ledger names cannot differ only by provider identifier casing: '{prior.Name}' and '{declaration}'.",
                        parameterName);
                }
                ledgers.TryAdd(declaration, (declaration, unit));
                if (names.TryGetValue(declaration, out var owner))
                {
                    throw new ArgumentException(
                        $"Schema manifest ledger '{declaration}' collides with storage unit '{owner.Name}' under provider identifier comparison.",
                        parameterName);
                }
            }
        }
    }

    public override string ToString() => Id.Value;

    /// <summary>
    /// A supersession names a column that is leaving and one that is arriving. The arriving column
    /// has to be declared, and the leaving one must not be: a column that is still declared is not
    /// superseded, it is simply present, and the expand plan would keep maintaining it.
    /// </summary>
    private static void ValidateSupersessions(StorageUnit unit, SchemaEvolutionMetadata evolution)
    {
        if (evolution.Supersessions.IsEmpty)
            return;
        var declared = unit.Columns.Select(column => column.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var supersession in evolution.Supersessions)
        {
            if (declared.Contains(supersession.Name))
            {
                throw new ArgumentException(
                    $"Superseded column '{supersession.Name}' is still declared by '{unit.Name}'. " +
                    "Remove it from the declaration: a superseded column is retained physically and read by " +
                    "nothing the declaration owns.",
                    nameof(unit));
            }
            if (!declared.Contains(supersession.ReplacementColumn))
            {
                throw new ArgumentException(
                    $"Replacement column '{supersession.ReplacementColumn}' for superseded column " +
                    $"'{supersession.Name}' is not declared by '{unit.Name}'.",
                    nameof(unit));
            }
        }
    }

    private static void Validate(StorageUnit unit)
    {
        StorageDeclarationReferenceValidation.ThrowIfInvalid(unit);
        PhysicalConstraintValidation.ThrowIfInvalid(unit);
        ConcurrencyDeclaration.ValidateDeclaration(unit);
        unit.AppendIdempotency?.Validate(unit);
        unit.RetentionIdempotency?.Validate(unit);
        if (string.IsNullOrWhiteSpace(unit.Id.Value))
            throw new ArgumentException("A schema subject requires a non-empty storage-unit id.", nameof(unit));
        if (string.IsNullOrWhiteSpace(unit.Name))
            throw new ArgumentException("A schema subject requires a non-empty name.", nameof(unit));

        var columns = unit.Columns ?? throw new ArgumentException("A schema subject requires columns.", nameof(unit));
        var columnNames = columns.Select(column => column.Name).ToArray();
        if (columnNames.Any(string.IsNullOrWhiteSpace) || columnNames.Distinct(StringComparer.Ordinal).Count() != columnNames.Length)
            throw new ArgumentException("Schema subject columns must have unique non-empty names.", nameof(unit));

        var logicalIds = columns.Select(column => column.LogicalId).ToArray();
        if (logicalIds.Any(string.IsNullOrWhiteSpace) ||
            logicalIds.Distinct(StringComparer.Ordinal).Count() != logicalIds.Length)
        {
            throw new ArgumentException(
                "Schema subject columns must have unique non-empty logical ids.", nameof(unit));
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
        if (indexes.Any(index => index.Columns is null || index.Columns.Count == 0))
        {
            throw new ArgumentException("Schema subject indexes must name at least one column.", nameof(unit));
        }

        AggregationProfileValidator.ValidateUnit(unit);
        var identifiers = PortabilityValidator.ValidatePhysicalIdentifiers(unit);
        if (!identifiers.IsPortable)
        {
            var refusal = identifiers.Refusals[0];
            throw new ArgumentException(
                $"{refusal.Code} at {refusal.Path}: {refusal.Message}", nameof(unit));
        }

        var duplicateIndexes = PortabilityValidator.ValidateDuplicatePhysicalIndexSignatures(unit);
        if (!duplicateIndexes.IsPortable)
        {
            var refusal = duplicateIndexes.Refusals[0];
            throw new ArgumentException(
                $"{refusal.Code} at {refusal.Path}: {refusal.Message}", nameof(unit));
        }

        // The rest of the portability pass belongs to provider/build seams and would reject
        // pre-existing declarations that remain valid in this schema model (for example, an
        // unbounded string used only by a legacy index).
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
        References = (source.References ?? []).Select(reference => new ReferenceDefinition
        {
            Name = reference.Name,
            Columns = (reference.Columns ?? []).ToImmutableArray(),
            TargetUnitId = reference.TargetUnitId,
            TargetScope = reference.TargetScope,
            Enforcement = reference.Enforcement,
            TargetName = reference.TargetName,
            TargetKeyColumns = reference.TargetKeyColumns?.ToImmutableArray(),
            TargetKeyHasProviderSequence = reference.TargetKeyHasProviderSequence
        }).ToImmutableArray(),
        CheckConstraints = (source.CheckConstraints ?? []).Select(check =>
        {
            var column = source.Columns.Single(candidate =>
                string.Equals(candidate.Name, check.Column, StringComparison.Ordinal));
            return new CheckConstraintDefinition
            {
                Name = check.Name,
                Column = check.Column,
                Operator = check.Operator,
                Value = new PortableDefault(SchemaValue.Snapshot(check.Value.Value, column.Type))
            };
        }).ToImmutableArray(),
        AggregationProfiles = (source.AggregationProfiles ?? []).Select(Snapshot).ToImmutableArray(),
        Scope = source.Scope,
        ForeignColumns = source.ForeignColumns,
        AppendIdempotency = source.AppendIdempotency is null ? null : source.AppendIdempotency with { },
        RetentionIdempotency = source.RetentionIdempotency is null ? null : source.RetentionIdempotency with { },
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
        LocaleSortKey = source.LocaleSortKey is null ? null : source.LocaleSortKey with { },
        Default = source.Default is null ? null : new PortableDefault(SchemaValue.Snapshot(source.Default.Value, source.Type)),
        Generation = source.Generation,
        Id = source.Id
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
            column.Default is null ? null : SchemaValue.Canonicalize(column.Default.Value, column.Type),
            .. LocaleSortKeyIdentity(column),
            .. LogicalIdentity(column)
        ]);

    internal static string?[] LocaleSortKeyIdentity(ColumnDefinition column) =>
        column.LocaleSortKey is null
            ? []
            :
            [
                column.LocaleSortKey.CultureName,
                column.LocaleSortKey.MaximumExpansionFactor.ToString(CultureInfo.InvariantCulture)
            ];

    /// <summary>
    /// A column that has never been renamed is planned under its own name, so its logical id
    /// describes nothing extra. Appending the id only once it diverges from the physical name
    /// keeps every already-deployed subject fingerprint byte-identical, so adding rename support
    /// is not itself a persisted schema boundary.
    /// </summary>
    internal static string?[] LogicalIdentity(ColumnDefinition column) =>
        string.Equals(column.LogicalId, column.Name, StringComparison.Ordinal)
            ? []
            : [column.LogicalId];

    private static string CanonicalDerivedColumn(DerivedColumnDefinition column) =>
        SchemaFingerprint.Canonicalize([column.Name, column.SourceColumn, column.Projection.ToString(), column.AlgorithmId]);

    private static string CanonicalIndex(IndexDefinition index) => CanonicalIndexPayload.From(index).Canonical;

    private static string CanonicalReference(ReferenceDefinition reference) =>
        SchemaFingerprint.Canonicalize(
            [
                reference.Name,
                reference.TargetUnitId.Value,
                .. reference.Columns,
                .. CanonicalTargetScope(reference),
                .. CanonicalPhysicalReference(reference)
            ]);

    private static string[] CanonicalTargetScope(ReferenceDefinition reference) =>
        reference.TargetScope is { } scope ? [$"target-scope:{scope}"] : [];

    private static string?[] CanonicalPhysicalReference(ReferenceDefinition reference) =>
        reference.Enforcement != ReferenceEnforcement.Physical
            ? []
            :
            [
                "enforcement:physical",
                reference.TargetName,
                reference.TargetKeyHasProviderSequence?.ToString(CultureInfo.InvariantCulture),
                .. (reference.TargetKeyColumns ?? []).Select(column => $"target-key:{column}")
            ];

    internal static string CanonicalCheckConstraint(StorageUnit unit, CheckConstraintDefinition check)
    {
        var column = unit.Columns.Single(candidate =>
            string.Equals(candidate.Name, check.Column, StringComparison.Ordinal));
        return SchemaFingerprint.Canonicalize(
        [
            check.Name,
            check.Column,
            check.Operator.ToString(),
            SchemaValue.Canonicalize(check.Value.Value, column.Type)
        ]);
    }

    private static AggregationProfile Snapshot(AggregationProfile profile) =>
        AggregationProfileSnapshot.Capture(profile);

    private static string CanonicalAggregationProfile(AggregationProfile profile) =>
        AggregationProfileCanonicalization.Canonicalize(profile);

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
        : this(subject, provider, providerDefinitions, [])
    {
    }

    private PhysicalSchemaTarget(
        SchemaSubject subject,
        ProviderIdentity provider,
        IEnumerable<ProviderPhysicalSchemaDefinition>? providerDefinitions,
        IEnumerable<SchemaRefusal> planningRefusals)
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
        PlanningRefusals = (planningRefusals ?? throw new ArgumentNullException(nameof(planningRefusals)))
            .ToImmutableArray();
    }

    public SchemaSubject Subject { get; }

    public PhysicalSchemaTargetIdentity Identity { get; }

    public ProviderIdentity Provider { get; }

    public ImmutableArray<ProviderPhysicalSchemaDefinition> ProviderDefinitions { get; }

    public string Fingerprint { get; }

    internal ImmutableArray<SchemaRefusal> PlanningRefusals { get; }

    internal PhysicalSchemaTarget WithPlanningRefusals(IEnumerable<SchemaRefusal> refusals) =>
        new(Subject, Provider, ProviderDefinitions, refusals);
}

/// <summary>Canonical, culture-independent schema fingerprint primitives.</summary>
public static class SchemaFingerprint
{
    public static string Create(IEnumerable<string?> parts) => CreateCanonical(Canonicalize(parts));

    public static string Canonicalize(IEnumerable<string?> parts) =>
        string.Concat(parts.Select(part =>
            $"{(part?.Length ?? -1).ToString(CultureInfo.InvariantCulture)}:{part ?? string.Empty};"));

    public static string CreateCanonical(string canonical) =>
        PortableHex.Lower(System.Security.Cryptography.SHA256.HashData(
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
