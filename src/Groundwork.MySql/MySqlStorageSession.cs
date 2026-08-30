using System.Data.Common;
using System.Globalization;
using Groundwork.Kernel;
using Groundwork.Query.Model;
using Groundwork.Store;
using Groundwork.Substrate.Relational;
using MySqlConnector;

namespace Groundwork.MySql;

internal class MySqlStorageSession : RelationalStorageSessionBase,
    IConcurrencyStorageSession,
    IExactAppendStorageSession,
    IRetentionStorageSession,
    IExactRetentionStorageSession,
    IPrivilegedCrossScopeQuerySession,
    ISetMutationStorageSession
{
    private readonly MySqlProviderConnection owner;
    private readonly MySqlSessionAdapter commands;

    internal MySqlStorageSession(
        MySqlProviderConnection owner,
        StorageUnit unit,
        StorageAccess access,
        MySqlConnection connection,
        MySqlTransaction? transaction,
        SemaphoreSlim gate,
        MySqlSessionLifetime lifetime,
        bool ownsConnection,
        IProviderCommandObserver? observer)
        : this(
            owner,
            unit,
            access,
            transaction,
            ownsConnection,
            observer,
            new MySqlSessionRuntime(unit, access, connection, gate, lifetime, observer))
    {
    }

    private MySqlStorageSession(
        MySqlProviderConnection owner,
        StorageUnit unit,
        StorageAccess access,
        MySqlTransaction? transaction,
        bool ownsConnection,
        IProviderCommandObserver? observer,
        MySqlSessionRuntime runtime)
        : base(
            unit,
            access,
            runtime.Commands,
            runtime.Appends,
            runtime.Retention,
            runtime.Commands.Connection,
            transaction,
            ownsConnection,
            observer,
            "mysql")
    {
        this.owner = owner;
        commands = runtime.Commands;
    }

    public AppendOutcomeReport AppendWithOutcomes(
        OperationId operationId,
        IReadOnlyList<StorageValues> values) => AppendWithOutcomesCore(operationId, values);

    public ValueTask<AppendOutcomeReport> AppendWithOutcomesAsync(
        OperationId operationId,
        IReadOnlyList<StorageValues> values,
        CancellationToken cancellationToken = default) =>
        AppendWithOutcomesCoreAsync(operationId, values, cancellationToken);

    public WriteOutcome ConditionalUpsert(StorageValues values, WriteOptions? options = null)
    {
        var prepared = PrepareConditional(values, options);
        return ExecuteProviderConditionalUpsertCore(
            mode => commands.ConditionalUpsert(prepared, options, mode));
    }

    public ValueTask<WriteOutcome> ConditionalUpsertAsync(
        StorageValues values,
        WriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var prepared = PrepareConditional(values, options);
        return ExecuteProviderConditionalUpsertCoreAsync(
            mode => commands.ConditionalUpsert(prepared, options, mode),
            cancellationToken);
    }

    public RetentionResult ApplyRetention(RetentionExecutionOptions? options = null) =>
        ApplyRetentionCore(options);

    public ValueTask<RetentionResult> ApplyRetentionAsync(RetentionExecutionOptions? options = null) =>
        ApplyRetentionCoreAsync(options);

    public RetentionOperationResult ApplyRetention(
        OperationId operationId,
        RetentionExecutionOptions? options = null) =>
        ApplyExactRetentionCore(operationId, options);

    public ValueTask<RetentionOperationResult> ApplyRetentionAsync(
        OperationId operationId,
        RetentionExecutionOptions? options = null) =>
        ApplyExactRetentionCoreAsync(operationId, options);

    public CrossScopeQueryResult QueryAcrossScopes(
        QueryRequest request,
        QueryRenderOptions? options = null) => QueryAcrossScopesCore(request, options);

    public ValueTask<CrossScopeQueryResult> QueryAcrossScopesAsync(
        QueryRequest request,
        QueryRenderOptions? options = null,
        CancellationToken cancellationToken = default) =>
        QueryAcrossScopesCoreAsync(request, options, cancellationToken);

    public SetMutationResult UpdateWhere(
        Predicate where,
        IReadOnlyDictionary<string, object?> assignments) => UpdateWhereCore(where, assignments);

    public ValueTask<SetMutationResult> UpdateWhereAsync(
        Predicate where,
        IReadOnlyDictionary<string, object?> assignments,
        CancellationToken cancellationToken = default) =>
        UpdateWhereCoreAsync(where, assignments, cancellationToken);

    public SetMutationResult DeleteWhere(Predicate where) => DeleteWhereCore(where);

    public ValueTask<SetMutationResult> DeleteWhereAsync(
        Predicate where,
        CancellationToken cancellationToken = default) =>
        DeleteWhereCoreAsync(where, cancellationToken);

    private StorageValues PrepareConditional(StorageValues values, WriteOptions? options)
    {
        ArgumentNullException.ThrowIfNull(values);
        WritePreconditionValidator.ValidateWrittenValues(Unit, values.Values);
        WritePreconditionValidator.Validate(Unit, WriteOperation.ConditionalUpsert, options);
        return new StorageValues(SearchKeyProjection.Populate(Unit, values.Values));
    }

}

internal sealed class OwnedMySqlStorageSession : MySqlStorageSession, IOwnedStorageSession
{
    private readonly MySqlConnection connection;
    private readonly MySqlSessionLifetime lifetime;

