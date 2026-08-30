using System.Text.Json.Serialization;

namespace Groundwork.Kernel;

/// <summary>
/// How a deployed column that the declaration does not describe is treated when the catalog is
/// compared to it.
/// </summary>
/// <remarks>
/// This is the only opt-out Groundwork offers from "the deployed catalog is exactly the compiled
/// target", and it is deliberately the narrowest one that makes coexistence possible: it never
/// touches a column the declaration does describe, never touches an index, and never covers a
/// foreign column the database will not fill in on its own. Everything else stays a refusal.
/// </remarks>
public enum ForeignColumnPolicy
{
    /// <summary>Any deployed column the declaration does not describe is drift.</summary>
    Refuse,

    /// <summary>
    /// A foreign column the database supplies a value for — nullable, defaulted, or generated — is
    /// reported as a tolerated-drift warning instead of refusing. A foreign column that a writer
    /// omitting it would fail on stays a refusal, because nothing about tolerating it would let
    /// Groundwork write the row.
    /// </summary>
    TolerateDatabaseSupplied
}

public enum PortableType
{
    String,
    Int32,
    Int64,
    Decimal,
    Boolean,
    DateTimeOffset,
    Guid,
    Binary,
    Json,

    /// <summary>
    /// IEEE-754 binary64, storage only. A <see cref="Double"/> column can be written and read
    /// back bit-for-bit, but it never becomes a query column: predicates, ordering, index
    /// membership, key membership, and grouping are refused, because binary floating point has
    /// no comparison semantics that hold across the supported stores. Declare
    /// <see cref="Decimal"/> or <see cref="Int64"/> for values that are compared.
    /// The member is appended so that the names already written into schema documents and
    /// fingerprints keep their meaning.
    /// </summary>
    Double
}

public enum ColumnGeneration
{
    Supplied,
    ProviderSequence
}

public enum PortableCollation
{
    Ordinal,
    OrdinalIgnoreCase,
    UnicodeOrdinalIgnoreCase
}

public enum PortableProjection
{
    UnicodeFold,
    BoundarySearchKey,
    LocaleSortKey,
    Sha256
}

public enum SortDirection
{
    Ascending,
    Descending
}

public enum MissingValueBehavior
{
    Included,
    Excluded
}

public enum ScopePolicy
{
    Global,
    Scoped
}

public enum ConcurrencyKind
{
    None,
    Optimistic
}

/// <summary>Whether a declared reference is query metadata only or also database-enforced.</summary>
public enum ReferenceEnforcement
{
    LogicalOnly,
    Physical
}

/// <summary>The closed comparison surface supported by portable database check constraints.</summary>
public enum CheckConstraintOperator
{
    Equal,
    NotEqual,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual
}

/// <summary>
/// Declares whether a storage unit owns an optimistic version token. Concurrency is opt-in:
/// <see cref="None"/> contributes no version column or write-path machinery.
/// </summary>
public sealed record ConcurrencyDeclaration
{
    /// <summary>Declares a unit without a version token.</summary>
    public static ConcurrencyDeclaration None { get; } = new() { Kind = ConcurrencyKind.None };

    /// <summary>
    /// Declares a system-owned Int64 version token. The logical token name is recorded in the
    /// declaration while providers may normalize its physical representation.
    /// </summary>
    public static ConcurrencyDeclaration Optimistic(string tokenColumn = "version")
    {
        if (string.IsNullOrWhiteSpace(tokenColumn))
            throw new ArgumentException("A concurrency token column must be non-empty.", nameof(tokenColumn));

        return new()
        {
            Kind = ConcurrencyKind.Optimistic,
            TokenColumn = tokenColumn
        };
    }

    public ConcurrencyKind Kind { get; init; }

    /// <summary>The declared logical name of the system-owned token, when optimistic.</summary>
    public string? TokenColumn { get; init; }

    public bool IsOptimistic => Kind == ConcurrencyKind.Optimistic;

    public bool IsNone => Kind == ConcurrencyKind.None;

