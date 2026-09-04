using System.Globalization;
using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Groundwork.Kernel;
using Groundwork.Query.Model;
using Groundwork.Substrate.Relational;
using Groundwork.Store;
using Groundwork.Diagnostics;

namespace Groundwork.Sqlite;

internal class SqliteStorageSession : IStorageSession, IProviderBoundStorageSession, IExactAppendStorageSession, IConcurrencyStorageSession, ICompareAndDeleteStorageSession, IBatchedStorageSession, IRetentionStorageSession, IStorageInspectionSession, IExactRetentionStorageSession, IExactRetentionAffectedKeysStorageSession, IPrivilegedCrossScopeQuerySession, ISetMutationStorageSession
{
    private readonly SqliteProviderConnection owner;
    private readonly SqliteConnection connection;
    private readonly SqliteTransaction? transaction;
    private readonly RelationalSessionExecution execution;
    private readonly RelationalSessionPointReads pointReads;
    private readonly RelationalSessionCrud crud;
    private readonly RelationalSessionRetention retention;
    private readonly RelationalSessionQueries queries;
    private readonly RelationalSessionAggregations aggregations;
    private readonly RelationalSessionSetMutations setMutations;
    private readonly RelationalSessionAppends appends;
    private readonly SchemaSessionLease schemaSession;
    private readonly IReadOnlyList<ProviderIndex>? runtimeCatalogIndexes;

    /// <summary>
    /// True when opened through <c>OpenOwnedSession</c>, so disposal returns this session's connection.
    /// A view from <c>OpenSession</c> and a session from a unit of work both belong to someone else.
    /// </summary>
    private readonly bool ownsConnection;

    internal SqliteStorageSession(
        SqliteProviderConnection owner,
        StorageUnit unit,
        StorageAccess access,
        SqliteConnection connection,
        SqliteTransaction? transaction,
        SchemaSessionLease schemaSession,
        IProviderCommandObserver? observer = null,
        bool ownsConnection = false,
        IReadOnlyList<ProviderIndex>? runtimeCatalogIndexes = null)
    {
        this.ownsConnection = ownsConnection;
        this.runtimeCatalogIndexes = runtimeCatalogIndexes;
        commandObserver = observer;
        this.owner = owner;
        Unit = unit;
        Access = access;
        this.connection = connection;
        this.transaction = transaction;
        this.schemaSession = schemaSession;
        execution = new RelationalSessionExecution(
            access,
            transaction,
            ownsConnection,
            new SqliteSessionExecutionAdapter(owner, connection, schemaSession, RollbackOrRetire),
            nameof(SqliteStorageSession));
        pointReads = new RelationalSessionPointReads(
            unit,
            access,
            UserColumns,
            VersionColumnDefinition,
            Command,
            new SqlitePointReadAdapter(),
            observer,
            "sqlite");
        crud = new RelationalSessionCrud(
            unit,
            UserColumns,
            SequenceColumnDefinition,
            VersionColumnDefinition,
            "SQLite",
            (key, readExecution) => pointReads.Read(key, readExecution),
            new SqliteCrudAdapter(this));
        retention = new RelationalSessionRetention(
            unit,
            access,
            new SqliteRetentionAdapter(this));
        queries = new RelationalSessionQueries(
            unit,
            access,
            connection,
            new SqliteQueryRenderer(),
            PhysicalIndexNames,
            FromSqlite,
            (command, renderOptions, _) =>
            {
                AssertExplainPlan(command, renderOptions);
                return default;
            },
            observer,
            "sqlite");
        aggregations = new RelationalSessionAggregations(
            unit,
            access,
            connection,
            new SqliteDialect(),
            FromSqlite,
            observer,
            "sqlite.aggregate");
        setMutations = new RelationalSessionSetMutations(
            unit,
            access,
            new SqliteQueryRenderer(),
            unit.Columns.FirstOrDefault(column => column.Name == SqliteSchemaCoordinator.VersionColumn)?.Name,
            Command,
            (command, name, value, column) => ((SqliteCommand)command).Parameters.AddWithValue(
                "@" + name,
                ToSqlite(value, column) ?? DBNull.Value),
            observer,
            "sqlite");
        appends = new RelationalSessionAppends(unit, access, new SqliteAppendAdapter(this));
    }

    /// <summary>
    /// Counts every provider command this session issues. It belongs to the session because the session is
    /// what issues commands; it used to be read off an individual write's options, so a batch observed only
    /// whatever happened to be staged first.
    /// </summary>
    private readonly IProviderCommandObserver? commandObserver;

    public StorageUnit Unit { get; }
    public StorageAccess Access { get; }

    // Test-only visibility lets the provider integration suite prove that an owned file-backed session
    // returns its physical handle, rather than merely becoming unusable at the public surface.
    internal bool IsConnectionOpen => connection.State == ConnectionState.Open;

    IStorageProviderConnection IProviderBoundStorageSession.ProviderConnection => owner;

    IReadOnlyList<ProviderIndex>? IProviderBoundStorageSession.RuntimeCatalogIndexes => runtimeCatalogIndexes;

    /// <summary>Maps every declared logical index name to the physical name the catalog carries.</summary>
    private IReadOnlyDictionary<string, string> PhysicalIndexNames() => Unit.Indexes.ToDictionary(
        index => index.Name,
        index => SqliteDialect.PhysicalIndexName(Unit.Name, index.Name),
        StringComparer.Ordinal);

    public QueryMaterializedResult Query(QueryRequest request, QueryRenderOptions? options = null) => Execute(() =>
        queries.Query(request, options, execution.Transaction, RelationalExecution.Synchronous)
            .GetAwaiter().GetResult());

    /// <summary>
    /// Reads the page on the async ADO.NET surface so the token still interrupts the native
    /// statement mid-execution, inside the gate that serializes every session command.
    /// </summary>
    public ValueTask<QueryMaterializedResult> QueryAsync(
        QueryRequest request,
        QueryRenderOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Completed(cancellationToken, () => Execute(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return queries.Query(
                    request,
                    options,
                    execution.Transaction,
                    RelationalExecution.Asynchronous(cancellationToken))
                .GetAwaiter().GetResult();
        }));

    public ValueTask<StoredEntry?> ReadAsync(StorageKey key, CancellationToken cancellationToken = default) =>
        Completed(cancellationToken, () => Read(key));