    internal OwnedMySqlStorageSession(
        MySqlProviderConnection owner,
        StorageUnit unit,
        StorageAccess access,
        MySqlConnection connection,
        IProviderCommandObserver? observer)
        : this(
            owner,
            unit,
            access,
            connection,
            new MySqlSessionLifetime(nameof(OwnedMySqlStorageSession)),
            observer)
    {
    }

    private OwnedMySqlStorageSession(
        MySqlProviderConnection owner,
        StorageUnit unit,
        StorageAccess access,
        MySqlConnection connection,
        MySqlSessionLifetime lifetime,
        IProviderCommandObserver? observer)
        : base(
            owner,
            unit,
            access,
            connection,
            transaction: null,
            new SemaphoreSlim(1, 1),
            lifetime,
            ownsConnection: true,
            observer)
    {
        this.connection = connection;
        this.lifetime = lifetime;
    }

    public bool IsReleased => lifetime.IsReleased;

    public void Dispose()
    {
        if (!lifetime.Release())
            return;
        Close();
        connection.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (!lifetime.Release())
            return;
        Close();
        await connection.DisposeAsync().ConfigureAwait(false);
    }
}

internal sealed class MySqlSessionRuntime
{
    internal MySqlSessionRuntime(
        StorageUnit unit,
        StorageAccess access,
        MySqlConnection connection,
        SemaphoreSlim gate,
        MySqlSessionLifetime lifetime,
        IProviderCommandObserver? observer)
    {
        Commands = new MySqlSessionAdapter(unit, access, connection, gate, lifetime, observer);
        var ledger = new MySqlLedgerCommands(Commands);
        Appends = new MySqlAppendAdapter(Commands, ledger);
        Retention = new MySqlRetentionAdapter(Commands, ledger);
    }

    internal MySqlSessionAdapter Commands { get; }
    internal MySqlAppendAdapter Appends { get; }
    internal MySqlRetentionAdapter Retention { get; }
}

internal sealed class MySqlSessionLifetime(string objectName)
{
    private int released;

    internal bool IsReleased => Volatile.Read(ref released) != 0;

    internal bool Release() => Interlocked.Exchange(ref released, 1) == 0;

    internal void ThrowIfReleased()
    {
        if (IsReleased)
            throw new ObjectDisposedException(objectName);
    }
}

internal sealed class MySqlSessionAdapter : RelationalStorageSessionAdapter
{
    private const string VersionColumn = "__groundwork_version";
    private const string ActionColumn = "__groundwork_action";
    private readonly StorageUnit unit;
    private readonly StorageAccess access;
    private readonly MySqlSessionLifetime lifetime;
    private readonly IProviderCommandObserver? observer;
    private readonly IReadOnlyList<ColumnDefinition> userColumns;
    private readonly ColumnDefinition? sequenceColumn;
    private readonly ColumnDefinition? versionColumn;
    private readonly ColumnDefinition? actionColumn;

    internal MySqlSessionAdapter(
        StorageUnit unit,
        StorageAccess access,
        MySqlConnection connection,
        SemaphoreSlim gate,
        MySqlSessionLifetime lifetime,
        IProviderCommandObserver? observer)
        : base(connection, new MySqlDialect(), gate)
    {
        this.unit = unit;
        this.access = access;
        this.lifetime = lifetime;
        this.observer = observer;
        userColumns = unit.Columns.Where(column => column.Name is not ProviderOwnedColumns.Scope and
            not VersionColumn and not ActionColumn).ToArray();
        sequenceColumn = userColumns.FirstOrDefault(column =>
            column.Generation == ColumnGeneration.ProviderSequence);
        versionColumn = unit.Columns.FirstOrDefault(column => column.Name == VersionColumn);
        actionColumn = unit.Columns.FirstOrDefault(column => column.Name == ActionColumn);
    }

    public override bool SerializeAmbientReads => true;

    public override IReadOnlyDictionary<string, string> PhysicalIndexNames(StorageUnit unit) =>
        unit.Indexes.ToDictionary(
            index => index.Name,
            index => MySqlDialect.PhysicalIndexName(unit.Name, index.Name),
            StringComparer.Ordinal);

    public override void EnsureUsable() => lifetime.ThrowIfReleased();

    protected override string LockingClause(bool forUpdate) => forUpdate ? " FOR UPDATE" : string.Empty;

    protected override void BindParameter(
        DbCommand command,
        string parameter,
        object? value,
        ColumnDefinition column) =>
        command.Parameters.Add(new MySqlParameter(parameter, Dialect.ConvertValue(value, column) ?? DBNull.Value));

    protected override ValueTask<WriteOutcome> Insert(
        StorageValues values,
        WriteOutcomeStatus status,
        RelationalExecution execution) => InsertCore(values, status, execution);

    protected override ValueTask<WriteOutcome> Update(
        StorageValues values,
        StorageKey key,
        StoredEntry? existing,
        WriteOptions? options,
        RelationalExecution execution) => UpdateCore(values, key, existing, options, execution);

    protected override ValueTask<WriteOutcome> Upsert(
        StorageValues values,
        StorageKey key,
        StoredEntry? existing,
        WriteOptions? options,
        RelationalExecution execution) => UpsertCore(values, existing, execution);

    protected override ValueTask<WriteOutcome> Delete(
        StorageKey key,
        StoredEntry? existing,
        WriteOptions? options,
        RelationalExecution execution) => DeleteCore(key, existing, options, execution);

