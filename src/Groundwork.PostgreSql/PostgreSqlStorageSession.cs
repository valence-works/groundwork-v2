using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Groundwork.Kernel;
using Groundwork.Query.Model;
using Groundwork.Substrate.Relational;
using Groundwork.Store;
using Groundwork.Diagnostics;
using Npgsql;
using NpgsqlTypes;

namespace Groundwork.PostgreSql;

internal class PostgreSqlStorageSession : IStorageSession, IProviderBoundStorageSession, IExactAppendStorageSession, IConcurrencyStorageSession, ICompareAndDeleteStorageSession, IBatchedStorageSession, IRetentionStorageSession, IStorageInspectionSession, IExactRetentionStorageSession, IExactRetentionAffectedKeysStorageSession, IPrivilegedCrossScopeQuerySession, ISetMutationStorageSession
{
    private readonly PostgreSqlProviderConnection owner;
    private readonly NpgsqlConnection connection;
    private readonly NpgsqlTransaction? transaction;
    private readonly RelationalSessionExecution execution;
    private readonly RelationalSessionPointReads pointReads;
    private readonly RelationalSessionCrud crud;
    private readonly RelationalSessionQueries queries;
    private readonly RelationalSessionAggregations aggregations;
    private readonly RelationalSessionSetMutations setMutations;
    private readonly RelationalSessionAppends appends;
    private readonly RelationalSessionRetention retention;
    private readonly SchemaSessionLease schemaSession;

    /// <summary>
    /// True when this session was opened through <c>OpenOwnedSession</c> and must return its connection on
    /// disposal. A session from <c>OpenSession</c> is a view over a connection the provider owns, and a
    /// session from a unit of work is owned by that unit — in both cases disposing here would release
    /// something belonging to someone else, so it only closes the session.
    /// </summary>
    private readonly bool ownsConnection;

    internal PostgreSqlStorageSession(
        PostgreSqlProviderConnection owner,
        StorageUnit unit,
        StorageAccess access,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        SchemaSessionLease schemaSession,
        IProviderCommandObserver? observer = null,
        bool ownsConnection = false)
    {
        this.ownsConnection = ownsConnection;
        this.owner = owner;
        Unit = unit;
        Access = access;
        this.connection = connection;
        this.transaction = transaction;
        this.schemaSession = schemaSession;
        commandObserver = observer;
        execution = new RelationalSessionExecution(
            access,
            transaction,
            ownsConnection,
            new PostgreSqlSessionExecutionAdapter(owner, connection, schemaSession),
            nameof(PostgreSqlStorageSession));
        pointReads = new RelationalSessionPointReads(
            unit,
            access,
            UserColumns,
            VersionColumn,
            Command,
            new PostgreSqlPointReadAdapter(this),
            observer,
            "postgresql");
        crud = new RelationalSessionCrud(
            unit,
            UserColumns,
            SequenceColumn,
            VersionColumn,
            "PostgreSQL",
            (key, mode) => ReadCore(key, mode),
            new PostgreSqlCrudAdapter(this));
        queries = new RelationalSessionQueries(
            unit,
            access,
            connection,
            new PostgreSqlQueryRenderer(),
            PhysicalIndexNames,
            FromDatabase,
            AssertExplainPlan,
            observer,
            "postgresql");
        aggregations = new RelationalSessionAggregations(
            unit,
            access,
            connection,
            new PostgreSqlDialect(),
            FromDatabase,
            observer,
            "postgresql.aggregate");
        setMutations = new RelationalSessionSetMutations(
            unit,
            access,
            new PostgreSqlQueryRenderer(),
            unit.Columns.FirstOrDefault(column => column.Name == PostgreSqlSchemaCoordinator.VersionColumn)?.Name,
            Command,
            (command, name, value, column) =>
                Add((NpgsqlCommand)command, name, ConvertValue(value, column), column.Name),
            observer,
            "postgresql");
        appends = new RelationalSessionAppends(unit, access, new PostgreSqlAppendAdapter(this));
        retention = new RelationalSessionRetention(unit, access, new PostgreSqlRetentionAdapter(this));
    }

    /// <summary>
    /// Counts every provider command this session issues. It lives on the session because the session is what
    /// issues commands; it used to be dug out of an individual write's options, which meant a batch observed
    /// only whatever was staged first.
    /// </summary>
    private readonly IProviderCommandObserver? commandObserver;

    private void Observe(string operation, string? commandText, ProviderCommandKind kind, bool isProbe = false) =>
        commandObserver?.Observe(new ProviderCommandEvent(operation, commandText, kind, isProbe));

    public StorageUnit Unit { get; }

    public StorageAccess Access { get; }

    IStorageProviderConnection IProviderBoundStorageSession.ProviderConnection => owner;

    /// <summary>Maps every declared logical index name to the physical name the catalog carries.</summary>
    private IReadOnlyDictionary<string, string> PhysicalIndexNames() => Unit.Indexes.ToDictionary(
        index => index.Name,
        index => PostgreSqlDialect.PhysicalIndexName(Unit.Name, index.Name),
        StringComparer.Ordinal);

