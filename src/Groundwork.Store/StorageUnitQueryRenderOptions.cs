using Groundwork.Kernel;
using Groundwork.Query.Model;

namespace Groundwork.Store;

/// <summary>Creates native-query metadata directly from an admitted storage declaration.</summary>
public static class StorageUnitQueryRenderOptions
{
    /// <summary>
    /// Converts every declared index into typed provider-neutral query metadata and optionally
    /// names the index whose native selection should be observed. The resulting declarations use
    /// <see cref="QueryIndexPinning.ProviderDefault"/>; selecting an index does not add a hint.
    /// </summary>
    /// <param name="unit">The same storage unit used to open the query session.</param>
    /// <param name="selectedIndex">
    /// Optional logical index name. When supplied, providers can emit explain-assert evidence for
    /// the selected declaration without forcing an optimizer hint.
    /// </param>
    public static QueryRenderOptions CreateQueryRenderOptions(
        this StorageUnit unit,
        string? selectedIndex = null)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var columns = unit.Columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
        var indexes = unit.Indexes.Select(index => new QueryIndexDeclaration(
            index.Name,
            index.Columns.Select(indexColumn =>
            {
                if (!columns.TryGetValue(indexColumn.Column, out var column))
                {
                    throw new ArgumentException(
                        $"Index '{index.Name}' refers to undeclared column '{indexColumn.Column}'.",
                        nameof(unit));
                }

                return new QueryIndexColumn(column.Name, column.IsNullable, QueryTypeFor(column));
            }),
            QueryIndexPinning.ProviderDefault,
            includesNulls: index.MissingValues == MissingValueBehavior.Included));
        var options = new QueryRenderOptions(indexes, selectedIndex);
        _ = options.FindSelectedIndex();
        return options;
    }

    private static QueryType QueryTypeFor(ColumnDefinition column) => column.Type switch
    {
        PortableType.Boolean => QueryType.Boolean,
        PortableType.Int32 => QueryType.Int32,
        PortableType.Int64 => QueryType.Int64,
        PortableType.Decimal => QueryType.Decimal,
        PortableType.String => QueryType.String,
        PortableType.DateTimeOffset => QueryType.DateTimeOffset,
        PortableType.Guid => QueryType.Guid,
        PortableType.Binary => QueryType.Binary,
        _ => throw new QueryRenderException(
            "GW-QUERY-018",
            $"Index column '{column.Name}' uses non-queryable portable type '{column.Type}'.")
    };
}
