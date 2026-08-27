using System.Collections.ObjectModel;
using System.Data.Common;
using System.Globalization;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;

namespace Groundwork.Substrate.Relational;

/// <summary>
/// Public provider-author contract for relational SQL, catalog, locking, fencing, and value
/// adaptation. Providers implement this type; no friend assembly or internal access is required.
/// </summary>
public abstract class RelationalDialect
{
    public const string SearchKeyDefinitionKind = "search-key-algorithm";
    public const string SearchKeyDefinitionSeparator = "\u001f";

    public const string SchemaHistoryTable = "__groundwork_schema_history";
    public const string SearchKeyAlgorithmsTable = "__groundwork_search_key_algorithms";

    public abstract string ProviderName { get; }

    /// <summary>Creates the provider's ordinary query renderer for shared predicate fragments.</summary>
    public virtual RelationalQueryRenderer CreateQueryRenderer() =>
        throw new NotSupportedException($"Relational dialect '{ProviderName}' does not expose a query renderer.");

    /// <summary>Whether CreateTableSql materializes the complete column set in one statement.</summary>
    public virtual bool CreateTableIncludesColumns => false;

    /// <summary>Renders the provider-native command for a declared grouped reduction.</summary>
    public virtual RelationalAggregationCommand RenderAggregation(
        StorageUnit unit,
        AggregationProfile profile,
        AggregationQuery? query = null) =>
        RelationalAggregationRenderer.Render(this, unit, profile, query);

    /// <summary>Renders exact membership for a JSON/array SetUnion output.</summary>
    public virtual string RenderAggregationContains(string expression, string literal) =>
        throw new NotSupportedException("A relational dialect must define exact SetUnion membership rendering.");

    /// <summary>Renders an ordinal source-string containment operation for aggregation input.</summary>
    public virtual string RenderAggregationSourceContains(string expression, string literal) =>
        throw new NotSupportedException("A relational dialect must define ordinal aggregation source containment rendering.");

    /// <summary>Renders an ordinal source-string suffix operation for aggregation input.</summary>
    public virtual string RenderAggregationSourceEndsWith(string expression, string literal) =>
        throw new NotSupportedException("A relational dialect must define ordinal aggregation source suffix rendering.");