    internal async ValueTask<WriteOutcome> InsertForAppend(
        StorageValues values,
        RelationalExecution execution) =>
        await InsertCore(values, WriteOutcomeStatus.Inserted, execution).ConfigureAwait(false);

    internal async ValueTask<WriteOutcome> ConditionalUpsert(
        StorageValues values,
        WriteOptions? options,
        RelationalExecution execution)
    {
        var precondition = options?.Precondition ?? WritePrecondition.Unconditional;
        if (precondition.Kind == WritePreconditionKind.CreateOnly)
        {
            var inserted = await InsertCore(values, WriteOutcomeStatus.Inserted, execution).ConfigureAwait(false);
            return inserted.Status == WriteOutcomeStatus.UniqueViolation
                ? new WriteOutcome(WriteOutcomeStatus.ConcurrencyConflict)
                : inserted;
        }

        if (versionColumn is null)
            return await AtomicConditionalUpsertWithoutVersion(values, execution).ConfigureAwait(false);

        var key = new StorageKey(unit.Key.Columns
            .Where(column => column != ProviderOwnedColumns.Scope)
            .ToDictionary(
                column => column,
                column => values.Values.TryGetValue(column, out var value)
                    ? value
                    : throw new ArgumentException($"Key column '{column}' is required.", nameof(values)),
                StringComparer.Ordinal));
        var updated = await ConditionalUpdate(values, key, precondition, execution).ConfigureAwait(false);
        if (updated is not null || precondition.Kind == WritePreconditionKind.IfVersion)
            return updated ?? new WriteOutcome(WriteOutcomeStatus.ConcurrencyConflict);

        var insertedOutcome = await InsertCore(values, WriteOutcomeStatus.Inserted, execution).ConfigureAwait(false);
        if (insertedOutcome.Status != WriteOutcomeStatus.UniqueViolation)
            return insertedOutcome;

        // Another writer inserted after our zero-row update. Retrying the update in the same
        // transaction preserves one atomic conditional-upsert decision.
        return await ConditionalUpdate(values, key, precondition, execution).ConfigureAwait(false)
            ?? new WriteOutcome(WriteOutcomeStatus.ConcurrencyConflict);
    }

    internal void Add(
        DbCommand command,
        string parameter,
        object? value,
        ColumnDefinition? column = null) =>
        command.Parameters.Add(new MySqlParameter(
            parameter,
            column is null ? value ?? DBNull.Value : Dialect.ConvertValue(value, column) ?? DBNull.Value));

    internal void Observe(string operation, string sql, ProviderCommandKind kind) =>
        observer?.Observe(new ProviderCommandEvent(operation, sql, kind, IsProbe: false));

