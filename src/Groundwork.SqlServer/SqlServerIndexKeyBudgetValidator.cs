using System.Globalization;
using Groundwork.Kernel;

namespace Groundwork.SqlServer;

/// <summary>Search-key expansion policies used by the folded-column projection.</summary>
internal enum SqlServerSearchKeyExpansionPolicy
{
    AsciiFold,
    UnicodeFold
}

/// <summary>Raised when SQL Server cannot represent a declared physical key.</summary>
public sealed class SqlServerKeyBudgetException : InvalidOperationException
{
    internal SqlServerKeyBudgetException(
        string indexName,
        long requiredBytes,
        int requiredColumns,
        string message)
        : base(message)
    {
        IndexName = indexName;
        RequiredBytes = requiredBytes;
        RequiredColumns = requiredColumns;
    }

    public string IndexName { get; }

    public long RequiredBytes { get; }

    public int RequiredColumns { get; }
}

/// <summary>
/// Computes SQL Server's worst-case nonclustered key width before schema application. Folded
/// derived keys are resolved back to their logical source so diagnostics describe the caller's
/// bound and policy rather than the widened physical varchar column.
/// </summary>
internal static class SqlServerIndexKeyBudgetValidator
{
    public const int MaximumKeyColumns = 32;
    public const int MaximumKeyBytes = 1700;
    public static void Validate(StorageUnit unit)
        => ValidateCore(unit, null);

    internal static void Validate(
        StorageUnit unit,
        IReadOnlyDictionary<string, SqlServerSearchKeyExpansionPolicy>? searchKeyPolicies)
        => ValidateCore(unit, searchKeyPolicies);

    private static void ValidateCore(
        StorageUnit unit,
        IReadOnlyDictionary<string, SqlServerSearchKeyExpansionPolicy>? searchKeyPolicies)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var columns = (unit.Columns ?? throw new ArgumentException("A storage unit requires columns.", nameof(unit)))
            .ToDictionary(column => column.Name, StringComparer.Ordinal);
        var derived = (unit.DerivedColumns ?? [])
            .Where(column => column.Projection == PortableProjection.BoundarySearchKey)
            .ToDictionary(column => column.Name, StringComparer.Ordinal);

        ValidateIndex(
            new IndexDefinition
            {
                Name = "PRIMARY KEY",
                Columns = unit.Key.Columns.Select(column => new IndexColumn(column)).ToArray()
            },
            columns,
            derived,
            searchKeyPolicies);