    public static void ValidateDeclaration(StorageUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var declaration = unit.Concurrency ?? throw new ArgumentException(
            "A storage unit requires a concurrency declaration.", nameof(unit));
        if (declaration.Kind is not (ConcurrencyKind.None or ConcurrencyKind.Optimistic))
            throw new ArgumentException("The concurrency kind is not supported.", nameof(unit));
        if (declaration.IsNone && declaration.TokenColumn is not null)
            throw new ArgumentException("A None concurrency declaration cannot name a token column.", nameof(unit));
        if (declaration.IsOptimistic && string.IsNullOrWhiteSpace(declaration.TokenColumn))
            throw new ArgumentException("An optimistic concurrency declaration requires a token column.", nameof(unit));

        var columns = unit.Columns ?? throw new ArgumentException(
            "A storage unit requires columns.", nameof(unit));
        if (declaration.IsOptimistic && declaration.TokenColumn is { } tokenName &&
            unit.Key?.Columns?.Contains(tokenName, StringComparer.Ordinal) == true)
        {
            throw new ArgumentException(
                $"Optimistic token column '{tokenName}' is system-owned and cannot be part of the storage key.", nameof(unit));
        }
        if (declaration.IsOptimistic && declaration.TokenColumn is { } indexedToken &&
            (unit.Indexes ?? []).FirstOrDefault(index =>
                index?.Columns?.Any(column => string.Equals(column?.Column, indexedToken, StringComparison.Ordinal)) == true) is { } tokenIndex)
        {
            throw new ArgumentException(
                $"Optimistic token column '{indexedToken}' is system-owned and cannot be part of index '{tokenIndex.Name}'.",
                nameof(unit));
        }
        if (declaration.IsOptimistic && columns.FirstOrDefault(column => column.Name == declaration.TokenColumn) is { } token &&
            (token.Type != PortableType.Int64 || token.IsNullable ||
             token.Default?.Value is not long defaultValue || defaultValue != 0))
        {
            throw new ArgumentException(
                $"Optimistic token column '{declaration.TokenColumn}' must be a non-null Int64 with default 0.", nameof(unit));
        }

        AggregationProfileValidator.ValidateUnit(unit);
    }
}

/// <summary>Provider-owned logical column names reserved by the public storage contract.</summary>
public static class ProviderOwnedColumns
{
    public const string Scope = "__groundwork_scope";
    public const string ScopeToken = "__groundwork_scope_token";
    internal const string Version = "__groundwork_version";
    internal const string Action = "__groundwork_action";

    /// <summary>
    /// Refuses invalid logical key/index references and application declarations that collide with
    /// provider-owned query fields. Reference diagnostics are evaluated first so every provider reports
    /// the same <c>GW-DECL-*</c> refusal for compound-invalid declarations.
    /// </summary>
    public static void ValidateLogicalDeclaration(StorageUnit unit)
    {
        StorageDeclarationReferenceValidation.ThrowIfInvalid(unit);
        ValidateReservedLogicalNames(unit);
    }

    internal static void ValidateReservedLogicalNames(StorageUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if ((unit.Columns ?? []).FirstOrDefault(column =>
                column?.Name is { } name &&
                name.StartsWith("__groundwork_", StringComparison.Ordinal)) is { } reserved)
        {
            throw new ArgumentException(
                $"Column '{reserved.Name}' uses the '__groundwork_' provider-owned prefix and cannot be declared by an application.", nameof(unit));
        }
    }

    internal static bool IsAllowedPhysicalColumn(string name) =>
        name is Scope or ScopeToken or Version or Action ||
        SearchKeyProjection.IsProviderOwnedColumn(name);

