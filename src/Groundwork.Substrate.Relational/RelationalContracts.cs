using System.Collections.ObjectModel;
using System.Data.Common;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;

namespace Groundwork.Substrate.Relational;

/// <summary>
/// Public provider-author contract for relational SQL, catalog, locking, fencing, and value
/// adaptation. Providers implement this type; no friend assembly or internal access is required.
/// </summary>
public abstract class RelationalDialect
{
    public abstract string ProviderName { get; }

    public abstract string QuoteIdentifier(string identifier);

    public abstract string MapType(ColumnDefinition definition);

    public abstract string? MapCollation(ColumnDefinition definition);

    public abstract string? MapDefault(ColumnDefinition definition);

    public abstract string CreateTableSql(string table, IReadOnlyList<string> columns, IReadOnlyList<string> primaryKey);

    public abstract string AddColumnSql(string table, string column, string definition);

    public abstract string FinalizeColumnSql(string table, string column);

    public abstract string CreateIndexSql(string table, IndexDefinition index, string? filter);

    public abstract string DropIndexSql(string table, string index);

    public abstract string ConditionalUpsertSql(RelationalWriteShape shape);

    public abstract string BatchInsertSql(RelationalWriteShape shape, int batchSize);

    public abstract object? ConvertValue(object? value, ColumnDefinition definition);

    public abstract void Validate(ColumnDefinition definition);

    public abstract bool TryMapUniqueViolation(DbException exception, out string indexName);

    public abstract void AcquireApplicationLock(DbConnection connection, string resource);

    public abstract void ReleaseApplicationLock(DbConnection connection, string resource);

    public abstract bool VerifyApplicationLock(DbConnection connection, string resource);

    /// <summary>Reads the provider's stable session identifier used by fencing diagnostics.</summary>
    public abstract long ReadServerSessionId(DbConnection connection);

    public abstract long AcquireFence(
        DbConnection connection,
        PhysicalSchemaTargetIdentity target,
        string owner);

    public abstract void AssertFence(
        DbConnection connection,
        DbTransaction transaction,
        PhysicalSchemaTargetIdentity target,
        string owner,
        long fence);

    public abstract void EnsureInfrastructure(DbConnection connection);

    public abstract PhysicalSchemaHistoryState ReadHistory(
        DbConnection connection,
        PhysicalSchemaTargetIdentity target);

    public abstract void PublishHistory(
        DbConnection connection,
        PhysicalSchemaAppliedState state);

    public abstract bool TableExists(
        DbConnection connection,
        DbTransaction transaction,
        string table);

    public abstract IReadOnlyDictionary<string, RelationalColumnMetadata> ReadColumns(
        DbConnection connection,
        DbTransaction transaction,
        string table);

    public abstract RelationalIndexMetadata? ReadIndex(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        string index);

    public virtual string? IndexFilter(IndexDefinition index) =>
        index.MissingValues == MissingValueBehavior.Excluded
            ? string.Join(
                " AND ",
                index.Columns.Select(column => $"{QuoteIdentifier(column.Column)} IS NOT NULL"))
            : null;

    public virtual string? BackfillColumnSql(string table, ColumnDefinition column) => null;

    public virtual void ApplyProviderDefinition(
        DbConnection connection,
        DbTransaction transaction,
        ProviderPhysicalSchemaDefinition definition)
    {
    }

    public virtual void ValidateTarget(
        DbConnection connection,
        DbTransaction transaction,
        PhysicalSchemaTarget target)
    {
    }
}

/// <summary>One column/value slot in a provider-supplied write statement.</summary>
public sealed record RelationalWriteColumn
{
    public RelationalWriteColumn(string name, string? parameterName = null)
    {
        Name = Require(name, nameof(name));
        ParameterName = string.IsNullOrWhiteSpace(parameterName) ? name : parameterName;
    }

    public string Name { get; }

    public string ParameterName { get; }

    private static string Require(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", parameterName)
            : value;
}

/// <summary>Provider-neutral shape passed to conditional-upsert and batch SQL hooks.</summary>
public sealed record RelationalWriteShape
{
    public RelationalWriteShape(
        string table,
        IEnumerable<RelationalWriteColumn> columns,
        IEnumerable<string> keyColumns,
        IEnumerable<string> updateColumns)
    {
        Table = Require(table, nameof(table));
        Columns = Snapshot(columns, nameof(columns));
        KeyColumns = Snapshot(keyColumns, nameof(keyColumns));
        UpdateColumns = Snapshot(updateColumns, nameof(updateColumns));
        if (Columns.Count == 0)
            throw new ArgumentException("A write shape requires at least one column.", nameof(columns));
        if (KeyColumns.Count == 0)
            throw new ArgumentException("A write shape requires at least one key column.", nameof(keyColumns));
    }

    public string Table { get; }

    public IReadOnlyList<RelationalWriteColumn> Columns { get; }

    public IReadOnlyList<string> KeyColumns { get; }

    public IReadOnlyList<string> UpdateColumns { get; }

    private static IReadOnlyList<T> Snapshot<T>(IEnumerable<T> values, string parameterName) =>
        new ReadOnlyCollection<T>((values ?? throw new ArgumentNullException(parameterName)).ToArray());

    private static string Require(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", parameterName)
            : value;
}

public sealed record RelationalColumnMetadata(
    string Name,
    string StoreType,
    bool IsNullable,
    string? DefaultValue,
    string? Collation,
    int PrimaryKeyOrder,
    bool IsComputed = false,
    bool IsPersisted = false,
    string? ComputedDefinition = null);

public sealed record RelationalIndexColumnMetadata(string Name, SortDirection Direction);

public sealed record RelationalIndexMetadata
{
    public RelationalIndexMetadata(
        bool isUnique,
        IEnumerable<RelationalIndexColumnMetadata> columns,
        string? filter)
    {
        IsUnique = isUnique;
        Columns = new ReadOnlyCollection<RelationalIndexColumnMetadata>(
            (columns ?? throw new ArgumentNullException(nameof(columns))).ToArray());
        Filter = filter;
    }

    public bool IsUnique { get; }

    public IReadOnlyList<RelationalIndexColumnMetadata> Columns { get; }

    public string? Filter { get; }
}
