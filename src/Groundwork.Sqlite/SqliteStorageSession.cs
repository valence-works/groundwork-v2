using System.Globalization;
using System.Data;
using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Groundwork.Kernel;
using Groundwork.Query.Model;
using Groundwork.Substrate.Relational;
using Groundwork.Testing;

namespace Groundwork.Sqlite;

internal sealed class SqliteStorageSession : IStorageSession, IConcurrencyStorageSession
{
    private readonly SqliteProviderConnection owner;
    private readonly SqliteConnection connection;
    private readonly SqliteTransaction? transaction;
    private SqliteTransaction? activeTransaction;
    private bool closed;

    internal SqliteStorageSession(
        SqliteProviderConnection owner,
        StorageUnit unit,
        StorageAccess access,
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        this.owner = owner;
        Unit = unit;
        Access = access;
        this.connection = connection;
        this.transaction = transaction;
    }

    public StorageUnit Unit { get; }
    public StorageAccess Access { get; }

    public QueryMaterializedResult Query(QueryRequest request, QueryRenderOptions? options = null) => Execute(() =>
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.Table.Value, Unit.Name, StringComparison.Ordinal))
            throw new ArgumentException($"Query table '{request.Table.Value}' does not match session unit '{Unit.Name}'.", nameof(request));
        var suppliedOptions = options ?? QueryRenderOptions.Default;
        var executionSource = WithScopePredicate(request);
        var renderOptions = suppliedOptions.WithIdentityTieBreaks(Unit.Key.Columns.Select(QueryColumn).Where(column => column is not null && column.Name != SqliteSchemaCoordinator.ScopeColumn)!.Select(column => column!)) with
        {
            Indexes = suppliedOptions.Indexes.Select(index => index.WithColumnTypes(Unit.Columns.ToDictionary(column => column.Name, column => QueryTypeOf(column.Type), StringComparer.Ordinal))).ToImmutableArray(),
            PhysicalIndexNames = Unit.Indexes.ToDictionary(
                index => index.Name,
                index => SqliteDialect.PhysicalIndexName(Unit.Name, index.Name),
                StringComparer.Ordinal)
        };
        var executionRequest = QueryRequestExecution.ForPage(executionSource, renderOptions);
        var command = new SqliteQueryRenderer().Render(executionRequest, renderOptions);
        var rows = RelationalQueryResultReader.Read(connection, command, (name, value) =>
        {
            if (name == "__groundwork_total_count") return value;
            var column = Unit.Columns.FirstOrDefault(item => item.Name == name);
            return column is null ? value : FromSqlite(value ?? DBNull.Value, column);
        });
        return QueryResultMaterializer.Materialize(executionSource, renderOptions, rows, command.SelectedIndex, command.IndexHintApplied,
            sourceIncludesRequestedOffset: true,
            sourceIncludesContinuation: true);
    });

    private QueryRequest WithScopePredicate(QueryRequest request) => Unit.Scope != ScopePolicy.Scoped
        ? request
        : QueryRequestExecution.WithProviderPredicate(request, new Predicate.And([
            request.Where,
            new Predicate.Equal(new ColumnRef(new TableId(Unit.Name), SqliteSchemaCoordinator.ScopeColumn, QueryType.String),
                QueryConstant.Of(new ColumnRef(new TableId(Unit.Name), SqliteSchemaCoordinator.ScopeColumn, QueryType.String), Access.Scope!.Value))]),
            QueryRequestExecution.ScopeBindingDiscriminator(Access.Scope!.Value));

    public StoredEntry? Read(StorageKey key) => Execute(() =>
    {
        var (where, parameters) = KeyPredicate(key.Values);
        var columns = UserColumns.Concat(VersionColumnDefinition is null ? [] : [VersionColumnDefinition]);
        using var command = Command($"SELECT {string.Join(", ", columns.Select(column => Quote(column.Name)))} FROM {Quote(Unit.Name)} WHERE {where};");
        AddParameters(command, parameters);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < UserColumns.Count; i++)
            values[UserColumns[i].Name] = FromSqlite(reader.GetValue(i), UserColumns[i]);
        var version = VersionColumnDefinition is null ? (long?)null : Convert.ToInt64(reader.GetValue(UserColumns.Count), CultureInfo.InvariantCulture);
        return new StoredEntry(new StorageValues(values), version);
    });

    public WriteOutcome Insert(StorageValues values, WriteOptions? options = null) => Mutate(values, options, Mutation.Insert);
    public WriteOutcome Update(StorageValues values, WriteOptions? options = null) => Mutate(values, options, Mutation.Update);
    public WriteOutcome Upsert(StorageValues values, WriteOptions? options = null) => Mutate(values, options, Mutation.Upsert);
    public WriteOutcome ConditionalUpsert(StorageValues values, WriteOptions? options = null) =>
        Execute(() => ConditionalUpsertCore(values, options));

    public WriteOutcome Delete(StorageKey key, WriteOptions? options = null) => ExecuteWrite(() =>
    {
        var existing = ReadCore(key);
        ValidateExpected(options, existing, Mutation.Delete);
        if (existing is null) return new WriteOutcome(WriteOutcomeStatus.NotFound);
        var (where, parameters) = KeyPredicate(key.Values);
        if (VersionColumnDefinition is not null)
        {
            where += $" AND {Quote(VersionColumnDefinition.Name)}=@expected";
            parameters["@expected"] = options!.ExpectedVersion!.Value;
        }
        using var command = Command($"DELETE FROM {Quote(Unit.Name)} WHERE {where};");
        AddParameters(command, parameters);
        command.ExecuteNonQuery();
        return new WriteOutcome(WriteOutcomeStatus.Deleted, existing.Version);
    });

    internal void Close() => closed = true;

    private ColumnRef? QueryColumn(string name)
    {
        var column = UserColumns.Concat(VersionColumnDefinition is null ? [] : [VersionColumnDefinition])
            .Single(item => item.Name == name);
        return column.Type switch
        {
            PortableType.Boolean => new ColumnRef(new TableId(Unit.Name), name, QueryType.Boolean, column.IsNullable),
            PortableType.Int32 => new ColumnRef(new TableId(Unit.Name), name, QueryType.Int32, column.IsNullable),
            PortableType.Int64 => new ColumnRef(new TableId(Unit.Name), name, QueryType.Int64, column.IsNullable),
            PortableType.Decimal => new ColumnRef(new TableId(Unit.Name), name, QueryType.Decimal, column.IsNullable, null,
                column.Precision is int precision ? checked((byte)precision) : null,
                column.Scale is int scale ? checked((byte)scale) : null),
            PortableType.String => new ColumnRef(new TableId(Unit.Name), name, QueryType.String, column.IsNullable, column.MaxLength),
            PortableType.DateTimeOffset => new ColumnRef(new TableId(Unit.Name), name, QueryType.DateTimeOffset, column.IsNullable),
            PortableType.Guid => new ColumnRef(new TableId(Unit.Name), name, QueryType.Guid, column.IsNullable),
            PortableType.Binary => new ColumnRef(new TableId(Unit.Name), name, QueryType.Binary, column.IsNullable, column.MaxLength),
            _ => null
        };
    }

    private static QueryType? QueryTypeOf(PortableType type) => type switch
    {
        PortableType.Boolean => QueryType.Boolean,
        PortableType.Int32 => QueryType.Int32,
        PortableType.Int64 => QueryType.Int64,
        PortableType.Decimal => QueryType.Decimal,
        PortableType.String => QueryType.String,
        PortableType.DateTimeOffset => QueryType.DateTimeOffset,
        PortableType.Guid => QueryType.Guid,
        PortableType.Binary => QueryType.Binary,
        _ => null
    };

    private WriteOutcome Mutate(
        StorageValues values,
        WriteOptions? options,
        Mutation mutation,
        bool exactOutcome = false) => ExecuteWrite(() =>
    {
        ArgumentNullException.ThrowIfNull(values);
        ValidateValues(values.Values, mutation == Mutation.Insert);
        var key = new StorageKey(LogicalKeyColumns.ToDictionary(
            column => column, column => values.Values.TryGetValue(column, out var value) ? value : throw new ArgumentException($"Key column '{column}' is required.", nameof(values)),
            StringComparer.Ordinal));
        var existing = ReadCore(key);
        if (mutation == Mutation.Insert && existing is not null)
            return new WriteOutcome(WriteOutcomeStatus.UniqueViolation, existing.Version);
        if (mutation == Mutation.Update && existing is null)
            return new WriteOutcome(WriteOutcomeStatus.NotFound);
        ValidateExpected(options, existing, mutation);

        var supplied = UserColumns.Where(column => values.Values.ContainsKey(column.Name)).ToArray();
        var columns = supplied.ToList();
        if (VersionColumnDefinition is not null)
            columns.Add(VersionColumnDefinition);
        if (Unit.Scope == ScopePolicy.Scoped)
            columns.Add(ScopeColumnDefinition!);

        if (mutation == Mutation.Upsert)
            return Upsert(values, existing, columns, exactOutcome);
        var sets = supplied.Where(column => !Unit.Key.Columns.Contains(column.Name, StringComparer.Ordinal))
            .Select(column => $"{Quote(column.Name)}=@{column.Name}").ToArray();
        var parameters = BuildParameters(values.Values, supplied);
        if (mutation == Mutation.Insert)
        {
            if (VersionColumnDefinition is not null) parameters["@__groundwork_version"] = 1L;
            if (ScopeColumnDefinition is not null) parameters["@__groundwork_scope"] = Access.Scope!.Value;
            using var insert = Command($"INSERT INTO {Quote(Unit.Name)} ({string.Join(", ", columns.Select(column => Quote(column.Name)))}) VALUES ({string.Join(", ", columns.Select(column => "@" + column.Name))});");
            AddParameters(insert, parameters);
            try { insert.ExecuteNonQuery(); return new WriteOutcome(WriteOutcomeStatus.Inserted, VersionColumnDefinition is null ? (long?)null : 1); }
            catch (SqliteException exception) when (new SqliteDialect().TryMapUniqueViolation(exception, out _)) { return new WriteOutcome(WriteOutcomeStatus.UniqueViolation); }
        }

        var (where, keyParameters) = KeyPredicate(key.Values);
        foreach (var pair in keyParameters) parameters[pair.Key] = pair.Value;
        if (VersionColumnDefinition is not null)
        {
            sets = sets.Append($"{Quote(VersionColumnDefinition.Name)}={Quote(VersionColumnDefinition.Name)}+1").ToArray();
            where += $" AND {Quote(VersionColumnDefinition.Name)}=@expected";
            parameters["@expected"] = options!.ExpectedVersion!.Value;
        }
        if (sets.Length == 0) return new WriteOutcome(WriteOutcomeStatus.Updated, existing!.Version);
        using var update = Command($"UPDATE {Quote(Unit.Name)} SET {string.Join(", ", sets)} WHERE {where};");
        AddParameters(update, parameters);
        var affected = update.ExecuteNonQuery();
        if (affected == 0) return new WriteOutcome(WriteOutcomeStatus.ConcurrencyConflict, existing?.Version);
        return new WriteOutcome(WriteOutcomeStatus.Updated, VersionColumnDefinition is null ? null : existing!.Version + 1);
    });

    private WriteOutcome ConditionalUpsertCore(StorageValues values, WriteOptions? options)
    {
        ArgumentNullException.ThrowIfNull(values);
        ValidateValues(values.Values, requireAllNonNullable: false);

        var key = new StorageKey(LogicalKeyColumns.ToDictionary(
            column => column,
            column => values.Values.TryGetValue(column, out var value)
                ? value
                : throw new ArgumentException($"Key column '{column}' is required.", nameof(values)),
            StringComparer.Ordinal));
        if (options?.ExpectedVersion is not null && VersionColumnDefinition is null)
            throw new InvalidOperationException($"Storage unit '{Unit.Name}' does not declare version machinery.");

        var supplied = UserColumns.Where(column => values.Values.ContainsKey(column.Name)).ToArray();
        var insertColumns = supplied.ToList();
        if (VersionColumnDefinition is not null)
            insertColumns.Add(VersionColumnDefinition);
        if (ScopeColumnDefinition is not null)
            insertColumns.Add(ScopeColumnDefinition);

        var updates = supplied
            .Where(column => !Unit.Key.Columns.Contains(column.Name, StringComparer.Ordinal) &&
                             column.Name != SqliteSchemaCoordinator.ScopeColumn &&
                             column.Name != "createdAt")
            .Select(column => $"{Quote(column.Name)}=excluded.{Quote(column.Name)}")
            .ToList();
        if (VersionColumnDefinition is not null)
            updates.Add($"{Quote(VersionColumnDefinition.Name)}={Quote(Unit.Name)}.{Quote(VersionColumnDefinition.Name)}+1");
        if (updates.Count == 0)
        {
            var noOpColumn = LogicalKeyColumns[0];
            updates.Add($"{Quote(noOpColumn)}={Quote(Unit.Name)}.{Quote(noOpColumn)}");
        }
        if (ActionColumnDefinition is not null)
            updates.Add($"{Quote(ActionColumnDefinition.Name)}='U'");

        var parameters = BuildParameters(values.Values, supplied);
        var (keyPredicate, keyParameters) = KeyPredicate(key.Values);
        foreach (var pair in keyParameters)
            parameters[pair.Key] = pair.Value;
        if (VersionColumnDefinition is not null)
        {
            parameters["@__groundwork_version"] = 1L;
            parameters["@__expected"] = options?.ExpectedVersion;
        }
        if (ScopeColumnDefinition is not null)
            parameters["@__groundwork_scope"] = Access.Scope!.Value;

        var insertValues = string.Join(", ", insertColumns.Select(column =>
            column.Name == SqliteSchemaCoordinator.VersionColumn ? "@__groundwork_version" :
            column.Name == SqliteSchemaCoordinator.ScopeColumn ? "@__groundwork_scope" : "@" + column.Name));
        var insertSource = VersionColumnDefinition is null || options?.ExpectedVersion is null
            ? $"VALUES ({insertValues})"
            : $"SELECT {insertValues} WHERE EXISTS (SELECT 1 FROM {Quote(Unit.Name)} WHERE {keyPredicate} AND {Quote(VersionColumnDefinition.Name)}=@__expected)";
        var conflict = string.Join(", ", Unit.Key.Columns.Select(Quote));
        var expected = VersionColumnDefinition is null
            ? string.Empty
            : $" WHERE @__expected IS NOT NULL AND {Quote(Unit.Name)}.{Quote(VersionColumnDefinition.Name)}=@__expected";
        var returning = VersionColumnDefinition is null
            ? Quote(ActionColumnDefinition!.Name)
            : Quote(VersionColumnDefinition.Name);
        var sql = $"INSERT INTO {Quote(Unit.Name)} ({string.Join(", ", insertColumns.Select(column => Quote(column.Name)))}) " +
                  $"{insertSource} ON CONFLICT ({conflict}) DO UPDATE SET {string.Join(", ", updates)}{expected} " +
                  $"RETURNING {returning};";
        using var command = Command(sql);
        AddParameters(command, parameters);
        options?.Observer?.Observe(new WritePathEvent("sqlite.conditional-upsert", sql, IsProbe: false));
        try
        {
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return DeferredConflict(key, options?.Observer);

            var inserted = ActionColumnDefinition is not null
                ? string.Equals(reader.GetString(0), "I", StringComparison.Ordinal)
                : options?.ExpectedVersion is null;
            var version = VersionColumnDefinition is null
                ? (long?)null
                : Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
            var status = inserted ? WriteOutcomeStatus.Inserted : WriteOutcomeStatus.Updated;
            return new WriteOutcome(status, version);
        }
        catch (SqliteException exception) when (new SqliteDialect().TryMapUniqueViolation(exception, out var indexName))
        {
            return new WriteOutcome(WriteOutcomeStatus.UniqueViolation, null, LogicalIndexName(indexName));
        }
    }

    private WriteOutcome DeferredConflict(StorageKey key, IWritePathObserver? observer) =>
        WriteOutcome.Deferred(
            WriteOutcomeStatus.ConcurrencyConflict,
            null,
            () =>
            {
                var existing = ReadCore(key, observer);
                return existing is null
                    ? new WriteOutcomeDetail(WriteOutcomeStatus.NotFound)
                    : new WriteOutcomeDetail(WriteOutcomeStatus.ConcurrencyConflict, existing.Version);
            });

    private string? LogicalIndexName(string? reportedName)
    {
        if (string.IsNullOrWhiteSpace(reportedName))
            return reportedName;

        // SQLite reports the exact table/column tuple rather than the named index.
        // Compare the complete logical tuple: a prefix match could otherwise report
        // the wrong declaration when both (a) and (a,b) are unique.
        var reportedColumns = reportedName.Split(',')
            .Select(part => part.Trim().Trim('"', '\'', '[', ']', '(', ')', '.'))
            .Select(part => part[(part.LastIndexOf('.') + 1)..].Trim('"', '\'', '[', ']'))
            .Where(column => !column.StartsWith("__groundwork_", StringComparison.Ordinal))
            .ToArray();
        var matches = Unit.Indexes.Where(index =>
            index.IsUnique &&
            index.Columns.Select(column => column.Column).SequenceEqual(reportedColumns, StringComparer.Ordinal))
            .ToArray();
        return matches.Length == 1 ? matches[0].Name : reportedName;
    }

    private WriteOutcome Upsert(
        StorageValues values,
        StoredEntry? existing,
        IReadOnlyList<ColumnDefinition> columns,
        bool exactOutcome)
    {
        var updateColumns = columns.Where(column =>
            !Unit.Key.Columns.Contains(column.Name, StringComparer.Ordinal) &&
            column.Name != SqliteSchemaCoordinator.ScopeColumn &&
            (!exactOutcome || column.Name != "createdAt")).ToArray();
        if (VersionColumnDefinition is not null && updateColumns.All(column => column.Name != VersionColumnDefinition.Name))
            updateColumns = updateColumns.Append(VersionColumnDefinition).ToArray();
        var keyNames = Unit.Key.Columns;
        var sql = $"INSERT INTO {Quote(Unit.Name)} ({string.Join(", ", columns.Select(column => Quote(column.Name)))}) VALUES ({string.Join(", ", columns.Select(column => "@" + column.Name))}) ON CONFLICT ({string.Join(", ", keyNames.Select(Quote))}) DO UPDATE SET " +
            string.Join(", ", updateColumns.Select(column => column.Name == SqliteSchemaCoordinator.VersionColumn
                ? $"{Quote(column.Name)}={Quote(column.Name)}+1" : $"{Quote(column.Name)}=excluded.{Quote(column.Name)}")) + ";";
        var parameters = BuildParameters(values.Values, columns.Where(column => values.Values.ContainsKey(column.Name)).ToArray());
        if (VersionColumnDefinition is not null && !parameters.ContainsKey("@__groundwork_version")) parameters["@__groundwork_version"] = 1L;
        if (ScopeColumnDefinition is not null && !parameters.ContainsKey("@__groundwork_scope")) parameters["@__groundwork_scope"] = Access.Scope!.Value;
        using var command = Command(sql);
        AddParameters(command, parameters);
        try
        {
            command.ExecuteNonQuery();
            var version = VersionColumnDefinition is null ? (long?)null : existing is null ? 1 : existing.Version + 1;
            return new WriteOutcome(
                exactOutcome
                    ? existing is null ? WriteOutcomeStatus.Inserted : WriteOutcomeStatus.Updated
                    : WriteOutcomeStatus.Upserted,
                version);
        }
        catch (SqliteException exception) when (new SqliteDialect().TryMapUniqueViolation(exception, out _)) { return new WriteOutcome(WriteOutcomeStatus.UniqueViolation, existing?.Version); }
    }

    private StoredEntry? ReadCore(StorageKey key, IWritePathObserver? observer = null)
    {
        var (where, parameters) = KeyPredicate(key.Values);
        var columns = UserColumns.Concat(VersionColumnDefinition is null ? [] : [VersionColumnDefinition]);
        using var command = Command($"SELECT {string.Join(", ", columns.Select(column => Quote(column.Name)))} FROM {Quote(Unit.Name)} WHERE {where};");
        AddParameters(command, parameters);
        observer?.Observe(new WritePathEvent("sqlite.write-probe", command.CommandText, IsProbe: true));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < UserColumns.Count; i++) values[UserColumns[i].Name] = FromSqlite(reader.GetValue(i), UserColumns[i]);
        return new StoredEntry(new StorageValues(values), VersionColumnDefinition is null ? null : Convert.ToInt64(reader.GetValue(UserColumns.Count), CultureInfo.InvariantCulture));
    }

    private void ValidateValues(IReadOnlyDictionary<string, object?> values, bool requireAllNonNullable)
    {
        var known = UserColumns.Select(column => column.Name).ToHashSet(StringComparer.Ordinal);
        var unknown = values.Keys.FirstOrDefault(key => !known.Contains(key));
        if (unknown is not null) throw new ArgumentException($"Column '{unknown}' is not declared by '{Unit.Name}'.", nameof(values));
        if (requireAllNonNullable)
            foreach (var column in UserColumns.Where(column => !column.IsNullable && column.Default is null))
                if (!values.TryGetValue(column.Name, out var value) || value is null)
                    throw new ArgumentException($"Non-nullable column '{column.Name}' is required.", nameof(values));
    }

    private void ValidateExpected(WriteOptions? options, StoredEntry? existing, Mutation mutation)
    {
        if (options?.ExpectedVersion is not null && VersionColumnDefinition is null)
            throw new InvalidOperationException($"Storage unit '{Unit.Name}' does not declare version machinery.");
        if (VersionColumnDefinition is null) return;
        if (mutation == Mutation.Insert)
        {
            if (existing is null && options?.ExpectedVersion is not null)
                throw new ConcurrencyConflictException();
            return;
        }
        if (mutation == Mutation.Upsert)
        {
            if (existing is null ? options?.ExpectedVersion is not null : options?.ExpectedVersion != existing.Version)
                throw new ConcurrencyConflictException(existing?.Version);
            return;
        }
        if (existing is null || options?.ExpectedVersion != existing.Version)
            throw new ConcurrencyConflictException(existing?.Version);
    }

    private (string Predicate, Dictionary<string, object?> Parameters) KeyPredicate(IReadOnlyDictionary<string, object?> values)
    {
        var clauses = new List<string>();
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var column in LogicalKeyColumns)
        {
            if (!values.TryGetValue(column, out var value)) throw new ArgumentException($"Key column '{column}' is required.", nameof(values));
            var parameter = "@key_" + column;
            clauses.Add($"{Quote(column)}={parameter}");
            parameters[parameter] = ToSqlite(value, Column(column));
        }
        if (ScopeColumnDefinition is not null)
        {
            clauses.Add($"{Quote(ScopeColumnDefinition.Name)}=@__groundwork_scope");
            parameters["@__groundwork_scope"] = Access.Scope!.Value;
        }
        return (string.Join(" AND ", clauses), parameters);
    }

    private Dictionary<string, object?> BuildParameters(IReadOnlyDictionary<string, object?> values, IEnumerable<ColumnDefinition> columns) =>
        columns.Where(column => values.ContainsKey(column.Name)).ToDictionary(column => "@" + column.Name, column => ToSqlite(values[column.Name], column), StringComparer.Ordinal);

    private SqliteCommand Command(string sql)
    {
        ThrowIfClosed();
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = activeTransaction ?? transaction;
        return command;
    }

    private static void AddParameters(SqliteCommand command, IReadOnlyDictionary<string, object?> parameters)
    {
        foreach (var pair in parameters) command.Parameters.AddWithValue(pair.Key, pair.Value ?? DBNull.Value);
    }

    private T Execute<T>(Func<T> operation)
    {
        try
        {
            if (transaction is not null) return operation();
            lock (owner.Gate) { owner.ThrowIfDisposed(); return operation(); }
        }
        catch (ConcurrencyConflictException exception) when (typeof(T) == typeof(WriteOutcome))
        {
            return (T)(object)new WriteOutcome(WriteOutcomeStatus.ConcurrencyConflict, exception.Version);
        }
    }

    private T ExecuteWrite<T>(Func<T> operation)
    {
        if (transaction is not null) return Translate(operation);
        lock (owner.Gate)
        {
            owner.ThrowIfDisposed();
            using var writeTransaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
            activeTransaction = writeTransaction;
            try
            {
                var result = Translate(operation);
                writeTransaction.Commit();
                return result;
            }
            catch
            {
                writeTransaction.Rollback();
                throw;
            }
            finally
            {
                activeTransaction = null;
            }
        }
    }

    private static T Translate<T>(Func<T> operation)
    {
        try { return operation(); }
        catch (ConcurrencyConflictException exception) when (typeof(T) == typeof(WriteOutcome))
        {
            return (T)(object)new WriteOutcome(WriteOutcomeStatus.ConcurrencyConflict, exception.Version);
        }
    }

    private ColumnDefinition Column(string name) => UserColumns.First(column => column.Name == name);
    private IReadOnlyList<ColumnDefinition> UserColumns => Unit.Columns.Where(column => column.Name is not SqliteSchemaCoordinator.ScopeColumn and not SqliteSchemaCoordinator.VersionColumn and not SqliteSchemaCoordinator.ActionColumn).ToArray();
    private IReadOnlyList<string> LogicalKeyColumns => Unit.Key.Columns.Where(column => column != SqliteSchemaCoordinator.ScopeColumn).ToArray();
    private ColumnDefinition? ScopeColumnDefinition => Unit.Columns.FirstOrDefault(column => column.Name == SqliteSchemaCoordinator.ScopeColumn);
    private ColumnDefinition? VersionColumnDefinition => Unit.Columns.FirstOrDefault(column => column.Name == SqliteSchemaCoordinator.VersionColumn);
    private ColumnDefinition? ActionColumnDefinition => Unit.Columns.FirstOrDefault(column => column.Name == SqliteSchemaCoordinator.ActionColumn);
    private static string Quote(string value) => SqliteProviderConnection.QuoteIdentifier(value);
    private static object? ToSqlite(object? value, ColumnDefinition definition) => SqliteProviderConnection.ToSqliteValue(value, definition);

    private static object? FromSqlite(object value, ColumnDefinition definition)
    {
        if (value is DBNull) return null;
        return definition.Type switch
        {
            PortableType.Boolean => Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0,
            PortableType.Int32 => Convert.ToInt32(value, CultureInfo.InvariantCulture),
            PortableType.Int64 => Convert.ToInt64(value, CultureInfo.InvariantCulture),
            PortableType.Decimal => decimal.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture),
            PortableType.Guid => Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)! ),
            PortableType.DateTimeOffset => DateTimeOffset.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            PortableType.Binary => ((byte[])value).ToArray(),
            PortableType.Json => value is string json ? JsonDocument.Parse(json).RootElement.Clone() : value,
            _ => value
        };
    }

    private void ThrowIfClosed()
    {
        if (closed) throw new ObjectDisposedException(nameof(SqliteStorageSession));
    }

    private enum Mutation { Insert, Update, Upsert, Delete }
    private sealed class ConcurrencyConflictException(long? version = null) : Exception { public long? Version { get; } = version; }
}