    /// <summary>
    /// Expands a logical declaration into the provider-owned physical shape shared by the runtime
    /// coordinators and the deployment tool: derived search keys, the scope column and its key and
    /// index prefixes, and the optimistic version or append-action column.
    /// </summary>
    public static StorageUnit Physicalize(
        StorageUnit source,
        ProviderOwnedColumnPolicy policy,
        Func<string, string>? normalizeStorageName = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(policy);
        ValidateLogicalDeclaration(source);
        ConcurrencyDeclaration.ValidateDeclaration(source);
        source = SearchKeyProjection.Expand(source);
        var columns = source.Columns.Select(column => column with { }).ToList();
        var key = source.Key.Columns.ToList();
        var indexes = source.Indexes.ToList();
        if (columns.Any(column => column.Name is Scope or Version or Action))
        {
            throw new ArgumentException(
                $"'{Scope}', '{Version}', and '{Action}' are reserved {policy.ProviderName} columns.", nameof(source));
        }
        if (source.Scope == ScopePolicy.Scoped)
        {
            columns.Add(new ColumnDefinition
            {
                Name = Scope,
                Type = PortableType.String,
                MaxLength = policy.ScopeMaxLength,
                IsNullable = false,
                Default = new PortableDefault(string.Empty)
            });
            if (policy.ScopeJoinsGeneratedKey ||
                !source.Columns.Any(column => column.Generation == ColumnGeneration.ProviderSequence))
            {
                key.Insert(0, Scope);
            }
            indexes = indexes.Select(index => index with { Columns = [new IndexColumn(Scope), .. index.Columns] }).ToList();
        }
        if (source.Concurrency.IsOptimistic)
        {
            RemoveDeclaredToken(source, columns);
            columns.Add(new ColumnDefinition { Name = Version, Type = PortableType.Int64, IsNullable = false, Default = new PortableDefault(0L) });
        }
        else if (policy.DeclaresAppendAction)
        {
            columns.Add(new ColumnDefinition { Name = Action, Type = PortableType.String, MaxLength = 1, IsNullable = false, Default = new PortableDefault("I") });
        }
        return source with
        {
            Name = normalizeStorageName?.Invoke(source.Name) ?? source.Name,
            Columns = columns,
            Key = new KeyDefinition { Columns = key },
            Indexes = indexes,
            References = PhysicalizeReferences(source, policy, normalizeStorageName),
            AggregationProfiles = source.AggregationProfiles.Select(AggregationProfileSnapshot.Capture).ToArray()
        };
    }

    private static IReadOnlyList<ReferenceDefinition> PhysicalizeReferences(
        StorageUnit source,
        ProviderOwnedColumnPolicy policy,
        Func<string, string>? normalizeStorageName) =>
        (source.References ?? []).Select(reference =>
        {
            if (reference.Enforcement != ReferenceEnforcement.Physical)
                return reference with { Columns = reference.Columns.ToArray() };
            if (reference.TargetKeyColumns is null ||
                reference.TargetKeyHasProviderSequence is null ||
                string.IsNullOrWhiteSpace(reference.TargetName))
            {
                throw new ArgumentException(
                    $"Physical reference '{reference.Name}' requires resolved target storage and key metadata.",
                    nameof(source));
            }

            var joinsScope = source.Scope == ScopePolicy.Scoped &&
                (policy.ScopeJoinsGeneratedKey || !reference.TargetKeyHasProviderSequence.Value);
            return reference with
            {
                Columns = joinsScope ? [Scope, .. reference.Columns] : reference.Columns.ToArray(),
                TargetName = normalizeStorageName?.Invoke(reference.TargetName!) ?? reference.TargetName,
                TargetKeyColumns = joinsScope
                    ? [Scope, .. reference.TargetKeyColumns]
                    : reference.TargetKeyColumns.ToArray()
            };
        }).ToArray();

    private static void RemoveDeclaredToken(StorageUnit source, List<ColumnDefinition> columns)
    {
        var token = source.Concurrency.TokenColumn!;
        var declared = columns.FirstOrDefault(column => column.Name == token);
        if (declared is null) return;
        if (declared.Type != PortableType.Int64 || declared.IsNullable ||
            declared.Default?.Value is not long defaultValue || defaultValue != 0)
        {
            throw new ArgumentException(
                $"Optimistic token column '{token}' must be a non-null Int64 with default 0.", nameof(source));
        }
        columns.Remove(declared);
    }
}

/// <summary>
/// The provider-owned physical column choices a coordinator contributes to
/// <see cref="ProviderOwnedColumns.Physicalize"/>. Everything else about the expansion is shared.
/// </summary>
public sealed record ProviderOwnedColumnPolicy
{
    public required string ProviderName { get; init; }

    /// <summary>The declared length of the scope column, when the provider bounds it.</summary>
    public int? ScopeMaxLength { get; init; }

    /// <summary>
    /// Whether scope joins the physical key of a unit that owns a provider-generated sequence.
    /// A provider whose generated identity must remain the sole physical key declares false.
    /// </summary>
    public bool ScopeJoinsGeneratedKey { get; init; } = true;

    /// <summary>Whether a non-optimistic unit carries the provider's append-action column.</summary>
    public bool DeclaresAppendAction { get; init; }
}

public enum TimestampDeclaration
{
    None
}

public readonly record struct StorageUnitId(string Value);

public sealed record PortableDefault(object? Value);