    public ValueTask<CrossScopeQueryResult> QueryAcrossScopesAsync(
        QueryRequest request,
        QueryRenderOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Completed(cancellationToken, () => QueryAcrossScopes(request, options));

    public ValueTask<AggregationResult> AggregateAsync(
        AggregationQuery query,
        CancellationToken cancellationToken = default) =>
        Completed(cancellationToken, () => Aggregate(query));

    public ValueTask<WriteOutcome> InsertAsync(
        StorageValues values,
        WriteOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Completed(cancellationToken, () => Insert(values, options));

    public ValueTask<WriteOutcome> UpdateAsync(
        StorageValues values,
        WriteOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Completed(cancellationToken, () => Update(values, options));

    public ValueTask<WriteOutcome> UpsertAsync(
        StorageValues values,
        WriteOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Completed(cancellationToken, () => Upsert(values, options));

    public ValueTask<WriteOutcome> DeleteAsync(
        StorageKey key,
        WriteOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Completed(cancellationToken, () => Delete(key, options));

    public ValueTask<WriteOutcome> ConditionalUpsertAsync(
        StorageValues values,
        WriteOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Completed(cancellationToken, () => ConditionalUpsert(values, options));

    public ValueTask<WriteOutcome> CompareAndDeleteAsync(
        StorageKey key,
        IReadOnlyDictionary<string, object?> expectedValues,
        WriteOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Completed(cancellationToken, () => CompareAndDelete(key, expectedValues, options));

    public ValueTask<WriteOutcome> AppendAsync(
        OperationId operationId,
        IReadOnlyList<StorageValues> values,
        CancellationToken cancellationToken = default) =>
        Completed(cancellationToken, () => Append(operationId, values));

    public ValueTask<AppendOutcomeReport> AppendWithOutcomesAsync(
        OperationId operationId,
        IReadOnlyList<StorageValues> values,
        CancellationToken cancellationToken = default) =>
        Completed(cancellationToken, () => AppendWithOutcomes(operationId, values));

    public ValueTask<IReadOnlyList<RowWriteOutcome>> ApplyBatchAsync(
        IReadOnlyList<RowWrite> writes,
        bool exactOutcomes,
        CancellationToken cancellationToken = default) =>
        Completed(cancellationToken,
            () => ((IBatchedStorageSession)this).ApplyBatch(writes, exactOutcomes));

    public ValueTask<StorageInspection> InspectAsync(CancellationToken cancellationToken = default) =>
        Completed(cancellationToken, Inspect);

    public ValueTask<RetentionResult> ApplyRetentionAsync(RetentionExecutionOptions? options = null) =>
        Completed(options?.CancellationToken ?? CancellationToken.None, () => ApplyRetention(options));

    public ValueTask<RetentionOperationResult> ApplyRetentionAsync(
        OperationId operationId,
        RetentionExecutionOptions? options = null) =>
        Completed(options?.CancellationToken ?? CancellationToken.None,
            () => ApplyRetention(operationId, options));

    /// <summary>
    /// Microsoft.Data.Sqlite completes its asynchronous ADO.NET surface synchronously, and this
    /// provider serializes every session command on a gate that a suspended continuation cannot
    /// hold. The asynchronous surface therefore observes cancellation, runs the same gated body on
    /// the calling thread, and returns an already-completed task: it never yields the thread.
    /// </summary>
    private static ValueTask<T> Completed<T>(CancellationToken cancellationToken, Func<T> operation)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(operation());
    }

    public CrossScopeQueryResult QueryAcrossScopes(
        QueryRequest request,
        QueryRenderOptions? options = null) => Execute(() =>
            queries.QueryAcrossScopes(request, options, RelationalExecution.Synchronous)
                .GetAwaiter().GetResult());

    public AggregationResult Aggregate(AggregationQuery query) => Execute(() =>
        aggregations.Aggregate(
                query,
                execution.Transaction,
                RelationalExecution.Synchronous)
            .GetAwaiter().GetResult());

    private void AssertExplainPlan(RelationalQueryCommand query, QueryRenderOptions options)
    {
        if (query.IsMatchNone || !ExplainAssertionMode.ShouldAssert(query.SelectedIndex)) return;
        var logicalIndex = query.SelectedIndex!;
        var physicalIndex = options.ResolvePhysicalIndexName(logicalIndex);
        var plans = new List<string>(query.Statements.Length);
        foreach (var statement in query.Statements)
        {
            using var explain = Command("EXPLAIN QUERY PLAN " + statement.TrimEnd().TrimEnd(';'));
            RelationalQueryResultReader.AddParameters(explain, query);
            using var reader = explain.ExecuteReader();
            var details = new List<string>();
            while (reader.Read())
                details.Add(string.Join('\t', Enumerable.Range(0, reader.FieldCount).Select(index => Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture))));
            plans.Add(string.Join(Environment.NewLine, details));
        }
        var rawPlan = string.Join(Environment.NewLine, plans);
        ExplainAssertionMode.AssertChosenIndex(
            "SQLite", logicalIndex, physicalIndex, query.IndexHintApplied, rawPlan,
            plans.All(plan => SqliteExplainPlanInspector.ChoseIndex(plan, physicalIndex)));
    }

    public StoredEntry? Read(StorageKey key)
    {
        pointReads.ValidatePublicRead();
        return Execute(() => pointReads.ReadPublic(key, RelationalExecution.Synchronous)
            .GetAwaiter().GetResult());
    }

    public WriteOutcome Insert(StorageValues values, WriteOptions? options = null)
    {
        return Mutate(crud.PrepareMutation(values, options, RelationalCrudKind.Insert));
    }

    public WriteOutcome Update(StorageValues values, WriteOptions? options = null)
    {
        return Mutate(crud.PrepareMutation(values, options, RelationalCrudKind.Update));
    }

    public WriteOutcome Upsert(StorageValues values, WriteOptions? options = null)
    {
        return Mutate(crud.PrepareMutation(values, options, RelationalCrudKind.Upsert));
    }

    public WriteOutcome ConditionalUpsert(StorageValues values, WriteOptions? options = null)
    {
        WritePreconditionValidator.ValidateWrittenValues(Unit, values.Values);
        WritePreconditionValidator.Validate(Unit, WriteOperation.ConditionalUpsert, options);
        var onAppend = Unit.Retention?.Trigger == RetentionTrigger.OnAppend;
        var registration = BeginOnAppend(onAppend);
        WriteOutcome outcome;
        try
        {
            outcome = Execute(() => ConditionalUpsertCore(values, options));
        }
        catch
        {
            CompleteOnAppend(registration, cleanupRequired: false);
            throw;
        }
        CompleteOnAppend(registration, onAppend && outcome.Status == WriteOutcomeStatus.Inserted);
        return outcome;
    }

    public IReadOnlyList<RowWriteOutcome> ApplyBatch(IReadOnlyList<RowWrite> writes)
    {
        var nativeOnAppend = IsNativeAppendBatch(writes);
        var registration = BeginOnAppend(nativeOnAppend);
        IReadOnlyList<RowWriteOutcome> outcomes;
        try
        {
            outcomes = ExecuteWrite(() => ApplyBatchCore(writes));
        }
        catch
        {
            CompleteOnAppend(registration, cleanupRequired: false);
            throw;
        }
        var succeeded = nativeOnAppend && OnAppendRetentionCoordinator.ContainsAppend(outcomes);
        CompleteOnAppend(registration, succeeded);
        return outcomes;
    }

    public WriteOutcome Delete(StorageKey key, WriteOptions? options = null)
    {
        var operation = crud.PrepareDelete(key, options);
        return ExecuteWrite(() => crud.Delete(
                operation,
                RelationalExecution.Synchronous)
            .GetAwaiter().GetResult());
    }

    public WriteOutcome CompareAndDelete(
        StorageKey key,
        IReadOnlyDictionary<string, object?> expectedValues,
        WriteOptions? options = null)
    {
        var canonicalKey = CompareAndDeleteValidation.CanonicalizeKey(Unit, key);
        var expected = CompareAndDeleteValidation.Validate(Unit, canonicalKey, expectedValues, options);
        return ExecuteWrite(() =>
        {
            var (where, parameters) = KeyPredicate(canonicalKey.Values);
            foreach (var pair in expected)
            {
                if (pair.Value is null)
                {
                    where += $" AND {Quote(pair.Key)} IS NULL";
                }
                else
                {
                    var parameter = "@compare_" + pair.Key;
                    where += $" AND {Quote(pair.Key)}={parameter}";
                    parameters[parameter] = ToSqlite(pair.Value, Column(pair.Key));
                }
            }
            if (VersionColumnDefinition is not null && options?.Precondition.Kind == WritePreconditionKind.IfVersion)
            {
                where += $" AND {Quote(VersionColumnDefinition.Name)}=@expected";
                parameters["@expected"] = options.Precondition.Version!.Value;
            }

            var returning = VersionColumnDefinition is null ? string.Empty : $" RETURNING {Quote(VersionColumnDefinition.Name)};";
            using var command = Command($"DELETE FROM {Quote(Unit.Name)} WHERE {where}{returning}");
            AddParameters(command, parameters);
            commandObserver?.Observe(new ProviderCommandEvent("sqlite.compare-and-delete", command.CommandText, ProviderCommandKind.Write, IsProbe: false));
            if (VersionColumnDefinition is not null)
            {
                using (var reader = command.ExecuteReader())
                {
                    if (reader.Read())
                        return new WriteOutcome(WriteOutcomeStatus.Deleted, Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture));
                }
            }
            else if (command.ExecuteNonQuery() != 0)
            {
                return new WriteOutcome(WriteOutcomeStatus.Deleted);
            }

            var existing = ReadCore(canonicalKey, "sqlite.compare-and-delete-read");
            if (existing is null)
                return new WriteOutcome(WriteOutcomeStatus.NotFound);
            if (options?.Precondition.Kind == WritePreconditionKind.IfVersion &&
                options.Precondition.Version != existing.Version)
                return new WriteOutcome(WriteOutcomeStatus.ConcurrencyConflict, existing.Version);
            return RelationalSessionPolicy.MatchesExpected(Unit, existing, expected)
                ? new WriteOutcome(WriteOutcomeStatus.ConcurrencyConflict, existing.Version)
                : new WriteOutcome(WriteOutcomeStatus.ComparisonMismatch, existing.Version);
        });
    }

    public SetMutationResult UpdateWhere(Predicate where, IReadOnlyDictionary<string, object?> assignments)
    {
        var operation = setMutations.PrepareUpdateWhere(where, assignments);
        return ExecuteWrite(() => operation(RelationalExecution.Synchronous).GetAwaiter().GetResult());
    }

    public ValueTask<SetMutationResult> UpdateWhereAsync(
        Predicate where,
        IReadOnlyDictionary<string, object?> assignments,
        CancellationToken cancellationToken = default) =>
        Completed(cancellationToken, () => UpdateWhere(where, assignments));

    public SetMutationResult DeleteWhere(Predicate where)
    {
        var operation = setMutations.PrepareDeleteWhere(where);
        return ExecuteWrite(() => operation(RelationalExecution.Synchronous).GetAwaiter().GetResult());
    }

    public ValueTask<SetMutationResult> DeleteWhereAsync(
        Predicate where,
        CancellationToken cancellationToken = default) =>
        Completed(cancellationToken, () => DeleteWhere(where));

    public RetentionResult ApplyRetention(RetentionExecutionOptions? options = null)
    {
        var operation = retention.Prepare(options);
        return ExecuteWrite(() => retention.Apply(operation, RelationalExecution.Synchronous)
            .GetAwaiter().GetResult());
    }

    public StorageInspection Inspect() => Execute(() =>
    {
        StorageAccessValidation.EnsurePointOperation(Access, "inspect");
        StorageInspectionSessionExtensions.EnsureProviderSequence(Unit);
        EnsureHighWaterTable();
        using var command = Command($"SELECT {Quote(HighWaterValue)} FROM {Quote(HighWaterTable)} WHERE {Quote(LedgerUnit)}=@unit AND {Quote(LedgerScope)}=@scope;");
        command.Parameters.AddWithValue("@unit", Unit.Id.Value);
        command.Parameters.AddWithValue("@scope", Access.Scope?.Value ?? string.Empty);
        var value = command.ExecuteScalar();
        return value is null or DBNull
            ? new StorageInspection(null)
            : new StorageInspection(Convert.ToInt64(value, CultureInfo.InvariantCulture));
    });

    public RetentionOperationResult ApplyRetention(OperationId operationId, RetentionExecutionOptions? options = null)
    {
        var operation = retention.PrepareExact(operationId, options);
        return ExecuteWrite(() => retention.ApplyExact(operation, RelationalExecution.Synchronous)
            .GetAwaiter().GetResult());
    }

    private RetentionResult ApplyRetentionCore(RetentionExecutionOptions options)
        => retention.Apply(
                retention.Prepare(options),
                RelationalExecution.Synchronous)
            .GetAwaiter().GetResult();

    public WriteOutcome Append(OperationId operationId, IReadOnlyList<StorageValues> values)
    {
        var operation = appends.Prepare(operationId, values, exactOutcomes: false);
        var onAppend = Unit.Retention?.Trigger == RetentionTrigger.OnAppend;
        var registration = BeginOnAppend(onAppend);
        RelationalAppendResult result;
        try
        {
            result = ExecuteWrite(() => appends.Append(operation, RelationalExecution.Synchronous)
                .GetAwaiter().GetResult());
        }
        catch
        {
            CompleteOnAppend(registration, cleanupRequired: false);
            throw;
        }
        CompleteOnAppend(registration, onAppend && result.Status is WriteOutcomeStatus.Inserted or WriteOutcomeStatus.Replayed);
        return new WriteOutcome(result.Status);
    }

    public AppendOutcomeReport AppendWithOutcomes(OperationId operationId, IReadOnlyList<StorageValues> values)
    {
        var operation = appends.Prepare(operationId, values, exactOutcomes: true);
        var onAppend = Unit.Retention?.Trigger == RetentionTrigger.OnAppend;
        var registration = BeginOnAppend(onAppend);
        AppendOutcomeReport outcome;
        try
        {
            outcome = ExecuteWrite(() => appends.Append(operation, RelationalExecution.Synchronous)
                .GetAwaiter().GetResult().ToReport());
        }
        catch
        {
            CompleteOnAppend(registration, cleanupRequired: false);
            throw;
        }
        CompleteOnAppend(
            registration,
            onAppend && outcome.Status is WriteOutcomeStatus.Inserted or WriteOutcomeStatus.Replayed);
        return outcome;
    }

    private IReadOnlyList<RowWriteOutcome> InsertAppendSequenceBatch(IReadOnlyList<RowWrite> writes)
    {
        ArgumentNullException.ThrowIfNull(writes);
        if (writes.Count == 0)
            return [];

        var prepared = writes.Select(write =>
        {
            var values = new StorageValues(SearchKeyProjection.Populate(Unit, write.Values!.Values));
            RelationalSessionPolicy.ValidateValues(Unit, UserColumns, "SQLite", values.Values, requireAllNonNullable: true);
            return (Write: write, Values: values);
        }).ToArray();
        var outcomes = new List<RowWriteOutcome>(writes.Count);
        EnsureSequenceRow();
        long? highWater = null;

        for (var start = 0; start < prepared.Length;)
        {
            var columns = SequencePhysicalColumns(prepared[start].Values);
            var end = start + 1;
            while (end < prepared.Length &&
                   SequencePhysicalColumns(prepared[end].Values).Select(column => column.Name)
                       .SequenceEqual(columns.Select(column => column.Name), StringComparer.Ordinal))
                end++;

            var maxRows = Math.Max(1, Math.Min(1_000, SqliteVariableLimit() / (columns.Count + 1)));
            foreach (var chunk in prepared[start..end].Chunk(maxRows))
            {
                var result = InsertAppendSequenceBatchChunk(chunk, columns);
                outcomes.AddRange(result.Outcomes);
                if (result.HighWater is { } value)
                    highWater = highWater is null ? value : Math.Max(highWater.Value, value);
                if (result.HighWater is null)
                    return outcomes;
            }

            start = end;
        }

        if (highWater is { } finalHighWater)
            RecordHighWaterBatch(finalHighWater);
        return outcomes;
    }

    private (long? HighWater, IReadOnlyList<RowWriteOutcome> Outcomes) InsertAppendSequenceBatchChunk(
        IReadOnlyList<(RowWrite Write, StorageValues Values)> writes,
        IReadOnlyList<ColumnDefinition> columns)
    {
        // AUTOINCREMENT is backed by sqlite_sequence. Reserve a range while this write transaction
        // holds SQLite's writer lock, then insert explicit IDs so each input ordinal has a stable key.
        var count = writes.Count;
        var last = AllocateSequenceRange(count);
        var first = checked(last - count + 1L);
        var insertColumns = new[] { SequenceColumnDefinition! }.Concat(columns).ToArray();
        var valuesSql = new List<string>(count);
        using var command = Command(string.Empty);
        for (var row = 0; row < count; row++)
        {
            var parameters = new List<string>(insertColumns.Length);
            foreach (var column in insertColumns)
            {
                var name = $"@r{row}_{column.Name}";
                parameters.Add(name);
                var value = column == SequenceColumnDefinition
                    ? checked(first + row)
                    : column.Name == SqliteSchemaCoordinator.VersionColumn
                        ? 1L
                        : column.Name == SqliteSchemaCoordinator.ScopeColumn
                            ? Access.Scope!.Value
                            : ToSqlite(writes[row].Values.Values[column.Name], column) ?? DBNull.Value;
                command.Parameters.AddWithValue(name, value);
            }
            valuesSql.Add($"({string.Join(", ", parameters)})");
        }

        command.CommandText = $"INSERT INTO {Quote(Unit.Name)} ({string.Join(", ", insertColumns.Select(column => Quote(column.Name)))}) VALUES {string.Join(", ", valuesSql)};";
        commandObserver?.Observe(new ProviderCommandEvent("sqlite.generated-sequence-batch", "SQLite correlated generated-sequence INSERT", ProviderCommandKind.Write, IsProbe: false));
        try
        {
            if (command.ExecuteNonQuery() != count)
                return (null, writes.Select(write => new RowWriteOutcome(
                    write.Write,
                    new WriteOutcome(WriteOutcomeStatus.UniqueViolation))).ToArray());
            return (last, writes.Select((write, index) => new RowWriteOutcome(
                write.Write,
                new WriteOutcome(
                    WriteOutcomeStatus.Inserted,
                    VersionColumnDefinition is null ? null : 1,
                    generatedValues: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        [SequenceColumnDefinition!.Name] = checked(last - count + index + 1L)
                    }))).ToArray());
        }
        catch (SqliteException exception) when (new SqliteDialect().TryMapUniqueViolation(exception, out var indexName))
        {
            return (null, writes.Select(write => new RowWriteOutcome(
                write.Write,
                new WriteOutcome(WriteOutcomeStatus.UniqueViolation, null, LogicalIndexName(indexName)))).ToArray());
        }
    }

    private void EnsureSequenceRow()
    {
        using var command = Command($"INSERT INTO {Quote("sqlite_sequence")} ({Quote("name")}, {Quote("seq")}) " +
            $"SELECT @name, 0 WHERE NOT EXISTS (SELECT 1 FROM {Quote("sqlite_sequence")} WHERE {Quote("name")}=@name);");
        command.Parameters.AddWithValue("@name", Unit.Name);
        commandObserver?.Observe(new ProviderCommandEvent("sqlite.generated-sequence-seed", "SQLite generated-sequence seed", ProviderCommandKind.Write, IsProbe: false));
        command.ExecuteNonQuery();
    }

    private long AllocateSequenceRange(int count)
    {
        using var command = Command($"UPDATE {Quote("sqlite_sequence")} SET {Quote("seq")}={Quote("seq")}+@count " +
            $"WHERE {Quote("name")}=@name RETURNING {Quote("seq")};");
        command.Parameters.AddWithValue("@name", Unit.Name);
        command.Parameters.AddWithValue("@count", count);
        commandObserver?.Observe(new ProviderCommandEvent("sqlite.generated-sequence-allocation", "SQLite generated-sequence range allocation", ProviderCommandKind.Write, IsProbe: false));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException($"SQLite generated-sequence metadata for '{Unit.Name}' was not available.");
        return Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
    }

    private IReadOnlyList<ColumnDefinition> SequencePhysicalColumns(StorageValues values)
    {
        var columns = UserColumns.Where(column => values.Values.ContainsKey(column.Name)).ToList();
        if (VersionColumnDefinition is not null)
            columns.Add(VersionColumnDefinition);
        if (ScopeColumnDefinition is not null)
            columns.Add(ScopeColumnDefinition);
        return columns;
    }

    private void RecordHighWaterBatch(long generatedValue)
    {
        if (SequenceColumnDefinition is null)
            return;
        EnsureHighWaterTable();
        using var command = Command($"INSERT INTO {Quote(HighWaterTable)} ({Quote(LedgerUnit)}, {Quote(LedgerScope)}, {Quote(HighWaterValue)}) VALUES (@unit, @scope, @value) ON CONFLICT ({Quote(LedgerUnit)}, {Quote(LedgerScope)}) DO UPDATE SET {Quote(HighWaterValue)}=MAX({Quote(HighWaterTable)}.{Quote(HighWaterValue)}, excluded.{Quote(HighWaterValue)});");
        command.Parameters.AddWithValue("@unit", Unit.Id.Value);
        command.Parameters.AddWithValue("@scope", Access.Scope?.Value ?? string.Empty);
        command.Parameters.AddWithValue("@value", generatedValue);
        commandObserver?.Observe(new ProviderCommandEvent("sqlite.generated-sequence-high-water", "SQLite generated-sequence high-water", ProviderCommandKind.Write, IsProbe: false));
        command.ExecuteNonQuery();
    }

    private void EnsureLedgerTable(string table)
    {
        using var command = Command($"CREATE TABLE IF NOT EXISTS {Quote(table)} (" +
            $"{Quote(LedgerUnit)} TEXT NOT NULL, " +
            $"{Quote(LedgerScope)} TEXT NOT NULL, " +
            $"{Quote(LedgerNonce)} TEXT NOT NULL, " +
            $"{Quote(LedgerCommittedAt)} TEXT NOT NULL, " +
            $"{Quote(LedgerFingerprint)} TEXT NULL, " +
            $"{Quote(LedgerResult)} TEXT NULL, " +
            $"PRIMARY KEY ({Quote(LedgerUnit)}, {Quote(LedgerScope)}, {Quote(LedgerNonce)}));");
        command.ExecuteNonQuery();

        EnsureLedgerColumn(table, LedgerFingerprint);
        EnsureLedgerColumn(table, LedgerResult);

        using var cleanupIndex = Command($"CREATE INDEX IF NOT EXISTS {Quote(IdempotencyRules.CleanupIndexName(table))} " +
            $"ON {Quote(table)} ({Quote(LedgerUnit)}, {Quote(LedgerCommittedAt)});");
        cleanupIndex.ExecuteNonQuery();
    }

    private void EnsureLedgerColumn(string table, string column)
    {
        var exists = false;
        using (var columns = Command($"PRAGMA table_info({Quote(table)});"))
        using (var reader = columns.ExecuteReader())
        {
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal))
                {
                    exists = true;
                    break;
                }
            }
        }
        if (exists)
            return;

        using var alter = Command($"ALTER TABLE {Quote(table)} ADD COLUMN {Quote(column)} TEXT NULL;");
        alter.ExecuteNonQuery();
    }

