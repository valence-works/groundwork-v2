using System.Collections.Immutable;

namespace Groundwork.Kernel.Schema;

/// <summary>
/// The row transform behind a folded search-key backfill, expressed as an ordinary data-migration
/// transform. Both the in-transaction derived-column backfill run by a schema apply and the chunked
/// data-migration runner drive this one implementation, so there is a single definition of what a
/// search-key value is rather than one per provider.
/// </summary>
public sealed class DerivedColumnTransform : IDataMigrationTransform
{
    private readonly StorageUnit projectionUnit;
    private readonly ImmutableArray<DerivedColumnDefinition> derived;

    public DerivedColumnTransform(StorageUnit unit, IEnumerable<DerivedColumnDefinition> derivedColumns)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(derivedColumns);
        derived = derivedColumns.ToImmutableArray();
        if (derived.IsEmpty)
            throw new ArgumentException("A derived-column transform needs at least one derived column.", nameof(derivedColumns));
        if (derived.Any(column => column is null))
            throw new ArgumentException("A derived-column transform cannot carry a null derived column.", nameof(derivedColumns));

        // Populate is driven by the declared derived columns, so the projection unit names exactly
        // the ones this transform is responsible for.
        projectionUnit = unit with { DerivedColumns = derived };
        SourceColumns = derived.Select(column => column.SourceColumn).Distinct(StringComparer.Ordinal).ToImmutableArray();
        TargetColumns = derived.Select(column => column.Name).ToImmutableArray();
        Identity = SchemaFingerprint.Create(
        [
            "derived-column-v1",
            .. derived
                .Select(column => SchemaFingerprint.Canonicalize(
                    [column.Name, column.SourceColumn, column.Projection.ToString(), column.AlgorithmId]))
                .OrderBy(canonical => canonical, StringComparer.Ordinal)
        ]);
    }

    public string Identity { get; }

    public ImmutableArray<string> SourceColumns { get; }

    public ImmutableArray<string> TargetColumns { get; }

    public DataMigrationValues Transform(DataMigrationRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var sources = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var source in SourceColumns)
            sources[source] = row.TryGetValue(source, out var value) ? value : null;

        var projected = SearchKeyProjection.Populate(projectionUnit, sources);
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var target in TargetColumns)
            values[target] = projected.TryGetValue(target, out var searchKey) ? searchKey : null;
        return DataMigrationValues.Set(values);
    }
}