/// <summary>
/// Declares a persisted ICU sort key for locale-aware ordering. The maximum expansion factor is
/// an enforced storage bound, not an estimate: writes and backfills refuse keys that exceed it.
/// </summary>
public sealed record LocaleSortKeyDefinition
{
    public required string CultureName { get; init; }

    public required int MaximumExpansionFactor { get; init; }
}

public sealed record ColumnDefinition
{
    public required string Name { get; init; }
    public required PortableType Type { get; init; }
    public bool IsNullable { get; init; } = true;
    public int? MaxLength { get; init; }
    public int? Precision { get; init; }
    public int? Scale { get; init; }
    public PortableCollation? Collation { get; init; }
    /// <summary>The logical collation retained when physical expansion uses ordinal storage.</summary>
    public PortableCollation? LogicalCollation { get; init; }
    /// <summary>Optional locale-aware ordering implemented by a provider-owned ICU sort key.</summary>
    public LocaleSortKeyDefinition? LocaleSortKey { get; init; }
    public PortableDefault? Default { get; init; }
    public ColumnGeneration Generation { get; init; } = ColumnGeneration.Supplied;

    /// <summary>
    /// The stable logical identity of this column. It defaults to <see cref="Name"/> and only has
    /// to be spelled once the physical name changes: schema planning keys its slots on the logical
    /// id, so retaining the original id across a renamed <see cref="Name"/> is what makes the
    /// change plan as a rename instead of a drop followed by an add.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; init; }

    /// <summary>The logical id this column is planned under; <see cref="Name"/> when none is declared.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string LogicalId => string.IsNullOrWhiteSpace(Id) ? Name : Id;
}

public sealed record KeyDefinition
{
    public required IReadOnlyList<string> Columns { get; init; }
}

/// <summary>
/// Declares a relationship from columns on one storage unit to another unit's key. References are
/// logical-only unless <see cref="Enforcement"/> explicitly opts into database enforcement.
/// </summary>
public sealed record ReferenceDefinition
{
    /// <summary>The stable name later query declarations use to select this relationship.</summary>
    public required string Name { get; init; }

    /// <summary>Columns on the referencing unit, in target-key order.</summary>
    public required IReadOnlyList<string> Columns { get; init; }

    /// <summary>The logical identity of the unit whose declared key is referenced.</summary>
    public required StorageUnitId TargetUnitId { get; init; }

    /// <summary>
    /// The target's scope policy as resolved when the relationship was declared or compiled.
    /// A null value preserves legacy hand-built and persisted declarations; providers must fail
    /// closed before reading target metadata when it is absent.
    /// </summary>
    public ScopePolicy? TargetScope { get; init; }

    /// <summary>Whether relational schema application also creates a physical foreign key.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ReferenceEnforcement Enforcement { get; init; }

    /// <summary>The resolved target storage name required by physical enforcement.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetName { get; init; }

    /// <summary>The resolved target key columns, in reference order.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? TargetKeyColumns { get; init; }

    /// <summary>Whether the resolved target key owns a provider-generated sequence column.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? TargetKeyHasProviderSequence { get; init; }
}

/// <summary>A named, single-column portable database check constraint.</summary>
public sealed record CheckConstraintDefinition
{
    public required string Name { get; init; }

    public required string Column { get; init; }

    public required CheckConstraintOperator Operator { get; init; }

    public required PortableDefault Value { get; init; }
}

public sealed record IndexColumn(string Column, SortDirection Direction = SortDirection.Ascending);

public sealed record IndexDefinition
{
    public required string Name { get; init; }
    public required IReadOnlyList<IndexColumn> Columns { get; init; }
    public bool IsUnique { get; init; }
    public MissingValueBehavior MissingValues { get; init; } = MissingValueBehavior.Included;
    public int SchemaVersion { get; init; } = 1;

    /// <summary>
    /// How columns this declaration does not describe are treated when a deployed catalog is
    /// compared to it. Opt in per unit to coexist read-side with a catalog another tool extends.
    /// </summary>
    /// <remarks>
    /// Deliberately absent from <see cref="SchemaSubject.Fingerprint"/> and from the persisted
    /// applied state. The fingerprint answers one question — is the deployed catalog the shape this
    /// build compiled — and a foreign column is by construction not part of that shape; folding a
    /// tolerance setting into it would make changing the setting look like a schema change, force a
    /// no-op apply to clear it, and split the deployment tool's compiled target from the host's.
    /// Applied state records what was applied, and tolerating a column applies nothing, so the
    /// policy is read from the live declaration at every comparison rather than from history.
    /// </remarks>
    [JsonIgnore]
    public ForeignColumnPolicy ForeignColumns { get; init; } = ForeignColumnPolicy.Refuse;
}

