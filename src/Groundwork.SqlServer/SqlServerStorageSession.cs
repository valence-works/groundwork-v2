using System.Data;
using System.Globalization;
using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Groundwork.Kernel;
using Groundwork.Query.Model;
using Groundwork.Substrate.Relational;
using Groundwork.Testing;

namespace Groundwork.SqlServer;

internal sealed class SqlServerStorageSession : IStorageSession, IConcurrencyStorageSession
{
    private readonly SqlServerProviderConnection owner;
    private readonly SqlConnection connection;
    private readonly SqlTransaction? transaction;
    private readonly SqlServerDialect dialect = new();
    private SqlTransaction? activeTransaction;
    private bool closed;

    internal SqlServerStorageSession(SqlServerProviderConnection owner, StorageUnit unit, StorageAccess access,
        SqlConnection connection, SqlTransaction? transaction)
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
        var renderOptions = suppliedOptions.WithIdentityTieBreaks(Unit.Key.Columns.Select(QueryColumn).Where(column => column is not null)!.Select(column => column!)) with
        {
            Indexes = suppliedOptions.Indexes.Select(index => index.WithColumnTypes(Unit.Columns.ToDictionary(column => column.Name, column => QueryTypeOf(column.Type), StringComparer.Ordinal))).ToImmutableArray(),
            PhysicalIndexNames = Unit.Indexes.ToDictionary(
                index => index.Name,
                index => SqlServerDialect.PhysicalIndexName(Unit.Name, index.Name),
                StringComparer.Ordinal)
        };
        var executionRequest = QueryRequestExecution.ForPage(executionSource, renderOptions);
        var command = new SqlServerQueryRenderer().Render(executionRequest, renderOptions);
        var rows = RelationalQueryResultReader.Read(connection, command, (name, value) =>
        {
            if (name == "__groundwork_total_count") return value;
            var column = Unit.Columns.FirstOrDefault(item => item.Name == name);
            return column is null ? value : FromSqlServer(value ?? DBNull.Value, column);
        });
        return QueryResultMaterializer.Materialize(request, renderOptions, rows, command.SelectedIndex, command.IndexHintApplied,
            sourceIncludesRequestedOffset: true,
            sourceIncludesContinuation: true);
    });

    private QueryRequest WithScopePredicate(QueryRequest request) => Unit.Scope != ScopePolicy.Scoped
        ? request
        : QueryRequestExecution.WithProviderPredicate(request, new Predicate.And([
            request.Where,
            new Predicate.Equal(new ColumnRef(new TableId(Unit.Name), SqlServerSchemaCoordinator.ScopeColumn, QueryType.String),
                QueryConstant.Of(new ColumnRef(new TableId(Unit.Name), SqlServerSchemaCoordinator.ScopeColumn, QueryType.String), Access.Scope!.Value))]));

    public StoredEntry? Read(StorageKey key) => Execute(() => ReadCore(key));

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
            parameters["@expected"] = (options!.ExpectedVersion!.Value, VersionColumnDefinition);
        }
        using var command = Command($"DELETE FROM {Quote(Unit.Name)} WHERE {where};");
        AddParameters(command, parameters);
        if (command.ExecuteNonQuery() == 0) return new WriteOutcome(WriteOutcomeStatus.ConcurrencyConflict, existing.Version);
        return new WriteOutcome(WriteOutcomeStatus.Deleted, existing.Version);
    });

    internal void Close() => closed = true;

    private ColumnRef? QueryColumn(string name)
    {
        var column = Unit.Columns.Single(item => item.Name == name);
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

    private WriteOutcome Mutate(StorageValues values, WriteOptions? options, Mutation mutation) => ExecuteWrite(() =>
    {
        ArgumentNullException.ThrowIfNull(values);
        ValidateValues(values.Values, mutation == Mutation.Insert);
        var key = new StorageKey(LogicalKeyColumns.ToDictionary(
            column => column,
            column => values.Values.TryGetValue(column, out var value)
                ? value
                : throw new ArgumentException($"Key column '{column}' is required.", nameof(values)),
            StringComparer.Ordinal));
        var existing = ReadCore(key);
        if (mutation == Mutation.Insert && existing is not null) return new WriteOutcome(WriteOutcomeStatus.UniqueViolation, existing.Version);
        if (mutation == Mutation.Update && existing is null) return new WriteOutcome(WriteOutcomeStatus.NotFound);
        ValidateExpected(options, existing, mutation);
        if (mutation == Mutation.Upsert)
        {
            if (existing is null) return InsertCore(values.Values);
            return UpdateCore(values.Values, existing, options);
        }

        var supplied = UserColumns.Where(column => values.Values.ContainsKey(column.Name)).ToArray();
        var columns = supplied.ToList();
        if (mutation == Mutation.Insert)
        {
            if (VersionColumnDefinition is not null) columns.Add(VersionColumnDefinition);
            if (ScopeColumnDefinition is not null) columns.Add(ScopeColumnDefinition);
            var parameters = BuildParameters(values.Values, supplied);
            if (VersionColumnDefinition is not null) parameters["@__groundwork_version"] = (1L, VersionColumnDefinition);
            if (ScopeColumnDefinition is not null) parameters["@__groundwork_scope"] = (Access.Scope!.Value, ScopeColumnDefinition);
            using var insert = Command($"INSERT INTO {Quote(Unit.Name)} ({string.Join(", ", columns.Select(column => Quote(column.Name)))}) VALUES ({string.Join(", ", columns.Select(column => "@" + column.Name))});");
            AddParameters(insert, parameters);
            try
            {
                insert.ExecuteNonQuery();
                return new WriteOutcome(WriteOutcomeStatus.Inserted, VersionColumnDefinition is null ? null : 1);
            }
            catch (SqlException exception) when (dialect.TryMapUniqueViolation(exception, out _))
            {
                return new WriteOutcome(WriteOutcomeStatus.UniqueViolation);
            }
        }
        return UpdateCore(values.Values, existing!, options);
    });

    private WriteOutcome InsertCore(IReadOnlyDictionary<string, object?> values)
    {
        var supplied = UserColumns.Where(column => values.ContainsKey(column.Name)).ToArray();
        var columns = supplied.ToList();
        if (VersionColumnDefinition is not null) columns.Add(VersionColumnDefinition);
        if (ScopeColumnDefinition is not null) columns.Add(ScopeColumnDefinition);
        var parameters = BuildParameters(values, supplied);
        if (VersionColumnDefinition is not null) parameters["@__groundwork_version"] = (1L, VersionColumnDefinition);
        if (ScopeColumnDefinition is not null) parameters["@__groundwork_scope"] = (Access.Scope!.Value, ScopeColumnDefinition);
        using var command = Command($"INSERT INTO {Quote(Unit.Name)} ({string.Join(", ", columns.Select(column => Quote(column.Name)))}) VALUES ({string.Join(", ", columns.Select(column => "@" + column.Name))});");
        AddParameters(command, parameters);
        try
        {
            command.ExecuteNonQuery();
            return new WriteOutcome(WriteOutcomeStatus.Upserted, VersionColumnDefinition is null ? null : 1);
        }
        catch (SqlException exception) when (dialect.TryMapUniqueViolation(exception, out _)) { return new WriteOutcome(WriteOutcomeStatus.UniqueViolation); }
    }

    private WriteOutcome UpdateCore(IReadOnlyDictionary<string, object?> values, StoredEntry existing, WriteOptions? options)
    {
        var supplied = UserColumns.Where(column => values.ContainsKey(column.Name) && !Unit.Key.Columns.Contains(column.Name, StringComparer.Ordinal)).ToArray();
        var sets = supplied.Select(column => $"{Quote(column.Name)}=@{column.Name}").ToList();
        var parameters = BuildParameters(values, supplied);
        var (where, keyParameters) = KeyPredicate(values);
        foreach (var pair in keyParameters) parameters[pair.Key] = pair.Value;
        if (VersionColumnDefinition is not null)
        {
            sets.Add($"{Quote(VersionColumnDefinition.Name)}={Quote(VersionColumnDefinition.Name)}+1");
            where += $" AND {Quote(VersionColumnDefinition.Name)}=@expected";
            parameters["@expected"] = (options!.ExpectedVersion!.Value, VersionColumnDefinition);
        }
        if (sets.Count == 0) return new WriteOutcome(WriteOutcomeStatus.Updated, existing.Version);
        using var command = Command($"UPDATE {Quote(Unit.Name)} SET {string.Join(", ", sets)} WHERE {where};");
        AddParameters(command, parameters);
        try
        {
            if (command.ExecuteNonQuery() == 0) return new WriteOutcome(WriteOutcomeStatus.ConcurrencyConflict, existing.Version);
            return new WriteOutcome(WriteOutcomeStatus.Updated, VersionColumnDefinition is null ? null : existing.Version + 1);
        }
        catch (SqlException exception) when (dialect.TryMapUniqueViolation(exception, out _))
        {
            return new WriteOutcome(WriteOutcomeStatus.UniqueViolation, existing.Version);
        }
    }

    private WriteOutcome ConditionalUpsertCore(StorageValues values, WriteOptions? options)
    {
        ArgumentNullException.ThrowIfNull(values);
        ValidateValues(values.Values, requireAllNonNullable: false);
        if (options?.ExpectedVersion is not null && VersionColumnDefinition is null)
            throw new InvalidOperationException($"Storage unit '{Unit.Name}' does not declare version machinery.");

        var key = new StorageKey(LogicalKeyColumns.ToDictionary(
            column => column,
            column => values.Values.TryGetValue(column, out var value)
                ? value
                : throw new ArgumentException($"Key column '{column}' is required.", nameof(values)),
            StringComparer.Ordinal));
        if (transaction is not null)
            return ExecuteConditionalBatch(values, options, key);

        using var writeTransaction = connection.BeginTransaction(IsolationLevel.Serializable);
        activeTransaction = writeTransaction;
        try
        {
            var result = ExecuteConditionalBatch(values, options, key);
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

    private WriteOutcome ExecuteConditionalBatch(
        StorageValues values,
        WriteOptions? options,
        StorageKey key)
    {
        var supplied = UserColumns.Where(column => values.Values.ContainsKey(column.Name)).ToArray();
        var updateColumns = supplied.Where(column =>
            !Unit.Key.Columns.Contains(column.Name, StringComparer.Ordinal) &&
            column.Name != "createdAt" &&
            column.Name != SqlServerSchemaCoordinator.ScopeColumn).ToArray();
        var updates = updateColumns.Select(column =>
            $"{Quote(column.Name)}=@{column.Name}").ToList();
        if (updates.Count == 0)
        {
            var noOpColumn = LogicalKeyColumns[0];
            updates.Add($"{Quote(noOpColumn)}={Quote(noOpColumn)}");
        }
        if (VersionColumnDefinition is not null)
            updates.Add($"{Quote(VersionColumnDefinition.Name)}={Quote(VersionColumnDefinition.Name)}+1");

        var parameters = BuildParameters(values.Values, supplied);
        var (_, keyParameters) = KeyPredicate(key.Values);
        foreach (var pair in keyParameters)
            parameters[pair.Key] = pair.Value;
        if (VersionColumnDefinition is not null)
            parameters["@expected"] = (options?.ExpectedVersion, VersionColumnDefinition);

        var where = string.Join(" AND ", LogicalKeyColumns.Select(column =>
            $"target.{Quote(column)}=@key_{column}"));
        if (ScopeColumnDefinition is not null)
            where += $" AND target.{Quote(ScopeColumnDefinition.Name)}=@__groundwork_scope";
        var updateCondition = VersionColumnDefinition is null
            ? "1=1"
            : $"@expected IS NOT NULL AND target.{Quote(VersionColumnDefinition.Name)}=@expected";
        var insertCondition = VersionColumnDefinition is null ? "1=1" : "@expected IS NULL";
        var insertColumns = supplied.ToList();
        if (VersionColumnDefinition is not null)
        {
            insertColumns.Add(VersionColumnDefinition);
            parameters["@__groundwork_version"] = (1L, VersionColumnDefinition);
        }
        if (ScopeColumnDefinition is not null)
        {
            insertColumns.Add(ScopeColumnDefinition);
            parameters["@__groundwork_scope"] = (Access.Scope!.Value, ScopeColumnDefinition);
        }

        var outputVersion = VersionColumnDefinition is null
            ? "CONVERT(bigint, NULL)"
            : $"inserted.{Quote(VersionColumnDefinition.Name)}";
        var sql = $"DECLARE @result TABLE ([operation] nvarchar(6) NOT NULL, [version] bigint NULL); " +
            $"UPDATE target WITH (UPDLOCK, SERIALIZABLE) SET {string.Join(", ", updates)} " +
            $"OUTPUT N'UPDATE', {outputVersion} INTO @result ([operation], [version]) " +
            $"FROM {Quote(Unit.Name)} AS target WHERE {where} AND ({updateCondition}); " +
            $"IF @@ROWCOUNT = 0 AND ({insertCondition}) BEGIN " +
            $"INSERT INTO {Quote(Unit.Name)} ({string.Join(", ", insertColumns.Select(column => Quote(column.Name)))}) " +
            $"OUTPUT N'INSERT', {(VersionColumnDefinition is null ? "CONVERT(bigint, NULL)" : "CONVERT(bigint, 1)")} " +
            $"INTO @result ([operation], [version]) VALUES ({string.Join(", ", insertColumns.Select(column => "@" + column.Name))}); END; " +
            "SELECT [operation], [version] FROM @result;";
        using var command = Command(sql);
        AddParameters(command, parameters);
        try
        {
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                var status = string.Equals(reader.GetString(0), "INSERT", StringComparison.Ordinal)
                    ? WriteOutcomeStatus.Inserted
                    : WriteOutcomeStatus.Updated;
                var version = reader.IsDBNull(1)
                    ? (long?)null
                    : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
                return new WriteOutcome(status, version);
            }
        }
        catch (SqlException exception) when (dialect.TryMapUniqueViolation(exception, out _))
        {
            var existing = ReadCore(key);
            return new WriteOutcome(WriteOutcomeStatus.ConcurrencyConflict, existing?.Version);
        }

        var current = ReadCore(key);
        return new WriteOutcome(WriteOutcomeStatus.ConcurrencyConflict, current?.Version);
    }

    private StoredEntry? ReadCore(StorageKey key)
    {
        var (where, parameters) = KeyPredicate(key.Values);
        var columns = UserColumns.Concat(VersionColumnDefinition is null ? [] : [VersionColumnDefinition]);
        using var command = Command($"SELECT {string.Join(", ", columns.Select(column => Quote(column.Name)))} FROM {Quote(Unit.Name)} WHERE {where};");
        AddParameters(command, parameters);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < UserColumns.Count; i++) values[UserColumns[i].Name] = FromSqlServer(reader.GetValue(i), UserColumns[i]);
        var version = VersionColumnDefinition is null ? (long?)null : Convert.ToInt64(reader.GetValue(UserColumns.Count), CultureInfo.InvariantCulture);
        return new StoredEntry(new StorageValues(values), version);
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
            if (existing is null && options?.ExpectedVersion is not null) throw new ConcurrencyConflictException();
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

    private (string Predicate, Dictionary<string, (object? Value, ColumnDefinition Definition)> Parameters) KeyPredicate(IReadOnlyDictionary<string, object?> values)
    {
        var clauses = new List<string>();
        var parameters = new Dictionary<string, (object?, ColumnDefinition)>(StringComparer.Ordinal);
        foreach (var column in LogicalKeyColumns)
        {
            if (!values.TryGetValue(column, out var value)) throw new ArgumentException($"Key column '{column}' is required.", nameof(values));
            var parameter = "@key_" + column;
            clauses.Add($"{Quote(column)}={parameter}");
            parameters[parameter] = (value, Column(column));
        }
        if (ScopeColumnDefinition is not null)
        {
            clauses.Add($"{Quote(ScopeColumnDefinition.Name)}=@__groundwork_scope");
            parameters["@__groundwork_scope"] = (Access.Scope!.Value, ScopeColumnDefinition);
        }
        return (string.Join(" AND ", clauses), parameters);
    }

    private Dictionary<string, (object? Value, ColumnDefinition Definition)> BuildParameters(IReadOnlyDictionary<string, object?> values, IEnumerable<ColumnDefinition> columns) =>
        columns.Where(column => values.ContainsKey(column.Name)).ToDictionary(column => "@" + column.Name, column => (values[column.Name], column), StringComparer.Ordinal);

    private SqlCommand Command(string sql)
    {
        if (closed) throw new ObjectDisposedException(nameof(SqlServerStorageSession));
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = activeTransaction ?? transaction;
        return command;
    }

    private static void AddParameters(SqlCommand command, IReadOnlyDictionary<string, (object? Value, ColumnDefinition Definition)> parameters)
    {
        foreach (var pair in parameters) SqlServerProviderConnection.AddParameter(command, pair.Key, pair.Value.Value, pair.Value.Definition);
    }

    private T Execute<T>(Func<T> operation)
    {
        try
        {
            if (transaction is not null) return operation();
            owner.ThrowIfDisposed();
            return operation();
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
            using var writeTransaction = connection.BeginTransaction(IsolationLevel.Serializable);
            activeTransaction = writeTransaction;
            try { var result = Translate(operation); writeTransaction.Commit(); return result; }
            catch { writeTransaction.Rollback(); throw; }
            finally { activeTransaction = null; }
        }
    }

    private static T Translate<T>(Func<T> operation)
    {
        try { return operation(); }
        catch (ConcurrencyConflictException exception) when (typeof(T) == typeof(WriteOutcome))
        { return (T)(object)new WriteOutcome(WriteOutcomeStatus.ConcurrencyConflict, exception.Version); }
    }

    private ColumnDefinition Column(string name) => UserColumns.First(column => column.Name == name);
    private IReadOnlyList<ColumnDefinition> UserColumns => Unit.Columns.Where(column => column.Name is not SqlServerSchemaCoordinator.ScopeColumn and not SqlServerSchemaCoordinator.VersionColumn).ToArray();
    private IReadOnlyList<string> LogicalKeyColumns => Unit.Key.Columns.Where(column => column != SqlServerSchemaCoordinator.ScopeColumn).ToArray();
    private ColumnDefinition? ScopeColumnDefinition => Unit.Columns.FirstOrDefault(column => column.Name == SqlServerSchemaCoordinator.ScopeColumn);
    private ColumnDefinition? VersionColumnDefinition => Unit.Columns.FirstOrDefault(column => column.Name == SqlServerSchemaCoordinator.VersionColumn);
    private static string Quote(string value) => SqlServerProviderConnection.QuoteIdentifier(value);

    private static object? FromSqlServer(object value, ColumnDefinition definition)
    {
        if (value is DBNull) return null;
        return definition.Type switch
        {
            PortableType.Boolean => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
            PortableType.Int32 => Convert.ToInt32(value, CultureInfo.InvariantCulture),
            PortableType.Int64 => Convert.ToInt64(value, CultureInfo.InvariantCulture),
            PortableType.Decimal => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
            PortableType.Guid => (Guid)value,
            PortableType.DateTimeOffset => ((DateTimeOffset)value).ToUniversalTime(),
            PortableType.Binary => ((byte[])value).ToArray(),
            PortableType.Json => JsonDocument.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!).RootElement.Clone(),
            _ => value
        };
    }

    private sealed class ConcurrencyConflictException(long? version = null) : Exception
    {
        public long? Version { get; } = version;
    }

    private enum Mutation { Insert, Update, Upsert, Delete }
}