        foreach (var index in unit.Indexes ?? [])
            ValidateIndex(index, columns, derived, searchKeyPolicies);
    }

    internal static int EstimateSearchKeyBytes(int sourceLength, SqlServerSearchKeyExpansionPolicy policy)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceLength);
        var factor = policy switch
        {
            SqlServerSearchKeyExpansionPolicy.AsciiFold => 5,
            SqlServerSearchKeyExpansionPolicy.UnicodeFold => 7,
            _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, null)
        };
        return checked(sourceLength * factor);
    }

    private static void ValidateIndex(
        IndexDefinition index,
        IReadOnlyDictionary<string, ColumnDefinition> columns,
        IReadOnlyDictionary<string, DerivedColumnDefinition> derived,
        IReadOnlyDictionary<string, SqlServerSearchKeyExpansionPolicy>? searchKeyPolicies)
    {
        ArgumentNullException.ThrowIfNull(index);
        var terms = new List<string>(index.Columns.Count);
        if (index.Columns.Count > MaximumKeyColumns)
        {
            throw new SqlServerKeyBudgetException(
                index.Name,
                0,
                index.Columns.Count,
                $"SQL Server physical index '{index.Name}' declares {index.Columns.Count} key columns; the provider limit is {MaximumKeyColumns}.");
        }

        long bytes = 0;
        foreach (var indexColumn in index.Columns)
        {
            if (!columns.TryGetValue(indexColumn.Column, out var column))
            {
                throw new InvalidOperationException(
                    $"SQL Server physical index '{index.Name}' references undeclared key column '{indexColumn.Column}'.");
            }

            var expansion = searchKeyPolicies is not null &&
                            searchKeyPolicies.TryGetValue(indexColumn.Column, out var policy)
                ? policy
                : DerivedPolicy(indexColumn.Column, derived, columns);
            var source = expansion is not null &&
                         derived.TryGetValue(indexColumn.Column, out var derivedColumn) &&
                         columns.TryGetValue(derivedColumn.SourceColumn, out var sourceColumn)
                ? sourceColumn
                : column;
            var displayName = source == column ? null : source.Name;
            var width = expansion is { } searchPolicy
                ? SearchKeyWidth(index.Name, source, searchPolicy, terms, displayName)
                : DeclaredWidth(index.Name, column, terms);
            bytes = checked(bytes + width);
        }

        if (bytes > MaximumKeyBytes)
        {
            throw new SqlServerKeyBudgetException(
                index.Name,
                bytes,
                index.Columns.Count,
                $"SQL Server physical index '{index.Name}' requires {bytes.ToString(CultureInfo.InvariantCulture)} bytes " +
                $"({string.Join(" + ", terms)}); the provider limit is {MaximumKeyBytes} bytes.");
        }
    }

    private static int SearchKeyWidth(
        string indexName,
        ColumnDefinition column,
        SqlServerSearchKeyExpansionPolicy policy,
        ICollection<string> terms,
        string? displayName = null)
    {
        if (column.Type != PortableType.String || column.MaxLength is not (> 0))
        {
            throw new SqlServerKeyBudgetException(
                indexName,
                0,
                0,
                $"SQL Server physical index '{indexName}' requires a positive MaxLength for search-key column '{column.Name}'.");
        }

        var width = EstimateSearchKeyBytes(column.MaxLength.Value, policy);
        var factor = policy == SqlServerSearchKeyExpansionPolicy.AsciiFold ? 5 : 7;
        terms.Add($"{displayName ?? column.Name}={column.MaxLength.Value}*{factor}");
        return width;
    }

    private static SqlServerSearchKeyExpansionPolicy? DerivedPolicy(
        string columnName,
        IReadOnlyDictionary<string, DerivedColumnDefinition> derived,
        IReadOnlyDictionary<string, ColumnDefinition> columns)
    {
        if (!derived.TryGetValue(columnName, out var projection))
            return null;
        if (!columns.TryGetValue(projection.SourceColumn, out var source) ||
            source.Type != PortableType.String ||
            !SearchKeyProjection.IsFolded(SearchKeyProjection.LogicalCollation(source)))
        {
            throw new InvalidOperationException(
                $"SQL Server derived search-key column '{columnName}' does not resolve to a folded String source.");
        }

        return SearchKeyProjection.LogicalCollation(source) switch
        {
            PortableCollation.OrdinalIgnoreCase => SqlServerSearchKeyExpansionPolicy.AsciiFold,
            PortableCollation.UnicodeOrdinalIgnoreCase => SqlServerSearchKeyExpansionPolicy.UnicodeFold,
            _ => throw new InvalidOperationException(
                $"SQL Server derived search-key column '{columnName}' has no supported folding policy.")
        };
    }

    private static int DeclaredWidth(
        string indexName,
        ColumnDefinition column,
        ICollection<string> terms)
    {
        switch (column.Type)
        {
            case PortableType.String:
                if (column.MaxLength is not (> 0))
                {
                    throw new SqlServerKeyBudgetException(
                        indexName,
                        0,
                        0,
                        $"SQL Server physical index '{indexName}' requires bounded String key column '{column.Name}'.");
                }

                var factor = column.Name.StartsWith(SearchKeyProjection.Prefix, StringComparison.Ordinal) ? 1 : 2;
                terms.Add($"{column.Name}={column.MaxLength.Value}*{factor}");
                return checked(column.MaxLength.Value * factor);
            case PortableType.Binary:
                if (column.MaxLength is not (> 0))
                {
                    throw new SqlServerKeyBudgetException(
                        indexName,
                        0,
                        0,
                        $"SQL Server physical index '{indexName}' requires bounded Binary key column '{column.Name}'.");
                }

                terms.Add($"{column.Name}={column.MaxLength.Value}");
                return column.MaxLength.Value;
            case PortableType.Int32:
                terms.Add($"{column.Name}=4");
                return 4;
            case PortableType.Int64:
                terms.Add($"{column.Name}=8");
                return 8;
            case PortableType.Decimal when column.Precision is >= 1 and <= 9:
                terms.Add($"{column.Name}=5(decimal precision {column.Precision})");
                return 5;
            case PortableType.Decimal when column.Precision is >= 10 and <= 19:
                terms.Add($"{column.Name}=9(decimal precision {column.Precision})");
                return 9;
            case PortableType.Decimal when column.Precision is >= 20 and <= 28:
                terms.Add($"{column.Name}=13(decimal precision {column.Precision})");
                return 13;
            case PortableType.Decimal when column.Precision is >= 29 and <= 38:
                terms.Add($"{column.Name}=17(decimal precision {column.Precision})");
                return 17;
            case PortableType.Decimal:
                throw new SqlServerKeyBudgetException(
                    indexName,
                    0,
                    0,
                    $"SQL Server physical index '{indexName}' requires Decimal key column '{column.Name}' to declare precision 1-38.");
            case PortableType.Boolean:
                terms.Add($"{column.Name}=1");
                return 1;
            case PortableType.DateTimeOffset:
                terms.Add($"{column.Name}=10");
                return 10;
            case PortableType.Guid:
                terms.Add($"{column.Name}=16");
                return 16;
            default:
                throw new SqlServerKeyBudgetException(
                    indexName,
                    0,
                    0,
                    $"SQL Server physical index '{indexName}' does not support {column.Type} key column '{column.Name}'.");
        }
    }
}
