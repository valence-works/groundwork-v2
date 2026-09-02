using System.Collections;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Groundwork.Kernel;

/// <summary>Provider-neutral physical expansion for folded and locale-ordered text columns.</summary>
public static class SearchKeyProjection
{
    public const string Prefix = "__groundwork_search_";
    public const string OrdinalIdentityPrefix = "__groundwork_ordinal_";

    public static bool IsProviderOwnedColumn(string columnName) =>
        columnName?.StartsWith(Prefix, StringComparison.Ordinal) == true ||
        columnName?.StartsWith(OrdinalIdentityPrefix, StringComparison.Ordinal) == true;

    public static IReadOnlyDictionary<string, object?> PublicValues(IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new ReadOnlyDictionary<string, object?>(values
            .Where(pair => !IsProviderOwnedColumn(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
    }

    public static bool IsFolded(PortableCollation? collation) =>
        collation is PortableCollation.OrdinalIgnoreCase or PortableCollation.UnicodeOrdinalIgnoreCase;

    public static bool IsProjected(ColumnDefinition column) =>
        column.Type == PortableType.String &&
        (IsFolded(LogicalCollation(column)) || column.LocaleSortKey is not null);

    internal static bool IsElementProjected(ColumnDefinition column) =>
        column.Type == PortableType.Json && column.ElementSearchKey is not null;

    public static PortableCollation? LogicalCollation(ColumnDefinition column) =>
        column.LogicalCollation ?? column.Collation;

    public static PortableStringComparisonPolicy Policy(PortableCollation collation) => collation switch
    {
        PortableCollation.OrdinalIgnoreCase => PortableStringComparisonPolicy.AsciiIgnoreCase,
        PortableCollation.UnicodeOrdinalIgnoreCase => PortableStringComparisonPolicy.UnicodeOrdinalIgnoreCase,
        _ => throw new ArgumentOutOfRangeException(nameof(collation), collation, "Only folded collations have search keys.")
    };

    internal static PortableStringComparisonPolicy ElementPolicy(ColumnDefinition column)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (!IsElementProjected(column))
            throw new ArgumentException("Only JSON columns with an element search-key declaration have an element policy.", nameof(column));
        return Policy(column.ElementSearchKey!.Collation);
    }

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

    internal static void ValidateAlgorithmId(string? algorithmId)
    {
        if (string.Equals(algorithmId, PortableStringComparison.OrdinalAlgorithmId, StringComparison.Ordinal))
            return;
        if (algorithmId?.StartsWith(PortableLocaleOrdering.AlgorithmName + ":", StringComparison.Ordinal) == true)
            _ = PortableLocaleOrdering.ParseAlgorithmId(algorithmId);
        else if (algorithmId?.StartsWith(PortableElementSearchKeyAlgorithm.Name + "+", StringComparison.Ordinal) == true)
            _ = PortableElementSearchKeyAlgorithm.Parse(algorithmId);
        else
            _ = PortableSearchKeyAlgorithmIdentity.Parse(algorithmId);
    }

    /// <summary>
    /// Identifies the additive projected-column migration that replaces an existing logical index
    /// term with its provider-owned search/sort key. Other index definition changes remain refused.
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
    /// Adds one provider-owned key for each folded, locale-ordered, or element-projected source.
    /// Scalar keys are nullable ASCII text and retarget declared physical indexes; element keys are
    /// positional JSON arrays and remain bounded-scan-only. Ordinal scalar columns are retargeted
    /// only by indexes that explicitly opt into ordinal identity covering.
    /// </summary>
    public static StorageUnit Expand(StorageUnit source)
    {
        ArgumentNullException.ThrowIfNull(source);
        StorageDeclarationReferenceValidation.ThrowIfInvalid(source);
        foreach (var column in source.Columns ?? [])
            ValidateElementSearchKey(column);
        foreach (var column in (source.Columns ?? []).Where(column => column.LocaleSortKey is not null))
        {
            var refusal = PortableLocaleOrdering.ValidateDeclaration(column, $"columns.{column.Name}.localeSortKey");
            if (refusal is not null)
                throw new InvalidOperationException($"{refusal.Code} at {refusal.Path}: {refusal.Message}");
        }
        var projectedSources = (source.Columns ?? []).Where(IsProjected).ToArray();
        var elementProjectedSources = (source.Columns ?? []).Where(IsElementProjected).ToArray();
        var ordinalIdentitySources = (source.Columns ?? []).Where(column => column.OrdinalIdentity is not null).ToArray();
        ValidateOrdinalIdentityIndexes(source, ordinalIdentitySources);

        var columns = (source.Columns ?? []).Select(column =>
            IsProjected(column)
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

        foreach (var column in projectedSources)
        {
            var name = ColumnName(column.Name);
            var existing = columns.FirstOrDefault(item => item.Name == name);
            var locale = column.LocaleSortKey;
            var expansionFactor = locale?.MaximumExpansionFactor ?? ExpansionFactor(LogicalCollation(column)!.Value);
            int? maxLength = column.MaxLength is int length
                ? checked(length * expansionFactor)
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

            var projection = locale is null ? PortableProjection.BoundarySearchKey : PortableProjection.LocaleSortKey;
            var algorithmId = locale is null
                ? PortableStringComparison.GetSearchKeyAlgorithmId(Policy(LogicalCollation(column)!.Value))
                : PortableLocaleOrdering.GetAlgorithmId(locale.CultureName);
            var existingDerived = derived.FirstOrDefault(item => item.Name == name);
            if (existingDerived is null)
            {
                derived.Add(new DerivedColumnDefinition
                {
                    Name = name,
                    SourceColumn = column.Name,
                    Projection = projection,
                    AlgorithmId = algorithmId
                });
            }
            else if (existingDerived.SourceColumn != column.Name || existingDerived.Projection != projection ||
                     !string.Equals(existingDerived.AlgorithmId, algorithmId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Search-key declaration '{name}' does not match source '{column.Name}'.");
            }
        }

        foreach (var column in elementProjectedSources)
        {
            var name = ColumnName(column.Name);
            var existing = columns.FirstOrDefault(item => item.Name == name);
            var expected = new ColumnDefinition
            {
                Name = name,
                Type = PortableType.Json,
                // A well-formed array is not guaranteed by the JSON type. A non-nullable owner
                // may carry an object or scalar, for which the derived key is deliberately null.
                IsNullable = true
            };
            if (existing is null)
                columns.Add(expected);
            else if (existing.Type != expected.Type || existing.IsNullable != expected.IsNullable)
                throw new InvalidOperationException($"Element search-key column '{name}' has incompatible physical metadata.");

            var algorithmId = PortableElementSearchKeyAlgorithm.ForPolicy(
                ElementPolicy(column),
                column.ElementSearchKey!.MaximumElementCodeUnits);
            var existingDerived = derived.FirstOrDefault(item => item.Name == name);
            if (existingDerived is null)
            {
                derived.Add(new DerivedColumnDefinition
                {
                    Name = name,
                    SourceColumn = column.Name,
                    Projection = PortableProjection.ElementBoundarySearchKey,
                    AlgorithmId = algorithmId
                });
            }
            else if (existingDerived.SourceColumn != column.Name ||
                     existingDerived.Projection != PortableProjection.ElementBoundarySearchKey ||
                     !string.Equals(existingDerived.AlgorithmId, algorithmId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Element search-key declaration '{name}' does not match source '{column.Name}'.");
            }
        }

        foreach (var column in ordinalIdentitySources)
        {
            var declaration = column.OrdinalIdentity!;
            if (column.Type != PortableType.String || column.IsNullable ||
                string.IsNullOrWhiteSpace(declaration.PhysicalColumn) ||
                !declaration.PhysicalColumn.StartsWith(OrdinalIdentityPrefix, StringComparison.Ordinal) ||
                string.Equals(declaration.PhysicalColumn, column.Name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Ordinal identity column '{column.Name}' requires a distinct non-null physical string column in the " +
                    $"'{OrdinalIdentityPrefix}' provider-owned namespace and a non-null source.");
            }

            var name = declaration.PhysicalColumn;
            var existing = columns.FirstOrDefault(item => item.Name == name);
            var expected = new ColumnDefinition
            {
                Name = name,
                Type = PortableType.String,
                IsNullable = false,
                MaxLength = column.MaxLength is int length ? checked(length * 4) : null,
                Collation = PortableCollation.Ordinal
            };
            if (existing is null)
                columns.Add(expected);
            else if (existing.Type != expected.Type || existing.IsNullable != expected.IsNullable ||
                     existing.MaxLength != expected.MaxLength || existing.Collation != expected.Collation)
            {
                throw new InvalidOperationException($"Ordinal identity column '{name}' has incompatible physical metadata.");
            }

            var existingDerived = derived.FirstOrDefault(item => item.Name == name);
            if (existingDerived is null)
            {
                derived.Add(new DerivedColumnDefinition
                {
                    Name = name,
                    SourceColumn = column.Name,
                    Projection = PortableProjection.OrdinalIdentity,
                    AlgorithmId = PortableStringComparison.OrdinalAlgorithmId
                });
            }
            else if (existingDerived.SourceColumn != column.Name ||
                     existingDerived.Projection != PortableProjection.OrdinalIdentity ||
                     !string.Equals(existingDerived.AlgorithmId, PortableStringComparison.OrdinalAlgorithmId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Ordinal identity declaration '{name}' does not match source '{column.Name}'.");
            }
        }

        var indexes = source.Indexes.Select(index =>
        {
            var physicalColumns = index.Columns.Select(column =>
            {
                var sourceColumn = (source.Columns ?? []).FirstOrDefault(item => item.Name == column.Column);
                if (sourceColumn is not null && IsProjected(sourceColumn))
                    return column with { Column = ColumnName(column.Column) };
                if (index.UseOrdinalIdentities && sourceColumn?.OrdinalIdentity is { } identity)
                    return column with { Column = identity.PhysicalColumn };
                return column with { };
            }).ToList();

            // Only an explicitly marked index is the covering shape used by projected DISTINCT:
            // retain each logical source as an included column after its injective physical key.
            // Providers without native included columns can lower this metadata to trailing keys.
            var includedColumns = (index.IncludedColumns ?? []).ToList();
            if (index.UseOrdinalIdentities)
            {
                foreach (var ordinalSource in index.Columns
                             .Where(column => (source.Columns ?? []).FirstOrDefault(item => item.Name == column.Column)?.OrdinalIdentity is not null)
                             .Select(column => column.Column))
                {
                    if (!includedColumns.Contains(ordinalSource, StringComparer.Ordinal))
                        includedColumns.Add(ordinalSource);
                }
            }

            return index with
            {
                Columns = physicalColumns.ToArray(),
                IncludedColumns = includedColumns.Count == 0 ? null : includedColumns.ToArray()
            };
        }).ToArray();

        return source with
        {
            Columns = new ReadOnlyCollection<ColumnDefinition>(columns),
            DerivedColumns = new ReadOnlyCollection<DerivedColumnDefinition>(derived),
            Indexes = indexes
        };
    }

    /// <summary>
    /// Lowers covering metadata to trailing key columns for providers that have no native INCLUDE
    /// clause. The declared key remains the prefix, so the index keeps its lookup ordering while
    /// still being physically covering.
    /// </summary>
    internal static StorageUnit LowerIncludedColumnsToKey(StorageUnit physical)
    {
        ArgumentNullException.ThrowIfNull(physical);
        return physical with
        {
            Indexes = physical.Indexes.Select(index =>
            {
                if (index.IncludedColumns is not { Count: > 0 })
                    return index;

                return index with
                {
                    Columns = [
                        .. index.Columns,
                        .. index.IncludedColumns.Select(column => new IndexColumn(column))
                    ],
                    IncludedColumns = null
                };
            }).ToArray()
        };
    }

    private static void ValidateOrdinalIdentityIndexes(
        StorageUnit source,
        IReadOnlyList<ColumnDefinition> ordinalIdentitySources)
    {
        var ordinalNames = ordinalIdentitySources
            .Select(column => column.Name)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var index in source.Indexes ?? [])
        {
            var explicitlyIncludedColumns = index.IncludedColumns ?? [];
            if (index.IsUnique && explicitlyIncludedColumns.Count > 0 &&
                (!index.UseOrdinalIdentities || explicitlyIncludedColumns.Any(column => !ordinalNames.Contains(column))))
            {
                throw new InvalidOperationException(
                    $"Unique index '{index.Name}' cannot declare portable included columns unless every included column " +
                    "is the ordinal identity source of this index; providers without native INCLUDE clauses lower " +
                    "included columns into the unique key.");
            }

            if (!index.UseOrdinalIdentities)
                continue;

            var identityColumns = index.Columns
                .Where(column => ordinalNames.Contains(column.Column))
                .Select(column => column.Column)
                .ToArray();
            if (identityColumns.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Index '{index.Name}' opts into ordinal identity covering but does not include an ordinal identity source.");
            }

            if (identityColumns.Any(column => IsProjected(
                    ordinalIdentitySources.First(sourceColumn => sourceColumn.Name == column))))
            {
                throw new InvalidOperationException(
                    $"Index '{index.Name}' cannot opt into ordinal identity covering for a source that also declares a folded or locale projection.");
            }
        }
    }

    /// <summary>Validates the optional JSON string-array search-key declaration on one column.</summary>
    internal static void ValidateElementSearchKey(ColumnDefinition column)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (column.ElementSearchKey is null)
            return;
        if (column.Type != PortableType.Json)
            throw new InvalidOperationException($"Element search keys require a JSON source column ('{column.Name}').");
        if (!IsFolded(column.ElementSearchKey.Collation))
            throw new InvalidOperationException(
                $"Element search-key column '{column.Name}' requires OrdinalIgnoreCase or UnicodeOrdinalIgnoreCase collation.");
        if (column.ElementSearchKey.MaximumElementCodeUnits is <= 0)
            throw new InvalidOperationException(
                $"Element search-key column '{column.Name}' requires a positive MaximumElementCodeUnits value.");
    }

    /// <summary>Computes hidden values for a write without allowing callers to supply them.</summary>
    public static IReadOnlyDictionary<string, object?> Populate(StorageUnit unit, IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(values);
        var unitColumns = unit.Columns ?? [];
        foreach (var column in unitColumns)
            ValidateElementSearchKey(column);
        var result = values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var declarations = unit.DerivedColumns
            .Where(column => column.Projection is PortableProjection.BoundarySearchKey or PortableProjection.LocaleSortKey or PortableProjection.ElementBoundarySearchKey or PortableProjection.OrdinalIdentity)
            .ToArray();
        if (declarations.Length == 0)
        {
            declarations = unitColumns
                .Where(column => IsProjected(column) || IsElementProjected(column))
                .Select(column => new DerivedColumnDefinition
                {
                    Name = ColumnName(column.Name),
                    SourceColumn = column.Name,
                    Projection = IsElementProjected(column)
                        ? PortableProjection.ElementBoundarySearchKey
                        : column.LocaleSortKey is null
                            ? PortableProjection.BoundarySearchKey
                            : PortableProjection.LocaleSortKey,
                    AlgorithmId = IsElementProjected(column)
                        ? PortableElementSearchKeyAlgorithm.ForPolicy(
                            ElementPolicy(column),
                            column.ElementSearchKey!.MaximumElementCodeUnits)
                        : column.LocaleSortKey is null
                            ? AlgorithmId(LogicalCollation(column)!.Value)
                            : PortableLocaleOrdering.GetAlgorithmId(column.LocaleSortKey.CultureName)
                })
                .ToArray();
        }
        foreach (var declaration in declarations)
        {
            if (result.ContainsKey(declaration.Name))
                throw new ArgumentException($"Search-key column '{declaration.Name}' is provider-owned and cannot be supplied.", nameof(values));
            var source = declaration.SourceColumn;
            if (result.TryGetValue(source, out var value))
            {
                object? projected = declaration.Projection == PortableProjection.ElementBoundarySearchKey
                    ? CreateElementProjectedValue(value, declaration, unit)
                    : value is string text
                        ? CreateProjectedValue(text, declaration)
                        : null;
                var maximumLength = unitColumns.FirstOrDefault(column => column.Name == declaration.Name)?.MaxLength;
                if (projected is string projectedText && projectedText.Length > maximumLength)
                {
                    throw new InvalidOperationException(
                        $"Locale sort key '{declaration.Name}' requires {projectedText.Length} characters, exceeding its declared maximum {maximumLength}. Increase MaximumExpansionFactor and rebuild the derived column.");
                }
                result[declaration.Name] = projected;
            }
        }
        return new ReadOnlyDictionary<string, object?>(result);
    }

    private static string CreateProjectedValue(string value, DerivedColumnDefinition declaration) =>
        declaration.Projection switch
        {
            PortableProjection.BoundarySearchKey => PortableStringComparison.CreateSearchKey(
                value,
                PortableSearchKeyAlgorithmIdentity.Parse(declaration.AlgorithmId).Policy),
            PortableProjection.OrdinalIdentity => PortableStringComparison.CreateOrdinal(value),
            PortableProjection.LocaleSortKey => PortableLocaleOrdering.CreateSortKey(
                value,
                PortableLocaleOrdering.ParseAlgorithmId(declaration.AlgorithmId).CultureName),
            _ => throw new InvalidOperationException(
                $"Derived search-key projection '{declaration.Projection}' is not supported.")
        };

    private static IReadOnlyList<string?>? CreateElementProjectedValue(
        object? value,
        DerivedColumnDefinition declaration,
        StorageUnit unit)
    {
        if (value is null)
            return null;
        if (!TryReadStringElements(value, out var elements))
            return null;

        var sourceColumn = unit.Columns.FirstOrDefault(column => column.Name == declaration.SourceColumn);
        var elementIdentity = PortableElementSearchKeyAlgorithm.Parse(declaration.AlgorithmId);
        var maximumCodeUnits = sourceColumn?.ElementSearchKey?.MaximumElementCodeUnits;
        if (maximumCodeUnits != elementIdentity.MaximumElementCodeUnits)
        {
            throw new InvalidOperationException(
                $"Element search-key '{declaration.Name}' has stale bound metadata. Rebuild the derived element search-key column before use.");
        }
        return elements
            .Select(element =>
            {
                if (element is null)
                    return (string?)null;
                if (!PortableStringComparison.IsWellFormedUnicode(element))
                    throw new InvalidOperationException(
                        $"Element search-key '{declaration.Name}' refuses an ill-formed UTF-16 string; repair the source value before writing it.");
                if (maximumCodeUnits is int maximum && element.Length > maximum)
                    throw new InvalidOperationException(
                        $"Element search-key '{declaration.Name}' refuses an element of {element.Length} UTF-16 code units; the declared maximum is {maximum}. Increase MaximumElementCodeUnits and rebuild the derived column.");
                return PortableStringComparison.CreateSearchKey(element, elementIdentity.Policy);
            })
            .ToArray();
    }

    private static bool TryReadStringElements(object value, out IReadOnlyList<string?> elements)
    {
        elements = Array.Empty<string?>();
        if (value is JsonNode node)
        {
            try
            {
                var nodeRoot = ParseJsonRoot(node.ToJsonString());
                if (nodeRoot.ValueKind != JsonValueKind.Array)
                    return false;
                elements = nodeRoot.EnumerateArray()
                    .Select(element => element.ValueKind == JsonValueKind.String ? element.GetString() : null)
                    .ToArray();
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }
        if (value is IEnumerable sequence && value is not string && value is not byte[] && value is not IDictionary)
        {
            elements = sequence.Cast<object?>()
                .Select(item => item as string)
                .ToArray();
            return true;
        }

        JsonElement root;
        try
        {
            root = value switch
            {
                JsonDocument document => document.RootElement,
                JsonElement element => element,
                string json => ParseJsonRoot(json),
                byte[] => default,
                _ => default
            };
        }
        catch (JsonException)
        {
            return false;
        }

        if (root.ValueKind != JsonValueKind.Array)
            return false;
        elements = root.EnumerateArray()
            .Select(element => element.ValueKind == JsonValueKind.String ? element.GetString() : null)
            .ToArray();
        return true;
    }

    private static JsonElement ParseJsonRoot(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