    private void EnsureHighWaterTable()
    {
        using var command = Command($"CREATE TABLE IF NOT EXISTS {Quote(HighWaterTable)} (" +
            $"{Quote(LedgerUnit)} TEXT NOT NULL, " +
            $"{Quote(LedgerScope)} TEXT NOT NULL, " +
            $"{Quote(HighWaterValue)} INTEGER NOT NULL, " +
            $"PRIMARY KEY ({Quote(LedgerUnit)}, {Quote(LedgerScope)}));");
        command.ExecuteNonQuery();
    }

    private void RecordHighWater(object? generatedValue)
    {
        if (SequenceColumnDefinition is null || generatedValue is null)
            return;
        EnsureHighWaterTable();
        using var command = Command($"INSERT INTO {Quote(HighWaterTable)} ({Quote(LedgerUnit)}, {Quote(LedgerScope)}, {Quote(HighWaterValue)}) VALUES (@unit, @scope, @value) ON CONFLICT ({Quote(LedgerUnit)}, {Quote(LedgerScope)}) DO UPDATE SET {Quote(HighWaterValue)}=MAX({Quote(HighWaterTable)}.{Quote(HighWaterValue)}, excluded.{Quote(HighWaterValue)});");
        command.Parameters.AddWithValue("@unit", Unit.Id.Value);
        command.Parameters.AddWithValue("@scope", Access.Scope?.Value ?? string.Empty);
        command.Parameters.AddWithValue("@value", Convert.ToInt64(generatedValue, CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private static void AddLedgerParameters(SqliteCommand command, string unit, string scope, string nonce)
    {
        command.Parameters.AddWithValue("@unit", unit);
        command.Parameters.AddWithValue("@scope", scope);
        command.Parameters.AddWithValue("@nonce", nonce);
    }

    private static string FormatLedgerTime(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private DateTimeOffset ProviderNow()
    {
        using var command = Command("SELECT strftime('%Y-%m-%dT%H:%M:%fZ', 'now');");
        return DateTimeOffset.Parse(
            Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture)!,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }

    internal void Close() => execution.Close();

    public bool IsReleased => execution.IsReleased;

    public void Dispose()
    {
        if (execution.IsReleased)
            return;
        execution.Close();
        if (ownsConnection)
            connection.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (execution.IsReleased)
            return;
        execution.Close();
        if (ownsConnection)
            await connection.DisposeAsync().ConfigureAwait(false);
    }

    private IReadOnlyList<RowWriteOutcome> ApplyBatchCore(IReadOnlyList<RowWrite> writes)
    {
        ArgumentNullException.ThrowIfNull(writes);
        if (writes.Count == 0)
            return [];
        if (SequenceColumnDefinition is not null)
            return ApplyBatchFallback(writes);

        // Non-unconditional writes and deletes need their per-row predicates and
        // conflict details. Keep those semantics exact; unconditional inserts and
        // upserts are the provider-native multi-row path.
        if (writes.Any(write => write.Options.Precondition.Kind != WritePreconditionKind.Unconditional))
            return ApplyBatchFallback(writes);
        if (RelationalSessionPolicy.HasSecondaryUniqueIndex(writes[0].Unit))
            return ApplyBatchFallback(writes);
        var physicalWrites = writes.Select(write => write.PopulateSearchKeyValues()).ToArray();

        return physicalWrites[0].Mode switch
        {
            RowWriteMode.Insert => ApplyInsertBatch(physicalWrites),
            RowWriteMode.Upsert => ApplyUpsertBatch(physicalWrites),
            _ => ApplyBatchFallback(writes)
        };
    }

    private bool IsNativeAppendBatch(IReadOnlyList<RowWrite> writes) =>
        Unit.Retention?.Trigger == RetentionTrigger.OnAppend &&
        writes.Count != 0 &&
        SequenceColumnDefinition is null &&
        writes.All(write => write.Options.Precondition.Kind == WritePreconditionKind.Unconditional) &&
        !RelationalSessionPolicy.HasSecondaryUniqueIndex(writes[0].Unit) &&
        writes.Select(write => write.ColumnSet).Distinct(StringComparer.Ordinal).Count() == 1 &&
        writes[0].Mode is RowWriteMode.Insert or RowWriteMode.Upsert;

    private IReadOnlyList<RowWriteOutcome> ApplyInsertBatch(IReadOnlyList<RowWrite> writes)
    {
        var supplied = UserColumns.Where(column => writes[0].Values!.Values.ContainsKey(column.Name)).ToArray();
        foreach (var write in writes)
        {
            RelationalSessionPolicy.ValidateValues(Unit, UserColumns, "SQLite", write.Values!.Values, requireAllNonNullable: true);
            var writeColumns = UserColumns
                .Where(column => write.Values!.Values.ContainsKey(column.Name))
                .Select(column => column.Name)
                .ToArray();
            if (!writeColumns.SequenceEqual(supplied.Select(column => column.Name), StringComparer.Ordinal))
                return ApplyBatchFallback(writes);
        }

        var columns = supplied.ToList();
        if (VersionColumnDefinition is not null)
            columns.Add(VersionColumnDefinition);
        if (ScopeColumnDefinition is not null)
            columns.Add(ScopeColumnDefinition);
        var maxRows = Math.Max(1, SqliteVariableLimit() / columns.Count);
        if (writes.Count > maxRows)
            return writes.Chunk(maxRows).SelectMany(ApplyInsertBatch).ToArray();
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

        var returning = string.Join(", ", Unit.Key.Columns.Select(Quote).Concat(
            VersionColumnDefinition is null ? [] : [Quote(VersionColumnDefinition.Name)]));
        command.CommandText = $"INSERT INTO {Quote(Unit.Name)} ({string.Join(", ", columns.Select(column => Quote(column.Name)))}) VALUES {string.Join(", ", valuesSql)} ON CONFLICT DO NOTHING RETURNING {returning};";
        commandObserver?.Observe(new ProviderCommandEvent("sqlite.batch-insert", "SQLite multi-row INSERT", ProviderCommandKind.Write, IsProbe: false));
        try
        {
            var inserted = ReadReturnedRows(command, writes[0].Unit);
            return writes.Select(write => new RowWriteOutcome(write,
                inserted.TryGetValue(write.Identity, out var version)
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
            RelationalSessionPolicy.ValidateValues(Unit, UserColumns, "SQLite", write.Values!.Values, requireAllNonNullable: false);
            var writeColumns = UserColumns
                .Where(column => write.Values!.Values.ContainsKey(column.Name))
                .Select(column => column.Name)
                .ToArray();
            if (!writeColumns.SequenceEqual(supplied.Select(column => column.Name), StringComparer.Ordinal))
                return ApplyBatchFallback(writes);
        }

        var columns = supplied.ToList();
        if (VersionColumnDefinition is not null)
            columns.Add(VersionColumnDefinition);
        if (ScopeColumnDefinition is not null)
            columns.Add(ScopeColumnDefinition);
        var maxRows = Math.Max(1, SqliteVariableLimit() / columns.Count);
        if (writes.Count > maxRows)
            return writes.Chunk(maxRows).SelectMany(ApplyUpsertBatch).ToArray();
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
                             column.Name != SqliteSchemaCoordinator.ScopeColumn &&
                             column.Name != "createdAt")
            .Select(column => $"{Quote(column.Name)}=excluded.{Quote(column.Name)}")
            .ToList();
        if (VersionColumnDefinition is not null)
            updateColumns.Add($"{Quote(VersionColumnDefinition.Name)}={Quote(Unit.Name)}.{Quote(VersionColumnDefinition.Name)}+1");
        if (updateColumns.Count == 0)
            updateColumns.Add($"{Quote(Unit.Key.Columns[0])}={Quote(Unit.Name)}.{Quote(Unit.Key.Columns[0])}");

        var returning = string.Join(", ", Unit.Key.Columns.Select(Quote).Concat(
            VersionColumnDefinition is null ? [] : [Quote(VersionColumnDefinition.Name)]));
        command.CommandText = $"INSERT INTO {Quote(Unit.Name)} ({string.Join(", ", columns.Select(column => Quote(column.Name)))}) VALUES {string.Join(", ", valuesSql)} ON CONFLICT ({string.Join(", ", Unit.Key.Columns.Select(Quote))}) DO UPDATE SET {string.Join(", ", updateColumns)} RETURNING {returning};";
        commandObserver?.Observe(new ProviderCommandEvent("sqlite.batch-upsert", "SQLite multi-row INSERT ON CONFLICT", ProviderCommandKind.Write, IsProbe: false));
        try
        {
            var returned = ReadReturnedRows(command, writes[0].Unit);
            return writes.Select(write => new RowWriteOutcome(write,
                returned.TryGetValue(write.Identity, out var version)
                    ? new WriteOutcome(WriteOutcomeStatus.Upserted, version)
                    : new WriteOutcome(WriteOutcomeStatus.UniqueViolation))).ToArray();
        }
        catch (SqliteException exception) when (new SqliteDialect().TryMapUniqueViolation(exception, out var indexName))
        {
            return writes.Select(write => new RowWriteOutcome(write,
                new WriteOutcome(WriteOutcomeStatus.UniqueViolation, null, LogicalIndexName(indexName)))).ToArray();
        }
    }

    private Dictionary<string, long?> ReadReturnedRows(SqliteCommand command, StorageUnit logicalUnit)
    {
        var returned = new Dictionary<string, long?>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var index = 0; index < Unit.Key.Columns.Count; index++)
            {
                var column = Unit.Key.Columns[index];
                if (column == SqliteSchemaCoordinator.ScopeColumn)
                    values[column] = Access.Scope!.Value;
                else
                    values[column] = FromSqlite(reader.GetValue(index), Column(column));
            }
            var versionOrdinal = Unit.Key.Columns.Count;
            var version = VersionColumnDefinition is null || reader.IsDBNull(versionOrdinal)
                ? (long?)null
                : Convert.ToInt64(reader.GetValue(versionOrdinal), CultureInfo.InvariantCulture);
            returned[RowWrite.IdentityFor(logicalUnit, values)] = version;
        }
        return returned;
    }

    private int SqliteVariableLimit() =>
        SQLitePCL.raw.sqlite3_limit(
            connection.Handle,
            SQLitePCL.raw.SQLITE_LIMIT_VARIABLE_NUMBER,
            -1);

    private IReadOnlyList<RowWriteOutcome> ApplyBatchFallback(IReadOnlyList<RowWrite> writes)
    {
        using (execution.EnterBatchFallback())
        {
            return writes.Select(write => new RowWriteOutcome(write, write.Mode switch
            {
                RowWriteMode.Insert => Insert(write.Values!, write.Options),
                RowWriteMode.Update => Update(write.Values!, write.Options),
                RowWriteMode.Upsert when write.Options.Precondition.Kind == WritePreconditionKind.IfVersion => ConditionalUpsert(write.Values!, write.Options),
                RowWriteMode.Upsert => Upsert(write.Values!, write.Options),
                RowWriteMode.ConditionalUpsert => ConditionalUpsert(write.Values!, write.Options),
                RowWriteMode.Delete => Delete(write.Key!, write.Options),
                RowWriteMode.CompareAndDelete => CompareAndDelete(write.Key!, write.ExpectedValues, write.Options),
                _ => throw new ArgumentOutOfRangeException(nameof(write.Mode), write.Mode, null)
            })).ToArray();
        }
    }

    private WriteOutcome Mutate(RelationalCrudMutation operation)
    {
        var onAppend = Unit.Retention?.Trigger == RetentionTrigger.OnAppend &&
            operation.Kind is RelationalCrudKind.Insert or RelationalCrudKind.Upsert;
        var registration = BeginOnAppend(onAppend);
        WriteOutcome outcome;
        try
        {
            outcome = ExecuteWrite(() => crud.Mutate(operation, RelationalExecution.Synchronous)
                .GetAwaiter().GetResult());
        }
        catch
        {
            CompleteOnAppend(registration, cleanupRequired: false);
            throw;
        }
        CompleteOnAppend(registration, onAppend && outcome.Succeeded);
        return outcome;
    }

    private OnAppendRetentionCoordinator.AppendRegistration? BeginOnAppend(bool eligible)
    {
        StorageAccessValidation.EnsurePointOperation(Access, "write");
        if (!eligible || transaction is not null)
            return null;

        var registration = OnAppendRetentionCoordinator.Begin(owner, Unit, Access.Scope?.Value);
        try
        {
            owner.NotifyOnAppendRegistered(commandObserver);
            return registration;
        }
        catch
        {
            CompleteOnAppend(registration, cleanupRequired: false);
            throw;
        }
    }

    private void CompleteOnAppend(
        OnAppendRetentionCoordinator.AppendRegistration? registration,
        bool cleanupRequired)
    {
        void Cleanup()
        {
            if (transaction is not null)
            {
                owner.ThrowIfDisposed();
                ApplyRetentionCore(new RetentionExecutionOptions());
                return;
            }

            if (owner.UsesSharedSessionConnection)
            {
                using var gateLease = owner.EnterGate();
                owner.ThrowIfDisposed();
                ApplyRetentionCore(new RetentionExecutionOptions());
                return;
            }

            owner.ThrowIfDisposed();
            ApplyRetentionCore(new RetentionExecutionOptions());
        }
        if (registration is not null)
        {
            registration.Complete(cleanupRequired, Cleanup);
            return;
        }
        if (!cleanupRequired)
            return;
        if (transaction is null)
            OnAppendRetentionCoordinator.Run(owner, Unit, Access.Scope?.Value, Cleanup);
        else
            Cleanup();
    }

    private WriteOutcome UpdateCore(
        StorageValues values,
        StorageKey key,
        StoredEntry? existing,
        WriteOptions? options)
    {
        var supplied = UserColumns.Where(column => values.Values.ContainsKey(column.Name)).ToArray();
        var sets = supplied.Where(column => !Unit.Key.Columns.Contains(column.Name, StringComparer.Ordinal))
            .Select(column => $"{Quote(column.Name)}=@{column.Name}").ToList();
        var parameters = BuildParameters(values.Values, supplied);
        var (where, keyParameters) = KeyPredicate(key.Values);
        foreach (var pair in keyParameters)
            parameters[pair.Key] = pair.Value;
        if (VersionColumnDefinition is not null)
        {
            sets.Add($"{Quote(VersionColumnDefinition.Name)}={Quote(VersionColumnDefinition.Name)}+1");
            if (options?.Precondition.Kind == WritePreconditionKind.IfVersion)
            {
                where += $" AND {Quote(VersionColumnDefinition.Name)}=@expected";
                parameters["@expected"] = options.Precondition.Version!.Value;
            }
        }
        if (sets.Count == 0)
        {
            var noOpColumn = LogicalKeyColumns[0];
            sets.Add($"{Quote(noOpColumn)}={Quote(noOpColumn)}");
        }
        var sql = $"UPDATE {Quote(Unit.Name)} SET {string.Join(", ", sets)} WHERE {where};";
        using var update = Command(sql);
        AddParameters(update, parameters);
        if (Unit.Concurrency.IsNone)
            commandObserver?.Observe(new ProviderCommandEvent("sqlite.update", sql, ProviderCommandKind.Write, IsProbe: false));
        var affected = update.ExecuteNonQuery();
        if (affected == 0)
            return new WriteOutcome(Unit.Concurrency.IsNone
                ? WriteOutcomeStatus.NotFound
                : WriteOutcomeStatus.ConcurrencyConflict, existing?.Version);
        return new WriteOutcome(WriteOutcomeStatus.Updated, VersionColumnDefinition is null ? null : existing!.Version + 1);
    }

    private WriteOutcome UpsertCore(
        StorageValues values,
        StorageKey key,
        StoredEntry? existing,
        WriteOptions? options)
    {
        if (SequenceColumnDefinition is not null && values.Values.ContainsKey(SequenceColumnDefinition.Name))
            return UpdateCore(values, key, existing, options);

        var supplied = UserColumns.Where(column => values.Values.ContainsKey(column.Name)).ToArray();
        var columns = supplied.ToList();
        if (VersionColumnDefinition is not null)
            columns.Add(VersionColumnDefinition);
        if (Unit.Scope == ScopePolicy.Scoped)
            columns.Add(ScopeColumnDefinition!);
        return Upsert(values, existing, columns, options);
    }

    private WriteOutcome DeleteCore(
        StorageKey key,
        StoredEntry? existing,
        WriteOptions? options)
    {
        if (Unit.Concurrency.IsNone)
        {
            var (noneWhere, noneParameters) = KeyPredicate(key.Values);
            using var noneCommand = Command($"DELETE FROM {Quote(Unit.Name)} WHERE {noneWhere};");
            commandObserver?.Observe(new ProviderCommandEvent("sqlite.delete", noneCommand.CommandText, ProviderCommandKind.Write, IsProbe: false));
            AddParameters(noneCommand, noneParameters);
            return noneCommand.ExecuteNonQuery() == 0
                ? new WriteOutcome(WriteOutcomeStatus.NotFound)
                : new WriteOutcome(WriteOutcomeStatus.Deleted);
        }

        var (where, parameters) = KeyPredicate(key.Values);
        if (VersionColumnDefinition is not null && options?.Precondition.Kind == WritePreconditionKind.IfVersion)
        {
            where += $" AND {Quote(VersionColumnDefinition.Name)}=@expected";
            parameters["@expected"] = options.Precondition.Version!.Value;
        }
        using var command = Command($"DELETE FROM {Quote(Unit.Name)} WHERE {where};");
        commandObserver?.Observe(new ProviderCommandEvent("sqlite.delete", command.CommandText, ProviderCommandKind.Write, IsProbe: false));
        AddParameters(command, parameters);
        return command.ExecuteNonQuery() == 0
            ? new WriteOutcome(WriteOutcomeStatus.ConcurrencyConflict, existing!.Version)
            : new WriteOutcome(WriteOutcomeStatus.Deleted, existing!.Version);
    }

    private WriteOutcome ConditionalUpsertCore(StorageValues values, WriteOptions? options)
    {
        ArgumentNullException.ThrowIfNull(values);
        values = new StorageValues(SearchKeyProjection.Populate(Unit, values.Values));
        RelationalSessionPolicy.ValidateValues(Unit, UserColumns, "SQLite", values.Values, requireAllNonNullable: false);

        var key = new StorageKey(LogicalKeyColumns.ToDictionary(
            column => column,
            column => values.Values.TryGetValue(column, out var value)
                ? value
                : throw new ArgumentException($"Key column '{column}' is required.", nameof(values)),
            StringComparer.Ordinal));
        if (options?.Precondition.Kind == WritePreconditionKind.IfVersion && VersionColumnDefinition is null)
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
            parameters["@__expected"] = options?.Precondition.Version;
        }
        if (ScopeColumnDefinition is not null)
            parameters["@__groundwork_scope"] = Access.Scope!.Value;

        var insertValues = string.Join(", ", insertColumns.Select(column =>
            column.Name == SqliteSchemaCoordinator.VersionColumn ? "@__groundwork_version" :
            column.Name == SqliteSchemaCoordinator.ScopeColumn ? "@__groundwork_scope" : "@" + column.Name));
        var insertSource = VersionColumnDefinition is null || options?.Precondition.Version is null
            ? $"VALUES ({insertValues})"
            : $"SELECT {insertValues} WHERE EXISTS (SELECT 1 FROM {Quote(Unit.Name)} WHERE {keyPredicate} AND {Quote(VersionColumnDefinition.Name)}=@__expected)";
        var conflict = string.Join(", ", Unit.Key.Columns.Select(Quote));
        var expected = VersionColumnDefinition is null || options?.Precondition.Kind != WritePreconditionKind.IfVersion
            ? options?.Precondition.Kind == WritePreconditionKind.CreateOnly && VersionColumnDefinition is not null
                ? " WHERE 0=1"
                : string.Empty
            : $" WHERE {Quote(Unit.Name)}.{Quote(VersionColumnDefinition.Name)}=@__expected";
        var returning = VersionColumnDefinition is null
            ? Quote(ActionColumnDefinition!.Name)
            : Quote(VersionColumnDefinition.Name);
        var sql = $"INSERT INTO {Quote(Unit.Name)} ({string.Join(", ", insertColumns.Select(column => Quote(column.Name)))}) " +
                  $"{insertSource} ON CONFLICT ({conflict}) DO UPDATE SET {string.Join(", ", updates)}{expected} " +
                  $"RETURNING {returning};";
        using var command = Command(sql);
        AddParameters(command, parameters);
        commandObserver?.Observe(new ProviderCommandEvent("sqlite.conditional-upsert", sql, ProviderCommandKind.Write, IsProbe: false));
        try
        {
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return DeferredConflict(key);

            var inserted = ActionColumnDefinition is not null
                ? string.Equals(reader.GetString(0), "I", StringComparison.Ordinal)
                : options?.Precondition.Kind != WritePreconditionKind.IfVersion;
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

    private WriteOutcome DeferredConflict(StorageKey key) =>
        WriteOutcome.Deferred(
            WriteOutcomeStatus.ConcurrencyConflict,
            null,
            () =>
            {
                schemaSession.EnsureCurrent();
                var existing = ReadCore(key);
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

    private WriteOutcome InsertCore(
        IReadOnlyDictionary<string, object?> values,
        WriteOutcomeStatus status)
    {
        var supplied = UserColumns.Where(column => values.ContainsKey(column.Name)).ToArray();
        var columns = supplied.ToList();
        if (VersionColumnDefinition is not null) columns.Add(VersionColumnDefinition);
        if (ScopeColumnDefinition is not null) columns.Add(ScopeColumnDefinition);
        var parameters = BuildParameters(values, supplied);
        if (VersionColumnDefinition is not null) parameters["@__groundwork_version"] = 1L;
        if (ScopeColumnDefinition is not null) parameters["@__groundwork_scope"] = Access.Scope!.Value;
        var returning = SequenceColumnDefinition is null ? string.Empty : $" RETURNING {Quote(SequenceColumnDefinition.Name)};";
        var sql = columns.Count == 0
            ? $"INSERT INTO {Quote(Unit.Name)} DEFAULT VALUES{returning}"
            : $"INSERT INTO {Quote(Unit.Name)} ({string.Join(", ", columns.Select(column => Quote(column.Name)))}) VALUES ({string.Join(", ", columns.Select(column => "@" + column.Name))}){returning}";
        using var command = Command(sql);
        AddParameters(command, parameters);
        commandObserver?.Observe(new ProviderCommandEvent("sqlite.insert", sql, ProviderCommandKind.Write, IsProbe: false));
        try
        {
            if (SequenceColumnDefinition is null)
            {
                command.ExecuteNonQuery();
                return new WriteOutcome(status, VersionColumnDefinition is null ? null : 1);
            }

            object? generatedValue;
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read())
                    return new WriteOutcome(WriteOutcomeStatus.UniqueViolation);
                generatedValue = FromSqlite(reader.GetValue(0), SequenceColumnDefinition);
            }
            RecordHighWater(generatedValue);
            return new WriteOutcome(
                status,
                VersionColumnDefinition is null ? null : 1,
                generatedValues: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [SequenceColumnDefinition.Name] = generatedValue
                });
        }
        catch (SqliteException exception) when (new SqliteDialect().TryMapUniqueViolation(exception, out _))
        {
            return new WriteOutcome(WriteOutcomeStatus.UniqueViolation);
        }
    }

    private WriteOutcome Upsert(
        StorageValues values,
        StoredEntry? existing,
        IReadOnlyList<ColumnDefinition> columns,
        WriteOptions? options)
    {
        var updateColumns = columns.Where(column =>
            !Unit.Key.Columns.Contains(column.Name, StringComparer.Ordinal) &&
            column.Name != SqliteSchemaCoordinator.ScopeColumn &&
            column.Name != "createdAt").ToArray();
        if (VersionColumnDefinition is not null && updateColumns.All(column => column.Name != VersionColumnDefinition.Name))
            updateColumns = updateColumns.Append(VersionColumnDefinition).ToArray();
        var keyNames = Unit.Key.Columns;
        var preconditionSql = options?.Precondition.Kind switch
        {
            WritePreconditionKind.CreateOnly => " WHERE 0=1",
            WritePreconditionKind.IfVersion when VersionColumnDefinition is not null =>
                $" WHERE {Quote(VersionColumnDefinition.Name)}=@expected",
            _ => string.Empty
        };
        var sql = $"INSERT INTO {Quote(Unit.Name)} ({string.Join(", ", columns.Select(column => Quote(column.Name)))}) VALUES ({string.Join(", ", columns.Select(column => "@" + column.Name))}) ON CONFLICT ({string.Join(", ", keyNames.Select(Quote))}) DO UPDATE SET " +
            string.Join(", ", updateColumns.Select(column => column.Name == SqliteSchemaCoordinator.VersionColumn
                ? $"{Quote(column.Name)}={Quote(column.Name)}+1" : $"{Quote(column.Name)}=excluded.{Quote(column.Name)}")) + preconditionSql + ";";
        var parameters = BuildParameters(values.Values, columns.Where(column => values.Values.ContainsKey(column.Name)).ToArray());
        if (VersionColumnDefinition is not null && !parameters.ContainsKey("@__groundwork_version")) parameters["@__groundwork_version"] = 1L;
        if (ScopeColumnDefinition is not null && !parameters.ContainsKey("@__groundwork_scope")) parameters["@__groundwork_scope"] = Access.Scope!.Value;
        if (options?.Precondition.Kind == WritePreconditionKind.IfVersion)
            parameters["@expected"] = options.Precondition.Version!.Value;
        using var command = Command(sql);
        AddParameters(command, parameters);
        commandObserver?.Observe(new ProviderCommandEvent("sqlite.upsert", sql, ProviderCommandKind.Write, IsProbe: false));
        try
        {
            command.ExecuteNonQuery();
            var version = VersionColumnDefinition is null ? (long?)null : existing is null ? 1 : existing.Version + 1;
            return new WriteOutcome(WriteOutcomeStatus.Upserted, version);
        }
        catch (SqliteException exception) when (new SqliteDialect().TryMapUniqueViolation(exception, out _)) { return new WriteOutcome(WriteOutcomeStatus.UniqueViolation, existing?.Version); }
    }

    private StoredEntry? ReadCore(
        StorageKey key,
        string? observerOperation = null,
        bool isProbe = true) => pointReads.Read(
            key,
            RelationalExecution.Synchronous,
            observerOperation: observerOperation,
            isProbe: isProbe).GetAwaiter().GetResult();

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
        execution.EnsureOpen();
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = (SqliteTransaction?)execution.Transaction;
        return command;
    }

    private static void AddParameters(SqliteCommand command, IReadOnlyDictionary<string, object?> parameters)
    {
        foreach (var pair in parameters) command.Parameters.AddWithValue(pair.Key, pair.Value ?? DBNull.Value);
    }

    private T Execute<T>(Func<T> operation) =>
        execution.Execute(
                () => ValueTask.FromResult(operation()),
                RelationalExecution.Synchronous)
            .GetAwaiter().GetResult();

    private T ExecuteWrite<T>(Func<T> operation) =>
        execution.ExecuteWrite(
                () => ValueTask.FromResult(operation()),
                RelationalExecution.Synchronous)
            .GetAwaiter().GetResult();

    private void RollbackOrRetire(SqliteTransaction writeTransaction)
    {
        try
        {
            SqliteTransactionCleanup.RollbackOrClearPool(writeTransaction, connection);
        }
        catch
        {
            // ClearPool marks the checked-out native handle non-poolable but leaves it open. A
            // failed rollback can leave the native transaction active, so this session must not
            // attempt another write against that handle before it is retired.
            execution.Close();
            if (owner.UsesSharedSessionConnection)
                owner.DisposeWhileHoldingGate();
            else
                connection.Close();
            throw;
        }
    }

    private ColumnDefinition Column(string name) => UserColumns.First(column => column.Name == name);
    private IReadOnlyList<ColumnDefinition> UserColumns => Unit.Columns.Where(column => column.Name is not SqliteSchemaCoordinator.ScopeColumn and not SqliteSchemaCoordinator.VersionColumn and not SqliteSchemaCoordinator.ActionColumn).ToArray();
    private IReadOnlyList<string> LogicalKeyColumns => Unit.Key.Columns.Where(column => column != SqliteSchemaCoordinator.ScopeColumn).ToArray();
    private ColumnDefinition? ScopeColumnDefinition => Unit.Columns.FirstOrDefault(column => column.Name == SqliteSchemaCoordinator.ScopeColumn);
    private ColumnDefinition? VersionColumnDefinition => Unit.Columns.FirstOrDefault(column => column.Name == SqliteSchemaCoordinator.VersionColumn);
    private ColumnDefinition? ActionColumnDefinition => Unit.Columns.FirstOrDefault(column => column.Name == SqliteSchemaCoordinator.ActionColumn);
    private const string LedgerUnit = "unit";
    private const string LedgerScope = "scope";
    private const string LedgerNonce = "nonce";
    private const string LedgerCommittedAt = "committed_at";
    private const string LedgerFingerprint = "input_fingerprint";
    private const string LedgerResult = "exact_result";
    private const string HighWaterTable = "__groundwork_sequence_high_waters";
    private const string HighWaterValue = "high_water";
    private ColumnDefinition? SequenceColumnDefinition => UserColumns.FirstOrDefault(column => column.Generation == ColumnGeneration.ProviderSequence);
    private static string Quote(string value) => SqliteProviderConnection.QuoteIdentifier(value);
    private static object? ToSqlite(object? value, ColumnDefinition definition) => SqliteProviderConnection.ToSqliteValue(value, definition);

    private static object? FromSqlite(object value, ColumnDefinition definition) =>
        SqliteDialect.ReadPortableValue(value, definition);

    private sealed class SqliteCrudAdapter(SqliteStorageSession session) : IRelationalCrudAdapter
    {
        public ValueTask<WriteOutcome> Insert(
            StorageValues values,
            WriteOutcomeStatus status,
            RelationalExecution execution) =>
            ValueTask.FromResult(session.InsertCore(values.Values, status));

        public ValueTask<WriteOutcome> Update(
            StorageValues values,
            StorageKey key,
            StoredEntry? existing,
            WriteOptions? options,
            RelationalExecution execution) =>
            ValueTask.FromResult(session.UpdateCore(values, key, existing, options));

        public ValueTask<WriteOutcome> Upsert(
            StorageValues values,
            StorageKey key,
            StoredEntry? existing,
            WriteOptions? options,
            RelationalExecution execution) =>
            ValueTask.FromResult(session.UpsertCore(values, key, existing, options));

        public ValueTask<WriteOutcome> Delete(
            StorageKey key,
            StoredEntry? existing,
            WriteOptions? options,
            RelationalExecution execution) =>
            ValueTask.FromResult(session.DeleteCore(key, existing, options));
    }

    private sealed class SqliteRetentionAdapter(SqliteStorageSession session) : IRelationalRetentionAdapter
    {
        public ValueTask<IReadOnlyList<object?>> ReadAffectedKeys(
            RelationalExactRetentionOperation operation,
            RelationalExecution execution)
        {
            var projection = operation.Retention.Options.AffectedKeyProjection ??
                throw new InvalidOperationException("An affected-key projection is required.");
            var declaration = operation.Retention.Declaration;
            var partition = declaration.PartitionColumns.Count == 0
                ? string.Empty
                : $"PARTITION BY {string.Join(", ", declaration.PartitionColumns.Select(Quote))} ";
            var scope = operation.Unit.Columns.Any(column => column.Name == SqliteSchemaCoordinator.ScopeColumn)
                ? $" WHERE {Quote(SqliteSchemaCoordinator.ScopeColumn)}=@__groundwork_scope"
                : string.Empty;
            var ordering = string.Join(", ", [
                $"{Quote(declaration.OrderColumn)} DESC",
                .. operation.Unit.Key.Columns
                    .Where(column => !string.Equals(column, declaration.OrderColumn, StringComparison.Ordinal))
                    .Select(column => $"{Quote(column)} ASC")]);
            var projectionColumn = Quote(projection.Column);
            if (session.Column(projection.Column).Type == PortableType.String)
                projectionColumn += " COLLATE GROUNDWORK_UTF16_ORDINAL";
            using var command = session.Command(
                $"WITH ranked AS (" +
                $"SELECT {projectionColumn}, ROW_NUMBER() OVER ({partition}ORDER BY {ordering}) AS __groundwork_retention_rank " +
                $"FROM {Quote(operation.Unit.Name)}{scope}) " +
                $"SELECT DISTINCT {projectionColumn} FROM ranked " +
                $"WHERE __groundwork_retention_rank > @keep " +
                $"ORDER BY {projectionColumn} LIMIT @affected_limit;");
            command.Parameters.AddWithValue("@keep", operation.Retention.KeepNewest);
            command.Parameters.AddWithValue("@affected_limit", checked(projection.MaxDistinctValues + 1));
            if (operation.Unit.Columns.Any(column => column.Name == SqliteSchemaCoordinator.ScopeColumn))
                command.Parameters.AddWithValue("@__groundwork_scope", operation.Scope);
            using var reader = command.ExecuteReader();
            var values = new List<object?>(Math.Min(projection.MaxDistinctValues + 1, 4096));
            var column = session.Column(projection.Column);
            while (reader.Read())
                values.Add(reader.IsDBNull(0) ? null : FromSqlite(reader.GetValue(0), column));
            session.commandObserver?.Observe(new ProviderCommandEvent(
                "sqlite.retention-affected-keys",
                command.CommandText,
                ProviderCommandKind.Read,
                IsProbe: false));
            return ValueTask.FromResult<IReadOnlyList<object?>>(values);
        }

        public ValueTask<int> DeleteBatch(
            RelationalRetentionOperation operation,
            RelationalExecution execution)
        {
            var declaration = operation.Declaration;
            var keyColumns = operation.Unit.Key.Columns;
            var partition = declaration.PartitionColumns.Count == 0
                ? string.Empty
                : $"PARTITION BY {string.Join(", ", declaration.PartitionColumns.Select(Quote))} ";
            var scope = operation.Unit.Columns.Any(column => column.Name == SqliteSchemaCoordinator.ScopeColumn)
                ? $" WHERE {Quote(SqliteSchemaCoordinator.ScopeColumn)}=@__groundwork_scope"
                : string.Empty;
            var keys = string.Join(", ", keyColumns.Select(Quote));
            var ordering = string.Join(", ", [
                $"{Quote(declaration.OrderColumn)} DESC",
                .. keyColumns
                    .Where(column => !string.Equals(column, declaration.OrderColumn, StringComparison.Ordinal))
                    .Select(column => $"{Quote(column)} ASC")]);
            var equality = string.Join(" AND ", keyColumns.Select(column =>
                $"target.{Quote(column)}=victim.{Quote(column)}"));
            using var command = session.Command(
                $"WITH ranked AS (" +
                $"SELECT {keys}, ROW_NUMBER() OVER ({partition}ORDER BY {ordering}) AS __groundwork_retention_rank " +
                $"FROM {Quote(operation.Unit.Name)}{scope}), victims AS (" +
                $"SELECT {keys} FROM ranked WHERE __groundwork_retention_rank > @keep LIMIT @limit) " +
                $"DELETE FROM {Quote(operation.Unit.Name)} AS target WHERE EXISTS (SELECT 1 FROM victims AS victim WHERE {equality});");
            command.Parameters.AddWithValue("@keep", operation.KeepNewest);
            command.Parameters.AddWithValue("@limit", operation.Options.MaxRowsPerBatch);
            if (operation.Unit.Columns.Any(column => column.Name == SqliteSchemaCoordinator.ScopeColumn))
                command.Parameters.AddWithValue("@__groundwork_scope", operation.Scope);
            var affected = command.ExecuteNonQuery();
            session.commandObserver?.Observe(new ProviderCommandEvent(
                "sqlite.retention-delete",
                command.CommandText,
                ProviderCommandKind.Write,
                IsProbe: false));
            return ValueTask.FromResult(affected);
        }

        public ValueTask<DateTimeOffset> PrepareLedger(
            RelationalExactRetentionOperation operation,
            RelationalExecution execution)
        {
            session.EnsureLedgerTable(operation.Declaration.LedgerName);
            return ValueTask.FromResult(session.ProviderNow());
        }

        public ValueTask ReclaimExpired(
            RelationalExactRetentionOperation operation,
            DateTimeOffset cutoff,
            RelationalExecution execution)
        {
            using var reclaim = session.Command(
                $"DELETE FROM {Quote(operation.Declaration.LedgerName)} WHERE rowid IN " +
                $"(SELECT rowid FROM {Quote(operation.Declaration.LedgerName)} " +
                $"WHERE {Quote(LedgerUnit)}=@reclaim_unit AND {Quote(LedgerCommittedAt)} <= @cutoff LIMIT 128);");
            reclaim.Parameters.AddWithValue("@reclaim_unit", operation.Unit.Id.Value);
            reclaim.Parameters.AddWithValue("@cutoff", FormatLedgerTime(cutoff));
            reclaim.ExecuteNonQuery();
            return default;
        }

        public ValueTask<RelationalRetentionLedgerEntry?> ReadLedger(
            RelationalExactRetentionOperation operation,
            RelationalExecution execution)
        {
            using var existing = session.Command(
                $"SELECT {Quote(LedgerCommittedAt)}, {Quote(LedgerFingerprint)}, {Quote(LedgerResult)} " +
                $"FROM {Quote(operation.Declaration.LedgerName)} WHERE {Quote(LedgerUnit)}=@unit " +
                $"AND {Quote(LedgerScope)}=@scope AND {Quote(LedgerNonce)}=@nonce;");
            AddLedgerParameters(existing, operation.Unit.Id.Value, operation.Scope, operation.OperationId.Nonce);
            using var reader = existing.ExecuteReader();
            if (!reader.Read())
                return ValueTask.FromResult<RelationalRetentionLedgerEntry?>(null);
            return ValueTask.FromResult<RelationalRetentionLedgerEntry?>(new(
                DateTimeOffset.Parse(
                    reader.GetString(0),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        public ValueTask DeleteLedger(
            RelationalExactRetentionOperation operation,
            RelationalRetentionLedgerEntry existing,
            RelationalExecution execution)
        {
            using var delete = session.Command(
                $"DELETE FROM {Quote(operation.Declaration.LedgerName)} WHERE {Quote(LedgerUnit)}=@unit " +
                $"AND {Quote(LedgerScope)}=@scope AND {Quote(LedgerNonce)}=@nonce " +
                $"AND {Quote(LedgerCommittedAt)}=@observed_committed_at;");
            AddLedgerParameters(delete, operation.Unit.Id.Value, operation.Scope, operation.OperationId.Nonce);
            delete.Parameters.AddWithValue("@observed_committed_at", FormatLedgerTime(existing.CommittedAt));
            delete.ExecuteNonQuery();
            return default;
        }

        public ValueTask<bool> TryClaimLedger(
            RelationalExactRetentionOperation operation,
            DateTimeOffset providerNow,
            RelationalExecution execution)
        {
            using var insert = session.Command(
                $"INSERT OR IGNORE INTO {Quote(operation.Declaration.LedgerName)} " +
                $"({Quote(LedgerUnit)}, {Quote(LedgerScope)}, {Quote(LedgerNonce)}, " +
                $"{Quote(LedgerCommittedAt)}, {Quote(LedgerFingerprint)}, {Quote(LedgerResult)}) " +
                "VALUES (@unit, @scope, @nonce, @committed_at, @fingerprint, @result);");
            AddLedgerParameters(insert, operation.Unit.Id.Value, operation.Scope, operation.OperationId.Nonce);
            insert.Parameters.AddWithValue("@committed_at", FormatLedgerTime(providerNow));
            insert.Parameters.AddWithValue("@fingerprint", operation.Fingerprint);
            insert.Parameters.AddWithValue("@result", string.Empty);
            return ValueTask.FromResult(insert.ExecuteNonQuery() == 1);
        }

        public ValueTask<RelationalRetentionReplayEntry?> ReadClaimWinner(
            RelationalExactRetentionOperation operation,
            RelationalExecution execution)
        {
            using var replay = session.Command(
                $"SELECT {Quote(LedgerFingerprint)}, {Quote(LedgerResult)} " +
                $"FROM {Quote(operation.Declaration.LedgerName)} WHERE {Quote(LedgerUnit)}=@unit " +
                $"AND {Quote(LedgerScope)}=@scope AND {Quote(LedgerNonce)}=@nonce;");
            AddLedgerParameters(replay, operation.Unit.Id.Value, operation.Scope, operation.OperationId.Nonce);
            using var reader = replay.ExecuteReader();
            if (!reader.Read())
                return ValueTask.FromResult<RelationalRetentionReplayEntry?>(null);
            return ValueTask.FromResult<RelationalRetentionReplayEntry?>(new(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        public ValueTask<bool> CompleteLedger(
            RelationalExactRetentionOperation operation,
            string serializedResult,
            RelationalExecution execution)
        {
            using var complete = session.Command(
                $"UPDATE {Quote(operation.Declaration.LedgerName)} SET {Quote(LedgerResult)}=@result " +
                $"WHERE {Quote(LedgerUnit)}=@unit AND {Quote(LedgerScope)}=@scope " +
                $"AND {Quote(LedgerNonce)}=@nonce;");
            AddLedgerParameters(complete, operation.Unit.Id.Value, operation.Scope, operation.OperationId.Nonce);
            complete.Parameters.AddWithValue("@result", serializedResult);
            return ValueTask.FromResult(complete.ExecuteNonQuery() == 1);
        }
    }

    private sealed class SqliteAppendAdapter(SqliteStorageSession session) : IRelationalAppendAdapter
    {
        public ValueTask<DateTimeOffset> PrepareLedger(
            RelationalAppendOperation operation,
            RelationalExecution execution)
        {
            session.EnsureLedgerTable(operation.Declaration.LedgerName);
            return ValueTask.FromResult(session.ProviderNow());
        }

        public ValueTask ReclaimExpired(
            RelationalAppendOperation operation,
            DateTimeOffset cutoff,
            RelationalExecution execution)
        {
            using var reclaim = session.Command(
                $"DELETE FROM {Quote(operation.Declaration.LedgerName)} WHERE rowid IN " +
                $"(SELECT rowid FROM {Quote(operation.Declaration.LedgerName)} " +
                $"WHERE {Quote(LedgerUnit)}=@reclaim_unit AND {Quote(LedgerCommittedAt)} <= @cutoff LIMIT 128);");
            reclaim.Parameters.AddWithValue("@reclaim_unit", operation.Unit.Id.Value);
            reclaim.Parameters.AddWithValue("@cutoff", FormatLedgerTime(cutoff));
            reclaim.ExecuteNonQuery();
            return default;
        }

        public ValueTask<RelationalAppendLedgerEntry?> ReadLedger(
            RelationalAppendOperation operation,
            RelationalExecution execution)
        {
            using var existing = session.Command(
                $"SELECT {Quote(LedgerCommittedAt)}, {Quote(LedgerFingerprint)}, {Quote(LedgerResult)} " +
                $"FROM {Quote(operation.Declaration.LedgerName)} WHERE {Quote(LedgerUnit)}=@unit " +
                $"AND {Quote(LedgerScope)}=@scope AND {Quote(LedgerNonce)}=@nonce;");
            AddLedgerParameters(existing, operation.Unit.Id.Value, operation.Scope, operation.OperationId.Nonce);
            using var reader = existing.ExecuteReader();
            if (!reader.Read())
                return ValueTask.FromResult<RelationalAppendLedgerEntry?>(null);
            return ValueTask.FromResult<RelationalAppendLedgerEntry?>(new(
                DateTimeOffset.Parse(
                    reader.GetString(0),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        public ValueTask DeleteLedger(
            RelationalAppendOperation operation,
            RelationalAppendLedgerEntry existing,
            RelationalExecution execution)
        {
            using var delete = session.Command(
                $"DELETE FROM {Quote(operation.Declaration.LedgerName)} WHERE {Quote(LedgerUnit)}=@unit " +
                $"AND {Quote(LedgerScope)}=@scope AND {Quote(LedgerNonce)}=@nonce " +
                $"AND {Quote(LedgerCommittedAt)}=@observed_committed_at;");
            AddLedgerParameters(delete, operation.Unit.Id.Value, operation.Scope, operation.OperationId.Nonce);
            delete.Parameters.AddWithValue("@observed_committed_at", FormatLedgerTime(existing.CommittedAt));
            delete.ExecuteNonQuery();
            return default;
        }

        public ValueTask<bool> TryClaimLedger(
            RelationalAppendOperation operation,
            DateTimeOffset providerNow,
            RelationalExecution execution)
        {
            using var insert = session.Command(
                $"INSERT OR IGNORE INTO {Quote(operation.Declaration.LedgerName)} " +
                $"({Quote(LedgerUnit)}, {Quote(LedgerScope)}, {Quote(LedgerNonce)}, " +
                $"{Quote(LedgerCommittedAt)}, {Quote(LedgerFingerprint)}, {Quote(LedgerResult)}) " +
                "VALUES (@unit, @scope, @nonce, @committed_at, @fingerprint, @result);");
            AddLedgerParameters(insert, operation.Unit.Id.Value, operation.Scope, operation.OperationId.Nonce);
            insert.Parameters.AddWithValue("@committed_at", FormatLedgerTime(providerNow));
            insert.Parameters.AddWithValue("@fingerprint", operation.Fingerprint);
            insert.Parameters.AddWithValue("@result", string.Empty);
            return ValueTask.FromResult(insert.ExecuteNonQuery() != 0);
        }

        public ValueTask<RelationalAppendReplayEntry?> ReadClaimWinner(
            RelationalAppendOperation operation,
            RelationalExecution execution)
        {
            using var replay = session.Command(
                $"SELECT {Quote(LedgerFingerprint)}, {Quote(LedgerResult)} " +
                $"FROM {Quote(operation.Declaration.LedgerName)} WHERE {Quote(LedgerUnit)}=@unit " +
                $"AND {Quote(LedgerScope)}=@scope AND {Quote(LedgerNonce)}=@nonce;");
            AddLedgerParameters(replay, operation.Unit.Id.Value, operation.Scope, operation.OperationId.Nonce);
            using var reader = replay.ExecuteReader();
            if (!reader.Read())
                return ValueTask.FromResult<RelationalAppendReplayEntry?>(null);
            return ValueTask.FromResult<RelationalAppendReplayEntry?>(new(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        public ValueTask<IReadOnlyList<RowWriteOutcome>> InsertPayload(
            RelationalAppendOperation operation,
            RelationalExecution execution)
        {
            var logicalUnit = IdempotencyRules.LogicalUnit(
                operation.Unit,
                SqliteSchemaCoordinator.ScopeColumn);
            var writes = operation.Values
                .Select(value => RowWrite.Insert(logicalUnit, value))
                .ToArray();
            IReadOnlyList<RowWriteOutcome> outcomes = session.SequenceColumnDefinition is not null
                ? session.InsertAppendSequenceBatch(writes)
                : session.ApplyBatchCore(writes);
            return ValueTask.FromResult(outcomes);
        }

        public ValueTask<bool> CompleteLedger(
            RelationalAppendOperation operation,
            string serializedOutcomes,
            RelationalExecution execution)
        {
            using var complete = session.Command(
                $"UPDATE {Quote(operation.Declaration.LedgerName)} SET {Quote(LedgerResult)}=@result " +
                $"WHERE {Quote(LedgerUnit)}=@unit AND {Quote(LedgerScope)}=@scope " +
                $"AND {Quote(LedgerNonce)}=@nonce;");
            AddLedgerParameters(complete, operation.Unit.Id.Value, operation.Scope, operation.OperationId.Nonce);
            complete.Parameters.AddWithValue("@result", serializedOutcomes);
            return ValueTask.FromResult(complete.ExecuteNonQuery() == 1);
        }
    }

    private sealed class SqliteSessionExecutionAdapter(
        SqliteProviderConnection owner,
        SqliteConnection connection,
        SchemaSessionLease schemaSession,
        Action<SqliteTransaction> rollback) : IRelationalSessionExecutionAdapter
    {
        public bool SerializeAmbientReads => false;

        public void EnsureUsable()
        {
            owner.ThrowIfDisposed();
            schemaSession.EnsureCurrent();
        }

        public ValueTask<IDisposable> EnterGate(RelationalExecution execution) =>
            owner.EnterGate(execution);

        public ValueTask<DbTransaction> BeginWrite(RelationalExecution execution) =>
            ValueTask.FromResult<DbTransaction>(
                connection.BeginTransaction(IsolationLevel.Serializable, deferred: false));

        public ValueTask Rollback(DbTransaction transaction, RelationalExecution execution)
        {
            rollback((SqliteTransaction)transaction);
            return default;
        }
    }

    private sealed class SqlitePointReadAdapter : IRelationalPointReadAdapter
    {
        public string QuoteIdentifier(string identifier) => Quote(identifier);

        public string Equality(ColumnDefinition column, string parameter, bool exactStringKeys) =>
            $"{Quote(column.Name)}={parameter}";

        public void Bind(DbCommand command, string parameter, object? value, ColumnDefinition column) =>
            ((SqliteCommand)command).Parameters.AddWithValue(
                parameter,
                ToSqlite(value, column) ?? DBNull.Value);

        public object? Decode(object value, ColumnDefinition column) => FromSqlite(value, column);

        public string LockingClause(bool forUpdate) => string.Empty;
    }
}

internal sealed class OwnedSqliteStorageSession : SqliteStorageSession, IOwnedStorageSession
{
    internal OwnedSqliteStorageSession(
        SqliteProviderConnection owner,
        StorageUnit unit,
        StorageAccess access,
        SqliteConnection connection,
        SchemaSessionLease schemaSession,
        IProviderCommandObserver? observer = null,
        bool ownsConnection = true)
        : base(owner, unit, access, connection, null, schemaSession, observer, ownsConnection)
    {
    }
}
