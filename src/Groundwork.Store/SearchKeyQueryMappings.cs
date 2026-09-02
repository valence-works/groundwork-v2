using Groundwork.Kernel;
using Groundwork.Query.Model;

namespace Groundwork.Store;

/// <summary>Builds the logical-to-physical search-key map shared by provider sessions.</summary>
public static class SearchKeyQueryMappings
{
    public static IReadOnlyDictionary<string, QuerySearchKeyColumn> For(StorageUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var derived = unit.DerivedColumns
            .Where(column => column.Projection is PortableProjection.BoundarySearchKey or PortableProjection.LocaleSortKey)
            .ToDictionary(column => column.SourceColumn, StringComparer.Ordinal);
        return unit.Columns
            .Where(column => column.Type == PortableType.String && !column.Name.StartsWith(SearchKeyProjection.Prefix, StringComparison.Ordinal))
            .ToDictionary(
                column => column.Name,
                column =>
                {
                    if (!derived.TryGetValue(column.Name, out var physical))
                        return new QuerySearchKeyColumn(column.Name, column.Name, QuerySearchKeyPolicy.Ordinal, column.MaxLength);
                    var physicalColumn = unit.Columns.FirstOrDefault(item => item.Name == physical.Name);
                    if (physical.Projection == PortableProjection.LocaleSortKey)
                    {
                        _ = PortableLocaleOrdering.ParseAlgorithmId(physical.AlgorithmId);
                        return new QuerySearchKeyColumn(
                            column.Name,
                            physical.Name,
                            QuerySearchKeyPolicy.Ordinal,
                            physicalColumn?.MaxLength,
                            orderByPhysicalColumn: true,
                            supportsPrefixPredicates: false);
                    }
                    var policy = PortableSearchKeyAlgorithmIdentity.Parse(physical.AlgorithmId).Policy switch
                    {
                        PortableStringComparisonPolicy.AsciiIgnoreCase => QuerySearchKeyPolicy.AsciiIgnoreCase,
                        PortableStringComparisonPolicy.UnicodeOrdinalIgnoreCase => QuerySearchKeyPolicy.UnicodeOrdinalIgnoreCase,
                        var unsupported => throw new InvalidOperationException(
                            $"Boundary search-key mapping '{physical.Name}' cannot use comparison policy '{unsupported}'.")
                    };
                    return new QuerySearchKeyColumn(
                        column.Name,
                        physical.Name,
                        policy,
                        physicalColumn?.MaxLength);
                },
                StringComparer.Ordinal);
    }

    /// <summary>Builds mappings for positional JSON element search-key arrays.</summary>
    public static IReadOnlyDictionary<string, QueryElementSearchKeyColumn> ElementFor(StorageUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var derived = unit.DerivedColumns
            .Where(column => column.Projection == PortableProjection.ElementBoundarySearchKey)
            .ToDictionary(column => column.SourceColumn, StringComparer.Ordinal);
        return unit.Columns
            .Where(column => column.Type == PortableType.Json && column.ElementSearchKey is not null)
            .ToDictionary(
                column => column.Name,
                column =>
                {
                    if (!derived.TryGetValue(column.Name, out var physical))
                    {
                        throw new InvalidOperationException(
                            $"Element search-key declaration '{column.Name}' has no expanded physical projection.");
                    }

                    var identity = PortableElementSearchKeyAlgorithm.Parse(physical.AlgorithmId);
                    if (identity.MaximumElementCodeUnits != column.ElementSearchKey!.MaximumElementCodeUnits)
                    {
                        throw new InvalidOperationException(
                            $"Element search-key mapping '{physical.Name}' has stale bound metadata. Rebuild the projection before use.");
                    }
                    var policy = identity.Policy switch
                    {
                        PortableStringComparisonPolicy.Ordinal => QuerySearchKeyPolicy.Ordinal,
                        PortableStringComparisonPolicy.AsciiIgnoreCase => QuerySearchKeyPolicy.AsciiIgnoreCase,
                        PortableStringComparisonPolicy.UnicodeOrdinalIgnoreCase => QuerySearchKeyPolicy.UnicodeOrdinalIgnoreCase,
                        var unsupported => throw new InvalidOperationException(
                            $"Element search-key mapping '{physical.Name}' cannot use comparison policy '{unsupported}'.")
                    };
                    return new QueryElementSearchKeyColumn(
                        column.Name,
                        physical.Name,
                        policy,
                        identity.MaximumElementCodeUnits);
                },
                StringComparer.Ordinal);
    }

    /// <summary>Retargets caller-declared logical index columns to their physical search keys.</summary>
    public static IReadOnlyList<QueryIndexDeclaration> RetargetIndexes(
        StorageUnit unit,
        IEnumerable<QueryIndexDeclaration> indexes)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(indexes);
        var mappings = For(unit);
        return indexes.Select(index => new QueryIndexDeclaration(
            index.Name,
            index.Columns.Select(column =>
            {
                var physical = mappings.TryGetValue(column, out var mapping)
                    ? mapping.PhysicalColumn
                    : column;
                return new QueryIndexColumn(
                    physical,
                    index.NullableColumns.Contains(column),
                    index.ColumnTypes.TryGetValue(column, out var type) ? type : null);
            }),
            index.Pinning,
            index.IncludesNulls)).ToArray();
    }
}