    private async ValueTask<WriteOutcome> InsertCore(
        StorageValues values,
        WriteOutcomeStatus status,
        RelationalExecution execution)
    {
        var supplied = userColumns.Where(column => values.Values.ContainsKey(column.Name)).ToList();
        var columns = new List<ColumnDefinition>(supplied);
        if (versionColumn is not null)
            columns.Add(versionColumn);
        var scopeColumn = unit.Columns.FirstOrDefault(column => column.Name == ProviderOwnedColumns.Scope);
        if (scopeColumn is not null)
            columns.Add(scopeColumn);
        var sql = columns.Count == 0
            ? $"INSERT INTO {Dialect.QuoteIdentifier(unit.Name)} () VALUES ();"
            : $"INSERT INTO {Dialect.QuoteIdentifier(unit.Name)} " +
              $"({string.Join(", ", columns.Select(column => Dialect.QuoteIdentifier(column.Name)))}) VALUES " +
              $"({string.Join(", ", columns.Select(column => "@" + column.Name))});";
        using var command = CreateCommand(sql);
        foreach (var column in supplied)
            Add(command, "@" + column.Name, values.Values[column.Name], column);
        if (versionColumn is not null)
            Add(command, "@" + versionColumn.Name, 1L, versionColumn);
        if (scopeColumn is not null)
            Add(command, "@" + scopeColumn.Name, access.Scope!.Value, scopeColumn);
        Observe("mysql.insert", sql, ProviderCommandKind.Write);
        try
        {
            await execution.ExecuteNonQuery(command).ConfigureAwait(false);
            IReadOnlyDictionary<string, object?>? generated = null;
            if (sequenceColumn is not null && !values.Values.ContainsKey(sequenceColumn.Name))
            {
                generated = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [sequenceColumn.Name] = checked(((MySqlCommand)command).LastInsertedId)
                };
            }
            return new WriteOutcome(status, versionColumn is null ? null : 1, generatedValues: generated);
        }
        catch (MySqlException exception) when (Dialect.TryMapUniqueViolation(exception, out var index))
        {
            return new WriteOutcome(WriteOutcomeStatus.UniqueViolation, uniqueIndexName: index);
        }
    }

    private async ValueTask<WriteOutcome> UpdateCore(
        StorageValues values,
        StorageKey key,
        StoredEntry? existing,
        WriteOptions? options,
        RelationalExecution execution)
    {
        var supplied = userColumns.Where(column =>
            values.Values.ContainsKey(column.Name) &&
            !unit.Key.Columns.Contains(column.Name, StringComparer.Ordinal)).ToArray();
        var sets = supplied.Select(column =>
            $"{Dialect.QuoteIdentifier(column.Name)}=@set_{column.Name}").ToList();
        if (versionColumn is not null)
            sets.Add($"{Dialect.QuoteIdentifier(versionColumn.Name)}={Dialect.QuoteIdentifier(versionColumn.Name)}+1");
        if (sets.Count == 0)
        {
            var keyColumn = unit.Key.Columns.First(column => column != ProviderOwnedColumns.Scope);
            sets.Add($"{Dialect.QuoteIdentifier(keyColumn)}={Dialect.QuoteIdentifier(keyColumn)}");
        }
        var (where, keyValues) = KeyPredicate(key);
        if (versionColumn is not null && options?.Precondition.Kind == WritePreconditionKind.IfVersion)
            where += $" AND {Dialect.QuoteIdentifier(versionColumn.Name)}=@expected";
        var sql = $"UPDATE {Dialect.QuoteIdentifier(unit.Name)} SET {string.Join(", ", sets)} WHERE {where};";
        using var command = CreateCommand(sql);
        foreach (var column in supplied)
            Add(command, "@set_" + column.Name, values.Values[column.Name], column);
        AddKeyParameters(command, keyValues);
        if (versionColumn is not null && options?.Precondition.Kind == WritePreconditionKind.IfVersion)
            Add(command, "@expected", options.Precondition.Version, versionColumn);
        Observe("mysql.update", sql, ProviderCommandKind.Write);
        var affected = await execution.ExecuteNonQuery(command).ConfigureAwait(false);
        if (affected == 0)
        {
            return new WriteOutcome(
                versionColumn is null ? WriteOutcomeStatus.NotFound : WriteOutcomeStatus.ConcurrencyConflict,
                existing?.Version);
        }
        return new WriteOutcome(
            WriteOutcomeStatus.Updated,
            versionColumn is null ? null : existing!.Version + 1);
    }

    private async ValueTask<WriteOutcome> UpsertCore(
        StorageValues values,
        StoredEntry? existing,
        RelationalExecution execution)
    {
        var supplied = userColumns.Where(column => values.Values.ContainsKey(column.Name)).ToList();
        var columns = new List<ColumnDefinition>(supplied);
        if (versionColumn is not null)
            columns.Add(versionColumn);
        var scopeColumn = unit.Columns.FirstOrDefault(column => column.Name == ProviderOwnedColumns.Scope);
        if (scopeColumn is not null)
            columns.Add(scopeColumn);
        var updates = supplied.Where(column =>
                !unit.Key.Columns.Contains(column.Name, StringComparer.Ordinal) &&
                column.Name != "createdAt")
            .Select(column =>
                $"{Dialect.QuoteIdentifier(column.Name)}=VALUES({Dialect.QuoteIdentifier(column.Name)})")
            .ToList();
        if (versionColumn is not null)
            updates.Add($"{Dialect.QuoteIdentifier(versionColumn.Name)}={Dialect.QuoteIdentifier(versionColumn.Name)}+1");
        if (updates.Count == 0)
        {
            var keyColumn = unit.Key.Columns.First(column => column != ProviderOwnedColumns.Scope);
            updates.Add($"{Dialect.QuoteIdentifier(keyColumn)}={Dialect.QuoteIdentifier(keyColumn)}");
        }
        var sql = $"INSERT INTO {Dialect.QuoteIdentifier(unit.Name)} " +
            $"({string.Join(", ", columns.Select(column => Dialect.QuoteIdentifier(column.Name)))}) VALUES " +
            $"({string.Join(", ", columns.Select(column => "@" + column.Name))}) " +
            $"ON DUPLICATE KEY UPDATE {string.Join(", ", updates)};";
        using var command = CreateCommand(sql);
        foreach (var column in supplied)
            Add(command, "@" + column.Name, values.Values[column.Name], column);
        if (versionColumn is not null)
            Add(command, "@" + versionColumn.Name, 1L, versionColumn);
        if (scopeColumn is not null)
            Add(command, "@" + scopeColumn.Name, access.Scope!.Value, scopeColumn);
        Observe("mysql.upsert", sql, ProviderCommandKind.Write);
        try
        {
            await execution.ExecuteNonQuery(command).ConfigureAwait(false);
            return new WriteOutcome(
                WriteOutcomeStatus.Upserted,
                versionColumn is null ? null : existing is null ? 1 : existing.Version + 1);
        }
        catch (MySqlException exception) when (Dialect.TryMapUniqueViolation(exception, out var index))
        {
            return new WriteOutcome(WriteOutcomeStatus.UniqueViolation, existing?.Version, index);
        }
    }

    private async ValueTask<WriteOutcome> DeleteCore(
        StorageKey key,
        StoredEntry? existing,
        WriteOptions? options,
        RelationalExecution execution)
    {
        var (where, keyValues) = KeyPredicate(key);
        if (versionColumn is not null && options?.Precondition.Kind == WritePreconditionKind.IfVersion)
            where += $" AND {Dialect.QuoteIdentifier(versionColumn.Name)}=@expected";
        var sql = $"DELETE FROM {Dialect.QuoteIdentifier(unit.Name)} WHERE {where};";
        using var command = CreateCommand(sql);
        AddKeyParameters(command, keyValues);
        if (versionColumn is not null && options?.Precondition.Kind == WritePreconditionKind.IfVersion)
            Add(command, "@expected", options.Precondition.Version, versionColumn);
        Observe("mysql.delete", sql, ProviderCommandKind.Write);
        var affected = await execution.ExecuteNonQuery(command).ConfigureAwait(false);
        return affected == 0
            ? new WriteOutcome(
                versionColumn is null ? WriteOutcomeStatus.NotFound : WriteOutcomeStatus.ConcurrencyConflict,
                existing?.Version)
            : new WriteOutcome(WriteOutcomeStatus.Deleted, existing?.Version);
    }

    private async ValueTask<WriteOutcome?> ConditionalUpdate(
        StorageValues values,
        StorageKey key,
        WritePrecondition precondition,
        RelationalExecution execution)
    {
        var supplied = userColumns.Where(column =>
            values.Values.ContainsKey(column.Name) &&
            !unit.Key.Columns.Contains(column.Name, StringComparer.Ordinal) &&
            column.Name != "createdAt").ToArray();
        var sets = supplied.Select(column =>
            $"{Dialect.QuoteIdentifier(column.Name)}=@set_{column.Name}").ToList();
        if (versionColumn is not null)
        {
            sets.Add(precondition.Kind == WritePreconditionKind.Unconditional
                ? $"{Dialect.QuoteIdentifier(versionColumn.Name)}=LAST_INSERT_ID({Dialect.QuoteIdentifier(versionColumn.Name)}+1)"
                : $"{Dialect.QuoteIdentifier(versionColumn.Name)}={Dialect.QuoteIdentifier(versionColumn.Name)}+1");
        }
        if (sets.Count == 0)
        {
            var keyColumn = unit.Key.Columns.First(column => column != ProviderOwnedColumns.Scope);
            sets.Add($"{Dialect.QuoteIdentifier(keyColumn)}={Dialect.QuoteIdentifier(keyColumn)}");
        }
        var (where, keyValues) = KeyPredicate(key);
        if (versionColumn is not null && precondition.Kind == WritePreconditionKind.IfVersion)
            where += $" AND {Dialect.QuoteIdentifier(versionColumn.Name)}=@expected";
        var sql = $"UPDATE {Dialect.QuoteIdentifier(unit.Name)} SET {string.Join(", ", sets)} WHERE {where};";
        using var command = CreateCommand(sql);
        foreach (var column in supplied)
            Add(command, "@set_" + column.Name, values.Values[column.Name], column);
        AddKeyParameters(command, keyValues);
        if (versionColumn is not null && precondition.Kind == WritePreconditionKind.IfVersion)
            Add(command, "@expected", precondition.Version, versionColumn);
        Observe("mysql.conditional-upsert", sql, ProviderCommandKind.Write);
        if (await execution.ExecuteNonQuery(command).ConfigureAwait(false) == 0)
            return null;

        long? version = null;
        if (versionColumn is not null)
        {
            if (precondition.Kind == WritePreconditionKind.IfVersion)
            {
                version = checked(precondition.Version!.Value + 1);
            }
            else
            {
                using var readVersion = CreateCommand("SELECT LAST_INSERT_ID();");
                version = Convert.ToInt64(
                    await execution.ExecuteScalar(readVersion).ConfigureAwait(false),
                    CultureInfo.InvariantCulture);
            }
        }
        return new WriteOutcome(WriteOutcomeStatus.Updated, version);
    }

    private (string Where, IReadOnlyDictionary<string, (object? Value, ColumnDefinition Column)> Values)
        KeyPredicate(StorageKey key)
    {
        var clauses = new List<string>();
        var values = new Dictionary<string, (object?, ColumnDefinition)>(StringComparer.Ordinal);
        var keyColumns = unit.Key.Columns.ToList();
        if (unit.Columns.Any(column => column.Name == ProviderOwnedColumns.Scope) &&
            !keyColumns.Contains(ProviderOwnedColumns.Scope, StringComparer.Ordinal))
        {
            keyColumns.Add(ProviderOwnedColumns.Scope);
        }
        foreach (var name in keyColumns)
        {
            var column = unit.Columns.Single(candidate => candidate.Name == name);
            var value = name == ProviderOwnedColumns.Scope
                ? access.Scope!.Value
                : key.Values.TryGetValue(name, out var supplied)
                    ? supplied
                    : throw new ArgumentException($"Key column '{name}' is required.", nameof(key));
            var parameter = "@key_" + name;
            clauses.Add($"{Dialect.QuoteIdentifier(name)}={parameter}");
            values.Add(parameter, (value, column));
        }
        return (string.Join(" AND ", clauses), values);
    }

    private async ValueTask<WriteOutcome> AtomicConditionalUpsertWithoutVersion(
        StorageValues values,
        RelationalExecution execution)
    {
        var marker = actionColumn ?? throw new InvalidOperationException(
            $"Storage unit '{unit.Name}' is missing its provider-owned action column.");
        var supplied = userColumns.Where(column => values.Values.ContainsKey(column.Name)).ToList();
        var columns = new List<ColumnDefinition>(supplied);
        var scopeColumn = unit.Columns.FirstOrDefault(column => column.Name == ProviderOwnedColumns.Scope);
        if (scopeColumn is not null)
            columns.Add(scopeColumn);
        var identityColumns = unit.Key.Columns.ToList();
        if (scopeColumn is not null &&
            !identityColumns.Contains(scopeColumn.Name, StringComparer.Ordinal))
        {
            identityColumns.Add(scopeColumn.Name);
        }
        var identityMatches = string.Join(" AND ", identityColumns.Select(column =>
            $"{Dialect.QuoteIdentifier(column)}=VALUES({Dialect.QuoteIdentifier(column)})"));
        var updates = supplied.Where(column =>
                !unit.Key.Columns.Contains(column.Name, StringComparer.Ordinal) &&
                column.Name != "createdAt")
            .Select(column =>
                $"{Dialect.QuoteIdentifier(column.Name)}=IF({identityMatches}," +
                $"VALUES({Dialect.QuoteIdentifier(column.Name)}),{Dialect.QuoteIdentifier(column.Name)})")
            .ToList();
        updates.Add($"{Dialect.QuoteIdentifier(marker.Name)}=" +
            $"IF({identityMatches},'U',{Dialect.QuoteIdentifier(marker.Name)})");

        var key = new StorageKey(unit.Key.Columns
            .Where(column => column != ProviderOwnedColumns.Scope)
            .ToDictionary(
                column => column,
                column => values.Values.TryGetValue(column, out var value)
                    ? value
                    : throw new ArgumentException($"Key column '{column}' is required.", nameof(values)),
                StringComparer.Ordinal));
        var (where, keyValues) = KeyPredicate(key);
        var sql = $"INSERT INTO {Dialect.QuoteIdentifier(unit.Name)} " +
            $"({string.Join(", ", columns.Select(column => Dialect.QuoteIdentifier(column.Name)))}) VALUES " +
            $"({string.Join(", ", columns.Select(column => "@" + column.Name))}) " +
            $"ON DUPLICATE KEY UPDATE {string.Join(", ", updates)}; " +
            $"SELECT {Dialect.QuoteIdentifier(marker.Name)} FROM {Dialect.QuoteIdentifier(unit.Name)} WHERE {where};";
        using var command = CreateCommand(sql);
        foreach (var column in supplied)
            Add(command, "@" + column.Name, values.Values[column.Name], column);
        if (scopeColumn is not null)
            Add(command, "@" + scopeColumn.Name, access.Scope!.Value, scopeColumn);
        AddKeyParameters(command, keyValues);
        Observe("mysql.conditional-upsert", sql, ProviderCommandKind.Write);
        try
        {
            await using var readerScope = await execution.ExecuteReader(command).ConfigureAwait(false);
            var reader = readerScope.Reader;
            do
            {
                if (reader.FieldCount != 0 && await execution.Read(reader).ConfigureAwait(false))
                {
                    return new WriteOutcome(
                        string.Equals(reader.GetString(0), "I", StringComparison.Ordinal)
                            ? WriteOutcomeStatus.Inserted
                            : WriteOutcomeStatus.Updated);
                }
            } while (await execution.NextResult(reader).ConfigureAwait(false));
            // ON DUPLICATE KEY can select any unique index. When it encountered a different
            // identity (including the same generated key in another scope), every assignment was
            // a no-op and the scoped identity lookup intentionally returns no row.
            return new WriteOutcome(WriteOutcomeStatus.UniqueViolation);
        }
        catch (MySqlException exception) when (Dialect.TryMapUniqueViolation(exception, out var index))
        {
            return new WriteOutcome(WriteOutcomeStatus.UniqueViolation, uniqueIndexName: index);
        }
    }

    private void AddKeyParameters(
        DbCommand command,
        IReadOnlyDictionary<string, (object? Value, ColumnDefinition Column)> values)
    {
        foreach (var pair in values)
            Add(command, pair.Key, pair.Value.Value, pair.Value.Column);
    }
}

