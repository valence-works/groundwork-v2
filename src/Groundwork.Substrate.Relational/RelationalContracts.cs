using System.Data;
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
    protected static void Execute(DbConnection connection, DbTransaction? transaction, string sql)
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

    /// <summary>Renders one named foreign-key table constraint.</summary>
    public virtual string ForeignKeyDefinitionSql(ReferenceDefinition reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.Enforcement != ReferenceEnforcement.Physical ||
            string.IsNullOrWhiteSpace(reference.TargetName) ||
            reference.TargetKeyColumns is null)
        {
            throw new ArgumentException("A physical foreign key requires resolved target metadata.", nameof(reference));
        }
        return $"CONSTRAINT {QuoteIdentifier(reference.Name)} FOREIGN KEY " +
            $"({string.Join(", ", reference.Columns.Select(QuoteIdentifier))}) REFERENCES " +
            $"{QuoteIdentifier(reference.TargetName)} ({string.Join(", ", reference.TargetKeyColumns.Select(QuoteIdentifier))})";
    }

    /// <summary>Renders one named portable check table constraint.</summary>
    public virtual string CheckConstraintDefinitionSql(
        CheckConstraintDefinition constraint,
        ColumnDefinition column) =>
        $"CONSTRAINT {QuoteIdentifier(constraint.Name)} CHECK ({CheckExpressionSql(constraint, column)})";

    public virtual string CreateForeignKeySql(string table, ReferenceDefinition reference) =>
        $"ALTER TABLE {QuoteIdentifier(table)} ADD {ForeignKeyDefinitionSql(reference)};";

    public virtual string CreateCheckConstraintSql(
        string table,
        CheckConstraintDefinition constraint,
        ColumnDefinition column) =>
        $"ALTER TABLE {QuoteIdentifier(table)} ADD {CheckConstraintDefinitionSql(constraint, column)};";

    public virtual void CreateForeignKey(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        ReferenceDefinition reference) =>
        Execute(connection, transaction, CreateForeignKeySql(table, reference));

    public virtual void CreateCheckConstraint(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        CheckConstraintDefinition constraint,
        ColumnDefinition column) =>
        Execute(connection, transaction, CreateCheckConstraintSql(table, constraint, column));

    public abstract string DropIndexSql(string table, string index);

    public abstract string ConditionalUpsertSql(RelationalWriteShape shape);

    public abstract string BatchInsertSql(RelationalWriteShape shape, int batchSize);

    public abstract object? ConvertValue(object? value, ColumnDefinition definition);

    /// <summary>
    /// The read direction of <see cref="ConvertValue"/>: maps one stored value back to the portable
    /// CLR shape the declaration names. A host-process data-migration transform must see the same
    /// value type whatever provider it runs against, so the row it is given is mapped through this
    /// rather than handed the driver's native representation.
    /// </summary>
    public virtual object? ReadValue(object? value, ColumnDefinition definition) =>
        value is DBNull ? null : value;

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

    /// <summary>The isolation one durable Groundwork unit runs at on this dialect.</summary>
    public virtual IsolationLevel TransactionIsolation => IsolationLevel.Unspecified;

    /// <summary>
    /// Begins a transaction on the surface the caller selected, at this dialect's isolation. The
    /// asynchronous data-migration path uses it so a chunk does not open its unit with a blocking
    /// call, and so it runs at the same isolation as a schema apply rather than the driver default.
    /// </summary>
    public virtual ValueTask<DbTransaction> BeginTransaction(DbConnection connection, RelationalExecution mode)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return mode.BeginTransaction(connection, TransactionIsolation);
    }

    /// <summary>Prepares connection-scoped settings before a schema-operation transaction begins.</summary>
    public virtual void PrepareSchemaBatch(DbConnection connection) { }

    /// <summary>Validates provider invariants before a schema-operation transaction commits.</summary>
    public virtual void ValidateSchemaBatch(DbConnection connection, DbTransaction transaction) { }

    /// <summary>Restores connection-scoped settings after a schema-operation transaction ends.</summary>
    public virtual void CompleteSchemaBatch(DbConnection connection) { }

    /// <summary>
    /// Reports whether a replayed schema operation is already physically satisfied. Dialects whose
    /// DDL commits independently of the caller transaction use this seam to make a failed batch
    /// recoverable without reissuing duplicate-object statements.
    /// </summary>
    protected internal virtual bool IsSchemaOperationSatisfied(
        DbConnection connection,
        DbTransaction transaction,
        PhysicalSchemaOperation operation) => false;

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

    /// <summary>Reads one named physical constraint, or null when the catalog does not contain it.</summary>
    public virtual RelationalConstraintMetadata? ReadConstraint(
        DbConnection connection,
        DbTransaction? transaction,
        string table,
        string constraint) => null;

    /// <summary>Compares a catalog constraint with its declaration using this dialect's SQL form.</summary>
    public virtual bool ConstraintMatches(
        RelationalConstraintMetadata actual,
        ReferenceDefinition expected) =>
        actual.Kind == RelationalConstraintKind.ForeignKey &&
        string.Equals(actual.TargetTable, expected.TargetName, StringComparison.Ordinal) &&
        actual.SourceColumns.SequenceEqual(expected.Columns, StringComparer.Ordinal) &&
        actual.TargetColumns.SequenceEqual(expected.TargetKeyColumns ?? [], StringComparer.Ordinal);

    public virtual bool ConstraintMatches(
        RelationalConstraintMetadata actual,
        CheckConstraintDefinition expected,
        ColumnDefinition column) =>
        actual.Kind == RelationalConstraintKind.Check &&
        string.Equals(
            NormalizeConstraintSql(actual.CheckExpression),
            NormalizeConstraintSql(CheckExpressionSql(expected, column)),
            StringComparison.OrdinalIgnoreCase);

    protected virtual string CheckExpressionSql(
        CheckConstraintDefinition constraint,
        ColumnDefinition column)
    {
        ArgumentNullException.ThrowIfNull(constraint);
        ArgumentNullException.ThrowIfNull(column);
        var identifier = QuoteIdentifier(constraint.Column);
        if (constraint.Value.Value is null)
        {
            return constraint.Operator switch
            {
                CheckConstraintOperator.Equal => $"{identifier} IS NULL",
                CheckConstraintOperator.NotEqual => $"{identifier} IS NOT NULL",
                _ => throw new ArgumentException("Only equality check operators accept null.", nameof(constraint))
            };
        }
        var comparison = constraint.Operator switch
        {
            CheckConstraintOperator.Equal => "=",
            CheckConstraintOperator.NotEqual => "<>",
            CheckConstraintOperator.GreaterThan => ">",
            CheckConstraintOperator.GreaterThanOrEqual => ">=",
            CheckConstraintOperator.LessThan => "<",
            CheckConstraintOperator.LessThanOrEqual => "<=",
            _ => throw new ArgumentOutOfRangeException(nameof(constraint), constraint.Operator, null)
        };
        return $"{identifier} {comparison} {RenderAggregationLiteral(constraint.Value.Value, column.Type)}";
    }

    protected static string NormalizeConstraintSql(string? sql) => string.IsNullOrWhiteSpace(sql)
        ? string.Empty
        : new string(sql.Where(character => !char.IsWhiteSpace(character) && character is not '(' and not ')').ToArray());

    public virtual string? IndexFilter(IndexDefinition index) =>
        index.MissingValues == MissingValueBehavior.Excluded
            ? string.Join(
                " AND ",
                index.Columns.Select(column => $"{QuoteIdentifier(column.Column)} IS NOT NULL"))
            : null;

    public virtual string? BackfillColumnSql(string table, ColumnDefinition column) => null;

    /// <summary>
    /// The most parameters one statement may bind on this provider. It bounds a data-migration
    /// chunk, so a chunk is sized to what the provider can actually bind instead of failing on it.
    /// The default is the smallest budget any shipped provider has, so an unrevised custom dialect
    /// is conservative rather than wrong.
    /// </summary>
    public virtual int ParameterBudget => 999;

    /// <summary>Caps an ordered scan at <paramref name="rows"/> rows.</summary>
    public virtual string LimitClause(int rows) => $" LIMIT {rows}";

    /// <summary>
    /// Upserts one row of the data-migration ledger, keyed by subject, provider, and migration id.
    /// A dialect that returns non-null must also create
    /// <see cref="RelationalDataMigrationLedger.TableName"/> in <see cref="EnsureInfrastructure"/>;
    /// returning null withholds the <see cref="Groundwork.Kernel.Schema.DataMigrationCapabilities.AppliedLedger"/>
    /// capability, and the kernel then refuses data migrations on this dialect rather than running
    /// them unrecorded.
    /// </summary>
    public virtual string? DataMigrationLedgerUpsertSql => null;

    public virtual void ApplyProviderDefinition(
        DbConnection connection,
        DbTransaction transaction,
        ProviderPhysicalSchemaDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(definition);
        if (!string.Equals(definition.Kind, RelationalInteropViewDefinition.Kind, StringComparison.Ordinal))
            return;

        PreflightProviderDefinition(connection, transaction, definition);
        var view = RelationalInteropViewDefinition.Parse(definition);
        ExecuteDefinition(connection, transaction, $"DROP VIEW IF EXISTS {QuoteIdentifier(view.ViewName)};");
        var projection = string.Join(", ", view.Columns.Select(column =>
        {
            var definition = column.ToColumn();
            return $"{RenderInteropViewExpression(definition)} AS {QuoteIdentifier(definition.Name)}";
        }));
        ExecuteDefinition(
            connection,
            transaction,
            $"CREATE VIEW {QuoteIdentifier(view.ViewName)} AS SELECT {projection} " +
            $"FROM {QuoteIdentifier(view.SourceName)} WHERE '{InteropViewMarker(definition)}' = '{InteropViewMarker(definition)}';");
    }

    /// <summary>Maps one physical column into its provider-idiomatic reporting representation.</summary>
    protected virtual string RenderInteropViewExpression(ColumnDefinition column) => QuoteIdentifier(column.Name);

    /// <summary>
    /// Refuses an unsupported or colliding provider definition before an operation batch performs
    /// any schema mutation. External dialects that opt into the shared interop-view emitter must
    /// also opt into exact live-definition inspection.
    /// </summary>
    public virtual void PreflightProviderDefinition(
        DbConnection connection,
        DbTransaction? transaction,
        ProviderPhysicalSchemaDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(definition);
        if (!string.Equals(definition.Kind, RelationalInteropViewDefinition.Kind, StringComparison.Ordinal))
            return;
        if (!SupportsInteropViewDefinitionInspection)
            throw new InvalidOperationException(
                $"Relational dialect '{ProviderName}' cannot inspect an interop view definition and therefore refuses to create one.");
        var view = RelationalInteropViewDefinition.Parse(definition);
        string? blocker;
        try
        {
            blocker = ReadInteropViewBlockingObject(connection, transaction, view.ViewName);
        }
        catch (DbException exception)
        {
            throw new InvalidOperationException(
                $"Interop view name '{view.ViewName}' could not be preflighted against the provider catalog.", exception);
        }
        if (blocker is not null)
            throw new InvalidOperationException(
                $"GW-PORT-015: Interop view '{view.ViewName}' collides with an existing {blocker}.");
    }

    /// <summary>
    /// Removes a provider-owned definition that no longer belongs to the deployed schema, because
    /// the storage it named was renamed or removed. A provider that materializes nothing in
    /// <see cref="ApplyProviderDefinition"/> has nothing to remove and inherits the empty default;
    /// one that creates a named object or a catalog row must delete it here, or every rename and
    /// retirement leaves another dead object behind in the user's database.
    /// </summary>
    public virtual void DropProviderDefinition(
        DbConnection connection,
        DbTransaction transaction,
        ProviderPhysicalSchemaDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(definition);
        if (!string.Equals(definition.Kind, RelationalInteropViewDefinition.Kind, StringComparison.Ordinal))
            return;
        var view = RelationalInteropViewDefinition.Parse(definition);
        ExecuteDefinition(connection, transaction, $"DROP VIEW IF EXISTS {QuoteIdentifier(view.ViewName)};");
    }

    public virtual void ValidateTarget(
        DbConnection connection,
        DbTransaction? transaction,
        PhysicalSchemaTarget target)
    {
        foreach (var definition in target.ProviderDefinitions.Where(definition =>
                     string.Equals(definition.Kind, RelationalInteropViewDefinition.Kind, StringComparison.Ordinal)))
        {
            var view = RelationalInteropViewDefinition.Parse(definition);
            if (!SupportsInteropViewDefinitionInspection)
                throw new InvalidOperationException(
                    $"Relational dialect '{ProviderName}' cannot inspect interop view '{view.ViewName}'.");
            string? liveDefinition;
            try
            {
                liveDefinition = ReadInteropViewDefinition(connection, transaction, view.ViewName);
            }
            catch (DbException exception)
            {
                throw new InvalidOperationException(
                    $"Interop view '{view.ViewName}' could not be inspected.", exception);
            }
            if (liveDefinition is null || !liveDefinition.Contains(InteropViewMarker(definition), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Interop view '{view.ViewName}' is missing or its deployed definition has drifted.");
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            var projection = string.Join(", ", view.Columns.Select(column => QuoteIdentifier(column.Name)));
            command.CommandText = $"SELECT {projection} FROM {QuoteIdentifier(view.ViewName)} WHERE 1=0;";
            try
            {
                using var reader = command.ExecuteReader();
            }
            catch (DbException exception)
            {
                throw new InvalidOperationException(
                    $"Interop view '{view.ViewName}' is missing or unreadable.", exception);
            }
        }
    }

    /// <summary>
    /// Reads the provider's catalog representation of a view definition. Shipped relational
    /// providers override this so target validation can distinguish a same-shaped replacement
    /// from the exact provider definition Groundwork deployed.
    /// </summary>
    protected virtual bool SupportsInteropViewDefinitionInspection => false;

    /// <summary>
    /// Returns the provider object kind that prevents a view from owning <paramref name="viewName"/>,
    /// or null when the name is free or already names a replaceable ordinary view.
    /// </summary>
    protected virtual string? ReadInteropViewBlockingObject(
        DbConnection connection,
        DbTransaction? transaction,
        string viewName) => TableExists(connection, transaction, viewName) ? "base table" : null;

    protected virtual string? ReadInteropViewDefinition(
        DbConnection connection,
        DbTransaction? transaction,
        string viewName) => null;

    /// <summary>Reads one nullable text value from a provider catalog query.</summary>
    protected static string? ReadCatalogText(
        DbConnection connection,
        DbTransaction? transaction,
        string sql,
        string parameterName,
        string value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = parameterName;
        parameter.Value = value;
        command.Parameters.Add(parameter);
        return command.ExecuteScalar() as string;
    }

    private static string InteropViewMarker(ProviderPhysicalSchemaDefinition definition) =>
        "groundwork:" + definition.Fingerprint;

    private static void ExecuteDefinition(DbConnection connection, DbTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
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

public enum RelationalConstraintKind
{
    ForeignKey,
    Check
}

public sealed record RelationalConstraintMetadata
{
    public RelationalConstraintMetadata(
        RelationalConstraintKind kind,
        IEnumerable<string>? sourceColumns = null,
        string? targetTable = null,
        IEnumerable<string>? targetColumns = null,
        string? checkExpression = null)
    {
        Kind = kind;
        SourceColumns = new ReadOnlyCollection<string>((sourceColumns ?? []).ToArray());
        TargetTable = targetTable;
        TargetColumns = new ReadOnlyCollection<string>((targetColumns ?? []).ToArray());
        CheckExpression = checkExpression;
    }

    public RelationalConstraintKind Kind { get; }

    public IReadOnlyList<string> SourceColumns { get; }

    public string? TargetTable { get; }

    public IReadOnlyList<string> TargetColumns { get; }

    public string? CheckExpression { get; }
}
