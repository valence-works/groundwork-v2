using System.Globalization;
using System.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Groundwork.Kernel;
using Groundwork.Testing;

namespace Groundwork.Sqlite;

internal sealed class SqliteStorageSession : IStorageSession, IConcurrencyStorageSession, IBatchedStorageSession
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

    public IReadOnlyList<RowWriteOutcome> ApplyBatch(IReadOnlyList<RowWrite> writes) =>
        ExecuteWrite(() => ApplyBatchCore(writes));

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

    private IReadOnlyList<RowWriteOutcome> ApplyBatchCore(IReadOnlyList<RowWrite> writes)
    {
        ArgumentNullException.ThrowIfNull(writes);
        if (writes.Count == 0)
            return [];

        // Expected-version writes and deletes need their per-row predicates and
        // conflict details. Keep those semantics exact; unconditional inserts and
        // upserts are the provider-native multi-row path.
        if (writes.Any(write => write.Options.ExpectedVersion is not null))
            return ApplyBatchFallback(writes);

        return writes[0].Mode switch
        {
            RowWriteMode.Insert => ApplyInsertBatch(writes),
            RowWriteMode.Upsert => ApplyUpsertBatch(writes),
            _ => ApplyBatchFallback(writes)
        };
    }

    private IReadOnlyList<RowWriteOutcome> ApplyInsertBatch(IReadOnlyList<RowWrite> writes)
    {
        var supplied = UserColumns.Where(column => writes[0].Values!.Values.ContainsKey(column.Name)).ToArray();
        foreach (var write in writes)
        {
            ValidateValues(write.Values!.Values, requireAllNonNullable: true);
            if (write.Values!.Values.Keys.Except(supplied.Select(column => column.Name), StringComparer.Ordinal).Any())
                return ApplyBatchFallback(writes);
        }

        var columns = supplied.ToList();
        if (VersionColumnDefinition is not null)
            columns.Add(VersionColumnDefinition);
        if (ScopeColumnDefinition is not null)
            columns.Add(ScopeColumnDefinition);
        var valuesSql = new List<string>();
        using var command = Command(string.Empty);
        for (var row = 0; row < writes.Count; row++)
        {
            var parameters = new List<string>();
            foreach (var column in columns)
            {
                var name = $"@r{row}_{column.Name}";
                parameters.Add(name);
                command.Parameters.AddWithValue(name, column.Name == SqliteSchemaCoordinator.VersionColumn
                    ? 1L
                    : column.Name == SqliteSchemaCoordinator.ScopeColumn
                        ? Access.Scope!.Value
                        : ToSqlite(writes[row].Values!.Values[column.Name], column) ?? DBNull.Value);
            }
            valuesSql.Add($"({string.Join(", ", parameters)})");
        }

        var returning = string.Join(", ", Unit.Key.Columns.Select(Quote));
        command.CommandText = $"INSERT INTO {Quote(Unit.Name)} ({string.Join(", ", columns.Select(column => Quote(column.Name)))}) VALUES {string.Join(", ", valuesSql)} ON CONFLICT DO NOTHING RETURNING {returning};";
        writes[0].Options.Observer?.Observe(new WritePathEvent("sqlite.batch-insert", "SQLite multi-row INSERT", IsProbe: false));
        try
        {
            var inserted = ReadReturnedKeys(command);
            var version = VersionColumnDefinition is null ? (long?)null : 1;
            return writes.Select(write => new RowWriteOutcome(write,
                inserted.Contains(write.Identity)
                    ? new WriteOutcome(WriteOutcomeStatus.Inserted, version)
                    : new WriteOutcome(WriteOutcomeStatus.UniqueViolation))).ToArray();
        }
        catch (SqliteException exception) when (new SqliteDialect().TryMapUniqueViolation(exception, out var indexName))
        {
            return writes.Select(write => new RowWriteOutcome(write,
                new WriteOutcome(WriteOutcomeStatus.UniqueViolation, null, LogicalIndexName(indexName)))).ToArray();
        }
    }

    private IReadOnlyList<RowWriteOutcome> ApplyUpsertBatch(IReadOnlyList<RowWrite> writes)
    {
        var supplied = UserColumns.Where(column => writes[0].Values!.Values.ContainsKey(column.Name)).ToArray();
        foreach (var write in writes)
        {
            ValidateValues(write.Values!.Values, requireAllNonNullable: false);
            if (write.Values!.Values.Keys.Except(supplied.Select(column => column.Name), StringComparer.Ordinal).Any())
                return ApplyBatchFallback(writes);
        }

        var columns = supplied.ToList();
        if (VersionColumnDefinition is not null)
            columns.Add(VersionColumnDefinition);
        if (ScopeColumnDefinition is not null)
            columns.Add(ScopeColumnDefinition);
        var valuesSql = new List<string>();
        using var command = Command(string.Empty);
        for (var row = 0; row < writes.Count; row++)
        {
            var parameters = new List<string>();
            foreach (var column in columns)
            {
                var name = $"@r{row}_{column.Name}";
                parameters.Add(name);
                command.Parameters.AddWithValue(name, column.Name == SqliteSchemaCoordinator.VersionColumn
                    ? 1L
                    : column.Name == SqliteSchemaCoordinator.ScopeColumn
                        ? Access.Scope!.Value
                        : ToSqlite(writes[row].Values!.Values[column.Name], column) ?? DBNull.Value);
            }
            valuesSql.Add($"({string.Join(", ", parameters)})");
        }

        var updateColumns = supplied
            .Where(column => !Unit.Key.Columns.Contains(column.Name, StringComparer.Ordinal) &&
                             column.Name != SqliteSchemaCoordinator.ScopeColumn)
            .Select(column => $"{Quote(column.Name)}=excluded.{Quote(column.Name)}")
            .ToList();
        if (VersionColumnDefinition is not null)
            updateColumns.Add($"{Quote(VersionColumnDefinition.Name)}={Quote(Unit.Name)}.{Quote(VersionColumnDefinition.Name)}+1");
        if (updateColumns.Count == 0)
            updateColumns.Add($"{Quote(Unit.Key.Columns[0])}={Quote(Unit.Name)}.{Quote(Unit.Key.Columns[0])}");

        var returning = string.Join(", ", Unit.Key.Columns.Select(Quote).Concat(
            VersionColumnDefinition is null ? [] : [Quote(VersionColumnDefinition.Name)]));
        command.CommandText = $"INSERT INTO {Quote(Unit.Name)} ({string.Join(", ", columns.Select(column => Quote(column.Name)))}) VALUES {string.Join(", ", valuesSql)} ON CONFLICT ({string.Join(", ", Unit.Key.Columns.Select(Quote))}) DO UPDATE SET {string.Join(", ", updateColumns)} RETURNING {returning};";
        writes[0].Options.Observer?.Observe(new WritePathEvent("sqlite.batch-upsert", "SQLite multi-row INSERT ON CONFLICT", IsProbe: false));
        try
        {
            var returned = ReadReturnedKeys(command);
            return writes.Select(write => new RowWriteOutcome(write,
                returned.Contains(write.Identity)
                    ? new WriteOutcome(WriteOutcomeStatus.Upserted)
                    : new WriteOutcome(WriteOutcomeStatus.UniqueViolation))).ToArray();
        }
        catch (SqliteException exception) when (new SqliteDialect().TryMapUniqueViolation(exception, out var indexName))
        {
            return writes.Select(write => new RowWriteOutcome(write,
                new WriteOutcome(WriteOutcomeStatus.UniqueViolation, null, LogicalIndexName(indexName)))).ToArray();
        }
    }

    private HashSet<string> ReadReturnedKeys(SqliteCommand command)
    {
        var identities = new HashSet<string>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var index = 0; index < Unit.Key.Columns.Count; index++)
            {
                var column = Unit.Key.Columns[index];
                if (column != SqliteSchemaCoordinator.ScopeColumn)
                    values[column] = FromSqlite(reader.GetValue(index), Column(column));
            }
            identities.Add(BatchIdentity(values));
        }
        return identities;
    }

    private string BatchIdentity(IReadOnlyDictionary<string, object?> values) => string.Join("\u001e",
        LogicalKeyColumns.Select(column => values[column] switch
        {
            null => "<null>",
            byte[] bytes => Convert.ToBase64String(bytes),
            DateTimeOffset timestamp => timestamp.UtcTicks.ToString(CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? "",
            _ => values[column]?.ToString() ?? ""
        }));

    private IReadOnlyList<RowWriteOutcome> ApplyBatchFallback(IReadOnlyList<RowWrite> writes) =>
        writes.Select(write => new RowWriteOutcome(write, write.Mode switch
        {
            RowWriteMode.Insert => Insert(write.Values!, write.Options),
            RowWriteMode.Update => Update(write.Values!, write.Options),
            RowWriteMode.Upsert => Upsert(write.Values!, write.Options),
            RowWriteMode.Delete => Delete(write.Key!, write.Options),
            _ => throw new ArgumentOutOfRangeException(nameof(write.Mode), write.Mode, null)
        })).ToArray();

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
