using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Schema;

namespace Groundwork.SchemaTool;

public static class SchemaCompilation
{
    public static IReadOnlyList<StorageUnit> Compile(SchemaDocument schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return schema.Tables.Select(Compile).ToArray();
    }

    public static StorageUnit Compile(SchemaTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var unit = new StorageUnit
        {
            Id = new StorageUnitId(table.Name),
            Name = table.Name,
            Columns = table.Columns.Select(column => new ColumnDefinition
            {
                Name = column.Name,
                Type = Map(column.Type),
                IsNullable = column.IsNullable,
                MaxLength = column.Length,
                Precision = column.Precision,
                Scale = column.Scale,
                Collation = column.Folding switch
                {
                    TextFolding.None => null,
                    TextFolding.AsciiIgnoreCase => PortableCollation.OrdinalIgnoreCase,
                    TextFolding.UnicodeOrdinalIgnoreCase => PortableCollation.UnicodeOrdinalIgnoreCase,
                    _ => throw new ArgumentOutOfRangeException(nameof(column.Folding), column.Folding, null)
                },
                Generation = column.Generation == SchemaGeneration.ProviderSequence
                    ? ColumnGeneration.ProviderSequence
                    : ColumnGeneration.Supplied
            }).ToArray(),
            Key = new KeyDefinition { Columns = table.Key.ToArray() },
            Indexes = table.Indexes.Select(index => new IndexDefinition
            {
                Name = index.Name,
                Columns = index.Columns.Select(column => new IndexColumn(
                    column.Name,
                    column.Descending ? SortDirection.Descending : SortDirection.Ascending)).ToArray(),
                IsUnique = index.Unique,
                MissingValues = index.IncludeNulls
                    ? MissingValueBehavior.Included
                    : MissingValueBehavior.Excluded
            }).ToArray()
        };
        return SearchKeyProjection.Expand(unit);
    }

    public static IReadOnlyList<PhysicalSchemaTarget> CompileTargets(
        SchemaDocument schema,
        ProviderIdentity provider) => Compile(schema)
        .Select(unit => new PhysicalSchemaTarget(new SchemaSubject(unit), provider))
        .ToArray();

    private static PortableType Map(SchemaValueType type) => type switch
    {
        SchemaValueType.String => PortableType.String,
        SchemaValueType.Int32 => PortableType.Int32,
        SchemaValueType.Int64 => PortableType.Int64,
        SchemaValueType.Decimal => PortableType.Decimal,
        SchemaValueType.Boolean => PortableType.Boolean,
        SchemaValueType.DateTimeOffset => PortableType.DateTimeOffset,
        SchemaValueType.Guid => PortableType.Guid,
        SchemaValueType.Binary => PortableType.Binary,
        SchemaValueType.Json => PortableType.Json,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };
}
