using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Collections.Immutable;
using System.Text.Json;
using Groundwork.Kernel;
using Groundwork.Query.Model;
using Groundwork.Substrate.Relational;
using Groundwork.Testing;
using Npgsql;
using NpgsqlTypes;

namespace Groundwork.PostgreSql;

internal sealed class PostgreSqlStorageSession : IStorageSession, IConcurrencyStorageSession, IBatchedStorageSession
{
    private readonly PostgreSqlProviderConnection owner;
    private readonly NpgsqlConnection connection;
    private readonly NpgsqlTransaction? transaction;
    private NpgsqlTransaction? activeTransaction;
    private bool closed;

    internal PostgreSqlStorageSession(
        PostgreSqlProviderConnection owner,
        StorageUnit unit,
        StorageAccess access,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction)
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
        var renderOptions = suppliedOptions.WithIdentityTieBreaks(Unit.Key.Columns.Where(name => name != PostgreSqlSchemaCoordinator.ScopeColumn).Select(QueryColumn).Where(column => column is not null)!.Select(column => column!)) with
        {
            Indexes = suppliedOptions.Indexes.Select(index => index.WithColumnTypes(Unit.Columns.ToDictionary(column => column.Name, column => QueryTypeOf(column.Type), StringComparer.Ordinal))).ToImmutableArray(),
            PhysicalIndexNames = Unit.Indexes.ToDictionary(
                index => index.Name,
                index => PostgreSqlDialect.PhysicalIndexName(Unit.Name, index.Name),
                StringComparer.Ordinal)
        };
        var executionRequest = QueryRequestExecution.ForPage(executionSource, renderOptions);
        var command = new PostgreSqlQueryRenderer().Render(executionRequest, renderOptions);
        var rows = RelationalQueryResultReader.Read(connection, command, (name, value) =>
        {
            if (name == "__groundwork_total_count") return value;
            var column = Unit.Columns.FirstOrDefault(item => item.Name == name);
            return column is null ? value : FromDatabase(value ?? DBNull.Value, column);
        });
        AssertExplainPlan(command, renderOptions);
        return QueryResultMaterializer.Materialize(executionSource, renderOptions, rows, command.SelectedIndex, command.IndexHintApplied,
            sourceIncludesRequestedOffset: true,
            sourceIncludesContinuation: true);
    });

    private void AssertExplainPlan(RelationalQueryCommand query, QueryRenderOptions options)
    {
        if (query.IsMatchNone || !ExplainAssertTestMode.ShouldAssert(query.SelectedIndex)) return;
        var logicalIndex = query.SelectedIndex!;
        var physicalIndex = options.ResolvePhysicalIndexName(logicalIndex);
        using var explain = Command("EXPLAIN (FORMAT JSON) " + query.CommandText.TrimEnd().TrimEnd(';'));
        RelationalQueryResultReader.AddParameters(explain, query);
        var rawPlan = Convert.ToString(explain.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
        ExplainAssertTestMode.AssertChosenIndex(
            "PostgreSQL", logicalIndex, physicalIndex, query.IndexHintApplied, rawPlan,
            PostgreSqlExplainPlanInspector.ChoseIndex(rawPlan, physicalIndex));
    }

    private QueryRequest WithScopePredicate(QueryRequest request) => Unit.Scope != ScopePolicy.Scoped
        ? request
        : QueryRequestExecution.WithProviderPredicate(request, new Predicate.And([
            request.Where,
            new Predicate.Equal(new ColumnRef(new TableId(Unit.Name), PostgreSqlSchemaCoordinator.ScopeColumn, QueryType.String),
                QueryConstant.Of(new ColumnRef(new TableId(Unit.Name), PostgreSqlSchemaCoordinator.ScopeColumn, QueryType.String), Access.Scope!.Value))]),
            QueryRequestExecution.ScopeBindingDiscriminator(Access.Scope!.Value));

    public StoredEntry? Read(StorageKey key) => Execute(() => ReadCore(key));

    public WriteOutcome Insert(StorageValues values, WriteOptions? options = null) =>
        Mutate(values, options, Mutation.Insert);

    public WriteOutcome Update(StorageValues values, WriteOptions? options = null) =>
        Mutate(values, options, Mutation.Update);

    public WriteOutcome Upsert(StorageValues values, WriteOptions? options = null) =>
        Mutate(values, options, Mutation.Upsert);

    public WriteOutcome ConditionalUpsert(StorageValues values, WriteOptions? options = null) =>
        Execute(() => ConditionalUpsertCore(values, options));

    public IReadOnlyList<RowWriteOutcome> ApplyBatch(IReadOnlyList<RowWrite> writes) =>
        ExecuteWrite(() => ApplyBatchCore(writes));

    public WriteOutcome Delete(StorageKey key, WriteOptions? options = null) => ExecuteWrite(() =>
    {
        var existing = ReadCore(key);
        ValidateExpected(options, existing, Mutation.Delete);
        if (existing is null)
            return new WriteOutcome(WriteOutcomeStatus.NotFound);
        var (where, parameters) = KeyPredicate(key.Values);
        if (VersionColumn is not null)
        {
            where += $" AND {Quote(VersionColumn.Name)}=@expected";
            parameters["@expected"] = options!.ExpectedVersion!.Value;
        }
        using var command = Command($"DELETE FROM {Quote(Unit.Name)} WHERE {where};");
        AddParameters(command, parameters);
        var affected = command.ExecuteNonQuery();
        return affected == 0
            ? new WriteOutcome(WriteOutcomeStatus.ConcurrencyConflict, existing.Version)
            : new WriteOutcome(WriteOutcomeStatus.Deleted, existing.Version);
    });

    internal void Close() => closed = true;

    private IReadOnlyList<RowWriteOutcome> ApplyBatchCore(IReadOnlyList<RowWrite> writes)
    {
        ArgumentNullException.ThrowIfNull(writes);
        if (writes.Count == 0)
            return [];
        if (SequenceColumn is not null)
            return ApplyBatchFallback(writes);
        if (writes.Any(write => write.Options.ExpectedVersion is not null))
            return ApplyBatchFallback(writes);
        if (HasSecondaryUniqueIndex(writes[0].Unit))
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
        var columns = PhysicalValues(writes[0].Values!.Values, includeVersion: VersionColumn is not null).Keys.ToArray();
        foreach (var write in writes)
        {
            ValidateValues(write.Values!.Values, requireAllNonNullable: true);
            if (!PhysicalValues(write.Values.Values, includeVersion: VersionColumn is not null).Keys.SequenceEqual(columns, StringComparer.Ordinal))
                return ApplyBatchFallback(writes);
        }
        var maxRows = Math.Max(1, Math.Min(1_000, 32_000 / columns.Length));
        if (writes.Count > maxRows)
            return writes.Chunk(maxRows).SelectMany(ApplyInsertBatch).ToArray();
        using var command = Command(string.Empty);
        var rows = AddBatchValues(command, writes, columns);
        var returning = string.Join(", ", Unit.Key.Columns.Select(Quote).Concat(
            VersionColumn is null ? [] : [Quote(VersionColumn.Name)]));
        command.CommandText = $"INSERT INTO {Quote(Unit.Name)} ({string.Join(", ", columns.Select(Quote))}) VALUES {string.Join(", ", rows)} ON CONFLICT DO NOTHING RETURNING {returning};";
        writes[0].Options.Observer?.Observe(new WritePathEvent("postgresql.batch-insert", "PostgreSQL multi-row INSERT", IsProbe: false));
        try
        {
            var returned = ReadReturnedRows(command, writes[0].Unit);
            return writes.Select(write => new RowWriteOutcome(write,
                returned.TryGetValue(write.Identity, out var version)
                    ? new WriteOutcome(WriteOutcomeStatus.Inserted, version)
                    : new WriteOutcome(WriteOutcomeStatus.UniqueViolation))).ToArray();
        }
        catch (DbException exception) when (new PostgreSqlDialect().TryMapUniqueViolation(exception, out var indexName))
        {
            return writes.Select(write => new RowWriteOutcome(write,
                new WriteOutcome(WriteOutcomeStatus.UniqueViolation, null, LogicalIndexName(indexName)))).ToArray();
        }
    }

    private IReadOnlyList<RowWriteOutcome> ApplyUpsertBatch(IReadOnlyList<RowWrite> writes)
    {
        var columns = PhysicalValues(writes[0].Values!.Values, includeVersion: VersionColumn is not null).Keys.ToArray();
        foreach (var write in writes)
        {
            ValidateValues(write.Values!.Values, requireAllNonNullable: false);
            if (!PhysicalValues(write.Values.Values, includeVersion: VersionColumn is not null).Keys.SequenceEqual(columns, StringComparer.Ordinal))
                return ApplyBatchFallback(writes);
        }
        var maxRows = Math.Max(1, Math.Min(1_000, 32_000 / columns.Length));
        if (writes.Count > maxRows)
            return writes.Chunk(maxRows).SelectMany(ApplyUpsertBatch).ToArray();
        using var command = Command(string.Empty);
        var rows = AddBatchValues(command, writes, columns);
        var conflictPredicate = PartialKeyPredicate();
        var conflict = $"({string.Join(", ", Unit.Key.Columns.Select(Quote))})" +
            (conflictPredicate is null ? string.Empty : $" WHERE {conflictPredicate}");
        var updates = columns
            .Where(column => !Unit.Key.Columns.Contains(column, StringComparer.Ordinal) &&
                             column != PostgreSqlSchemaCoordinator.ScopeColumn &&
                             column != "createdAt" &&
                             column != PostgreSqlSchemaCoordinator.VersionColumn)
            .Select(column => $"{Quote(column)}=EXCLUDED.{Quote(column)}")
            .ToList();
        if (VersionColumn is not null)
            updates.Add($"{Quote(VersionColumn.Name)}={Quote(Unit.Name)}.{Quote(VersionColumn.Name)}+1");
        if (updates.Count == 0)
            updates.Add($"{Quote(Unit.Key.Columns[0])}={Quote(Unit.Name)}.{Quote(Unit.Key.Columns[0])}");
        var returning = string.Join(", ", Unit.Key.Columns.Select(Quote).Concat(
            VersionColumn is null ? [] : [Quote(VersionColumn.Name)]));
        command.CommandText = $"INSERT INTO {Quote(Unit.Name)} ({string.Join(", ", columns.Select(Quote))}) VALUES {string.Join(", ", rows)} ON CONFLICT {conflict} DO UPDATE SET {string.Join(", ", updates)} RETURNING {returning};";
        writes[0].Options.Observer?.Observe(new WritePathEvent("postgresql.batch-upsert", "PostgreSQL multi-row INSERT ON CONFLICT", IsProbe: false));
        try
        {
            var returned = ReadReturnedRows(command, writes[0].Unit);
            return writes.Select(write => new RowWriteOutcome(write,
                returned.TryGetValue(write.Identity, out var version)
                    ? new WriteOutcome(WriteOutcomeStatus.Upserted, version)
                    : new WriteOutcome(WriteOutcomeStatus.UniqueViolation))).ToArray();
        }
        catch (DbException exception) when (new PostgreSqlDialect().TryMapUniqueViolation(exception, out var indexName))
        {
            return writes.Select(write => new RowWriteOutcome(write,
                new WriteOutcome(WriteOutcomeStatus.UniqueViolation, null, LogicalIndexName(indexName)))).ToArray();
        }
    }

    private List<string> AddBatchValues(NpgsqlCommand command, IReadOnlyList<RowWrite> writes, IReadOnlyList<string> columns)
    {
        var rows = new List<string>(writes.Count);
        for (var row = 0; row < writes.Count; row++)
        {
            var physical = PhysicalValues(writes[row].Values!.Values, includeVersion: VersionColumn is not null);
            var parameters = new List<string>(columns.Count);
            foreach (var column in columns)
            {
                var name = $"r{row}_{column}";
                parameters.Add("@" + name);
                Add(command, name, physical[column]);
            }
            rows.Add($"({string.Join(", ", parameters)})");
        }
        return rows;
    }

    private Dictionary<string, long?> ReadReturnedRows(NpgsqlCommand command, StorageUnit logicalUnit)
    {
        var returned = new Dictionary<string, long?>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var index = 0; index < Unit.Key.Columns.Count; index++)
            {
                var column = Unit.Key.Columns[index];
                if (column != PostgreSqlSchemaCoordinator.ScopeColumn)
                    values[column] = FromDatabase(reader.GetValue(index), Column(column));
            }
            var versionOrdinal = Unit.Key.Columns.Count;
            var version = VersionColumn is null || reader.IsDBNull(versionOrdinal)
                ? (long?)null
                : Convert.ToInt64(reader.GetValue(versionOrdinal), CultureInfo.InvariantCulture);
            returned[RowWrite.IdentityFor(logicalUnit, values)] = version;
        }
        return returned;
    }


    private IReadOnlyList<RowWriteOutcome> ApplyBatchFallback(IReadOnlyList<RowWrite> writes) =>
        writes.Select(write => new RowWriteOutcome(write, write.Mode switch
        {
            RowWriteMode.Insert => Insert(write.Values!, write.Options),
            RowWriteMode.Update => Update(write.Values!, write.Options),
            RowWriteMode.Upsert when write.Options.ExpectedVersion is not null => ConditionalUpsert(write.Values!, write.Options),
            RowWriteMode.Upsert => Upsert(write.Values!, write.Options),
            RowWriteMode.ConditionalUpsert => ConditionalUpsert(write.Values!, write.Options),
            RowWriteMode.Delete => Delete(write.Key!, write.Options),
            _ => throw new ArgumentOutOfRangeException(nameof(write.Mode), write.Mode, null)
        })).ToArray();

    private static bool HasSecondaryUniqueIndex(StorageUnit logicalUnit) =>
        logicalUnit.Indexes.Any(index => index.IsUnique &&
            !index.Columns.Select(column => column.Column)
                .SequenceEqual(logicalUnit.Key.Columns, StringComparer.Ordinal));
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

        // A provider sequence has no caller-visible key until the insert commits. Treat an
        // upsert without a generated key as an insert; accepting a synthetic read here would
        // both defeat the native identity and make the returned generated value ambiguous.
        if (SequenceColumn is not null &&
            (mutation is Mutation.Insert or Mutation.Upsert) &&
            !values.Values.ContainsKey(SequenceColumn.Name))
        {
            ValidateExpected(options, null, mutation);
            return InsertCore(values, mutation == Mutation.Upsert ? WriteOutcomeStatus.Upserted : WriteOutcomeStatus.Inserted);
        }

        var key = KeyFromValues(values.Values);
        var existing = ReadCore(key);
        if (mutation == Mutation.Insert && existing is not null)
            return new WriteOutcome(WriteOutcomeStatus.UniqueViolation, existing.Version);
        if (mutation == Mutation.Update && existing is null)
            return new WriteOutcome(WriteOutcomeStatus.NotFound);
        ValidateExpected(options, existing, mutation);
        return mutation switch
        {
            Mutation.Insert => InsertCore(values),
            Mutation.Update => UpdateCore(values, key, existing!, options),
            Mutation.Upsert => UpsertCore(values, key, existing, options, exactOutcome: false),
            Mutation.ConditionalUpsert => UpsertCore(values, key, existing, options, exactOutcome: true),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null)
        };
    });

    private WriteOutcome ConditionalUpsertCore(StorageValues values, WriteOptions? options)
    {
        ArgumentNullException.ThrowIfNull(values);
        ValidateValues(values.Values, requireAllNonNullable: false);
        if (options?.ExpectedVersion is not null && VersionColumn is null)
            throw new InvalidOperationException($"Storage unit '{Unit.Name}' does not declare version machinery.");

        var key = KeyFromValues(values.Values);
        var physical = PhysicalValues(values.Values, includeVersion: VersionColumn is not null);
        var (keyPredicate, keyParameters) = KeyPredicate(key.Values);
        var columns = physical.Keys.ToArray();
        var updateColumns = columns.Where(column =>
            !Unit.Key.Columns.Contains(column, StringComparer.Ordinal) &&
            column != PostgreSqlSchemaCoordinator.ScopeColumn &&
            column != "createdAt" &&
            column != PostgreSqlSchemaCoordinator.VersionColumn).ToArray();
        var updates = updateColumns.Select(column =>
            $"{Quote(column)}=EXCLUDED.{Quote(column)}").ToList();
        if (VersionColumn is not null)
            updates.Add($"{Quote(VersionColumn.Name)}={Quote(Unit.Name)}.{Quote(VersionColumn.Name)}+1");
        if (updates.Count == 0)
        {
            var noOpColumn = Unit.Key.Columns[0];
            updates.Add($"{Quote(noOpColumn)}={Quote(Unit.Name)}.{Quote(noOpColumn)}");
        }

        var conflictPredicate = PartialKeyPredicate();
        var conflict = $"({string.Join(", ", Unit.Key.Columns.Select(Quote))})" +
            (conflictPredicate is null ? string.Empty : $" WHERE {conflictPredicate}");
        var actionPredicate = VersionColumn is null
            ? string.Empty
            : $" WHERE @expected::bigint IS NOT NULL AND {Quote(Unit.Name)}.{Quote(VersionColumn.Name)}=@expected::bigint";
        var source = VersionColumn is null || options?.ExpectedVersion is null
            ? $"VALUES ({string.Join(", ", columns.Select(column => "@" + column))})"
            : $"SELECT {string.Join(", ", columns.Select(column => "@" + column))} WHERE EXISTS (SELECT 1 FROM {Quote(Unit.Name)} WHERE {keyPredicate} AND {Quote(VersionColumn.Name)}=@expected::bigint)";
        var returning = VersionColumn is null
            ? "(xmax = 0) AS \"__groundwork_inserted\", NULL::bigint AS \"__groundwork_version\""
            : $"(xmax = 0) AS \"__groundwork_inserted\", {Quote(VersionColumn.Name)} AS \"__groundwork_version\"";
        var sql = $"INSERT INTO {Quote(Unit.Name)} ({string.Join(", ", columns.Select(Quote))}) " +
                  $"{source} ON CONFLICT {conflict} DO UPDATE SET {string.Join(", ", updates)}{actionPredicate} " +
                  $"RETURNING {returning};";
        using var command = Command(sql);
        AddParameters(command, physical);
        foreach (var pair in keyParameters)
            if (!physical.ContainsKey(pair.Key.TrimStart('@')))
                Add(command, pair.Key.TrimStart('@'), pair.Value);
        if (VersionColumn is not null)
            Add(command, "expected", options?.ExpectedVersion);
        options?.Observer?.Observe(new WritePathEvent("postgresql.conditional-upsert", sql, IsProbe: false));
        try
        {
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return DeferredConflict(key, options?.Observer);

            var inserted = reader.GetBoolean(0);
            var versionOrdinal = 1;
            var version = reader.IsDBNull(versionOrdinal) ? (long?)null : reader.GetInt64(versionOrdinal);
            return new WriteOutcome(inserted ? WriteOutcomeStatus.Inserted : WriteOutcomeStatus.Updated, version);
        }
        catch (DbException exception) when (new PostgreSqlDialect().TryMapUniqueViolation(exception, out var indexName))
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

    private string? LogicalIndexName(string? physicalName)
    {
        if (string.IsNullOrWhiteSpace(physicalName))
            return physicalName;

        return Unit.Indexes.FirstOrDefault(index =>
            string.Equals(
                PostgreSqlDialect.PhysicalIndexName(Unit.Name, index.Name),
                physicalName,
                StringComparison.Ordinal))?.Name ?? physicalName;
    }

    private WriteOutcome InsertCore(StorageValues values, WriteOutcomeStatus status = WriteOutcomeStatus.Inserted)
    {
        var physical = PhysicalValues(values.Values, includeVersion: VersionColumn is not null);
        var columns = physical.Keys.ToArray();
        var returning = SequenceColumn is null ? string.Empty : $" RETURNING {Quote(SequenceColumn.Name)};";
        using var command = Command($"INSERT INTO {Quote(Unit.Name)} ({string.Join(", ", columns.Select(Quote))}) VALUES ({string.Join(", ", columns.Select(column => "@" + column))}){returning}");
        AddParameters(command, physical);
        try
        {
            if (SequenceColumn is null)
            {
                command.ExecuteNonQuery();
                return new WriteOutcome(status, VersionColumn is null ? null : 1);
            }

            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return new WriteOutcome(WriteOutcomeStatus.UniqueViolation);
            return new WriteOutcome(
                status,
                VersionColumn is null ? null : 1,
                generatedValues: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [SequenceColumn.Name] = FromDatabase(reader.GetValue(0), SequenceColumn)
                });
        }
        catch (DbException exception) when (new PostgreSqlDialect().TryMapUniqueViolation(exception, out _))
        {
            return new WriteOutcome(WriteOutcomeStatus.UniqueViolation);
        }
    }

    private WriteOutcome UpdateCore(
        StorageValues values,
        StorageKey key,
        StoredEntry existing,
        WriteOptions? options)
    {
        var supplied = UserColumns.Where(column => values.Values.ContainsKey(column.Name) &&
            !Unit.Key.Columns.Contains(column.Name, StringComparer.Ordinal)).ToArray();
        var sets = supplied.Select(column => $"{Quote(column.Name)}=@{column.Name}").ToList();
        var parameters = PhysicalValues(values.Values, includeVersion: false);
        var (where, keyParameters) = KeyPredicate(key.Values);
        foreach (var pair in keyParameters) parameters[pair.Key] = pair.Value;
        if (VersionColumn is not null)
        {
            sets.Add($"{Quote(VersionColumn.Name)}={Quote(VersionColumn.Name)}+1");
            where += $" AND {Quote(VersionColumn.Name)}=@expected";
            parameters["@expected"] = options!.ExpectedVersion!.Value;
        }
        if (sets.Count == 0)
            return new WriteOutcome(WriteOutcomeStatus.Updated, existing.Version);
        using var command = Command($"UPDATE {Quote(Unit.Name)} SET {string.Join(", ", sets)} WHERE {where};");
        AddParameters(command, parameters);
        if (command.ExecuteNonQuery() == 0)
            return new WriteOutcome(WriteOutcomeStatus.ConcurrencyConflict, existing.Version);
        return new WriteOutcome(WriteOutcomeStatus.Updated,
            VersionColumn is null ? null : existing.Version + 1);
    }

    private WriteOutcome UpsertCore(
        StorageValues values,
        StorageKey key,
        StoredEntry? existing,
        WriteOptions? options,
        bool exactOutcome)
    {
        var physical = PhysicalValues(values.Values, includeVersion: VersionColumn is not null);
        var columns = physical.Keys.ToArray();
        var updateColumns = columns.Where(column =>
            !Unit.Key.Columns.Contains(column, StringComparer.Ordinal) &&
            column != PostgreSqlSchemaCoordinator.ScopeColumn &&
            column != "createdAt" &&
            column != PostgreSqlSchemaCoordinator.VersionColumn).ToArray();
        var conflictPredicate = PartialKeyPredicate();
        var conflict = $"({string.Join(", ", Unit.Key.Columns.Select(Quote))})" +
            (conflictPredicate is null ? string.Empty : $" WHERE {conflictPredicate}");
        var updates = new List<string>(updateColumns.Select(column =>
            $"{Quote(column)}=EXCLUDED.{Quote(column)}"));
        if (VersionColumn is not null)
            updates.Add($"{Quote(VersionColumn.Name)}={Quote(Unit.Name)}.{Quote(VersionColumn.Name)}+1");
        var action = updates.Count == 0
            ? "DO NOTHING"
            : "DO UPDATE SET " + string.Join(", ", updates) +
              (exactOutcome && VersionColumn is not null ? $" WHERE {Quote(Unit.Name)}.{Quote(VersionColumn.Name)}=@expected" : string.Empty);
        var returning = exactOutcome
            ? " RETURNING (xmax = 0) AS \"__groundwork_inserted\", " +
              (VersionColumn is null ? "NULL::bigint" : Quote(VersionColumn.Name)) + " AS \"__groundwork_version\""
            : string.Empty;
        var sql = $"INSERT INTO {Quote(Unit.Name)} ({string.Join(", ", columns.Select(Quote))}) VALUES ({string.Join(", ", columns.Select(column => "@" + column))}) ON CONFLICT {conflict} {action}{returning};";
        using var command = Command(sql);
        AddParameters(command, physical);
        if (exactOutcome && VersionColumn is not null)
            Add(command, "expected", options?.ExpectedVersion);
        try
        {
            if (!exactOutcome)
            {
                command.ExecuteNonQuery();
                return new WriteOutcome(WriteOutcomeStatus.Upserted,
                    VersionColumn is null ? null : existing is null ? 1 : existing.Version + 1);
            }

            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return new WriteOutcome(WriteOutcomeStatus.ConcurrencyConflict, existing?.Version);
            var inserted = reader.GetBoolean(0);
            var version = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
            return new WriteOutcome(inserted ? WriteOutcomeStatus.Inserted : WriteOutcomeStatus.Updated, version);
        }
        catch (DbException exception) when (new PostgreSqlDialect().TryMapUniqueViolation(exception, out _))
        {
            return new WriteOutcome(WriteOutcomeStatus.UniqueViolation, existing?.Version);
        }
    }

    private StoredEntry? ReadCore(StorageKey key, IWritePathObserver? observer = null)
    {
        var (where, parameters) = KeyPredicate(key.Values);
        var columns = UserColumns.Concat(VersionColumn is null ? [] : [VersionColumn]).ToArray();
        using var command = Command($"SELECT {string.Join(", ", columns.Select(column => Quote(column.Name)))} FROM {Quote(Unit.Name)} WHERE {where};");
        AddParameters(command, parameters);
        observer?.Observe(new WritePathEvent("postgresql.write-probe", command.CommandText, IsProbe: true));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var index = 0; index < UserColumns.Count; index++)
            values[UserColumns[index].Name] = FromDatabase(reader.GetValue(index), UserColumns[index]);
        var version = VersionColumn is null ? (long?)null : reader.GetInt64(UserColumns.Count);
        return new StoredEntry(new StorageValues(values), version);
    }

    private StorageKey KeyFromValues(IReadOnlyDictionary<string, object?> values) =>
        new(Unit.Key.Columns.Where(column => column != PostgreSqlSchemaCoordinator.ScopeColumn)
            .ToDictionary(column => column, column => values.TryGetValue(column, out var value)
                ? value
                : throw new ArgumentException($"Key column '{column}' is required.", nameof(values)), StringComparer.Ordinal));

    private (string Predicate, Dictionary<string, object?> Parameters) KeyPredicate(IReadOnlyDictionary<string, object?> values)
    {
        var clauses = new List<string>();
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var column in Unit.Key.Columns)
        {
            object? value;
            string parameter;
            if (column == PostgreSqlSchemaCoordinator.ScopeColumn)
            {
                value = Access.Scope!.Value;
                parameter = "@__groundwork_scope";
            }
            else
            {
                value = values.TryGetValue(column, out var supplied)
                    ? supplied
                    : throw new ArgumentException($"Key column '{column}' is required.", nameof(values));
                parameter = "@key_" + column;
            }
            clauses.Add($"{Quote(column)}={parameter}");
            parameters[parameter] = ConvertValue(value, Column(column));
        }
        return (string.Join(" AND ", clauses), parameters);
    }

    private Dictionary<string, object?> PhysicalValues(
        IReadOnlyDictionary<string, object?> values,
        bool includeVersion)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var column in UserColumns)
            if (values.TryGetValue(column.Name, out var value))
                result[column.Name] = ConvertValue(value, column);
        if (Unit.Scope == ScopePolicy.Scoped)
            result[PostgreSqlSchemaCoordinator.ScopeColumn] = Access.Scope!.Value;
        if (includeVersion)
            result[PostgreSqlSchemaCoordinator.VersionColumn] = 1L;
        return result;
    }

    private string? PartialKeyPredicate()
    {
        var index = Unit.Indexes.FirstOrDefault(index => index.IsUnique &&
            index.MissingValues == MissingValueBehavior.Excluded &&
            index.Columns.Select(column => column.Column).SequenceEqual(Unit.Key.Columns, StringComparer.Ordinal));
        return index is null ? null : new PostgreSqlDialect().IndexFilter(index);
    }

    private void ValidateValues(IReadOnlyDictionary<string, object?> values, bool requireAllNonNullable)
    {
        var known = UserColumns.Select(column => column.Name).ToHashSet(StringComparer.Ordinal);
        var unknown = values.Keys.FirstOrDefault(key => !known.Contains(key));
        if (unknown is not null)
            throw new ArgumentException($"Column '{unknown}' is not declared by '{Unit.Name}'.", nameof(values));
        foreach (var generated in UserColumns.Where(column => column.Generation == ColumnGeneration.ProviderSequence))
            if (values.ContainsKey(generated.Name))
                throw new ArgumentException($"ProviderSequence column '{generated.Name}' is assigned by PostgreSQL and cannot be supplied or updated.", nameof(values));
        if (!requireAllNonNullable)
            return;
        foreach (var column in UserColumns.Where(column => !column.IsNullable && column.Default is null))
        {
            if (column.Generation == ColumnGeneration.ProviderSequence)
                continue;
            if (!values.TryGetValue(column.Name, out var value) || value is null)
                throw new ArgumentException($"Non-nullable column '{column.Name}' is required.", nameof(values));
        }
    }

    private void ValidateExpected(WriteOptions? options, StoredEntry? existing, Mutation mutation)
    {
        if (options?.ExpectedVersion is not null && VersionColumn is null)
            throw new InvalidOperationException($"Storage unit '{Unit.Name}' does not declare version machinery.");
        if (VersionColumn is null)
            return;
        if (mutation == Mutation.Insert)
        {
            if (existing is null && options?.ExpectedVersion is not null)
                throw new ConcurrencyConflictException();
            return;
        }
        if (existing is null ? options?.ExpectedVersion is not null : options?.ExpectedVersion != existing.Version)
            throw new ConcurrencyConflictException(existing?.Version);
    }

    private T Execute<T>(Func<T> operation)
    {
        try
        {
            ThrowIfClosed();
            return operation();
        }
        catch (ConcurrencyConflictException exception) when (typeof(T) == typeof(WriteOutcome))
        {
            return (T)(object)new WriteOutcome(WriteOutcomeStatus.ConcurrencyConflict, exception.Version);
        }
    }

    private T ExecuteWrite<T>(Func<T> operation)
    {
        ThrowIfClosed();
        if (transaction is not null)
            return Translate(operation);
        using var write = connection.BeginTransaction(IsolationLevel.ReadCommitted);
        activeTransaction = write;
        try
        {
            var result = Translate(operation);
            write.Commit();
            return result;
        }
        catch
        {
            write.Rollback();
            throw;
        }
        finally
        {
            activeTransaction = null;
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

    private NpgsqlCommand Command(string sql)
    {
        ThrowIfClosed();
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = activeTransaction ?? transaction;
        return command;
    }

    private void AddParameters(NpgsqlCommand command, IReadOnlyDictionary<string, object?> parameters)
    {
        foreach (var pair in parameters)
            Add(command, pair.Key.TrimStart('@'), pair.Value);
    }

    private void Add(NpgsqlCommand command, string name, object? value)
    {
        var parameter = command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        if (name == "expected")
            parameter.NpgsqlDbType = NpgsqlDbType.Bigint;
        if (Unit.Columns.FirstOrDefault(column => column.Name == name)?.Type == PortableType.Json)
            parameter.NpgsqlDbType = NpgsqlDbType.Jsonb;
    }

    private static object? ConvertValue(object? value, ColumnDefinition definition) =>
        new PostgreSqlDialect().ConvertValue(value, definition);

    private static object? FromDatabase(object value, ColumnDefinition definition)
    {
        if (value is DBNull)
            return null;
        return definition.Type switch
        {
            PortableType.DateTimeOffset => new DateTimeOffset(Convert.ToInt64(value, CultureInfo.InvariantCulture), TimeSpan.Zero),
            PortableType.Json when value is string json => JsonDocument.Parse(json).RootElement.Clone(),
            PortableType.Json when value is JsonDocument document => document.RootElement.Clone(),
            PortableType.Json when value is JsonElement element => element.Clone(),
            PortableType.Binary when value is byte[] bytes => bytes.ToArray(),
            _ => value
        };
    }

    private ColumnDefinition Column(string name) => Unit.Columns.First(column => column.Name == name);

    private IReadOnlyList<ColumnDefinition> UserColumns =>
        Unit.Columns.Where(column => column.Name is not PostgreSqlSchemaCoordinator.ScopeColumn and not PostgreSqlSchemaCoordinator.VersionColumn).ToArray();

    private ColumnDefinition? SequenceColumn => UserColumns.FirstOrDefault(column =>
        column.Generation == ColumnGeneration.ProviderSequence);

    private ColumnDefinition? VersionColumn => Unit.Columns.FirstOrDefault(column => column.Name == PostgreSqlSchemaCoordinator.VersionColumn);

    private static string Quote(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private void ThrowIfClosed()
    {
        owner.ThrowIfDisposed();
        if (closed)
            throw new ObjectDisposedException(nameof(PostgreSqlStorageSession));
    }

    private enum Mutation
    {
        Insert,
        Update,
        Upsert,
        ConditionalUpsert,
        Delete
    }

    private sealed class ConcurrencyConflictException(long? version = null) : Exception
    {
        public long? Version { get; } = version;
    }
}
