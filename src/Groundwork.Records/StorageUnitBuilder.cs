using Groundwork.Kernel;
using KernelStorageUnit = Groundwork.Kernel.StorageUnit;

namespace Groundwork.Records;

/// <summary>Entry point for the compact fluent declaration surface.</summary>
public static class StorageUnit
{
    public static StorageDeclarationBuilder Declare(string id, string name) =>
        new(new DeclarationState(id, name));
}

/// <summary>Mutable authoring state whose Build result is an immutable declaration snapshot.</summary>
public sealed class StorageDeclarationBuilder
{
    private readonly DeclarationState state;

    internal StorageDeclarationBuilder(DeclarationState state) => this.state = state;

    public StorageDeclarationBuilder String(
        string name,
        int maxLength,
        Action<ColumnBuilder>? configure = null) =>
        AddColumn(name, PortableType.String, configure, builder => builder.MaxLength(maxLength));

    public StorageDeclarationBuilder String(string name, Action<ColumnBuilder>? configure = null) =>
        AddColumn(name, PortableType.String, configure);

    public StorageDeclarationBuilder Int32(string name, Action<ColumnBuilder>? configure = null) =>
        AddColumn(name, PortableType.Int32, configure);

    public StorageDeclarationBuilder Int64(string name, Action<ColumnBuilder>? configure = null) =>
        AddColumn(name, PortableType.Int64, configure);

    public StorageDeclarationBuilder Decimal(
        string name,
        int precision,
        int scale,
        Action<ColumnBuilder>? configure = null) =>
        AddColumn(name, PortableType.Decimal, configure, builder => builder.Precision(precision, scale));

    public StorageDeclarationBuilder Decimal(string name, Action<ColumnBuilder>? configure = null) =>
        AddColumn(name, PortableType.Decimal, configure);

    public StorageDeclarationBuilder Boolean(string name, Action<ColumnBuilder>? configure = null) =>
        AddColumn(name, PortableType.Boolean, configure);

    public StorageDeclarationBuilder Timestamp(string name, Action<ColumnBuilder>? configure = null) =>
        AddColumn(name, PortableType.DateTimeOffset, configure);

    public StorageDeclarationBuilder DateTimeOffset(string name, Action<ColumnBuilder>? configure = null) =>
        AddColumn(name, PortableType.DateTimeOffset, configure);

    public StorageDeclarationBuilder Guid(string name, Action<ColumnBuilder>? configure = null) =>
        AddColumn(name, PortableType.Guid, configure);

    public StorageDeclarationBuilder Binary(
        string name,
        int maxLength,
        Action<ColumnBuilder>? configure = null) =>
        AddColumn(name, PortableType.Binary, configure, builder => builder.MaxLength(maxLength));

    public StorageDeclarationBuilder Binary(string name, Action<ColumnBuilder>? configure = null) =>
        AddColumn(name, PortableType.Binary, configure);

    public StorageDeclarationBuilder Json(string name, Action<ColumnBuilder>? configure = null) =>
        AddColumn(name, PortableType.Json, configure);

    public StorageDeclarationBuilder Key(params string[] columns)
    {
        state.SetKey(columns);
        return this;
    }

    public StorageDeclarationBuilder UniqueIndex(string name, params string[] columns)
    {
        state.AddIndex(name, columns.Select(column => new IndexColumn(column)), unique: true);
        return this;
    }

    public StorageDeclarationBuilder UniqueIndex(string name, Action<IndexBuilder> configure)
    {
        return AddIndex(name, configure, unique: true);
    }

    public StorageDeclarationBuilder Index(string name, params string[] columns)
    {
        state.AddIndex(name, columns.Select(column => new IndexColumn(column)), unique: false);
        return this;
    }

    public StorageDeclarationBuilder Index(string name, Action<IndexBuilder> configure) =>
        AddIndex(name, configure, unique: false);

    public KernelStorageUnit Build(PortabilityValidationContext? context = null) => state.Build(context);

    private StorageDeclarationBuilder AddColumn(
        string name,
        PortableType type,
        Action<ColumnBuilder>? configure,
        Action<ColumnBuilder>? initial = null)
    {
        var builder = new ColumnBuilder();
        initial?.Invoke(builder);
        configure?.Invoke(builder);
        state.AddColumn(builder.Build(name, type));
        return this;
    }

    private StorageDeclarationBuilder AddIndex(string name, Action<IndexBuilder> configure, bool unique)
    {
        if (configure is null)
            throw new ArgumentNullException(nameof(configure));
        var builder = new IndexBuilder();
        configure(builder);
        state.AddIndex(name, builder.Columns, unique);
        return this;
    }
}

/// <summary>Column policy options shared by both authoring surfaces.</summary>
public sealed class ColumnBuilder
{
    private bool? isNullable;
    private int? maxLength;
    private int? precision;
    private int? scale;
    private PortableCollation? collation;
    private object? defaultValue;
    private bool hasDefault;
    private ColumnGeneration generation = ColumnGeneration.Supplied;

    public ColumnBuilder Required()
    {
        isNullable = false;
        return this;
    }

    public ColumnBuilder Nullable()
    {
        isNullable = true;
        return this;
    }

    public ColumnBuilder MaxLength(int value)
    {
        maxLength = value;
        return this;
    }

    public ColumnBuilder Precision(int value, int scaleValue)
    {
        precision = value;
        scale = scaleValue;
        return this;
    }

    public ColumnBuilder Collation(PortableCollation value)
    {
        collation = value;
        return this;
    }

    public ColumnBuilder Default(object? value)
    {
        defaultValue = value;
        hasDefault = true;
        return this;
    }

    public ColumnBuilder ProviderSequence()
    {
        generation = ColumnGeneration.ProviderSequence;
        return this;
    }

    internal ColumnBuilder InferNullable(bool value)
    {
        isNullable ??= value;
        return this;
    }

    internal ColumnDefinition Build(string name, PortableType type) => new()
    {
        Name = RequireName(name),
        Type = type,
        IsNullable = isNullable ?? true,
        MaxLength = maxLength,
        Precision = precision,
        Scale = scale,
        Collation = collation,
        Default = hasDefault ? new PortableDefault(defaultValue) : null,
        Generation = generation
    };

    private static string RequireName(string value) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A non-empty column name is required.", nameof(value)) : value;
}

/// <summary>Sort and missing-value options for an index declaration.</summary>
public sealed class IndexBuilder
{
    private readonly List<IndexColumn> columns = [];

    internal IReadOnlyList<IndexColumn> Columns => columns;

    public IndexBuilder Ascending(string column)
    {
        columns.Add(new IndexColumn(column, SortDirection.Ascending));
        return this;
    }

    public IndexBuilder Descending(string column)
    {
        columns.Add(new IndexColumn(column, SortDirection.Descending));
        return this;
    }
}
