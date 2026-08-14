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

public enum ConcurrencyDeclaration
{
    None
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
}

public sealed record StorageUnit
{
    public required StorageUnitId Id { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<ColumnDefinition> Columns { get; init; }
    public required KeyDefinition Key { get; init; }
    public IReadOnlyList<DerivedColumnDefinition> DerivedColumns { get; init; } = [];
    public IReadOnlyList<IndexDefinition> Indexes { get; init; } = [];
    public ScopePolicy Scope { get; init; } = ScopePolicy.Global;
    public ConcurrencyDeclaration Concurrency { get; init; } = ConcurrencyDeclaration.None;
    public TimestampDeclaration Timestamps { get; init; } = TimestampDeclaration.None;
    public int SchemaVersion { get; init; } = 1;
}