    /// <summary>Renders a typed literal for a post-reduction aggregation predicate.</summary>
    public virtual string RenderAggregationLiteral(object? value, PortableType type) => value switch
    {
        null => "NULL",
        string text => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'",
        bool boolean => boolean ? "1" : "0",
        DateTimeOffset instant => "'" + instant.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) + "'",
        Guid guid => "'" + guid.ToString("D") + "'",
        byte[] bytes => "X'" + Convert.ToHexString(bytes) + "'",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? "NULL",
        _ => throw new AggregationValidationException([new("GW-AGG-PRED-011", $"The predicate value is not compatible with {type}.", "postPredicate.values")])
    };

    /// <summary>Renders one portable null-aware output ordering term for grouped aggregation.</summary>
    protected internal virtual string RenderAggregationOrder(string expression, PortableType type, SortDirection direction)
    {
        var descending = direction == SortDirection.Descending;
        var order = descending ? "DESC" : "ASC";
        return $"CASE WHEN {expression} IS NULL THEN {(descending ? 1 : 0)} ELSE {(descending ? 0 : 1)} END, {expression} {order}";
    }

    public abstract string QuoteIdentifier(string identifier);

    public abstract string MapType(ColumnDefinition definition);

    public abstract string? MapCollation(ColumnDefinition definition);

    public abstract string? MapDefault(ColumnDefinition definition);

    public abstract string CreateTableSql(string table, IReadOnlyList<string> columns, IReadOnlyList<string> primaryKey);

    /// <summary>
    /// Emits a create-table statement with the declaration's provider-sequence column, when
    /// present. The overload keeps the original provider-authoring contract intact while
    /// allowing SQLite to spell its inline INTEGER PRIMARY KEY AUTOINCREMENT form.
    /// </summary>
    public virtual string CreateTableSql(
        string table,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> primaryKey,
        string? providerSequenceColumn) =>
        CreateTableSql(table, columns, primaryKey);

    /// <summary>Optional provider-specific generated-column syntax for an existing column list.</summary>
    public virtual string? MapGeneration(ColumnDefinition definition) => null;

    public abstract string AddColumnSql(string table, string column, string definition);

    /// <summary>
    /// Emits the provider-specific finalization for a column that has already been backfilled.
    /// The complete declaration is supplied because providers such as SQL Server need the type,
    /// nullability, and collation when changing the temporary column definition.
    /// </summary>
    public abstract string FinalizeColumnSql(string table, string column, ColumnDefinition definition);

    /// <summary>
    /// Applies finalization for a previously backfilled column. Providers without native ALTER
    /// COLUMN support may override this hook to perform a transactional table rebuild.
    /// </summary>
    public virtual void FinalizeColumn(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        ColumnDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(definition);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = RelationalSql.FinalizeColumn(this, table, definition);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Redefines an existing column in place. The default reuses <see cref="FinalizeColumn"/>,
    /// because finalizing a backfilled column and widening or narrowing one are the same physical
    /// act on every dialect Groundwork ships: replace the column's definition with the declared one.
    /// </summary>
    public virtual void AlterColumn(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        ColumnDefinition definition) =>
        FinalizeColumn(connection, transaction, table, definition);

    /// <summary>Removes one column and every value stored in it.</summary>
    public virtual string DropColumnSql(string table, string column) =>
        $"ALTER TABLE {QuoteIdentifier(table)} DROP COLUMN {QuoteIdentifier(column)};";

    /// <summary>
    /// Removes one column. Providers that cannot express the removal as a single statement override
    /// this hook; the default executes <see cref="DropColumnSql"/>.
    /// </summary>
    public virtual void DropColumn(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        ColumnDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(definition);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = DropColumnSql(table, definition.Name);
        command.ExecuteNonQuery();
    }

    /// <summary>Removes the primary storage and every row in it.</summary>
    public virtual string DropTableSql(string table) => $"DROP TABLE {QuoteIdentifier(table)};";

    /// <summary>Renames the primary storage, carrying its rows with it.</summary>
    public virtual string RenameTableSql(string table, string renamed) =>
        $"ALTER TABLE {QuoteIdentifier(table)} RENAME TO {QuoteIdentifier(renamed)};";

    /// <summary>
    /// Renames the primary storage. Providers whose physical index names embed the storage name
    /// override this hook so their catalog stays addressable afterwards.
    /// </summary>
    public virtual void RenameTable(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        string renamed)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = RenameTableSql(table, renamed);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Moves one index onto renamed storage. Every dialect Groundwork ships derives its physical
    /// index name from the storage name, so the index has to move with it. The portable default
    /// drops and recreates; a dialect with a native index rename overrides this to keep it cheap.
    /// </summary>
    public virtual void RenameIndex(
        DbConnection connection,
        DbTransaction transaction,
        string fromTable,
        string toTable,
        IndexDefinition index)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(index);
        Execute(connection, transaction, DropIndexSql(fromTable, index.Name));
        Execute(connection, transaction, CreateIndexSql(toTable, index, IndexFilter(index)));
    }

    /// <summary>Runs one statement on the dialect's schema connection and transaction.</summary>
    protected static void Execute(DbConnection connection, DbTransaction transaction, string sql)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Renames one column in place, carrying its values with it.</summary>
    public virtual string RenameColumnSql(string table, string column, string renamed) =>
        $"ALTER TABLE {QuoteIdentifier(table)} RENAME COLUMN {QuoteIdentifier(column)} TO {QuoteIdentifier(renamed)};";

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

    /// <summary>Begins the provider's transaction mode for a schema operation batch.</summary>
    public virtual DbTransaction BeginTransaction(DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection.BeginTransaction();
    }

    public abstract void EnsureInfrastructure(DbConnection connection);

    public abstract PhysicalSchemaHistoryState ReadHistory(
        DbConnection connection,
        PhysicalSchemaTargetIdentity target);

    /// <summary>
    /// Atomically publishes applied history using the supplied transaction and fence.
    /// <paramref name="expectedAppliedTargetFingerprint"/> is the previously durable target
    /// fingerprint (or <see langword="null"/> when no history row is expected), not the new
    /// fingerprint. Providers must perform the compare-and-swap against that value and throw when
    /// it no longer matches.
    /// </summary>
    public abstract void PublishHistory(
        DbConnection connection,
        DbTransaction transaction,
        PhysicalSchemaTargetIdentity target,
        PhysicalSchemaAppliedState state,
        string? expectedAppliedTargetFingerprint,
        string owner,
        long fence);

    public abstract bool TableExists(
        DbConnection connection,
        DbTransaction? transaction,
        string table);

    public abstract IReadOnlyDictionary<string, RelationalColumnMetadata> ReadColumns(
        DbConnection connection,
        DbTransaction? transaction,
        string table);

    /// <summary>
    /// Reads provider-persisted search-key algorithm ids for derived columns. Providers that do
    /// not persist derived search keys return an empty map; a target declaring one is then
    /// conservatively refused during runtime admission rather than admitted without evidence.
    /// </summary>
    public virtual IReadOnlyDictionary<string, string> ReadDerivedSearchKeyAlgorithms(
        DbConnection connection,
        DbTransaction? transaction,
        string table) => new Dictionary<string, string>(StringComparer.Ordinal);

    public abstract RelationalIndexMetadata? ReadIndex(
        DbConnection connection,
        DbTransaction? transaction,
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
        DbTransaction? transaction,
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
        KeyColumns = SnapshotNames(keyColumns, nameof(keyColumns));
        UpdateColumns = SnapshotNames(updateColumns, nameof(updateColumns));
        if (Columns.Count == 0)
            throw new ArgumentException("A write shape requires at least one column.", nameof(columns));
        if (KeyColumns.Count == 0)
            throw new ArgumentException("A write shape requires at least one key column.", nameof(keyColumns));

        var columnNames = Columns.Select(column => column.Name).ToArray();
        if (columnNames.Distinct(StringComparer.Ordinal).Count() != columnNames.Length)
            throw new ArgumentException("A write shape cannot contain duplicate columns.", nameof(columns));
        var parameterNames = Columns.Select(column => column.ParameterName).ToArray();
        if (parameterNames.Distinct(StringComparer.Ordinal).Count() != parameterNames.Length)
            throw new ArgumentException("A write shape cannot contain duplicate parameter names.", nameof(columns));

        var available = columnNames.ToHashSet(StringComparer.Ordinal);
        if (KeyColumns.Distinct(StringComparer.Ordinal).Count() != KeyColumns.Count)
            throw new ArgumentException("A write shape cannot contain duplicate key columns.", nameof(keyColumns));
        if (UpdateColumns.Distinct(StringComparer.Ordinal).Count() != UpdateColumns.Count)
            throw new ArgumentException("A write shape cannot contain duplicate update columns.", nameof(updateColumns));
        if (KeyColumns.Any(column => !available.Contains(column)))
            throw new ArgumentException("Every key column must be present in columns.", nameof(keyColumns));
        if (UpdateColumns.Any(column => !available.Contains(column)))
            throw new ArgumentException("Every update column must be present in columns.", nameof(updateColumns));
        if (KeyColumns.Any(UpdateColumns.Contains))
            throw new ArgumentException("Key columns cannot also be update columns.", nameof(updateColumns));
    }

    public string Table { get; }

    public IReadOnlyList<RelationalWriteColumn> Columns { get; }

    public IReadOnlyList<string> KeyColumns { get; }

    public IReadOnlyList<string> UpdateColumns { get; }

    private static IReadOnlyList<RelationalWriteColumn> Snapshot(
        IEnumerable<RelationalWriteColumn> values,
        string parameterName)
    {
        var snapshot = (values ?? throw new ArgumentNullException(parameterName)).ToArray();
        if (snapshot.Any(column => column is null))
            throw new ArgumentException("A write shape cannot contain null columns.", parameterName);
        return new ReadOnlyCollection<RelationalWriteColumn>(snapshot);
    }

    private static IReadOnlyList<string> SnapshotNames(
        IEnumerable<string> values,
        string parameterName)
    {
        var snapshot = (values ?? throw new ArgumentNullException(parameterName)).ToArray();
        if (snapshot.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("A write shape cannot contain empty column names.", parameterName);
        return new ReadOnlyCollection<string>(snapshot);
    }

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
    string? ComputedDefinition = null,
    ColumnGeneration Generation = ColumnGeneration.Supplied);

public sealed record RelationalIndexColumnMetadata(
    string Name,
    SortDirection Direction,
    bool? NullsFirst = null);

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
