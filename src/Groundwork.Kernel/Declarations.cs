namespace Groundwork.Kernel;

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
    Json
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

public enum TimestampDeclaration
{
    None
}

public readonly record struct StorageUnitId(string Value);

public sealed record PortableDefault(object? Value);

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
    public PortableDefault? Default { get; init; }
    public ColumnGeneration Generation { get; init; } = ColumnGeneration.Supplied;
}

public sealed record KeyDefinition
{
    public required IReadOnlyList<string> Columns { get; init; }
}

public sealed record IndexColumn(string Column, SortDirection Direction = SortDirection.Ascending);

public sealed record IndexDefinition
{
    public required string Name { get; init; }
    public required IReadOnlyList<IndexColumn> Columns { get; init; }
    public bool IsUnique { get; init; }
    public MissingValueBehavior MissingValues { get; init; } = MissingValueBehavior.Included;
    public int SchemaVersion { get; init; } = 1;
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
    public required StorageUnitId Id { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<ColumnDefinition> Columns { get; init; }
    public required KeyDefinition Key { get; init; }
    public IReadOnlyList<DerivedColumnDefinition> DerivedColumns { get; init; } = [];
    public IReadOnlyList<IndexDefinition> Indexes { get; init; } = [];
    /// <summary>Named, closed aggregation shapes available to callers of this unit.</summary>
    public IReadOnlyList<AggregationProfile> AggregationProfiles { get; init; } = [];
    public ScopePolicy Scope { get; init; } = ScopePolicy.Global;
    public ConcurrencyDeclaration Concurrency { get; init; } = ConcurrencyDeclaration.None;
    public TimestampDeclaration Timestamps { get; init; } = TimestampDeclaration.None;
    public int SchemaVersion { get; init; } = 1;
}