internal sealed class MySqlAppendAdapter(
    MySqlSessionAdapter commands,
    MySqlLedgerCommands ledger) : RelationalAppendAdapter
{
    protected override ValueTask<DateTimeOffset> PrepareLedger(
        RelationalAppendCommand operation,
        RelationalExecution execution) => ledger.ProviderNow(execution);

    protected override ValueTask ReclaimExpired(
        RelationalAppendCommand operation,
        DateTimeOffset cutoff,
        RelationalExecution execution) =>
        ledger.Reclaim(operation.Declaration.LedgerName, operation.Unit.Id.Value, cutoff, execution);

    protected override ValueTask<RelationalAppendLedgerState?> ReadLedger(
        RelationalAppendCommand operation,
        RelationalExecution execution) =>
        ledger.ReadAppend(
            operation.Declaration.LedgerName,
            operation.Unit.Id.Value,
            operation.Scope,
            operation.OperationId.Nonce,
            execution);

    protected override ValueTask DeleteLedger(
        RelationalAppendCommand operation,
        RelationalAppendLedgerState existing,
        RelationalExecution execution) =>
        ledger.Delete(
            operation.Declaration.LedgerName,
            operation.Unit.Id.Value,
            operation.Scope,
            operation.OperationId.Nonce,
            existing.CommittedAt,
            execution);

    protected override ValueTask<bool> TryClaimLedger(
        RelationalAppendCommand operation,
        DateTimeOffset providerNow,
        RelationalExecution execution) =>
        ledger.TryClaim(
            operation.Declaration.LedgerName,
            operation.Unit.Id.Value,
            operation.Scope,
            operation.OperationId.Nonce,
            providerNow,
            operation.Fingerprint,
            execution);

    protected override ValueTask<RelationalAppendReplayState?> ReadClaimWinner(
        RelationalAppendCommand operation,
        RelationalExecution execution) =>
        ledger.ReadAppendReplay(
            operation.Declaration.LedgerName,
            operation.Unit.Id.Value,
            operation.Scope,
            operation.OperationId.Nonce,
            execution);

    protected override async ValueTask<IReadOnlyList<RowWriteOutcome>> InsertPayload(
        RelationalAppendCommand operation,
        RelationalExecution execution)
    {
        var outcomes = new List<RowWriteOutcome>(operation.Values.Count);
        foreach (var values in operation.Values)
        {
            var outcome = await commands.InsertForAppend(values, execution).ConfigureAwait(false);
            outcomes.Add(new RowWriteOutcome(RowWrite.Insert(operation.Unit, values), outcome));
            if (!outcome.Succeeded)
                break;
        }
        return outcomes;
    }

    protected override ValueTask<bool> CompleteLedger(
        RelationalAppendCommand operation,
        string serializedOutcomes,
        RelationalExecution execution) =>
        ledger.Complete(
            operation.Declaration.LedgerName,
            operation.Unit.Id.Value,
            operation.Scope,
            operation.OperationId.Nonce,
            serializedOutcomes,
            execution);
}

