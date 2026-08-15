using System.Collections.ObjectModel;

namespace Groundwork.Kernel;

/// <summary>Provider-neutral physical expansion for folded text columns.</summary>
public static class SearchKeyProjection
{
    public const string Prefix = "__groundwork_search_";

    public static bool IsProviderOwnedColumn(string columnName) =>
        columnName?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    public static IReadOnlyDictionary<string, object?> PublicValues(IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new ReadOnlyDictionary<string, object?>(values
            .Where(pair => !IsProviderOwnedColumn(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
    }

    public static bool IsFolded(PortableCollation? collation) =>
        collation is PortableCollation.OrdinalIgnoreCase or PortableCollation.UnicodeOrdinalIgnoreCase;

    public static PortableCollation? LogicalCollation(ColumnDefinition column) =>
        column.LogicalCollation ?? column.Collation;

    public static PortableStringComparisonPolicy Policy(PortableCollation collation) => collation switch
    {
        PortableCollation.OrdinalIgnoreCase => PortableStringComparisonPolicy.AsciiIgnoreCase,
        PortableCollation.UnicodeOrdinalIgnoreCase => PortableStringComparisonPolicy.UnicodeOrdinalIgnoreCase,
        _ => throw new ArgumentOutOfRangeException(nameof(collation), collation, "Only folded collations have search keys.")
    };

    public static string ColumnName(string sourceColumn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceColumn);
        return Prefix + sourceColumn;
    }

    public static int ExpansionFactor(PortableCollation collation) => Policy(collation) switch
    {
        PortableStringComparisonPolicy.AsciiIgnoreCase => 5,
        PortableStringComparisonPolicy.UnicodeOrdinalIgnoreCase => 7,
        _ => 1
    };

    public static string AlgorithmId(PortableCollation collation) =>
        PortableStringComparison.GetSearchKeyAlgorithmId(Policy(collation));

    /// <summary>
    /// Identifies the additive folded-column migration that replaces an existing logical index
    /// term with its provider-owned search key. Other index definition changes remain refused.
    /// </summary>
    public static bool IsIndexRetarget(
        IndexDefinition previous,
        IndexDefinition desired,
        IReadOnlyList<DerivedColumnDefinition> desiredDerivedColumns)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(desiredDerivedColumns);
        if (!string.Equals(previous.Name, desired.Name, StringComparison.Ordinal) ||
            previous.IsUnique != desired.IsUnique ||
            previous.MissingValues != desired.MissingValues ||
            previous.SchemaVersion != desired.SchemaVersion ||
            previous.Columns.Count != desired.Columns.Count)
        {
            return false;
        }

        var derivedByName = desiredDerivedColumns.ToDictionary(column => column.Name, StringComparer.Ordinal);
        var replaced = false;
        for (var index = 0; index < previous.Columns.Count; index++)
        {
            var oldColumn = previous.Columns[index];
            var newColumn = desired.Columns[index];
            if (oldColumn.Direction != newColumn.Direction)
                return false;
            if (string.Equals(oldColumn.Column, newColumn.Column, StringComparison.Ordinal))
                continue;
            if (!derivedByName.TryGetValue(newColumn.Column, out var derived) ||
                !string.Equals(derived.SourceColumn, oldColumn.Column, StringComparison.Ordinal))
            {
                return false;
            }

            replaced = true;
        }

        return replaced;
    }

    /// <summary>
    /// Adds exactly one nullable ASCII-text key for each folded source column and retargets
    /// declared physical indexes. Ordinal source columns deliberately remain base-column only.
    /// </summary>
    public static StorageUnit Expand(StorageUnit source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var columns = (source.Columns ?? []).Select(column =>
            column.Type == PortableType.String && IsFolded(LogicalCollation(column))
                ? column with
                {
                    // A previously uncollated source already has the provider's ordinal
                    // physical behavior (BINARY/BIN2). Keep that physical declaration stable
                    // while recording the new logical policy separately.
                    Collation = column.Collation is null ? null : PortableCollation.Ordinal,
                    LogicalCollation = LogicalCollation(column)
                }
                : column with { }).ToList();
        var derived = (source.DerivedColumns ?? []).Select(column => column with { }).ToList();
        var folded = (source.Columns ?? [])
            .Where(column => column.Type == PortableType.String && IsFolded(LogicalCollation(column)))
            .ToArray();

        foreach (var column in folded)
        {
            var name = ColumnName(column.Name);
            var existing = columns.FirstOrDefault(item => item.Name == name);
            var policy = Policy(LogicalCollation(column)!.Value);
            int? maxLength = column.MaxLength is int length
                ? checked(length * ExpansionFactor(LogicalCollation(column)!.Value))
                : null;
            var expected = new ColumnDefinition
            {
                Name = name,
                Type = PortableType.String,
                IsNullable = column.IsNullable,
                MaxLength = maxLength,
                Collation = PortableCollation.Ordinal
            };
            if (existing is null)
                columns.Add(expected);
            else if (existing.Type != expected.Type || existing.MaxLength != expected.MaxLength || existing.Collation != expected.Collation)
                throw new InvalidOperationException($"Search-key column '{name}' has incompatible physical metadata.");

            var algorithmId = PortableStringComparison.GetSearchKeyAlgorithmId(policy);
            var existingDerived = derived.FirstOrDefault(item => item.Name == name);
            if (existingDerived is null)
            {
                derived.Add(new DerivedColumnDefinition
                {
                    Name = name,
                    SourceColumn = column.Name,
                    Projection = PortableProjection.BoundarySearchKey,
                    AlgorithmId = algorithmId
                });
            }
            else if (existingDerived.SourceColumn != column.Name || existingDerived.Projection != PortableProjection.BoundarySearchKey ||
                     !string.Equals(existingDerived.AlgorithmId, algorithmId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Search-key declaration '{name}' does not match source '{column.Name}'.");
            }
        }

        var indexes = source.Indexes.Select(index => index with
        {
            Columns = index.Columns.Select(column =>
            {
                var sourceColumn = (source.Columns ?? []).FirstOrDefault(item => item.Name == column.Column);
                return sourceColumn is not null && sourceColumn.Type == PortableType.String && IsFolded(LogicalCollation(sourceColumn))
                    ? column with { Column = ColumnName(column.Column) }
                    : column with { };
            }).ToArray()
        }).ToArray();

        return source with
        {
            Columns = new ReadOnlyCollection<ColumnDefinition>(columns),
            DerivedColumns = new ReadOnlyCollection<DerivedColumnDefinition>(derived),
            Indexes = indexes
        };
    }

    /// <summary>Computes hidden values for a write without allowing callers to supply them.</summary>
    public static IReadOnlyDictionary<string, object?> Populate(StorageUnit unit, IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(values);
        var result = values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var declarations = unit.DerivedColumns
            .Where(column => column.Projection == PortableProjection.BoundarySearchKey)
            .ToArray();
        if (declarations.Length == 0)
        {
            declarations = unit.Columns
                .Where(column => column.Type == PortableType.String && IsFolded(LogicalCollation(column)))
                .Select(column => new DerivedColumnDefinition
                {
                    Name = ColumnName(column.Name),
                    SourceColumn = column.Name,
                    Projection = PortableProjection.BoundarySearchKey,
                    AlgorithmId = AlgorithmId(LogicalCollation(column)!.Value)
                })
                .ToArray();
        }
        foreach (var declaration in declarations)
        {
            if (result.ContainsKey(declaration.Name))
                throw new ArgumentException($"Search-key column '{declaration.Name}' is provider-owned and cannot be supplied.", nameof(values));
            var source = declaration.SourceColumn;
            var policy = declaration.AlgorithmId?.Contains(
                PortableStringComparison.AsciiIgnoreCaseAlgorithmId,
                StringComparison.Ordinal) == true
                ? PortableStringComparisonPolicy.AsciiIgnoreCase
                : PortableStringComparisonPolicy.UnicodeOrdinalIgnoreCase;
            if (result.TryGetValue(source, out var value))
            {
                result[declaration.Name] = value is string text
                    ? PortableStringComparison.CreateSearchKey(text, policy)
                    : null;
            }
        }
        return new ReadOnlyDictionary<string, object?>(result);
    }
}