public sealed record DerivedColumnDefinition
{
    public required string Name { get; init; }
    public required string SourceColumn { get; init; }
    public required PortableProjection Projection { get; init; }

    /// <summary>
    /// The complete algorithm identity used to produce the derived value. This is intentionally
    /// persisted with the declaration: a change to either folding or prefix-boundary encoding is
    /// a rebuild, not an additive metadata edit.
    /// </summary>
    public string? AlgorithmId { get; init; }
}

public sealed record StorageUnit
{
    /// <summary>Starts a provider-neutral fluent declaration.</summary>
    public static StorageDeclarationBuilder Declare(string id, string name) =>
        new(new StorageDeclarationState(id, name));

    public required StorageUnitId Id { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<ColumnDefinition> Columns { get; init; }
    public required KeyDefinition Key { get; init; }
    public IReadOnlyList<DerivedColumnDefinition> DerivedColumns { get; init; } = [];
    public IReadOnlyList<IndexDefinition> Indexes { get; init; } = [];
    /// <summary>Logical relationships, some of which may opt into physical enforcement.</summary>
    public IReadOnlyList<ReferenceDefinition> References { get; init; } = [];
    /// <summary>Named portable checks that relational providers enforce in their catalog.</summary>
    public IReadOnlyList<CheckConstraintDefinition> CheckConstraints { get; init; } = [];
    /// <summary>Named, closed aggregation shapes available to callers of this unit.</summary>
    public IReadOnlyList<AggregationProfile> AggregationProfiles { get; init; } = [];
    /// <summary>
    /// Optional read-side view whose provider-native columns use idiomatic reporting types.
    /// Scoped units expose the provider-owned scope column so every row remains attributable;
    /// database grants, not Groundwork sessions, must enforce who may read that cross-scope view.
    /// </summary>
    public InteropViewDeclaration? InteropView { get; init; }
    public ScopePolicy Scope { get; init; } = ScopePolicy.Global;
    /// <summary>Optional durable idempotency contract for batch appends.</summary>
    public AppendIdempotencyDeclaration? AppendIdempotency { get; init; }
    /// <summary>Alias for <see cref="AppendIdempotency"/> used by generic consumers.</summary>
    public AppendIdempotencyDeclaration? Idempotency
    {
        get => AppendIdempotency;
        init => AppendIdempotency = value;
    }
    public ConcurrencyDeclaration Concurrency { get; init; } = ConcurrencyDeclaration.None;
    public TimestampDeclaration Timestamps { get; init; } = TimestampDeclaration.None;
    public RetentionDeclaration? Retention { get; init; }
    /// <summary>Optional durable replay contract for operation-identified retention.</summary>
    public RetentionIdempotencyDeclaration? RetentionIdempotency { get; init; }
    public int SchemaVersion { get; init; } = 1;

    /// <summary>
    /// How columns this declaration does not describe are treated when a deployed catalog is
    /// compared to it. Opt in per unit to coexist read-side with a catalog another tool extends.
    /// </summary>
    /// <remarks>
    /// Deliberately absent from <see cref="SchemaSubject.Fingerprint"/> and from the persisted
    /// applied state. The fingerprint answers one question — is the deployed catalog the shape this
    /// build compiled — and a foreign column is by construction not part of that shape; folding a
    /// tolerance setting into it would make changing the setting look like a schema change, force a
    /// no-op apply to clear it, and split the deployment tool's compiled target from the host's.
    /// Applied state records what was applied, and tolerating a column applies nothing, so the
    /// policy is read from the live declaration at every comparison rather than from history.
    /// </remarks>
    [JsonIgnore]
    public ForeignColumnPolicy ForeignColumns { get; init; } = ForeignColumnPolicy.Refuse;
}

/// <summary>Opts one storage unit into a named provider-native reporting view.</summary>
public sealed record InteropViewDeclaration
{
    public InteropViewDeclaration(string name) =>
        Name = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("An interop view requires a non-empty name.", nameof(name))
            : name;

    public string Name { get; }
}