internal sealed class MySqlRetentionAdapter(
    MySqlSessionAdapter commands,
    MySqlLedgerCommands ledger) : RelationalRetentionAdapter
{
    protected override async ValueTask<int> DeleteBatch(
        RelationalRetentionCommand operation,
        RelationalExecution execution)
    {
        var keys = operation.Unit.Key.Columns;
        var partition = operation.Declaration.PartitionColumns.Count == 0
            ? string.Empty
            : $"PARTITION BY {string.Join(", ", operation.Declaration.PartitionColumns.Select(MySqlDialect.Quote))} ";
        var scoped = operation.Unit.Columns.Any(column => column.Name == ProviderOwnedColumns.Scope);
        var scope = scoped ? $" WHERE {MySqlDialect.Quote(ProviderOwnedColumns.Scope)}=@scope" : string.Empty;
        var selected = string.Join(", ", keys.Select(MySqlDialect.Quote));
        var ordering = string.Join(", ", [
            $"{MySqlDialect.Quote(operation.Declaration.OrderColumn)} DESC",
            .. keys.Select(column => $"{MySqlDialect.Quote(column)} ASC")]);
        var equality = string.Join(" AND ", keys.Select(column =>
            $"target.{MySqlDialect.Quote(column)}=victim.{MySqlDialect.Quote(column)}"));
        var sql = $"DELETE target FROM {MySqlDialect.Quote(operation.Unit.Name)} AS target JOIN (" +
            $"SELECT {selected} FROM (SELECT {selected}, ROW_NUMBER() OVER ({partition}ORDER BY {ordering}) AS `__groundwork_rank` " +
            $"FROM {MySqlDialect.Quote(operation.Unit.Name)}{scope}) AS ranked " +
            "WHERE `__groundwork_rank` > @keep LIMIT @limit) AS victim ON " + equality + ";";
        using var command = commands.CreateCommand(sql);
        commands.Add(command, "@keep", operation.KeepNewest);
        commands.Add(command, "@limit", operation.Options.MaxRowsPerBatch);
        if (scoped)
            commands.Add(command, "@scope", operation.Scope);
        commands.Observe("mysql.retention-delete", sql, ProviderCommandKind.Write);
        return await execution.ExecuteNonQuery(command).ConfigureAwait(false);
    }

    protected override ValueTask<DateTimeOffset> PrepareLedger(
        RelationalExactRetentionCommand operation,
        RelationalExecution execution) => ledger.ProviderNow(execution);

    protected override ValueTask ReclaimExpired(
        RelationalExactRetentionCommand operation,
        DateTimeOffset cutoff,
        RelationalExecution execution) =>
        ledger.Reclaim(operation.Declaration.LedgerName, operation.Retention.Unit.Id.Value, cutoff, execution);

    protected override ValueTask<RelationalRetentionLedgerState?> ReadLedger(
        RelationalExactRetentionCommand operation,
        RelationalExecution execution) =>
        ledger.ReadRetention(
            operation.Declaration.LedgerName,
            operation.Retention.Unit.Id.Value,
            operation.Scope,
            operation.OperationId.Nonce,
            execution);

    protected override ValueTask DeleteLedger(
        RelationalExactRetentionCommand operation,
        RelationalRetentionLedgerState existing,
        RelationalExecution execution) =>
        ledger.Delete(
            operation.Declaration.LedgerName,
            operation.Retention.Unit.Id.Value,
            operation.Scope,
            operation.OperationId.Nonce,
            existing.CommittedAt,
            execution);

    protected override ValueTask<bool> TryClaimLedger(
        RelationalExactRetentionCommand operation,
        DateTimeOffset providerNow,
        RelationalExecution execution) =>
        ledger.TryClaim(
            operation.Declaration.LedgerName,
            operation.Retention.Unit.Id.Value,
            operation.Scope,
            operation.OperationId.Nonce,
            providerNow,
            operation.Fingerprint,
            execution);

    protected override ValueTask<RelationalRetentionReplayState?> ReadClaimWinner(
        RelationalExactRetentionCommand operation,
        RelationalExecution execution) =>
        ledger.ReadRetentionReplay(
            operation.Declaration.LedgerName,
            operation.Retention.Unit.Id.Value,
            operation.Scope,
            operation.OperationId.Nonce,
            execution);

    protected override ValueTask<bool> CompleteLedger(
        RelationalExactRetentionCommand operation,
        string serializedResult,
        RelationalExecution execution) =>
        ledger.Complete(
            operation.Declaration.LedgerName,
            operation.Retention.Unit.Id.Value,
            operation.Scope,
            operation.OperationId.Nonce,
            serializedResult,
            execution);
}