    public QueryMaterializedResult Query(QueryRequest request, QueryRenderOptions? options = null) =>
        QueryCore(request, options, RelationalExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask<QueryMaterializedResult> QueryAsync(
        QueryRequest request,
        QueryRenderOptions? options = null,
        CancellationToken cancellationToken = default) =>
        QueryCore(request, options, RelationalExecution.Asynchronous(cancellationToken));

    private ValueTask<QueryMaterializedResult> QueryCore(
        QueryRequest request,
        QueryRenderOptions? options,
        RelationalExecution mode) => Execute(
            () => queries.Query(request, options, execution.Transaction, mode),
            mode);

    public CrossScopeQueryResult QueryAcrossScopes(
        QueryRequest request,
        QueryRenderOptions? options = null) =>
        QueryAcrossScopesCore(request, options, RelationalExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask<CrossScopeQueryResult> QueryAcrossScopesAsync(
        QueryRequest request,
        QueryRenderOptions? options = null,
        CancellationToken cancellationToken = default) =>
        QueryAcrossScopesCore(request, options, RelationalExecution.Asynchronous(cancellationToken));

    private ValueTask<CrossScopeQueryResult> QueryAcrossScopesCore(
        QueryRequest request,
        QueryRenderOptions? options,
        RelationalExecution mode) => Execute(
            () => queries.QueryAcrossScopes(request, options, mode),
            mode);

    public AggregationResult Aggregate(AggregationQuery query) =>
        AggregateCore(query, RelationalExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask<AggregationResult> AggregateAsync(
        AggregationQuery query,
        CancellationToken cancellationToken = default) =>
        AggregateCore(query, RelationalExecution.Asynchronous(cancellationToken));

    private ValueTask<AggregationResult> AggregateCore(AggregationQuery query, RelationalExecution mode) =>
        Execute(() => aggregations.Aggregate(query, execution.Transaction, mode), mode);

    private async ValueTask AssertExplainPlan(RelationalQueryCommand query, QueryRenderOptions options, RelationalExecution mode)
    {
        if (query.IsMatchNone || !ExplainAssertionMode.ShouldAssert(query.SelectedIndex)) return;
        var logicalIndex = query.SelectedIndex!;
        var physicalIndex = options.ResolvePhysicalIndexName(logicalIndex);
        var plans = new List<string>(query.Statements.Length);
        foreach (var statement in query.Statements)
        {
            using var explain = Command(ExplainCommandText(statement));
            RelationalQueryResultReader.AddParameters(explain, query);
            plans.Add(Convert.ToString(
                await mode.ExecuteScalar(explain).ConfigureAwait(false),
                CultureInfo.InvariantCulture) ?? string.Empty);
        }
        var rawPlan = string.Join(Environment.NewLine, plans);
        ExplainAssertionMode.AssertChosenIndex(
            "PostgreSQL", logicalIndex, physicalIndex, query.IndexHintApplied, rawPlan,
            plans.All(plan => PostgreSqlExplainPlanInspector.ChoseIndex(plan, physicalIndex)));
    }

    internal static string ExplainCommandText(string statement) =>
        "EXPLAIN (VERBOSE, FORMAT JSON) " + statement.TrimEnd().TrimEnd(';');

    public StoredEntry? Read(StorageKey key) =>
        ReadEntry(key, RelationalExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask<StoredEntry?> ReadAsync(StorageKey key, CancellationToken cancellationToken = default) =>
        ReadEntry(key, RelationalExecution.Asynchronous(cancellationToken));

    private ValueTask<StoredEntry?> ReadEntry(StorageKey key, RelationalExecution mode)
    {
        pointReads.ValidatePublicRead();
        return Execute(() => pointReads.ReadPublic(key, mode), mode);
    }

    public WriteOutcome Insert(StorageValues values, WriteOptions? options = null) =>
        InsertAsync(values, options, RelationalExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask<WriteOutcome> InsertAsync(
        StorageValues values,
        WriteOptions? options = null,
        CancellationToken cancellationToken = default) =>
        InsertAsync(values, options, RelationalExecution.Asynchronous(cancellationToken));

    private ValueTask<WriteOutcome> InsertAsync(StorageValues values, WriteOptions? options, RelationalExecution mode)
    {
        var operation = crud.PrepareMutation(values, options, RelationalCrudKind.Insert);
        return Mutate(operation, mode);
    }

    public WriteOutcome Update(StorageValues values, WriteOptions? options = null) =>
        UpdateAsync(values, options, RelationalExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask<WriteOutcome> UpdateAsync(
        StorageValues values,
        WriteOptions? options = null,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(values, options, RelationalExecution.Asynchronous(cancellationToken));

    private ValueTask<WriteOutcome> UpdateAsync(StorageValues values, WriteOptions? options, RelationalExecution mode)
    {
        var operation = crud.PrepareMutation(values, options, RelationalCrudKind.Update);
        return Mutate(operation, mode);
    }

    public WriteOutcome Upsert(StorageValues values, WriteOptions? options = null) =>
        UpsertAsync(values, options, RelationalExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask<WriteOutcome> UpsertAsync(
        StorageValues values,
        WriteOptions? options = null,
        CancellationToken cancellationToken = default) =>
        UpsertAsync(values, options, RelationalExecution.Asynchronous(cancellationToken));

    private ValueTask<WriteOutcome> UpsertAsync(StorageValues values, WriteOptions? options, RelationalExecution mode)
    {
        var operation = crud.PrepareMutation(values, options, RelationalCrudKind.Upsert);
        return Mutate(operation, mode);
    }

    public WriteOutcome ConditionalUpsert(StorageValues values, WriteOptions? options = null) =>
        ConditionalUpsertAsync(values, options, RelationalExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask<WriteOutcome> ConditionalUpsertAsync(
        StorageValues values,
        WriteOptions? options = null,
        CancellationToken cancellationToken = default) =>
        ConditionalUpsertAsync(values, options, RelationalExecution.Asynchronous(cancellationToken));

    private async ValueTask<WriteOutcome> ConditionalUpsertAsync(
        StorageValues values,
        WriteOptions? options,
        RelationalExecution mode)
    {
        StorageAccessValidation.EnsurePointOperation(Access, "write");
        WritePreconditionValidator.ValidateWrittenValues(Unit, values.Values);
        WritePreconditionValidator.Validate(Unit, WriteOperation.ConditionalUpsert, options);
        var outcome = await Execute(() => ConditionalUpsertCore(values, options, mode), mode).ConfigureAwait(false);
        if (outcome.Status == WriteOutcomeStatus.Inserted && Unit.Retention?.Trigger == RetentionTrigger.OnAppend)
            await ApplyOnAppendRetention(mode).ConfigureAwait(false);
        return outcome;
    }

    public IReadOnlyList<RowWriteOutcome> ApplyBatch(IReadOnlyList<RowWrite> writes) =>
        ApplyBatchAsync(writes, RelationalExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask<IReadOnlyList<RowWriteOutcome>> ApplyBatchAsync(
        IReadOnlyList<RowWrite> writes,
        bool exactOutcomes,
        CancellationToken cancellationToken = default) =>
        ApplyBatchAsync(writes, RelationalExecution.Asynchronous(cancellationToken));

    private async ValueTask<IReadOnlyList<RowWriteOutcome>> ApplyBatchAsync(
        IReadOnlyList<RowWrite> writes,
        RelationalExecution mode)
    {
        var nativeOnAppend = IsNativeAppendBatch(writes);
        var outcomes = await ExecuteWrite(() => ApplyBatchCore(writes, mode), mode).ConfigureAwait(false);
        if (nativeOnAppend && OnAppendRetentionCoordinator.ContainsAppend(outcomes))
            await ApplyOnAppendRetention(mode).ConfigureAwait(false);
        return outcomes;
    }

    public WriteOutcome Delete(StorageKey key, WriteOptions? options = null) =>
        DeleteAsync(key, options, RelationalExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask<WriteOutcome> DeleteAsync(
        StorageKey key,
        WriteOptions? options = null,
        CancellationToken cancellationToken = default) =>
        DeleteAsync(key, options, RelationalExecution.Asynchronous(cancellationToken));

    private ValueTask<WriteOutcome> DeleteAsync(StorageKey key, WriteOptions? options, RelationalExecution mode)
    {
        var operation = crud.PrepareDelete(key, options);
        return ExecuteWrite(() => crud.Delete(operation, mode), mode);
    }

    public WriteOutcome CompareAndDelete(
        StorageKey key,
        IReadOnlyDictionary<string, object?> expectedValues,
        WriteOptions? options = null) =>
        CompareAndDeleteAsync(key, expectedValues, options, RelationalExecution.Synchronous)
            .GetAwaiter().GetResult();

    public ValueTask<WriteOutcome> CompareAndDeleteAsync(
        StorageKey key,
        IReadOnlyDictionary<string, object?> expectedValues,
        WriteOptions? options = null,
        CancellationToken cancellationToken = default) =>
        CompareAndDeleteAsync(key, expectedValues, options, RelationalExecution.Asynchronous(cancellationToken));

    private ValueTask<WriteOutcome> CompareAndDeleteAsync(
        StorageKey key,
        IReadOnlyDictionary<string, object?> expectedValues,
        WriteOptions? options,
        RelationalExecution mode)
    {
        var canonicalKey = CompareAndDeleteValidation.CanonicalizeKey(Unit, key);
        var expected = CompareAndDeleteValidation.Validate(Unit, canonicalKey, expectedValues, options);
        return ExecuteWrite(async () =>
        {
            // Lock the identity row in the current transaction before classifying a
            // zero-row delete. This keeps absence and comparison mismatch tied to one
            // serializable provider decision even when the surrounding UOW uses
            // PostgreSQL's default ReadCommitted isolation.
            var existing = await ReadCore(canonicalKey, mode, forUpdate: true, observerOperation: "postgresql.compare-and-delete-read").ConfigureAwait(false);
            if (existing is null)
                return new WriteOutcome(WriteOutcomeStatus.NotFound);
            if (options?.Precondition.Kind == WritePreconditionKind.IfVersion &&
                options.Precondition.Version != existing.Version)
                return new WriteOutcome(WriteOutcomeStatus.ConcurrencyConflict, existing.Version);
            if (!RelationalSessionPolicy.MatchesExpected(Unit, existing, expected))
                return new WriteOutcome(WriteOutcomeStatus.ComparisonMismatch, existing.Version);

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
                    parameters[parameter] = ConvertValue(pair.Value, Column(pair.Key));
                }
            }
            if (VersionColumn is not null && options?.Precondition.Kind == WritePreconditionKind.IfVersion)
            {
                where += $" AND {Quote(VersionColumn.Name)}=@expected";
                parameters["@expected"] = options.Precondition.Version!.Value;
            }

            using var command = Command($"DELETE FROM {Quote(Unit.Name)} WHERE {where};");
            AddParameters(command, parameters);
            commandObserver?.Observe(new ProviderCommandEvent("postgresql.compare-and-delete", command.CommandText, ProviderCommandKind.Write, IsProbe: false));
            return (await mode.ExecuteNonQuery(command).ConfigureAwait(false)) != 0
                ? new WriteOutcome(WriteOutcomeStatus.Deleted, existing.Version)
                : new WriteOutcome(WriteOutcomeStatus.ConcurrencyConflict, existing.Version);
        }, mode);
    }

    public SetMutationResult UpdateWhere(Predicate where, IReadOnlyDictionary<string, object?> assignments) =>
        UpdateWhere(where, assignments, RelationalExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask<SetMutationResult> UpdateWhereAsync(
        Predicate where,
        IReadOnlyDictionary<string, object?> assignments,
        CancellationToken cancellationToken = default) =>
        UpdateWhere(where, assignments, RelationalExecution.Asynchronous(cancellationToken));

    private ValueTask<SetMutationResult> UpdateWhere(
        Predicate where,
        IReadOnlyDictionary<string, object?> assignments,
        RelationalExecution mode)
    {
        var operation = setMutations.PrepareUpdateWhere(where, assignments);
        return ExecuteWrite(() => operation(mode), mode);
    }

    public SetMutationResult DeleteWhere(Predicate where) =>
        DeleteWhere(where, RelationalExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask<SetMutationResult> DeleteWhereAsync(
        Predicate where,
        CancellationToken cancellationToken = default) =>
        DeleteWhere(where, RelationalExecution.Asynchronous(cancellationToken));

    private ValueTask<SetMutationResult> DeleteWhere(Predicate where, RelationalExecution mode)
    {
        var operation = setMutations.PrepareDeleteWhere(where);
        return ExecuteWrite(() => operation(mode), mode);
    }

    public RetentionResult ApplyRetention(RetentionExecutionOptions? options = null) =>
        ApplyRetention(options, RelationalExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask<RetentionResult> ApplyRetentionAsync(RetentionExecutionOptions? options = null) =>
        ApplyRetention(options, RelationalExecution.Asynchronous(options?.CancellationToken ?? CancellationToken.None));

    private ValueTask<RetentionResult> ApplyRetention(
        RetentionExecutionOptions? options,
        RelationalExecution mode)
    {
        var operation = retention.Prepare(options);
        return ExecuteWrite(() => retention.Apply(operation, mode), mode);
    }

    public StorageInspection Inspect() =>
        InspectCore(RelationalExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask<StorageInspection> InspectAsync(CancellationToken cancellationToken = default) =>
        InspectCore(RelationalExecution.Asynchronous(cancellationToken));

    private ValueTask<StorageInspection> InspectCore(RelationalExecution mode) => Execute(async () =>
    {
        StorageAccessValidation.EnsurePointOperation(Access, "inspect");
        StorageInspectionSessionExtensions.EnsureProviderSequence(Unit);
        await EnsureHighWaterTable(mode).ConfigureAwait(false);
        using var command = Command($"SELECT {Quote(HighWaterValue)} FROM {Quote(HighWaterTable)} WHERE {Quote(LedgerUnit)}=@unit AND {Quote(LedgerScope)}=@scope;");
        Add(command, "unit", Unit.Id.Value);
        Add(command, "scope", Access.Scope?.Value ?? string.Empty);
        var value = await mode.ExecuteScalar(command).ConfigureAwait(false);
        return value is null or DBNull
            ? new StorageInspection(null)
            : new StorageInspection(Convert.ToInt64(value, CultureInfo.InvariantCulture));
    }, mode);

    public RetentionOperationResult ApplyRetention(OperationId operationId, RetentionExecutionOptions? options = null) =>
        ApplyRetention(operationId, options, RelationalExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask<RetentionOperationResult> ApplyRetentionAsync(
        OperationId operationId,
        RetentionExecutionOptions? options = null) =>
        ApplyRetention(operationId, options,
            RelationalExecution.Asynchronous(options?.CancellationToken ?? CancellationToken.None));

    private ValueTask<RetentionOperationResult> ApplyRetention(
        OperationId operationId,
        RetentionExecutionOptions? options,
        RelationalExecution mode)
    {
        var operation = retention.PrepareExact(operationId, options);
        return ExecuteWrite(() => retention.ApplyExact(operation, mode), mode);
    }

    public WriteOutcome Append(OperationId operationId, IReadOnlyList<StorageValues> values) =>
        AppendAsync(operationId, values, RelationalExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask<WriteOutcome> AppendAsync(
        OperationId operationId,
        IReadOnlyList<StorageValues> values,
        CancellationToken cancellationToken = default) =>
        AppendAsync(operationId, values, RelationalExecution.Asynchronous(cancellationToken));

    private async ValueTask<WriteOutcome> AppendAsync(
        OperationId operationId,
        IReadOnlyList<StorageValues> values,
        RelationalExecution mode)
    {
        var operation = appends.Prepare(operationId, values, exactOutcomes: false);
        var onAppend = Unit.Retention?.Trigger == RetentionTrigger.OnAppend;
        var registration = BeginOnAppend(onAppend);
        RelationalAppendResult result;
        try
        {
            result = await ExecuteWrite(() => appends.Append(operation, mode), mode).ConfigureAwait(false);
        }
        catch
        {
            await CompleteOnAppend(registration, cleanupRequired: false, mode).ConfigureAwait(false);
            throw;
        }
        await CompleteOnAppend(
            registration,
            onAppend && result.Status is WriteOutcomeStatus.Inserted or WriteOutcomeStatus.Replayed,
            mode).ConfigureAwait(false);
        return new WriteOutcome(result.Status);
    }

    public AppendOutcomeReport AppendWithOutcomes(OperationId operationId, IReadOnlyList<StorageValues> values) =>
        AppendWithOutcomesAsync(operationId, values, RelationalExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask<AppendOutcomeReport> AppendWithOutcomesAsync(
        OperationId operationId,
        IReadOnlyList<StorageValues> values,
        CancellationToken cancellationToken = default) =>
        AppendWithOutcomesAsync(operationId, values, RelationalExecution.Asynchronous(cancellationToken));

    private async ValueTask<AppendOutcomeReport> AppendWithOutcomesAsync(
        OperationId operationId,
        IReadOnlyList<StorageValues> values,
        RelationalExecution mode)
    {
        var operation = appends.Prepare(operationId, values, exactOutcomes: true);
        var onAppend = Unit.Retention?.Trigger == RetentionTrigger.OnAppend;
        var registration = BeginOnAppend(onAppend);
        AppendOutcomeReport outcome;
        try
        {
            outcome = await ExecuteWrite(async () => (await appends.Append(
                operation, mode).ConfigureAwait(false)).ToReport(), mode).ConfigureAwait(false);
        }
        catch
        {
            await CompleteOnAppend(registration, cleanupRequired: false, mode).ConfigureAwait(false);
            throw;
        }
        await CompleteOnAppend(
            registration,
            onAppend && outcome.Status is WriteOutcomeStatus.Inserted or WriteOutcomeStatus.Replayed,
            mode).ConfigureAwait(false);
        return outcome;
    }

    private async ValueTask<IReadOnlyList<RowWriteOutcome>> InsertAppendSequenceBatch(
        IReadOnlyList<RowWrite> writes,
        RelationalExecution mode)
    {
        ArgumentNullException.ThrowIfNull(writes);
        if (writes.Count == 0)
            return [];

        var prepared = writes.Select(write =>
        {
            var values = new StorageValues(SearchKeyProjection.Populate(Unit, write.Values!.Values));
            RelationalSessionPolicy.ValidateValues(Unit, UserColumns, "PostgreSQL", values.Values, requireAllNonNullable: true);
            return (Write: write, Values: values);
        }).ToArray();
        var outcomes = new List<RowWriteOutcome>(writes.Count);
        long? highWater = null;

        for (var start = 0; start < prepared.Length;)
        {
            var columns = SequencePhysicalColumns(prepared[start].Values);
            var end = start + 1;
            while (end < prepared.Length &&
                   SequencePhysicalColumns(prepared[end].Values).Select(column => column.Name)
                       .SequenceEqual(columns.Select(column => column.Name), StringComparer.Ordinal))
                end++;

            var maxRows = Math.Max(1, Math.Min(1_000, 32_000 / (columns.Count + 1)));
            foreach (var chunk in prepared[start..end].Chunk(maxRows))
            {
                var result = await InsertAppendSequenceBatchChunk(chunk, columns, mode).ConfigureAwait(false);
                outcomes.AddRange(result.Outcomes);
                if (result.HighWater is { } value)
                    highWater = highWater is null ? value : Math.Max(highWater.Value, value);
                if (result.HighWater is null)
                    return outcomes;
            }

            start = end;
        }

        if (highWater is { } finalHighWater)
            await RecordHighWaterBatch(finalHighWater, mode).ConfigureAwait(false);
        return outcomes;
    }

    private async ValueTask<(long? HighWater, IReadOnlyList<RowWriteOutcome> Outcomes)> InsertAppendSequenceBatchChunk(
        IReadOnlyList<(RowWrite Write, StorageValues Values)> writes,
        IReadOnlyList<ColumnDefinition> columns,
        RelationalExecution mode)
    {
        // RETURNING does not expose an INSERT...SELECT source ordinal. Allocate identity values in a
        // set-based statement that returns the ordinal explicitly, then insert those values through
        // the BY DEFAULT identity column. This keeps correlation independent of provider row order.
        var generated = await AllocateSequenceRange(writes.Count, mode).ConfigureAwait(false);
        var insertColumns = new[] { SequenceColumn! }.Concat(columns).ToArray();
        using var command = Command(string.Empty);
        var rows = new List<string>(writes.Count);
        for (var row = 0; row < writes.Count; row++)
        {
            var physical = PhysicalValues(writes[row].Values.Values, includeVersion: VersionColumn is not null);
            var parameters = new List<string>(insertColumns.Length);
            foreach (var column in insertColumns)
            {
                var name = $"r{row}_{column.Name}";
                parameters.Add("@" + name);
                var value = column == SequenceColumn
                    ? generated[row]
                    : physical[column.Name];
                Add(command, name, value, column.Name);
            }
            rows.Add($"({string.Join(", ", parameters)})");
        }

        command.CommandText = $"INSERT INTO {Quote(Unit.Name)} ({string.Join(", ", insertColumns.Select(column => Quote(column.Name)))}) VALUES {string.Join(", ", rows)};";
        commandObserver?.Observe(new ProviderCommandEvent("postgresql.generated-sequence-batch", "PostgreSQL correlated generated-sequence INSERT", ProviderCommandKind.Write, IsProbe: false));
        try
        {
            if (await mode.ExecuteNonQuery(command).ConfigureAwait(false) != writes.Count)
                throw new InvalidOperationException("PostgreSQL generated-sequence batch did not insert every allocated row.");
            return (generated.Max(), writes.Select((write, index) => new RowWriteOutcome(
                write.Write,
                new WriteOutcome(
                    WriteOutcomeStatus.Inserted,
                    VersionColumn is null ? null : 1,
                    generatedValues: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        [SequenceColumn!.Name] = generated[index]
                    }))).ToArray());
        }
        catch (DbException exception) when (new PostgreSqlDialect().TryMapUniqueViolation(exception, out var indexName))
        {
            return (null, writes.Select(write => new RowWriteOutcome(
                write.Write,
                new WriteOutcome(WriteOutcomeStatus.UniqueViolation, null, LogicalIndexName(indexName)))).ToArray());
        }
    }

    private async ValueTask<long[]> AllocateSequenceRange(int count, RelationalExecution mode)
    {
        using var command = Command(string.Empty);
        var rows = new List<string>(count);
        for (var row = 0; row < count; row++)
        {
            var name = "sequence_ordinal_" + row;
            rows.Add("(@" + name + ")");
            AddTyped(command, name, row, NpgsqlDbType.Integer);
        }
        AddTyped(command, "sequence_table", Quote(Unit.Name), NpgsqlDbType.Text);
        AddTyped(command, "sequence_column", SequenceColumn!.Name, NpgsqlDbType.Text);
        command.CommandText = $"SELECT source.{Quote("ordinal")}, nextval(pg_get_serial_sequence(@sequence_table, @sequence_column)) " +
            $"FROM (VALUES {string.Join(", ", rows)}) AS source ({Quote("ordinal")}) ORDER BY source.{Quote("ordinal")};";
        commandObserver?.Observe(new ProviderCommandEvent("postgresql.generated-sequence-allocation", "PostgreSQL correlated generated-sequence allocation", ProviderCommandKind.Write, IsProbe: false));

        var generated = new long[count];
        var seen = new bool[count];
        await using var readerScope = await mode.ExecuteReader(command).ConfigureAwait(false);
        var reader = readerScope.Reader;
        var returned = 0;
        while (await mode.Read(reader).ConfigureAwait(false))
        {
            var ordinal = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
            var value = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
            if (ordinal < 0 || ordinal >= count || seen[ordinal])
                throw new InvalidOperationException("PostgreSQL generated-sequence allocation returned an invalid input ordinal.");
            seen[ordinal] = true;
            generated[ordinal] = value;
            returned++;
        }
        if (returned != count || seen.Any(item => !item))
            throw new InvalidOperationException("PostgreSQL generated-sequence allocation did not return every input ordinal.");
        return generated;
    }

    private async ValueTask RecordHighWaterBatch(long generatedValue, RelationalExecution mode)
    {
        if (SequenceColumn is null)
            return;
        await EnsureHighWaterTable(mode).ConfigureAwait(false);
        using var command = Command($"INSERT INTO {Quote(HighWaterTable)} ({Quote(LedgerUnit)}, {Quote(LedgerScope)}, {Quote(HighWaterValue)}) VALUES (@unit, @scope, @value) ON CONFLICT ({Quote(LedgerUnit)}, {Quote(LedgerScope)}) DO UPDATE SET {Quote(HighWaterValue)}=GREATEST({Quote(HighWaterTable)}.{Quote(HighWaterValue)}, EXCLUDED.{Quote(HighWaterValue)});");
        AddTyped(command, "unit", Unit.Id.Value, NpgsqlDbType.Text);
        AddTyped(command, "scope", Access.Scope?.Value ?? string.Empty, NpgsqlDbType.Text);
        AddTyped(command, "value", generatedValue, NpgsqlDbType.Bigint);
        commandObserver?.Observe(new ProviderCommandEvent("postgresql.generated-sequence-high-water", "PostgreSQL generated-sequence high-water", ProviderCommandKind.Write, IsProbe: false));
        await mode.ExecuteNonQuery(command).ConfigureAwait(false);
    }

    private IReadOnlyList<ColumnDefinition> SequencePhysicalColumns(StorageValues values) =>
        PhysicalValues(values.Values, includeVersion: VersionColumn is not null)
            .Keys.Select(Column)
            .ToArray();

    private async ValueTask EnsureLedgerTable(string table, RelationalExecution mode)
    {
        if (await LedgerIsCurrent(table, mode).ConfigureAwait(false))
            return;
        await ClaimLazyDdl(table, mode).ConfigureAwait(false);
        if (await LedgerIsCurrent(table, mode).ConfigureAwait(false))
            return;

        using (var command = Command($"CREATE TABLE IF NOT EXISTS {Quote(table)} (" +
            $"{Quote(LedgerUnit)} text NOT NULL, " +
            $"{Quote(LedgerScope)} text NOT NULL, " +
            $"{Quote(LedgerNonce)} text NOT NULL, " +
            $"{Quote(LedgerCommittedAt)} text NOT NULL, " +
            $"{Quote(LedgerFingerprint)} text NULL, " +
            $"{Quote(LedgerResult)} text NULL, " +
            $"PRIMARY KEY ({Quote(LedgerUnit)}, {Quote(LedgerScope)}, {Quote(LedgerNonce)}));"))
            await mode.ExecuteNonQuery(command).ConfigureAwait(false);

        await EnsureLedgerColumn(table, LedgerFingerprint, mode).ConfigureAwait(false);
        await EnsureLedgerColumn(table, LedgerResult, mode).ConfigureAwait(false);

        using var cleanupIndex = Command($"CREATE INDEX IF NOT EXISTS {Quote(IdempotencyRules.CleanupIndexName(table))} " +
            $"ON {Quote(table)} ({Quote(LedgerUnit)}, {Quote(LedgerCommittedAt)});");
        await mode.ExecuteNonQuery(cleanupIndex).ConfigureAwait(false);
    }

    /// <summary>
    /// Reports whether the ledger already carries every object the bootstrap would create:
    /// the table, its two additive columns, and the cleanup index. Reading the catalog takes
    /// no lock, so a steady-state append issues no DDL at all.
    /// </summary>
    private async ValueTask<bool> LedgerIsCurrent(string table, RelationalExecution mode)
    {
        using var command = Command(
            "SELECT to_regclass(@ledger) IS NOT NULL AND to_regclass(@cleanup_index) IS NOT NULL AND (" +
            "SELECT count(*) FROM pg_catalog.pg_attribute WHERE attrelid = to_regclass(@ledger) " +
            "AND NOT attisdropped AND attname::text IN (@fingerprint, @result)) = 2;");
        Add(command, "ledger", Quote(table));
        Add(command, "cleanup_index", Quote(IdempotencyRules.CleanupIndexName(table)));
        Add(command, "fingerprint", LedgerFingerprint);
        Add(command, "result", LedgerResult);
        return await mode.ExecuteScalar(command).ConfigureAwait(false) is true;
    }

    /// <summary>
    /// Serializes lazy, write-path DDL on the name of the object it creates.
    /// PostgreSQL's <c>IF NOT EXISTS</c> is check-then-act rather than atomic: concurrent
    /// creators all pass the check, and every loser fails with <c>23505</c> on a shared catalog
    /// index instead of returning a Groundwork status. A transaction-scoped advisory lock lets
    /// exactly one writer create the object, and the writers behind it observe it created.
    /// </summary>
    private async ValueTask ClaimLazyDdl(string resource, RelationalExecution mode)
    {
        using var command = Command("SELECT pg_advisory_xact_lock(hashtextextended(@resource, 0));");
        Add(command, "resource", LazyDdlLockPrefix + resource);
        await mode.ExecuteNonQuery(command).ConfigureAwait(false);
    }

    /// <summary>
    /// Additive upgrade of a ledger created before the exact-outcome columns existed.
    /// Unlike <c>CREATE TABLE</c>/<c>CREATE INDEX</c>, <c>ADD COLUMN IF NOT EXISTS</c> re-reads the
    /// column list after taking the table's ACCESS EXCLUSIVE lock, so concurrent callers do not
    /// race; only the bootstrap around it needs serializing.
    /// </summary>
    private async ValueTask EnsureLedgerColumn(string table, string column, RelationalExecution mode)
    {
        using var alter = Command($"ALTER TABLE {Quote(table)} ADD COLUMN IF NOT EXISTS {Quote(column)} text NULL;");
        await mode.ExecuteNonQuery(alter).ConfigureAwait(false);
    }

    private async ValueTask EnsureHighWaterTable(RelationalExecution mode)
    {
        if (await TableExists(HighWaterTable, mode).ConfigureAwait(false))
            return;
        await ClaimLazyDdl(HighWaterTable, mode).ConfigureAwait(false);
        if (await TableExists(HighWaterTable, mode).ConfigureAwait(false))
            return;

        using var command = Command($"CREATE TABLE IF NOT EXISTS {Quote(HighWaterTable)} (" +
            $"{Quote(LedgerUnit)} text NOT NULL, " +
            $"{Quote(LedgerScope)} text NOT NULL, " +
            $"{Quote(HighWaterValue)} bigint NOT NULL, " +
            $"PRIMARY KEY ({Quote(LedgerUnit)}, {Quote(LedgerScope)}));");
        await mode.ExecuteNonQuery(command).ConfigureAwait(false);
    }

    private async ValueTask<bool> TableExists(string table, RelationalExecution mode)
    {
        using var command = Command("SELECT to_regclass(@table) IS NOT NULL;");
        Add(command, "table", Quote(table));
        return await mode.ExecuteScalar(command).ConfigureAwait(false) is true;
    }

    private async ValueTask RecordHighWater(object? generatedValue, RelationalExecution mode)
    {
        if (SequenceColumn is null || generatedValue is null)
            return;
        await EnsureHighWaterTable(mode).ConfigureAwait(false);
        using var command = Command($"INSERT INTO {Quote(HighWaterTable)} ({Quote(LedgerUnit)}, {Quote(LedgerScope)}, {Quote(HighWaterValue)}) VALUES (@unit, @scope, @value) ON CONFLICT ({Quote(LedgerUnit)}, {Quote(LedgerScope)}) DO UPDATE SET {Quote(HighWaterValue)}=GREATEST({Quote(HighWaterTable)}.{Quote(HighWaterValue)}, EXCLUDED.{Quote(HighWaterValue)});");
        Add(command, "unit", Unit.Id.Value);
        Add(command, "scope", Access.Scope?.Value ?? string.Empty);
        Add(command, "value", Convert.ToInt64(generatedValue, CultureInfo.InvariantCulture));
        await mode.ExecuteNonQuery(command).ConfigureAwait(false);
    }

    private void AddLedgerParameters(NpgsqlCommand command, string unit, string scope, string nonce)
    {
        Add(command, "unit", unit);
        Add(command, "scope", scope);
        Add(command, "nonce", nonce);
    }

    private async ValueTask<DateTimeOffset> ProviderNow(RelationalExecution mode)
    {
        using var command = Command("SELECT clock_timestamp();");
        var value = await mode.ExecuteScalar(command).ConfigureAwait(false);
        return value switch
        {
            DateTimeOffset timestamp => timestamp.ToUniversalTime(),
            DateTime timestamp => new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)),
            _ => DateTimeOffset.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)
        };
    }

    private static string FormatLedgerTime(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

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

    private ValueTask<IReadOnlyList<RowWriteOutcome>> ApplyBatchCore(IReadOnlyList<RowWrite> writes, RelationalExecution mode)
    {
        ArgumentNullException.ThrowIfNull(writes);
        if (writes.Count == 0)
            return new ValueTask<IReadOnlyList<RowWriteOutcome>>([]);
        if (SequenceColumn is not null)
            return ApplyBatchFallback(writes, mode);
        if (writes.Any(write => write.Options.Precondition.Kind != WritePreconditionKind.Unconditional))
            return ApplyBatchFallback(writes, mode);
        if (RelationalSessionPolicy.HasSecondaryUniqueIndex(writes[0].Unit))
            return ApplyBatchFallback(writes, mode);
        var physicalWrites = writes.Select(write => write.PopulateSearchKeyValues()).ToArray();
        return physicalWrites[0].Mode switch
        {
            RowWriteMode.Insert => ApplyInsertBatch(physicalWrites, mode),
            RowWriteMode.Upsert => ApplyUpsertBatch(physicalWrites, mode),
            _ => ApplyBatchFallback(writes, mode)
        };
    }

    private bool IsNativeAppendBatch(IReadOnlyList<RowWrite> writes) =>
        Unit.Retention?.Trigger == RetentionTrigger.OnAppend &&
        writes.Count != 0 &&
        SequenceColumn is null &&
        writes.All(write => write.Options.Precondition.Kind == WritePreconditionKind.Unconditional) &&
        !RelationalSessionPolicy.HasSecondaryUniqueIndex(writes[0].Unit) &&
        writes.Select(write => write.ColumnSet).Distinct(StringComparer.Ordinal).Count() == 1 &&
        writes[0].Mode is RowWriteMode.Insert or RowWriteMode.Upsert;

    private async ValueTask<IReadOnlyList<RowWriteOutcome>> ApplyInsertBatch(IReadOnlyList<RowWrite> writes, RelationalExecution mode)
    {
        var columns = PhysicalValues(writes[0].Values!.Values, includeVersion: VersionColumn is not null).Keys.ToArray();
        foreach (var write in writes)
        {
            RelationalSessionPolicy.ValidateValues(Unit, UserColumns, "PostgreSQL", write.Values!.Values, requireAllNonNullable: true);
            if (!PhysicalValues(write.Values.Values, includeVersion: VersionColumn is not null).Keys.SequenceEqual(columns, StringComparer.Ordinal))
                return await ApplyBatchFallback(writes, mode).ConfigureAwait(false);
        }
        var maxRows = Math.Max(1, Math.Min(1_000, 32_000 / columns.Length));
        if (writes.Count > maxRows)
        {
            var chunked = new List<RowWriteOutcome>(writes.Count);
            foreach (var chunk in writes.Chunk(maxRows))
                chunked.AddRange(await ApplyInsertBatch(chunk, mode).ConfigureAwait(false));
            return chunked;
        }
        using var command = Command(string.Empty);
        var rows = AddBatchValues(command, writes, columns);
        var returning = string.Join(", ", Unit.Key.Columns.Select(Quote).Concat(
            VersionColumn is null ? [] : [Quote(VersionColumn.Name)]));
        command.CommandText = $"INSERT INTO {Quote(Unit.Name)} ({string.Join(", ", columns.Select(Quote))}) VALUES {string.Join(", ", rows)} ON CONFLICT DO NOTHING RETURNING {returning};";
        commandObserver?.Observe(new ProviderCommandEvent("postgresql.batch-insert", "PostgreSQL multi-row INSERT", ProviderCommandKind.Write, IsProbe: false));
        try
        {
            var returned = await ReadReturnedRows(command, writes[0].Unit, mode).ConfigureAwait(false);
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

    private async ValueTask<IReadOnlyList<RowWriteOutcome>> ApplyUpsertBatch(IReadOnlyList<RowWrite> writes, RelationalExecution mode)
    {
        var columns = PhysicalValues(writes[0].Values!.Values, includeVersion: VersionColumn is not null).Keys.ToArray();
        foreach (var write in writes)
        {
            RelationalSessionPolicy.ValidateValues(Unit, UserColumns, "PostgreSQL", write.Values!.Values, requireAllNonNullable: false);
            if (!PhysicalValues(write.Values.Values, includeVersion: VersionColumn is not null).Keys.SequenceEqual(columns, StringComparer.Ordinal))
                return await ApplyBatchFallback(writes, mode).ConfigureAwait(false);
        }
        var maxRows = Math.Max(1, Math.Min(1_000, 32_000 / columns.Length));
        if (writes.Count > maxRows)
        {
            var chunked = new List<RowWriteOutcome>(writes.Count);
            foreach (var chunk in writes.Chunk(maxRows))
                chunked.AddRange(await ApplyUpsertBatch(chunk, mode).ConfigureAwait(false));
            return chunked;
        }
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
        commandObserver?.Observe(new ProviderCommandEvent("postgresql.batch-upsert", "PostgreSQL multi-row INSERT ON CONFLICT", ProviderCommandKind.Write, IsProbe: false));
        try
        {
            var returned = await ReadReturnedRows(command, writes[0].Unit, mode).ConfigureAwait(false);
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
                Add(command, name, physical[column], column);
            }
            rows.Add($"({string.Join(", ", parameters)})");
        }
        return rows;
    }

    private async ValueTask<Dictionary<string, long?>> ReadReturnedRows(NpgsqlCommand command, StorageUnit logicalUnit, RelationalExecution mode)
    {
        var returned = new Dictionary<string, long?>(StringComparer.Ordinal);
        await using var readerScope = await mode.ExecuteReader(command).ConfigureAwait(false);
        var reader = readerScope.Reader;
        while (await mode.Read(reader).ConfigureAwait(false))
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var index = 0; index < Unit.Key.Columns.Count; index++)
            {
                var column = Unit.Key.Columns[index];
                if (column == PostgreSqlSchemaCoordinator.ScopeColumn)
                    values[column] = Access.Scope!.Value;
                else
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


    private async ValueTask<IReadOnlyList<RowWriteOutcome>> ApplyBatchFallback(IReadOnlyList<RowWrite> writes, RelationalExecution mode)
    {
        // #207's captured 23-hour dump (PID 25945) showed ApplyBatchAsync -> ExecuteWrite holding
        // the PostgreSQL connection SemaphoreSlim while ApplyBatchFallback -> ConditionalUpsertAsync
        // -> Execute tried to acquire that same non-reentrant gate. Keep the fallback in an operation-
        // local scope so it reuses the active transaction instead of waiting on itself.
        var outcomes = new List<RowWriteOutcome>(writes.Count);
        using (execution.EnterBatchFallback())
        {
            foreach (var write in writes)
            {
                outcomes.Add(new RowWriteOutcome(write, write.Mode switch
                {
                    RowWriteMode.Insert => await InsertAsync(write.Values!, write.Options, mode).ConfigureAwait(false),
                    RowWriteMode.Update => await UpdateAsync(write.Values!, write.Options, mode).ConfigureAwait(false),
                    RowWriteMode.Upsert when write.Options.Precondition.Kind == WritePreconditionKind.IfVersion =>
                        await ConditionalUpsertAsync(write.Values!, write.Options, mode).ConfigureAwait(false),
                    RowWriteMode.Upsert => await UpsertAsync(write.Values!, write.Options, mode).ConfigureAwait(false),
                    RowWriteMode.ConditionalUpsert => await ConditionalUpsertAsync(write.Values!, write.Options, mode).ConfigureAwait(false),
                    RowWriteMode.Delete => await DeleteAsync(write.Key!, write.Options, mode).ConfigureAwait(false),
                    RowWriteMode.CompareAndDelete => await CompareAndDeleteAsync(write.Key!, write.ExpectedValues, write.Options, mode).ConfigureAwait(false),
                    _ => throw new ArgumentOutOfRangeException(nameof(writes), write.Mode, null)
                }));
            }
            return outcomes;
        }
    }

    private async ValueTask<WriteOutcome> Mutate(RelationalCrudMutation operation, RelationalExecution mode)
    {
        var outcome = await ExecuteWrite(() => crud.Mutate(operation, mode), mode).ConfigureAwait(false);
        if (outcome.Succeeded && Unit.Retention?.Trigger == RetentionTrigger.OnAppend &&
            operation.Kind is RelationalCrudKind.Insert or RelationalCrudKind.Upsert)
            await ApplyOnAppendRetention(mode).ConfigureAwait(false);
        return outcome;
    }

    private ValueTask ApplyOnAppendRetention(RelationalExecution mode)
    {
        async ValueTask Cleanup() =>
            await ApplyRetention(new RetentionExecutionOptions(), mode).ConfigureAwait(false);
        return transaction is null
            ? OnAppendRetentionCoordinator.Run(owner, Unit, Access.Scope?.Value, Cleanup)
            : Cleanup();
    }

    private OnAppendRetentionCoordinator.AppendRegistration? BeginOnAppend(bool eligible)
    {
        StorageAccessValidation.EnsurePointOperation(Access, "write");
        return eligible && transaction is null
            ? OnAppendRetentionCoordinator.Begin(owner, Unit, Access.Scope?.Value)
            : null;
    }

    private ValueTask CompleteOnAppend(
        OnAppendRetentionCoordinator.AppendRegistration? registration,
        bool cleanupRequired,
        RelationalExecution mode)
    {
        async ValueTask Cleanup() =>
            await ApplyRetention(new RetentionExecutionOptions(), mode).ConfigureAwait(false);
        if (registration is not null)
            return registration.Complete(cleanupRequired, Cleanup);
        if (!cleanupRequired)
            return ValueTask.CompletedTask;
        return transaction is null
            ? OnAppendRetentionCoordinator.Run(owner, Unit, Access.Scope?.Value, Cleanup)
            : Cleanup();
    }

    private async ValueTask<WriteOutcome> ConditionalUpsertCore(StorageValues values, WriteOptions? options, RelationalExecution mode)
    {
        ArgumentNullException.ThrowIfNull(values);
        values = new StorageValues(SearchKeyProjection.Populate(Unit, values.Values));
        RelationalSessionPolicy.ValidateValues(Unit, UserColumns, "PostgreSQL", values.Values, requireAllNonNullable: false);
        if (options?.Precondition.Kind == WritePreconditionKind.IfVersion && VersionColumn is null)
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
        var actionPredicate = VersionColumn is null || options?.Precondition.Kind == WritePreconditionKind.Unconditional
            ? string.Empty
            : options?.Precondition.Kind == WritePreconditionKind.CreateOnly
                ? " WHERE FALSE"
                : $" WHERE {Quote(Unit.Name)}.{Quote(VersionColumn.Name)}=@expected::bigint";
        var source = VersionColumn is null || options?.Precondition.Version is null
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
            Add(command, "expected", options?.Precondition.Version);
        commandObserver?.Observe(new ProviderCommandEvent("postgresql.conditional-upsert", sql, ProviderCommandKind.Write, IsProbe: false));
        try
        {
            await using var readerScope = await mode.ExecuteReader(command).ConfigureAwait(false);
            var reader = readerScope.Reader;
            if (!(await mode.Read(reader).ConfigureAwait(false)))
                return DeferredConflict(key);

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

    private WriteOutcome DeferredConflict(StorageKey key) =>
        WriteOutcome.Deferred(
            WriteOutcomeStatus.ConcurrencyConflict,
            null,
            () =>
            {
                schemaSession.EnsureCurrent();
                var existing = ReadCore(key, RelationalExecution.Synchronous).GetAwaiter().GetResult();
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

    private async ValueTask<WriteOutcome> InsertCore(
        StorageValues values,
        RelationalExecution mode,
        WriteOutcomeStatus status = WriteOutcomeStatus.Inserted)
    {
        var physical = PhysicalValues(values.Values, includeVersion: VersionColumn is not null);
        var columns = physical.Keys.ToArray();
        var returning = SequenceColumn is null ? string.Empty : $" RETURNING {Quote(SequenceColumn.Name)};";
        var sql = columns.Length == 0
            ? $"INSERT INTO {Quote(Unit.Name)} DEFAULT VALUES{returning}"
            : $"INSERT INTO {Quote(Unit.Name)} ({string.Join(", ", columns.Select(Quote))}) VALUES ({string.Join(", ", columns.Select(column => "@" + column))}){returning}";
        using var command = Command(sql);
        AddParameters(command, physical);
        commandObserver?.Observe(new ProviderCommandEvent("postgresql.insert", sql, ProviderCommandKind.Write, IsProbe: false));
        try
        {
            if (SequenceColumn is null)
            {
                await mode.ExecuteNonQuery(command).ConfigureAwait(false);
                return new WriteOutcome(status, VersionColumn is null ? null : 1);
            }

            object? generatedValue;
            await using (var readerScope = await mode.ExecuteReader(command).ConfigureAwait(false))
            {
                var reader = readerScope.Reader;
                if (!(await mode.Read(reader).ConfigureAwait(false)))
                    return new WriteOutcome(WriteOutcomeStatus.UniqueViolation);
                generatedValue = FromDatabase(reader.GetValue(0), SequenceColumn);
            }
            await RecordHighWater(generatedValue, mode).ConfigureAwait(false);
            return new WriteOutcome(
                status,
                VersionColumn is null ? null : 1,
                generatedValues: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [SequenceColumn.Name] = generatedValue
                });
        }
        catch (DbException exception) when (new PostgreSqlDialect().TryMapUniqueViolation(exception, out _))
        {
            return new WriteOutcome(WriteOutcomeStatus.UniqueViolation);
        }
    }

    private async ValueTask<WriteOutcome> DeleteCore(
        StorageKey key,
        StoredEntry? existing,
        WriteOptions? options,
        RelationalExecution mode)
    {
        var (where, parameters) = KeyPredicate(key.Values);
        if (VersionColumn is not null && options?.Precondition.Kind == WritePreconditionKind.IfVersion)
        {
            where += $" AND {Quote(VersionColumn.Name)}=@expected";
            parameters["@expected"] = options.Precondition.Version!.Value;
        }
        using var command = Command($"DELETE FROM {Quote(Unit.Name)} WHERE {where};");
        AddParameters(command, parameters);
        commandObserver?.Observe(new ProviderCommandEvent(
            "postgresql.delete",
            command.CommandText,
            ProviderCommandKind.Write,
            IsProbe: false));
        var affected = await mode.ExecuteNonQuery(command).ConfigureAwait(false);
        if (affected != 0)
            return new WriteOutcome(WriteOutcomeStatus.Deleted, existing?.Version);
        return new WriteOutcome(
            Unit.Concurrency.IsNone ? WriteOutcomeStatus.NotFound : WriteOutcomeStatus.ConcurrencyConflict,
            existing?.Version);
    }

    private async ValueTask<WriteOutcome> UpdateCore(
        StorageValues values,
        StorageKey key,
        StoredEntry? existing,
        WriteOptions? options,
        RelationalExecution mode)
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
            if (options?.Precondition.Kind == WritePreconditionKind.IfVersion)
            {
                where += $" AND {Quote(VersionColumn.Name)}=@expected";
                parameters["@expected"] = options.Precondition.Version!.Value;
            }
        }
        if (sets.Count == 0)
        {
            var noOpColumn = Unit.Key.Columns.First(column =>
                column != PostgreSqlSchemaCoordinator.ScopeColumn);
            sets.Add($"{Quote(noOpColumn)}={Quote(noOpColumn)}");
        }
        var sql = $"UPDATE {Quote(Unit.Name)} SET {string.Join(", ", sets)} WHERE {where};";
        using var command = Command(sql);
        AddParameters(command, parameters);
        if (Unit.Concurrency.IsNone)
            commandObserver?.Observe(new ProviderCommandEvent("postgresql.update", sql, ProviderCommandKind.Write, IsProbe: false));
        if ((await mode.ExecuteNonQuery(command).ConfigureAwait(false)) == 0)
            return new WriteOutcome(Unit.Concurrency.IsNone
                ? WriteOutcomeStatus.NotFound
                : WriteOutcomeStatus.ConcurrencyConflict, existing?.Version);
        return new WriteOutcome(WriteOutcomeStatus.Updated,
            VersionColumn is null ? null : existing!.Version + 1);
    }

    private async ValueTask<WriteOutcome> UpsertCore(
        StorageValues values,
        StorageKey key,
        StoredEntry? existing,
        WriteOptions? options,
        bool exactOutcome,
        RelationalExecution mode)
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
            Add(command, "expected", options?.Precondition.Version);
        commandObserver?.Observe(new ProviderCommandEvent(
            exactOutcome ? "postgresql.conditional-upsert" : "postgresql.upsert",
            sql,
            ProviderCommandKind.Write,
            IsProbe: false));
        try
        {
            if (!exactOutcome)
            {
                await mode.ExecuteNonQuery(command).ConfigureAwait(false);
                return new WriteOutcome(WriteOutcomeStatus.Upserted,
                    VersionColumn is null ? null : existing is null ? 1 : existing.Version + 1);
            }

            await using var readerScope = await mode.ExecuteReader(command).ConfigureAwait(false);
            var reader = readerScope.Reader;
            if (!(await mode.Read(reader).ConfigureAwait(false)))
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

    private async ValueTask<StoredEntry?> ReadCore(
        StorageKey key,
        RelationalExecution mode,
        bool forUpdate = false,
        string? observerOperation = null,
        bool isProbe = true) => await pointReads.Read(
            key,
            mode,
            forUpdate,
            observerOperation,
            exactStringKeys: false,
            isProbe).ConfigureAwait(false);

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

    private ValueTask<T> Execute<T>(Func<ValueTask<T>> operation, RelationalExecution mode) =>
        execution.Execute(operation, mode);

    private ValueTask<T> ExecuteWrite<T>(Func<ValueTask<T>> operation, RelationalExecution mode) =>
        execution.ExecuteWrite(operation, mode);

    private NpgsqlCommand Command(string sql)
    {
        execution.EnsureOpen();
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = (NpgsqlTransaction?)execution.Transaction;
        return command;
    }

    private void AddParameters(NpgsqlCommand command, IReadOnlyDictionary<string, object?> parameters)
    {
        foreach (var pair in parameters)
            Add(command, pair.Key.TrimStart('@'), pair.Value);
    }

    /// <summary>
    /// Adds one parameter, typed from the column it carries.
    /// <para>
    /// <paramref name="column"/> is separate from <paramref name="name"/> because a batched statement
    /// cannot name its parameters after their columns — it holds one row per write, so the names are
    /// prefixed to keep them unique. Deriving the type from the placeholder instead would silently miss
    /// for every batched write, and PostgreSQL rejects an untyped JSON value with
    /// <c>42804: column "…" is of type jsonb but expression is of type text</c>.
    /// </para>
    /// </summary>
    private void Add(NpgsqlCommand command, string name, object? value, string? column = null)
    {
        var parameter = command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var typedAs = column ?? name;
        if (typedAs == "expected")
            parameter.NpgsqlDbType = NpgsqlDbType.Bigint;
        if (Unit.Columns.FirstOrDefault(candidate => candidate.Name == typedAs)?.Type == PortableType.Json)
            parameter.NpgsqlDbType = NpgsqlDbType.Jsonb;
    }

    private static void AddTyped(NpgsqlCommand command, string name, object? value, NpgsqlDbType type)
    {
        var parameter = command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        parameter.NpgsqlDbType = type;
    }

    private static object? ConvertValue(object? value, ColumnDefinition definition) =>
        new PostgreSqlDialect().ConvertValue(value, definition);

    private static object? FromDatabase(object value, ColumnDefinition definition) =>
        PostgreSqlDialect.ReadPortableValue(value, definition);

    private ColumnDefinition Column(string name) => Unit.Columns.First(column => column.Name == name);

    private IReadOnlyList<ColumnDefinition> UserColumns =>
        Unit.Columns.Where(column => column.Name is not PostgreSqlSchemaCoordinator.ScopeColumn and not PostgreSqlSchemaCoordinator.VersionColumn).ToArray();

    private ColumnDefinition? SequenceColumn => UserColumns.FirstOrDefault(column =>
        column.Generation == ColumnGeneration.ProviderSequence);

    private ColumnDefinition? VersionColumn => Unit.Columns.FirstOrDefault(column => column.Name == PostgreSqlSchemaCoordinator.VersionColumn);

    private const string LedgerUnit = "unit";
    private const string LedgerScope = "scope";
    private const string LedgerNonce = "nonce";
    private const string LedgerCommittedAt = "committed_at";
    private const string LedgerFingerprint = "input_fingerprint";
    private const string LedgerResult = "exact_result";
    private const string HighWaterTable = "__groundwork_sequence_high_waters";
    private const string LazyDdlLockPrefix = "groundwork:lazy-ddl:";
    private const string HighWaterValue = "high_water";

    private static string Quote(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private sealed class PostgreSqlAppendAdapter(PostgreSqlStorageSession session) : IRelationalAppendAdapter
    {
        public async ValueTask<DateTimeOffset> PrepareLedger(
            RelationalAppendOperation operation,
            RelationalExecution execution)
        {
            await session.EnsureLedgerTable(operation.Declaration.LedgerName, execution).ConfigureAwait(false);
            return await session.ProviderNow(execution).ConfigureAwait(false);
        }

        public async ValueTask ReclaimExpired(
            RelationalAppendOperation operation,
            DateTimeOffset cutoff,
            RelationalExecution execution)
        {
            using var reclaim = session.Command(
                $"DELETE FROM {Quote(operation.Declaration.LedgerName)} WHERE ctid IN " +
                $"(SELECT ctid FROM {Quote(operation.Declaration.LedgerName)} WHERE " +
                $"{Quote(LedgerUnit)}=@reclaim_unit AND {Quote(LedgerCommittedAt)} <= @cutoff LIMIT 128);");
            session.Add(reclaim, "reclaim_unit", operation.Unit.Id.Value);
            session.Add(reclaim, "cutoff", FormatLedgerTime(cutoff));
            await execution.ExecuteNonQuery(reclaim).ConfigureAwait(false);
        }

        public async ValueTask<RelationalAppendLedgerEntry?> ReadLedger(
            RelationalAppendOperation operation,
            RelationalExecution execution)
        {
            using var existing = session.Command(
                $"SELECT {Quote(LedgerCommittedAt)}, {Quote(LedgerFingerprint)}, {Quote(LedgerResult)} " +
                $"FROM {Quote(operation.Declaration.LedgerName)} WHERE {Quote(LedgerUnit)}=@unit " +
                $"AND {Quote(LedgerScope)}=@scope AND {Quote(LedgerNonce)}=@nonce;");
            session.AddLedgerParameters(existing, operation.Unit.Id.Value, operation.Scope, operation.OperationId.Nonce);
            await using var readerScope = await execution.ExecuteReader(existing).ConfigureAwait(false);
            var reader = readerScope.Reader;
            if (!(await execution.Read(reader).ConfigureAwait(false)))
                return null;
            return new RelationalAppendLedgerEntry(
                DateTimeOffset.Parse(
                    Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind),
                reader.IsDBNull(1) ? null : Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture),
                reader.IsDBNull(2) ? null : Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture));
        }

        public async ValueTask DeleteLedger(
            RelationalAppendOperation operation,
            RelationalAppendLedgerEntry existing,
            RelationalExecution execution)
        {
            using var delete = session.Command(
                $"DELETE FROM {Quote(operation.Declaration.LedgerName)} WHERE {Quote(LedgerUnit)}=@unit " +
                $"AND {Quote(LedgerScope)}=@scope AND {Quote(LedgerNonce)}=@nonce " +
                $"AND {Quote(LedgerCommittedAt)}=@observed_committed_at;");
            session.AddLedgerParameters(delete, operation.Unit.Id.Value, operation.Scope, operation.OperationId.Nonce);
            session.Add(delete, "observed_committed_at", FormatLedgerTime(existing.CommittedAt));
            await execution.ExecuteNonQuery(delete).ConfigureAwait(false);
        }

        public async ValueTask<bool> TryClaimLedger(
            RelationalAppendOperation operation,
            DateTimeOffset providerNow,
            RelationalExecution execution)
        {
            using var insert = session.Command(
                $"INSERT INTO {Quote(operation.Declaration.LedgerName)} " +
                $"({Quote(LedgerUnit)}, {Quote(LedgerScope)}, {Quote(LedgerNonce)}, " +
                $"{Quote(LedgerCommittedAt)}, {Quote(LedgerFingerprint)}, {Quote(LedgerResult)}) " +
                $"VALUES (@unit, @scope, @nonce, @committed_at, @fingerprint, @result) " +
                $"ON CONFLICT ({Quote(LedgerUnit)}, {Quote(LedgerScope)}, {Quote(LedgerNonce)}) DO NOTHING;");
            session.AddLedgerParameters(insert, operation.Unit.Id.Value, operation.Scope, operation.OperationId.Nonce);
            session.Add(insert, "committed_at", FormatLedgerTime(providerNow));
            session.Add(insert, "fingerprint", operation.Fingerprint);
            session.Add(insert, "result", string.Empty);
            return await execution.ExecuteNonQuery(insert).ConfigureAwait(false) == 1;
        }

        public async ValueTask<RelationalAppendReplayEntry?> ReadClaimWinner(
            RelationalAppendOperation operation,
            RelationalExecution execution)
        {
            using var replay = session.Command(
                $"SELECT {Quote(LedgerFingerprint)}, {Quote(LedgerResult)} " +
                $"FROM {Quote(operation.Declaration.LedgerName)} WHERE {Quote(LedgerUnit)}=@unit " +
                $"AND {Quote(LedgerScope)}=@scope AND {Quote(LedgerNonce)}=@nonce;");
            session.AddLedgerParameters(replay, operation.Unit.Id.Value, operation.Scope, operation.OperationId.Nonce);
            await using var readerScope = await execution.ExecuteReader(replay).ConfigureAwait(false);
            var reader = readerScope.Reader;
            if (!(await execution.Read(reader).ConfigureAwait(false)))
                return null;
            return new RelationalAppendReplayEntry(
                reader.IsDBNull(0) ? null : Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture),
                reader.IsDBNull(1) ? null : Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture));
        }

        public async ValueTask<IReadOnlyList<RowWriteOutcome>> InsertPayload(
            RelationalAppendOperation operation,
            RelationalExecution execution)
        {
            var logicalUnit = IdempotencyRules.LogicalUnit(
                operation.Unit,
                PostgreSqlSchemaCoordinator.ScopeColumn);
            var writes = operation.Values
                .Select(value => RowWrite.Insert(logicalUnit, value))
                .ToArray();
            if (session.SequenceColumn is null)
                return await session.ApplyBatchCore(writes, execution).ConfigureAwait(false);

            return await session.InsertAppendSequenceBatch(writes, execution).ConfigureAwait(false);
        }

        public async ValueTask<bool> CompleteLedger(
            RelationalAppendOperation operation,
            string serializedOutcomes,
            RelationalExecution execution)
        {
            using var complete = session.Command(
                $"UPDATE {Quote(operation.Declaration.LedgerName)} SET {Quote(LedgerResult)}=@result " +
                $"WHERE {Quote(LedgerUnit)}=@unit AND {Quote(LedgerScope)}=@scope " +
                $"AND {Quote(LedgerNonce)}=@nonce;");
            session.AddLedgerParameters(complete, operation.Unit.Id.Value, operation.Scope, operation.OperationId.Nonce);
            session.Add(complete, "result", serializedOutcomes);
            return await execution.ExecuteNonQuery(complete).ConfigureAwait(false) == 1;
        }
    }

    private sealed class PostgreSqlRetentionAdapter(
        PostgreSqlStorageSession session) : IRelationalRetentionAdapter, IRelationalAffectedRetentionSnapshotAdapter
    {
        public async ValueTask AcquireAffectedRetentionSnapshot(
            RelationalExactRetentionOperation operation,
            RelationalExecution execution)
        {
            using var command = session.Command(
                $"LOCK TABLE {Quote(operation.Unit.Name)} IN SHARE ROW EXCLUSIVE MODE;");
            await execution.ExecuteNonQuery(command).ConfigureAwait(false);
            session.Observe(
                "postgresql.retention-affected-lock",
                command.CommandText,
                ProviderCommandKind.Write);
        }

        public async ValueTask<IReadOnlyList<object?>> ReadAffectedKeys(
            RelationalExactRetentionOperation operation,
            RelationalExecution execution)
        {
            var projection = operation.Retention.Options.AffectedKeyProjection ??
                throw new InvalidOperationException("An affected-key projection is required.");
            var declaration = operation.Retention.Declaration;
            var partition = declaration.PartitionColumns.Count == 0
                ? string.Empty
                : $"PARTITION BY {string.Join(", ", declaration.PartitionColumns.Select(Quote))} ";
            var scoped = operation.Unit.Columns.Any(column => column.Name == PostgreSqlSchemaCoordinator.ScopeColumn);
            var scope = scoped
                ? $" WHERE {Quote(PostgreSqlSchemaCoordinator.ScopeColumn)}=@__groundwork_scope"
                : string.Empty;
            var ordering = string.Join(", ", [
                $"{Quote(declaration.OrderColumn)} DESC",
                .. operation.Unit.Key.Columns
                    .Where(column => !string.Equals(column, declaration.OrderColumn, StringComparison.Ordinal))
                    .Select(column => $"{Quote(column)} ASC")]);
            var projectionColumn = Quote(projection.Column);
            if (session.Column(projection.Column).Type == PortableType.String)
                projectionColumn += " COLLATE \"C\"";
            using var command = session.Command(
                $"WITH ranked AS (" +
                $"SELECT {projectionColumn}, ROW_NUMBER() OVER ({partition}ORDER BY {ordering}) AS __groundwork_retention_rank " +
                $"FROM {Quote(operation.Unit.Name)}{scope}) " +
                $"SELECT DISTINCT {projectionColumn} FROM ranked " +
                $"WHERE __groundwork_retention_rank > @keep " +
                $"ORDER BY {projectionColumn} ASC NULLS FIRST LIMIT @affected_limit;");
            session.Add(command, "keep", operation.Retention.KeepNewest);
            session.Add(command, "affected_limit", checked(projection.MaxDistinctValues + 1));
            if (scoped)
                session.Add(command, "__groundwork_scope", operation.Scope);
            await using var readerScope = await execution.ExecuteReader(command).ConfigureAwait(false);
            var reader = readerScope.Reader;
            var values = new List<object?>(Math.Min(projection.MaxDistinctValues + 1, 4096));
            var column = session.Column(projection.Column);
            while (await execution.Read(reader).ConfigureAwait(false))
                values.Add(reader.IsDBNull(0) ? null : FromDatabase(reader.GetValue(0), column));
            session.Observe("postgresql.retention-affected-keys", command.CommandText, ProviderCommandKind.Read);
            return values;
        }

        public async ValueTask<int> DeleteBatch(
            RelationalRetentionOperation operation,
            RelationalExecution execution)
        {
            var keyColumns = operation.Unit.Key.Columns;
            var partition = operation.Declaration.PartitionColumns.Count == 0
                ? string.Empty
                : $"PARTITION BY {string.Join(", ", operation.Declaration.PartitionColumns.Select(Quote))} ";
            var scoped = operation.Unit.Columns.Any(column => column.Name == PostgreSqlSchemaCoordinator.ScopeColumn);
            var scope = scoped
                ? $" WHERE {Quote(PostgreSqlSchemaCoordinator.ScopeColumn)}=@__groundwork_scope"
                : string.Empty;
            var keys = string.Join(", ", keyColumns.Select(Quote));
            var ordering = string.Join(", ", [
                $"{Quote(operation.Declaration.OrderColumn)} DESC",
                .. keyColumns
                    .Where(column => !string.Equals(column, operation.Declaration.OrderColumn, StringComparison.Ordinal))
                    .Select(column => $"{Quote(column)} ASC")]);
            var equality = string.Join(" AND ", keyColumns.Select(column =>
                $"target.{Quote(column)}=victim.{Quote(column)}"));
            using var command = session.Command($"WITH ranked AS (" +
                $"SELECT {keys}, ROW_NUMBER() OVER ({partition}ORDER BY {ordering}) AS __groundwork_retention_rank " +
                $"FROM {Quote(operation.Unit.Name)}{scope}), victims AS (" +
                $"SELECT {keys} FROM ranked WHERE __groundwork_retention_rank > @keep LIMIT @limit) " +
                $"DELETE FROM {Quote(operation.Unit.Name)} AS target USING victims AS victim WHERE {equality};");
            session.Add(command, "keep", operation.KeepNewest);
            session.Add(command, "limit", operation.Options.MaxRowsPerBatch);
            if (scoped)
                session.Add(command, "__groundwork_scope", operation.Scope);
            var affected = await execution.ExecuteNonQuery(command).ConfigureAwait(false);
            session.Observe("postgresql.retention-delete", command.CommandText, ProviderCommandKind.Write);
            return affected;
        }

        public async ValueTask<DateTimeOffset> PrepareLedger(
            RelationalExactRetentionOperation operation,
            RelationalExecution execution)
        {
            await session.EnsureLedgerTable(operation.Declaration.LedgerName, execution).ConfigureAwait(false);
            return await session.ProviderNow(execution).ConfigureAwait(false);
        }

        public async ValueTask ReclaimExpired(
            RelationalExactRetentionOperation operation,
            DateTimeOffset cutoff,
            RelationalExecution execution)
        {
            using var reclaim = session.Command(
                $"DELETE FROM {Quote(operation.Declaration.LedgerName)} WHERE ctid IN (" +
                $"SELECT ctid FROM {Quote(operation.Declaration.LedgerName)} " +
                $"WHERE {Quote(LedgerUnit)}=@reclaim_unit AND {Quote(LedgerCommittedAt)} <= @cutoff LIMIT 128);");
            session.Add(reclaim, "reclaim_unit", operation.Unit.Id.Value);
            session.Add(reclaim, "cutoff", FormatLedgerTime(cutoff));
            await execution.ExecuteNonQuery(reclaim).ConfigureAwait(false);
        }

        public async ValueTask<RelationalRetentionLedgerEntry?> ReadLedger(
            RelationalExactRetentionOperation operation,
            RelationalExecution execution)
        {
            using var command = session.Command(
                $"SELECT {Quote(LedgerCommittedAt)}, {Quote(LedgerFingerprint)}, {Quote(LedgerResult)} " +
                $"FROM {Quote(operation.Declaration.LedgerName)} WHERE {Quote(LedgerUnit)}=@unit " +
                $"AND {Quote(LedgerScope)}=@scope AND {Quote(LedgerNonce)}=@nonce;");
            session.AddLedgerParameters(command, operation.Unit.Id.Value, operation.Scope, operation.OperationId.Nonce);
            await using var readerScope = await execution.ExecuteReader(command).ConfigureAwait(false);
            var reader = readerScope.Reader;
            if (!(await execution.Read(reader).ConfigureAwait(false)))
                return null;
            return new RelationalRetentionLedgerEntry(
                DateTimeOffset.Parse(
                    Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind),
                reader.IsDBNull(1) ? null : Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture),
                reader.IsDBNull(2) ? null : Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture));
        }

        public async ValueTask DeleteLedger(
            RelationalExactRetentionOperation operation,
            RelationalRetentionLedgerEntry existing,
            RelationalExecution execution)
        {
            using var delete = session.Command(
                $"DELETE FROM {Quote(operation.Declaration.LedgerName)} WHERE {Quote(LedgerUnit)}=@unit " +
                $"AND {Quote(LedgerScope)}=@scope AND {Quote(LedgerNonce)}=@nonce " +
                $"AND {Quote(LedgerCommittedAt)}=@observed_committed_at;");
            session.AddLedgerParameters(delete, operation.Unit.Id.Value, operation.Scope, operation.OperationId.Nonce);
            session.Add(delete, "observed_committed_at", FormatLedgerTime(existing.CommittedAt));
            await execution.ExecuteNonQuery(delete).ConfigureAwait(false);
        }

        public async ValueTask<bool> TryClaimLedger(
            RelationalExactRetentionOperation operation,
            DateTimeOffset providerNow,
            RelationalExecution execution)
        {
            using var insert = session.Command(
                $"INSERT INTO {Quote(operation.Declaration.LedgerName)} " +
                $"({Quote(LedgerUnit)}, {Quote(LedgerScope)}, {Quote(LedgerNonce)}, " +
                $"{Quote(LedgerCommittedAt)}, {Quote(LedgerFingerprint)}, {Quote(LedgerResult)}) " +
                $"VALUES (@unit, @scope, @nonce, @committed_at, @fingerprint, @result) " +
                $"ON CONFLICT ({Quote(LedgerUnit)}, {Quote(LedgerScope)}, {Quote(LedgerNonce)}) DO NOTHING;");
            session.AddLedgerParameters(insert, operation.Unit.Id.Value, operation.Scope, operation.OperationId.Nonce);
            session.Add(insert, "committed_at", FormatLedgerTime(providerNow));
            session.Add(insert, "fingerprint", operation.Fingerprint);
            session.Add(insert, "result", string.Empty);
            return await execution.ExecuteNonQuery(insert).ConfigureAwait(false) == 1;
        }

        public async ValueTask<RelationalRetentionReplayEntry?> ReadClaimWinner(
            RelationalExactRetentionOperation operation,
            RelationalExecution execution)
        {
            using var replay = session.Command(
                $"SELECT {Quote(LedgerFingerprint)}, {Quote(LedgerResult)} " +
                $"FROM {Quote(operation.Declaration.LedgerName)} WHERE {Quote(LedgerUnit)}=@unit " +
                $"AND {Quote(LedgerScope)}=@scope AND {Quote(LedgerNonce)}=@nonce;");
            session.AddLedgerParameters(replay, operation.Unit.Id.Value, operation.Scope, operation.OperationId.Nonce);
            await using var readerScope = await execution.ExecuteReader(replay).ConfigureAwait(false);
            var reader = readerScope.Reader;
            if (!(await execution.Read(reader).ConfigureAwait(false)))
                return null;
            return new RelationalRetentionReplayEntry(
                reader.IsDBNull(0) ? null : Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture),
                reader.IsDBNull(1) ? null : Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture));
        }

        public async ValueTask<bool> CompleteLedger(
            RelationalExactRetentionOperation operation,
            string serializedResult,
            RelationalExecution execution)
        {
            using var complete = session.Command(
                $"UPDATE {Quote(operation.Declaration.LedgerName)} SET {Quote(LedgerResult)}=@result " +
                $"WHERE {Quote(LedgerUnit)}=@unit AND {Quote(LedgerScope)}=@scope " +
                $"AND {Quote(LedgerNonce)}=@nonce;");
            session.AddLedgerParameters(complete, operation.Unit.Id.Value, operation.Scope, operation.OperationId.Nonce);
            session.Add(complete, "result", serializedResult);
            return await execution.ExecuteNonQuery(complete).ConfigureAwait(false) == 1;
        }
    }

    private sealed class PostgreSqlSessionExecutionAdapter(
        PostgreSqlProviderConnection owner,
        NpgsqlConnection connection,
        SchemaSessionLease schemaSession) : IRelationalSessionExecutionAdapter
    {
        public bool SerializeAmbientReads => true;

        public void EnsureUsable()
        {
            owner.ThrowIfDisposed();
            schemaSession.EnsureCurrent();
        }

        public ValueTask<IDisposable> EnterGate(RelationalExecution execution) =>
            owner.EnterGate(execution);

        public ValueTask<DbTransaction> BeginWrite(RelationalExecution execution) =>
            execution.BeginTransaction(connection, IsolationLevel.ReadCommitted);

        public ValueTask Rollback(DbTransaction transaction, RelationalExecution execution) =>
            execution.Rollback(transaction);
    }

    private sealed class PostgreSqlPointReadAdapter(
        PostgreSqlStorageSession session) : IRelationalPointReadAdapter
    {
        public string QuoteIdentifier(string identifier) => Quote(identifier);

        public string Equality(ColumnDefinition column, string parameter, bool exactStringKeys) =>
            $"{Quote(column.Name)}={parameter}";

        public void Bind(DbCommand command, string parameter, object? value, ColumnDefinition column) =>
            session.Add((NpgsqlCommand)command, parameter.TrimStart('@'), ConvertValue(value, column), column.Name);

        public object? Decode(object value, ColumnDefinition column) => FromDatabase(value, column);

        public string LockingClause(bool forUpdate) => forUpdate ? " FOR UPDATE" : string.Empty;
    }

    private sealed class PostgreSqlCrudAdapter(
        PostgreSqlStorageSession session) : IRelationalCrudAdapter
    {
        public ValueTask<WriteOutcome> Insert(
            StorageValues values,
            WriteOutcomeStatus status,
            RelationalExecution execution) =>
            session.InsertCore(values, execution, status);

        public ValueTask<WriteOutcome> Update(
            StorageValues values,
            StorageKey key,
            StoredEntry? existing,
            WriteOptions? options,
            RelationalExecution execution) =>
            session.UpdateCore(values, key, existing, options, execution);

        public ValueTask<WriteOutcome> Upsert(
            StorageValues values,
            StorageKey key,
            StoredEntry? existing,
            WriteOptions? options,
            RelationalExecution execution) =>
            session.SequenceColumn is not null && values.Values.ContainsKey(session.SequenceColumn.Name)
                ? session.UpdateCore(values, key, existing, options, execution)
                : session.UpsertCore(values, key, existing, options, exactOutcome: false, execution);

        public ValueTask<WriteOutcome> Delete(
            StorageKey key,
            StoredEntry? existing,
            WriteOptions? options,
            RelationalExecution execution) =>
            session.DeleteCore(key, existing, options, execution);
    }

}

internal sealed class OwnedPostgreSqlStorageSession : PostgreSqlStorageSession, IOwnedStorageSession
{
    internal OwnedPostgreSqlStorageSession(
        PostgreSqlProviderConnection owner,
        StorageUnit unit,
        StorageAccess access,
        NpgsqlConnection connection,
        SchemaSessionLease schemaSession,
        IProviderCommandObserver? observer = null)
        : base(owner, unit, access, connection, null, schemaSession, observer, ownsConnection: true)
    {
    }
}