internal sealed class MySqlLedgerCommands(MySqlSessionAdapter commands)
{
    internal async ValueTask<DateTimeOffset> ProviderNow(RelationalExecution execution)
    {
        using var command = commands.CreateCommand("SELECT UTC_TIMESTAMP(6);");
        var value = await execution.ExecuteScalar(command).ConfigureAwait(false);
        var timestamp = Convert.ToDateTime(value, CultureInfo.InvariantCulture);
        return new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc));
    }

    internal async ValueTask Reclaim(
        string table,
        string unit,
        DateTimeOffset cutoff,
        RelationalExecution execution)
    {
        using var command = commands.CreateCommand(
            $"DELETE FROM {MySqlDialect.Quote(table)} WHERE `unit`=@unit AND `committed_at` <= @cutoff LIMIT 128;");
        commands.Add(command, "@unit", unit);
        commands.Add(command, "@cutoff", Format(cutoff));
        await execution.ExecuteNonQuery(command).ConfigureAwait(false);
    }

    internal async ValueTask<RelationalAppendLedgerState?> ReadAppend(
        string table,
        string unit,
        string scope,
        string nonce,
        RelationalExecution execution)
    {
        var row = await Read(table, unit, scope, nonce, execution).ConfigureAwait(false);
        return row is null ? null : new RelationalAppendLedgerState(row.Value.Time, row.Value.Fingerprint, row.Value.Result);
    }

    internal async ValueTask<RelationalRetentionLedgerState?> ReadRetention(
        string table,
        string unit,
        string scope,
        string nonce,
        RelationalExecution execution)
    {
        var row = await Read(table, unit, scope, nonce, execution).ConfigureAwait(false);
        return row is null ? null : new RelationalRetentionLedgerState(row.Value.Time, row.Value.Fingerprint, row.Value.Result);
    }

    internal async ValueTask Delete(
        string table,
        string unit,
        string scope,
        string nonce,
        DateTimeOffset observed,
        RelationalExecution execution)
    {
        using var command = commands.CreateCommand(
            $"DELETE FROM {MySqlDialect.Quote(table)} WHERE `unit`=@unit AND `scope`=@scope " +
            "AND `nonce`=@nonce AND `committed_at`=@observed;");
        AddIdentity(command, unit, scope, nonce);
        commands.Add(command, "@observed", Format(observed));
        await execution.ExecuteNonQuery(command).ConfigureAwait(false);
    }

    internal async ValueTask<bool> TryClaim(
        string table,
        string unit,
        string scope,
        string nonce,
        DateTimeOffset providerNow,
        string fingerprint,
        RelationalExecution execution)
    {
        using var command = commands.CreateCommand(
            $"INSERT IGNORE INTO {MySqlDialect.Quote(table)} " +
            "(`unit`, `scope`, `nonce`, `committed_at`, `input_fingerprint`, `exact_result`) " +
            "VALUES (@unit, @scope, @nonce, @committed, @fingerprint, '');");
        AddIdentity(command, unit, scope, nonce);
        commands.Add(command, "@committed", Format(providerNow));
        commands.Add(command, "@fingerprint", fingerprint);
        return await execution.ExecuteNonQuery(command).ConfigureAwait(false) == 1;
    }

    internal async ValueTask<RelationalAppendReplayState?> ReadAppendReplay(
        string table,
        string unit,
        string scope,
        string nonce,
        RelationalExecution execution)
    {
        var row = await Read(table, unit, scope, nonce, execution).ConfigureAwait(false);
        return row is null ? null : new RelationalAppendReplayState(row.Value.Fingerprint, row.Value.Result);
    }

    internal async ValueTask<RelationalRetentionReplayState?> ReadRetentionReplay(
        string table,
        string unit,
        string scope,
        string nonce,
        RelationalExecution execution)
    {
        var row = await Read(table, unit, scope, nonce, execution).ConfigureAwait(false);
        return row is null ? null : new RelationalRetentionReplayState(row.Value.Fingerprint, row.Value.Result);
    }

    internal async ValueTask<bool> Complete(
        string table,
        string unit,
        string scope,
        string nonce,
        string result,
        RelationalExecution execution)
    {
        using var command = commands.CreateCommand(
            $"UPDATE {MySqlDialect.Quote(table)} SET `exact_result`=@result " +
            "WHERE `unit`=@unit AND `scope`=@scope AND `nonce`=@nonce;");
        AddIdentity(command, unit, scope, nonce);
        commands.Add(command, "@result", result);
        return await execution.ExecuteNonQuery(command).ConfigureAwait(false) == 1;
    }

    private async ValueTask<(DateTimeOffset Time, string? Fingerprint, string? Result)?> Read(
        string table,
        string unit,
        string scope,
        string nonce,
        RelationalExecution execution)
    {
        using var command = commands.CreateCommand(
            $"SELECT `committed_at`, `input_fingerprint`, `exact_result` FROM {MySqlDialect.Quote(table)} " +
            "WHERE `unit`=@unit AND `scope`=@scope AND `nonce`=@nonce;");
        AddIdentity(command, unit, scope, nonce);
        await using var readerScope = await execution.ExecuteReader(command).ConfigureAwait(false);
        var reader = readerScope.Reader;
        if (!await execution.Read(reader).ConfigureAwait(false))
            return null;
        return (
            DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private void AddIdentity(DbCommand command, string unit, string scope, string nonce)
    {
        commands.Add(command, "@unit", unit);
        commands.Add(command, "@scope", scope);
        commands.Add(command, "@nonce", nonce);
    }

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
