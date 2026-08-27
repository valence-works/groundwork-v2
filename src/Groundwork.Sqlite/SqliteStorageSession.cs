using System.Globalization;
using System.Data;
using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Groundwork.Kernel;
using Groundwork.Query.Model;
using Groundwork.Substrate.Relational;
using Groundwork.Store;
using Groundwork.Diagnostics;

namespace Groundwork.Sqlite;

internal sealed class SqliteStorageSession : IStorageSession, IExactAppendStorageSession, IConcurrencyStorageSession, ICompareAndDeleteStorageSession, IBatchedStorageSession, IRetentionStorageSession, IStorageInspectionSession, IExactRetentionStorageSession, IPrivilegedCrossScopeQuerySession
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
        SqliteTransaction? transaction,
        IProviderCommandObserver? observer = null)
    {
        commandObserver = observer;
        this.owner = owner;
        Unit = unit;
        Access = access;
        this.connection = connection;
        this.transaction = transaction;
    }

    /// <summary>
    /// Counts every provider command this session issues. It belongs to the session because the session is
    /// what issues commands; it used to be read off an individual write's options, so a batch observed only
    /// whatever happened to be staged first.
    /// </summary>
    private readonly IProviderCommandObserver? commandObserver;

    public StorageUnit Unit { get; }
    public StorageAccess Access { get; }

    public QueryMaterializedResult Query(QueryRequest request, QueryRenderOptions? options = null) => Execute(() =>
    {
        var (executionSource, renderOptions, command) = PrepareQuery(request, options);
        var rows = RelationalQueryResultReader.Read(connection, command, DecodeQueryValue, activeTransaction ?? transaction);
        AssertExplainPlan(command, renderOptions);
        return QueryResultMaterializer.Materialize(executionSource, renderOptions, rows, command.SelectedIndex, command.IndexHintApplied,
            sourceIncludesRequestedOffset: true,
            sourceIncludesContinuation: true);
    });

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
            var (executionSource, renderOptions, command) = PrepareQuery(request, options);
            var rows = RelationalQueryResultReader.Read(
                    connection, command, DecodeQueryValue, activeTransaction ?? transaction,
                    RelationalExecution.Asynchronous(cancellationToken))
                .GetAwaiter().GetResult();
            AssertExplainPlan(command, renderOptions);
            return QueryResultMaterializer.Materialize(executionSource, renderOptions, rows, command.SelectedIndex, command.IndexHintApplied,
                sourceIncludesRequestedOffset: true,
                sourceIncludesContinuation: true);
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

    private (QueryRequest ExecutionSource, QueryRenderOptions RenderOptions, RelationalQueryCommand Command) PrepareQuery(
        QueryRequest request,
        QueryRenderOptions? options)
    {
        ArgumentNullException.ThrowIfNull(request);
        StorageAccessValidation.EnsureOrdinaryQuery(Access);
        if (!string.Equals(request.Table.Value, Unit.Name, StringComparison.Ordinal))
            throw new ArgumentException($"Query table '{request.Table.Value}' does not match session unit '{Unit.Name}'.", nameof(request));
        var suppliedOptions = options ?? QueryRenderOptions.Default;
        var executionSource = WithScopePredicate(request);
        var renderOptions = suppliedOptions.WithIdentityTieBreaks(Unit.Key.Columns.Where(name => name != SqliteSchemaCoordinator.ScopeColumn).Select(QueryColumn).Where(column => column is not null)!.Select(column => column!)) with
        {
            Indexes = SearchKeyQueryMappings.RetargetIndexes(Unit, suppliedOptions.Indexes)
                .Select(index => index.WithColumnTypes(Unit.Columns.ToDictionary(column => column.Name, column => QueryTypeOf(column.Type), StringComparer.Ordinal))).ToImmutableArray(),
            PhysicalIndexNames = Unit.Indexes.ToDictionary(
                index => index.Name,
                index => SqliteDialect.PhysicalIndexName(Unit.Name, index.Name),
                StringComparer.Ordinal),
            SearchKeyColumns = SearchKeyQueryMappings.For(Unit)
        };
        var executionRequest = QueryRequestExecution.ForPage(executionSource, renderOptions);
        var command = new SqliteQueryRenderer().Render(executionRequest, renderOptions);
        commandObserver?.Observe(new ProviderCommandEvent("sqlite.query", command.CommandText, ProviderCommandKind.Read, IsProbe: false));
        return (executionSource, renderOptions, command);
    }

    private object? DecodeQueryValue(string name, object? value)
    {
        if (name == "__groundwork_total_count") return value;
        var column = Unit.Columns.FirstOrDefault(item => item.Name == name);
        return column is null ? value : FromSqlite(value ?? DBNull.Value, column);
    }

    public CrossScopeQueryResult QueryAcrossScopes(
        QueryRequest request,
        QueryRenderOptions? options = null) => Execute(() =>
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Access.IsPrivilegedAcrossScopes)
            throw new InvalidOperationException(
                "GW-ACCESS-001: cross-scope queries require explicit privileged across-scope access.");
        if (!string.Equals(request.Table.Value, Unit.Name, StringComparison.Ordinal))
            throw new ArgumentException(
                $"Query table '{request.Table.Value}' does not match session unit '{Unit.Name}'.",
                nameof(request));
        StorageAccessValidation.ObservePrivilegedQuery(Access, Unit);

        var suppliedOptions = options ?? QueryRenderOptions.Default;
        var table = new TableId(Unit.Name);
        var scopeToken = new ColumnRef(
            table,
            CrossScopeQueryMaterializer.ScopeTokenColumn,
            QueryType.String,
            isNullable: false);
        var renderOptions = suppliedOptions.WithIdentityTieBreaks(
            new[] { scopeToken }
                .Concat(Unit.Key.Columns
                    .Where(name => name != SqliteSchemaCoordinator.ScopeColumn)
                    .Select(QueryColumn)
                    .Where(column => column is not null)
                    .Select(column => column!))) with
        {
            Indexes = SearchKeyQueryMappings.RetargetIndexes(Unit, suppliedOptions.Indexes)
                .Select(index => index.WithColumnTypes(Unit.Columns.ToDictionary(
                    column => column.Name,
                    column => QueryTypeOf(column.Type),
                    StringComparer.Ordinal)))
                .ToImmutableArray(),
            PhysicalIndexNames = Unit.Indexes.ToDictionary(
                index => index.Name,
                index => SqliteDialect.PhysicalIndexName(Unit.Name, index.Name),
                StringComparer.Ordinal),
            SearchKeyColumns = SearchKeyQueryMappings.For(Unit),
            LatestPartitionColumns = [scopeToken]
        };
        var executionSource = QueryRequestExecution.WithProviderPredicate(
            request,
            request.Where,
            CrossScopeQueryMaterializer.BindingDiscriminator(Access));
        var executionRequest = EnsureScopeProjection(
            QueryRequestExecution.ForPage(executionSource, renderOptions));
        var command = new SqliteQueryRenderer().Render(executionRequest, renderOptions);
        commandObserver?.Observe(new ProviderCommandEvent("sqlite.query-across-scopes", command.CommandText, ProviderCommandKind.Read, IsProbe: false));
        var rows = RelationalQueryResultReader.Read(connection, command, (name, value) =>
        {
            if (name == "__groundwork_total_count") return value;
            var column = Unit.Columns.FirstOrDefault(item => item.Name == name);
            return column is null ? value : FromSqlite(value ?? DBNull.Value, column);
        });
        AssertExplainPlan(command, renderOptions);
        var materialized = QueryResultMaterializer.Materialize(
            executionSource,
            renderOptions,
            rows,
            command.SelectedIndex,
            command.IndexHintApplied,
            sourceIncludesRequestedOffset: true,
            sourceIncludesContinuation: true);
        return CrossScopeQueryMaterializer.FromNativePage(
            materialized,
            rows,
            SqliteSchemaCoordinator.ScopeColumn);
    });

    public AggregationResult Aggregate(AggregationQuery query) => Execute(() =>
    {
        ArgumentNullException.ThrowIfNull(query);
        StorageAccessValidation.EnsurePointOperation(Access, "aggregate");
        var profile = AggregationProfileValidator.ResolveOrThrow(Unit, query.ProfileName);
        var mode = RelationalExecution.Synchronous;
        var decode = (string name, object? value) =>
        {
            var column = Unit.Columns.FirstOrDefault(item => item.Name == name);
            return column is null ? value : FromSqlite(value ?? DBNull.Value, column);
        };
        return (Unit.Scope == ScopePolicy.Scoped
            ? RelationalAggregationExecutor.ExecuteScoped(
                connection,
                activeTransaction ?? transaction,
                new SqliteDialect(),
                Unit,
                profile,
                query,
                decode,
                SqliteSchemaCoordinator.ScopeColumn,
                Access.Scope!,
                mode,
                commandObserver,
                "sqlite.aggregate")
            : RelationalAggregationExecutor.Execute(
            connection,
            activeTransaction ?? transaction,
            new SqliteDialect(),
            Unit,
            profile,
            query,
            decode,
            mode,
            commandObserver,
            "sqlite.aggregate")).GetAwaiter().GetResult();
    });

    private void AssertExplainPlan(RelationalQueryCommand query, QueryRenderOptions options)
    {
        if (query.IsMatchNone || !ExplainAssertionMode.ShouldAssert(query.SelectedIndex)) return;
        var logicalIndex = query.SelectedIndex!;
        var physicalIndex = options.ResolvePhysicalIndexName(logicalIndex);
        using var explain = Command("EXPLAIN QUERY PLAN " + query.CommandText.TrimEnd().TrimEnd(';'));
        RelationalQueryResultReader.AddParameters(explain, query);
        using var reader = explain.ExecuteReader();
        var details = new List<string>();
        while (reader.Read())
            details.Add(string.Join('\t', Enumerable.Range(0, reader.FieldCount).Select(index => Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture))));
        var rawPlan = string.Join(Environment.NewLine, details);
        ExplainAssertionMode.AssertChosenIndex(
            "SQLite", logicalIndex, physicalIndex, query.IndexHintApplied, rawPlan,
            SqliteExplainPlanInspector.ChoseIndex(rawPlan, physicalIndex));
    }

    private QueryRequest WithScopePredicate(QueryRequest request) => Unit.Scope != ScopePolicy.Scoped
        ? request
        : QueryRequestExecution.WithProviderPredicate(request, new Predicate.And([
            request.Where,
            new Predicate.Equal(new ColumnRef(new TableId(Unit.Name), SqliteSchemaCoordinator.ScopeColumn, QueryType.String),
                QueryConstant.Of(new ColumnRef(new TableId(Unit.Name), SqliteSchemaCoordinator.ScopeColumn, QueryType.String), Access.Scope!.Value))]),
            QueryRequestExecution.ScopeBindingDiscriminator(Access.Scope!.Value));

    public StoredEntry? Read(StorageKey key)
    {
        StorageAccessValidation.EnsurePointOperation(Access, "read");
        return Execute(() => PublicEntry(ReadCore(key, observerOperation: "sqlite.read", isProbe: false)));
    }

    private QueryRequest EnsureScopeProjection(QueryRequest request)
    {
        if (request.Projection.AllColumns || request.Projection.Columns.Any(column =>
                string.Equals(column.Name, SqliteSchemaCoordinator.ScopeColumn, StringComparison.Ordinal)))
            return request;
        var scope = new ColumnRef(
            new TableId(Unit.Name),
            SqliteSchemaCoordinator.ScopeColumn,
            QueryType.String,
            isNullable: false);
        return QueryRequestExecution.WithProjection(
            request,
            Projection.ColumnsOnly([.. request.Projection.Columns, scope]));
    }

    private static StoredEntry? PublicEntry(StoredEntry? entry) => entry is null
        ? null
        : new StoredEntry(new StorageValues(SearchKeyProjection.PublicValues(entry.Values.Values)), entry.Version);

    public WriteOutcome Insert(StorageValues values, WriteOptions? options = null)
    {
        WritePreconditionValidator.ValidateSystemOwnedValues(Unit, values.Values);
        WritePreconditionValidator.Validate(Unit, WriteOperation.Insert, options);
        return Mutate(values, options, Mutation.Insert);
    }

    public WriteOutcome Update(StorageValues values, WriteOptions? options = null)
    {
        WritePreconditionValidator.ValidateSystemOwnedValues(Unit, values.Values);
        WritePreconditionValidator.Validate(Unit, WriteOperation.Update, options);
        return Mutate(values, options, Mutation.Update);
    }

    public WriteOutcome Upsert(StorageValues values, WriteOptions? options = null)
    {
        WritePreconditionValidator.ValidateSystemOwnedValues(Unit, values.Values);
        WritePreconditionValidator.Validate(Unit, WriteOperation.Upsert, options);
        return Mutate(values, options, Mutation.Upsert);
    }

    public WriteOutcome ConditionalUpsert(StorageValues values, WriteOptions? options = null)
    {
        WritePreconditionValidator.ValidateSystemOwnedValues(Unit, values.Values);
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
        WritePreconditionValidator.Validate(Unit, WriteOperation.Delete, options);
        return ExecuteWrite(() =>
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

            var existing = ReadCore(key);
            ValidateExpected(options, existing, Mutation.Delete);
            if (existing is null)
                return new WriteOutcome(WriteOutcomeStatus.NotFound);
            var (where, parameters) = KeyPredicate(key.Values);
            if (VersionColumnDefinition is not null && options?.Precondition.Kind == WritePreconditionKind.IfVersion)
            {
                where += $" AND {Quote(VersionColumnDefinition.Name)}=@expected";
                parameters["@expected"] = options.Precondition.Version!.Value;
            }
            using var command = Command($"DELETE FROM {Quote(Unit.Name)} WHERE {where};");
            commandObserver?.Observe(new ProviderCommandEvent("sqlite.delete", command.CommandText, ProviderCommandKind.Write, IsProbe: false));
            AddParameters(command, parameters);
            command.ExecuteNonQuery();
            return new WriteOutcome(WriteOutcomeStatus.Deleted, existing.Version);
        });
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
            return MatchesExpected(existing, expected)
                ? new WriteOutcome(WriteOutcomeStatus.ConcurrencyConflict, existing.Version)
                : new WriteOutcome(WriteOutcomeStatus.ComparisonMismatch, existing.Version);
        });
    }

    private bool MatchesExpected(StoredEntry existing, IReadOnlyDictionary<string, object?> expected) =>
        expected.All(pair =>
        {
            var definition = Column(pair.Key);
            return existing.Values.Values.TryGetValue(pair.Key, out var actual) &&
                CompareAndDeleteValidation.ValuesEqual(actual, pair.Value, definition.Type);
        });

    public RetentionResult ApplyRetention(RetentionExecutionOptions? options = null) =>
        ExecuteWrite(() => ApplyRetentionCore(options ?? new RetentionExecutionOptions()));

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
        var declaration = Unit.RetentionIdempotency ?? throw new InvalidOperationException(
            $"Storage unit '{Unit.Name}' does not declare retention idempotency; declare RetentionIdempotency before using operation-identified retention.");
        declaration.Validate(Unit);
        options ??= new RetentionExecutionOptions();
        RetentionSessionExtensions.ValidateExecutionOptions(options);
        RetentionOperationCodec.ValidateOperation(operationId);
        return ExecuteWrite(() => ApplyExactRetentionCore(operationId, declaration, options));
    }

    private RetentionOperationResult ApplyExactRetentionCore(
        OperationId operationId,
        RetentionIdempotencyDeclaration declaration,
        RetentionExecutionOptions options)
    {
        EnsureLedgerTable(declaration.LedgerName);
        var providerNow = ProviderNow();
        var scope = Access.Scope?.Value ?? string.Empty;
        var fingerprint = RetentionOperationCodec.Fingerprint(Unit, options);
        var cutoff = IdempotencyRules.ReclamationCutoff(providerNow, declaration.Window);

        using (var reclaim = Command($"DELETE FROM {Quote(declaration.LedgerName)} WHERE rowid IN (SELECT rowid FROM {Quote(declaration.LedgerName)} WHERE {Quote(LedgerUnit)}=@reclaim_unit AND {Quote(LedgerCommittedAt)} <= @cutoff LIMIT 128);") )
        {
            reclaim.Parameters.AddWithValue("@reclaim_unit", Unit.Id.Value);
            reclaim.Parameters.AddWithValue("@cutoff", FormatLedgerTime(cutoff));
            reclaim.ExecuteNonQuery();
        }

        var existing = ReadRetentionLedger(declaration.LedgerName, operationId, scope);
        if (existing is not null)
        {
            var (committedAt, storedFingerprint, storedResult) = existing.Value;
            if (IdempotencyRules.IsWithinWindow(committedAt, providerNow, declaration.Window))
            {
                if (string.IsNullOrEmpty(storedFingerprint) || string.IsNullOrEmpty(storedResult))
                    throw new InvalidOperationException(
                        "GW-RETENTION-002: an existing exact retention ledger entry has no exact result; use a new operation nonce.");
                if (!string.Equals(storedFingerprint, fingerprint, StringComparison.Ordinal))
                    throw new RetentionIdempotencyConflictException(Unit.Id.Value, scope, operationId.Nonce, storedFingerprint, fingerprint);
                return RetentionOperationCodec.DeserializeResult(storedResult) with { Status = RetentionOperationStatus.Replayed };
            }

            using var deleteExpired = Command($"DELETE FROM {Quote(declaration.LedgerName)} WHERE {Quote(LedgerUnit)}=@unit AND {Quote(LedgerScope)}=@scope AND {Quote(LedgerNonce)}=@nonce;");
            AddLedgerParameters(deleteExpired, Unit.Id.Value, scope, operationId.Nonce);
            deleteExpired.ExecuteNonQuery();
        }

        using (var insertLedger = Command($"INSERT OR IGNORE INTO {Quote(declaration.LedgerName)} ({Quote(LedgerUnit)}, {Quote(LedgerScope)}, {Quote(LedgerNonce)}, {Quote(LedgerCommittedAt)}, {Quote(LedgerFingerprint)}, {Quote(LedgerResult)}) VALUES (@unit, @scope, @nonce, @committed_at, @fingerprint, @result);"))
        {
            AddLedgerParameters(insertLedger, Unit.Id.Value, scope, operationId.Nonce);
            insertLedger.Parameters.AddWithValue("@committed_at", FormatLedgerTime(providerNow));
            insertLedger.Parameters.AddWithValue("@fingerprint", fingerprint);
            insertLedger.Parameters.AddWithValue("@result", string.Empty);
            if (insertLedger.ExecuteNonQuery() == 0)
            {
                var raced = ReadRetentionLedger(declaration.LedgerName, operationId, scope);
                if (raced is null || string.IsNullOrEmpty(raced.Value.storedFingerprint) || string.IsNullOrEmpty(raced.Value.storedResult))
                    throw new InvalidOperationException(
                        "GW-RETENTION-002: an existing exact retention ledger entry has no exact result; use a new operation nonce.");
                if (!string.Equals(raced.Value.storedFingerprint, fingerprint, StringComparison.Ordinal))
                    throw new RetentionIdempotencyConflictException(Unit.Id.Value, scope, operationId.Nonce, raced.Value.storedFingerprint, fingerprint);
                return RetentionOperationCodec.DeserializeResult(raced.Value.storedResult) with { Status = RetentionOperationStatus.Replayed };
            }
        }

        // Exact retention executes inside this same transaction. Cancellation or any
        // provider failure rolls back both deletes and the placeholder ledger row.
        var retention = ApplyRetentionCore(options);
        var result = new RetentionOperationResult(
            RetentionOperationStatus.Executed,
            retention.DeletedRows,
            retention.Batches,
            retention.Completed);
        using var complete = Command($"UPDATE {Quote(declaration.LedgerName)} SET {Quote(LedgerResult)}=@result WHERE {Quote(LedgerUnit)}=@unit AND {Quote(LedgerScope)}=@scope AND {Quote(LedgerNonce)}=@nonce;");
        AddLedgerParameters(complete, Unit.Id.Value, scope, operationId.Nonce);
        complete.Parameters.AddWithValue("@result", RetentionOperationCodec.SerializeResult(result));
        complete.ExecuteNonQuery();
        return result;
    }

    private (DateTimeOffset committedAt, string? storedFingerprint, string? storedResult)? ReadRetentionLedger(
        string table,
        OperationId operationId,
        string scope)
    {
        using var command = Command($"SELECT {Quote(LedgerCommittedAt)}, {Quote(LedgerFingerprint)}, {Quote(LedgerResult)} FROM {Quote(table)} WHERE {Quote(LedgerUnit)}=@unit AND {Quote(LedgerScope)}=@scope AND {Quote(LedgerNonce)}=@nonce;");
        AddLedgerParameters(command, Unit.Id.Value, scope, operationId.Nonce);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        return (
            DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private RetentionResult ApplyRetentionCore(RetentionExecutionOptions options)
    {
        if (options.MaxRowsPerBatch <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.MaxRowsPerBatch));
        var declaration = Unit.Retention ??
            throw new InvalidOperationException($"Storage unit '{Unit.Name}' does not declare retention.");
        var keepNewest = RetentionSessionExtensions.EffectiveKeepNewest(Unit, options);
        var keyColumns = Unit.Key.Columns;
        var partition = declaration.PartitionColumns.Count == 0
            ? string.Empty
            : $"PARTITION BY {string.Join(", ", declaration.PartitionColumns.Select(Quote))} ";
        var scope = Unit.Columns.Any(column => column.Name == SqliteSchemaCoordinator.ScopeColumn)
            ? $" WHERE {Quote(SqliteSchemaCoordinator.ScopeColumn)}=@__groundwork_scope"
            : string.Empty;
        var keys = string.Join(", ", keyColumns.Select(Quote));
        var ordering = string.Join(", ", [
            $"{Quote(declaration.OrderColumn)} DESC",
            .. keyColumns.Select(column => $"{Quote(column)} ASC")]);
        var equality = string.Join(" AND ", keyColumns.Select(column =>
            $"target.{Quote(column)}=victim.{Quote(column)}"));
        var deleted = 0;
        var batches = 0;
        while (true)
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            using var command = Command($"WITH ranked AS (" +
                $"SELECT {keys}, ROW_NUMBER() OVER ({partition}ORDER BY {ordering}) AS __groundwork_retention_rank " +
                $"FROM {Quote(Unit.Name)}{scope}), victims AS (" +
                $"SELECT {keys} FROM ranked WHERE __groundwork_retention_rank > @keep LIMIT @limit) " +
                $"DELETE FROM {Quote(Unit.Name)} AS target WHERE EXISTS (SELECT 1 FROM victims AS victim WHERE {equality});");
            command.Parameters.AddWithValue("@keep", keepNewest);
            command.Parameters.AddWithValue("@limit", options.MaxRowsPerBatch);
            if (Unit.Columns.Any(column => column.Name == SqliteSchemaCoordinator.ScopeColumn))
                command.Parameters.AddWithValue("@__groundwork_scope", Access.Scope!.Value);
            var affected = command.ExecuteNonQuery();
            commandObserver?.Observe(new ProviderCommandEvent("sqlite.retention-delete", command.CommandText, ProviderCommandKind.Write, IsProbe: false));
            if (affected == 0)
                break;
            deleted += affected;
            batches++;
            if (affected < options.MaxRowsPerBatch)
                break;
        }
        return new RetentionResult(deleted, batches);
    }

    public WriteOutcome Append(OperationId operationId, IReadOnlyList<StorageValues> values)
    {
        var declaration = IdempotencyRules.RequireDeclaration(Unit);
        IdempotencyRules.ValidateOperation(Unit, operationId, values);
        foreach (var value in values)
            WritePreconditionValidator.ValidateSystemOwnedValues(Unit, value.Values);
        var onAppend = Unit.Retention?.Trigger == RetentionTrigger.OnAppend;
        var registration = BeginOnAppend(onAppend);
        AppendExecution execution;
        try
        {
            execution = ExecuteWrite(() => AppendCore(operationId, values, declaration, exactOutcomes: false));
        }
        catch
        {
            CompleteOnAppend(registration, cleanupRequired: false);
            throw;
        }
        CompleteOnAppend(registration, onAppend && execution.Status is WriteOutcomeStatus.Inserted or WriteOutcomeStatus.Replayed);
        return new WriteOutcome(execution.Status);
    }

    public AppendOutcomeReport AppendWithOutcomes(OperationId operationId, IReadOnlyList<StorageValues> values)
    {
        var declaration = IdempotencyRules.RequireDeclaration(Unit);
        IdempotencyRules.ValidateOperation(Unit, operationId, values);
        foreach (var value in values)
            WritePreconditionValidator.ValidateSystemOwnedValues(Unit, value.Values);
        var onAppend = Unit.Retention?.Trigger == RetentionTrigger.OnAppend;
        var registration = BeginOnAppend(onAppend);
        AppendOutcomeReport outcome;
        try
        {
            outcome = ExecuteWrite(() => AppendCore(operationId, values, declaration, exactOutcomes: true).ToReport());
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

    private AppendExecution AppendCore(
        OperationId operationId,
        IReadOnlyList<StorageValues> values,
        AppendIdempotencyDeclaration declaration,
        bool exactOutcomes)
    {
        EnsureLedgerTable(declaration.LedgerName);
        var providerNow = ProviderNow();
        var scope = Access.Scope?.Value ?? string.Empty;
        var fingerprint = ExactAppendCodec.Fingerprint(Unit, values);
        var cutoff = IdempotencyRules.ReclamationCutoff(providerNow, declaration.Window);

        using (var reclaim = Command($"DELETE FROM {Quote(declaration.LedgerName)} WHERE rowid IN (SELECT rowid FROM {Quote(declaration.LedgerName)} WHERE {Quote(LedgerUnit)}=@reclaim_unit AND {Quote(LedgerCommittedAt)} <= @cutoff LIMIT 128);"))
        {
            reclaim.Parameters.AddWithValue("@reclaim_unit", Unit.Id.Value);
            reclaim.Parameters.AddWithValue("@cutoff", FormatLedgerTime(cutoff));
            reclaim.ExecuteNonQuery();
        }

        var expiredExisting = false;
        using (var existing = Command($"SELECT {Quote(LedgerCommittedAt)}, {Quote(LedgerFingerprint)}, {Quote(LedgerResult)} FROM {Quote(declaration.LedgerName)} WHERE {Quote(LedgerUnit)}=@unit AND {Quote(LedgerScope)}=@scope AND {Quote(LedgerNonce)}=@nonce;"))
        {
            AddLedgerParameters(existing, Unit.Id.Value, scope, operationId.Nonce);
            using var reader = existing.ExecuteReader();
            if (reader.Read())
            {
                var committedAt = DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                if (IdempotencyRules.IsWithinWindow(committedAt, providerNow, declaration.Window))
                {
                    var storedFingerprint = reader.IsDBNull(1) ? null : reader.GetString(1);
                    var storedResult = reader.IsDBNull(2) ? null : reader.GetString(2);
                    if (string.IsNullOrEmpty(storedFingerprint) || string.IsNullOrEmpty(storedResult))
                    {
                        if (!exactOutcomes)
                            return new AppendExecution(WriteOutcomeStatus.Replayed, null);
                        throw new InvalidOperationException("GW-APPEND-002: an existing append ledger entry has no exact result; use a new operation nonce.");
                    }
                    if (!exactOutcomes)
                        return new AppendExecution(WriteOutcomeStatus.Replayed, ExactAppendCodec.DeserializeOutcomes(storedResult));
                    if (!string.Equals(storedFingerprint, fingerprint, StringComparison.Ordinal))
                        throw new AppendIdempotencyConflictException(Unit.Id.Value, scope, operationId.Nonce, storedFingerprint, fingerprint);
                    return new AppendExecution(WriteOutcomeStatus.Replayed, ExactAppendCodec.DeserializeOutcomes(storedResult));
                }
                expiredExisting = true;
            }
        }

        if (expiredExisting)
        {
            using var deleteExpired = Command($"DELETE FROM {Quote(declaration.LedgerName)} WHERE {Quote(LedgerUnit)}=@unit AND {Quote(LedgerScope)}=@scope AND {Quote(LedgerNonce)}=@nonce;");
            AddLedgerParameters(deleteExpired, Unit.Id.Value, scope, operationId.Nonce);
            deleteExpired.ExecuteNonQuery();
        }

        using (var insertLedger = Command($"INSERT OR IGNORE INTO {Quote(declaration.LedgerName)} ({Quote(LedgerUnit)}, {Quote(LedgerScope)}, {Quote(LedgerNonce)}, {Quote(LedgerCommittedAt)}, {Quote(LedgerFingerprint)}, {Quote(LedgerResult)}) VALUES (@unit, @scope, @nonce, @committed_at, @fingerprint, @result);"))
        {
            AddLedgerParameters(insertLedger, Unit.Id.Value, scope, operationId.Nonce);
            insertLedger.Parameters.AddWithValue("@committed_at", FormatLedgerTime(providerNow));
            insertLedger.Parameters.AddWithValue("@fingerprint", fingerprint);
            insertLedger.Parameters.AddWithValue("@result", string.Empty);
            if (insertLedger.ExecuteNonQuery() == 0)
            {
                using var replay = Command($"SELECT {Quote(LedgerFingerprint)}, {Quote(LedgerResult)} FROM {Quote(declaration.LedgerName)} WHERE {Quote(LedgerUnit)}=@unit AND {Quote(LedgerScope)}=@scope AND {Quote(LedgerNonce)}=@nonce;");
                AddLedgerParameters(replay, Unit.Id.Value, scope, operationId.Nonce);
                using var reader = replay.ExecuteReader();
                if (!reader.Read() || reader.IsDBNull(0) || reader.IsDBNull(1) || string.IsNullOrEmpty(reader.GetString(1)))
                {
                    if (!exactOutcomes)
                        return new AppendExecution(WriteOutcomeStatus.Replayed, null);
                    throw new InvalidOperationException("GW-APPEND-002: an existing append ledger entry has no exact result; use a new operation nonce.");
                }
                var storedFingerprint = reader.GetString(0);
                if (!exactOutcomes)
                    return new AppendExecution(WriteOutcomeStatus.Replayed, ExactAppendCodec.DeserializeOutcomes(reader.GetString(1)));
                if (!string.Equals(storedFingerprint, fingerprint, StringComparison.Ordinal))
                    throw new AppendIdempotencyConflictException(Unit.Id.Value, scope, operationId.Nonce, storedFingerprint, fingerprint);
                return new AppendExecution(WriteOutcomeStatus.Replayed, ExactAppendCodec.DeserializeOutcomes(reader.GetString(1)));
            }
        }

        var logicalUnit = IdempotencyRules.LogicalUnit(Unit, SqliteSchemaCoordinator.ScopeColumn);
        var writes = values
            .Select(value => RowWrite.Insert(logicalUnit, value))
            .ToArray();
        var outcomes = SequenceColumnDefinition is not null
            ? writes.Select(InsertAppendSequence).ToArray()
            : ApplyBatchCore(writes);
        if (outcomes.Any(outcome => !outcome.Outcome.Succeeded))
            throw new InvalidOperationException("An idempotent append payload row was not accepted; the ledger and payload were rolled back.");
        var report = new AppendExecution(WriteOutcomeStatus.Inserted, outcomes.Select(outcome => outcome.Outcome).ToArray());
        using (var complete = Command($"UPDATE {Quote(declaration.LedgerName)} SET {Quote(LedgerResult)}=@result WHERE {Quote(LedgerUnit)}=@unit AND {Quote(LedgerScope)}=@scope AND {Quote(LedgerNonce)}=@nonce;"))
        {
            AddLedgerParameters(complete, Unit.Id.Value, scope, operationId.Nonce);
            complete.Parameters.AddWithValue("@result", ExactAppendCodec.SerializeOutcomes(report.Outcomes!));
            complete.ExecuteNonQuery();
        }
        return report;
    }

    private RowWriteOutcome InsertAppendSequence(RowWrite write)
    {
        var values = new StorageValues(SearchKeyProjection.Populate(Unit, write.Values!.Values));
        ValidateValues(values.Values, requireAllNonNullable: true);
        return new RowWriteOutcome(write, InsertCore(values.Values, WriteOutcomeStatus.Inserted));
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

    internal void Close() => closed = true;

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
        if (HasSecondaryUniqueIndex(writes[0].Unit))
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
        !HasSecondaryUniqueIndex(writes[0].Unit) &&
        writes.Select(write => write.ColumnSet).Distinct(StringComparer.Ordinal).Count() == 1 &&
        writes[0].Mode is RowWriteMode.Insert or RowWriteMode.Upsert;

    private IReadOnlyList<RowWriteOutcome> ApplyInsertBatch(IReadOnlyList<RowWrite> writes)
    {
        var supplied = UserColumns.Where(column => writes[0].Values!.Values.ContainsKey(column.Name)).ToArray();
        foreach (var write in writes)
        {
            ValidateValues(write.Values!.Values, requireAllNonNullable: true);
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
            ValidateValues(write.Values!.Values, requireAllNonNullable: false);
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

    private IReadOnlyList<RowWriteOutcome> ApplyBatchFallback(IReadOnlyList<RowWrite> writes) =>
        writes.Select(write => new RowWriteOutcome(write, write.Mode switch
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

    private static bool HasSecondaryUniqueIndex(StorageUnit logicalUnit) =>
        logicalUnit.Indexes.Any(index => index.IsUnique &&
            !index.Columns.Select(column => column.Column)
                .SequenceEqual(logicalUnit.Key.Columns, StringComparer.Ordinal));
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
        bool exactOutcome = false)
    {
        var onAppend = Unit.Retention?.Trigger == RetentionTrigger.OnAppend &&
            mutation is Mutation.Insert or Mutation.Upsert;
        var registration = BeginOnAppend(onAppend);
        WriteOutcome outcome;
        try
        {
            outcome = MutateCore(values, options, mutation, exactOutcome);
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
        return eligible && transaction is null
            ? OnAppendRetentionCoordinator.Begin(owner, Unit, Access.Scope?.Value)
            : null;
    }

    private void CompleteOnAppend(
        OnAppendRetentionCoordinator.AppendRegistration? registration,
        bool cleanupRequired)
    {
        void Cleanup()
        {
            if (owner.UsesSharedSessionConnection)
            {
                lock (owner.Gate)
                {
                    owner.ThrowIfDisposed();
                    ApplyRetentionCore(new RetentionExecutionOptions());
                }
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

    private WriteOutcome MutateCore(
        StorageValues values,
        WriteOptions? options,
        Mutation mutation,
        bool exactOutcome = false) => ExecuteWrite(() =>
    {
        ArgumentNullException.ThrowIfNull(values);
        values = new StorageValues(SearchKeyProjection.Populate(Unit, values.Values));
        ValidateValues(values.Values, mutation == Mutation.Insert,
            allowGeneratedLocator: mutation is Mutation.Update or Mutation.Upsert);
        if (SequenceColumnDefinition is not null &&
            (mutation is Mutation.Insert or Mutation.Upsert) &&
            !values.Values.ContainsKey(SequenceColumnDefinition.Name))
        {
            ValidateExpected(options, null, mutation);
            return InsertCore(values.Values, mutation == Mutation.Upsert ? WriteOutcomeStatus.Upserted : WriteOutcomeStatus.Inserted);
        }
        var key = new StorageKey(LogicalKeyColumns.ToDictionary(
            column => column, column => values.Values.TryGetValue(column, out var value) ? value : throw new ArgumentException($"Key column '{column}' is required.", nameof(values)),
            StringComparer.Ordinal));
        // None mode has no token to inspect. Keep direct writes single-statement and let the
        // database report uniqueness/not-found from the write itself.
        var existing = Unit.Concurrency.IsNone ? null : ReadCore(key);
        if (mutation == Mutation.Insert && existing is not null)
            return new WriteOutcome(WriteOutcomeStatus.UniqueViolation, existing.Version);
        if (mutation == Mutation.Update && existing is null && Unit.Concurrency.IsOptimistic)
            return new WriteOutcome(WriteOutcomeStatus.NotFound);
        if (mutation == Mutation.Upsert && SequenceColumnDefinition is not null &&
            values.Values.ContainsKey(SequenceColumnDefinition.Name) && existing is null && Unit.Concurrency.IsOptimistic)
            return new WriteOutcome(WriteOutcomeStatus.NotFound);
        ValidateExpected(options, existing, mutation);

        var supplied = UserColumns.Where(column => values.Values.ContainsKey(column.Name)).ToArray();
        var columns = supplied.ToList();
        if (VersionColumnDefinition is not null)
            columns.Add(VersionColumnDefinition);
        if (Unit.Scope == ScopePolicy.Scoped)
            columns.Add(ScopeColumnDefinition!);

        if (mutation == Mutation.Upsert && (SequenceColumnDefinition is null ||
            !values.Values.ContainsKey(SequenceColumnDefinition.Name)))
            return Upsert(values, existing, columns, exactOutcome, options);
        var sets = supplied.Where(column => !Unit.Key.Columns.Contains(column.Name, StringComparer.Ordinal))
            .Select(column => $"{Quote(column.Name)}=@{column.Name}").ToList();
        var parameters = BuildParameters(values.Values, supplied);
        if (mutation == Mutation.Insert)
        {
            if (VersionColumnDefinition is not null) parameters["@__groundwork_version"] = 1L;
            if (ScopeColumnDefinition is not null) parameters["@__groundwork_scope"] = Access.Scope!.Value;
            var returning = SequenceColumnDefinition is null ? string.Empty : $" RETURNING {Quote(SequenceColumnDefinition.Name)};";
            using var insert = Command($"INSERT INTO {Quote(Unit.Name)} ({string.Join(", ", columns.Select(column => Quote(column.Name)))}) VALUES ({string.Join(", ", columns.Select(column => "@" + column.Name))}){returning}");
            AddParameters(insert, parameters);
            commandObserver?.Observe(new ProviderCommandEvent("sqlite.insert", insert.CommandText, ProviderCommandKind.Write, IsProbe: false));
            try
            {
                if (SequenceColumnDefinition is null)
                {
                    insert.ExecuteNonQuery();
                    return new WriteOutcome(WriteOutcomeStatus.Inserted, VersionColumnDefinition is null ? (long?)null : 1);
                }

                object? generatedValue;
                using (var reader = insert.ExecuteReader())
                {
                    if (!reader.Read())
                        return new WriteOutcome(WriteOutcomeStatus.UniqueViolation);
                    generatedValue = FromSqlite(reader.GetValue(0), SequenceColumnDefinition);
                }
                RecordHighWater(generatedValue);
                return new WriteOutcome(
                    WriteOutcomeStatus.Inserted,
                    VersionColumnDefinition is null ? null : 1,
                    generatedValues: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        [SequenceColumnDefinition.Name] = generatedValue
                    });
            }
            catch (SqliteException exception) when (new SqliteDialect().TryMapUniqueViolation(exception, out _)) { return new WriteOutcome(WriteOutcomeStatus.UniqueViolation); }
        }

        var (where, keyParameters) = KeyPredicate(key.Values);
        foreach (var pair in keyParameters) parameters[pair.Key] = pair.Value;
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
    });

    private WriteOutcome ConditionalUpsertCore(StorageValues values, WriteOptions? options)
    {
        ArgumentNullException.ThrowIfNull(values);
        values = new StorageValues(SearchKeyProjection.Populate(Unit, values.Values));
        ValidateValues(values.Values, requireAllNonNullable: false);

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
        bool exactOutcome,
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
            return new WriteOutcome(
                exactOutcome
                    ? existing is null ? WriteOutcomeStatus.Inserted : WriteOutcomeStatus.Updated
                    : WriteOutcomeStatus.Upserted,
                version);
        }
        catch (SqliteException exception) when (new SqliteDialect().TryMapUniqueViolation(exception, out _)) { return new WriteOutcome(WriteOutcomeStatus.UniqueViolation, existing?.Version); }
    }

    private StoredEntry? ReadCore(
        StorageKey key,
        string? observerOperation = null,
        bool isProbe = true)
    {
        var (where, parameters) = KeyPredicate(key.Values);
        var columns = UserColumns.Concat(VersionColumnDefinition is null ? [] : [VersionColumnDefinition]);
        using var command = Command($"SELECT {string.Join(", ", columns.Select(column => Quote(column.Name)))} FROM {Quote(Unit.Name)} WHERE {where};");
        AddParameters(command, parameters);
        commandObserver?.Observe(new ProviderCommandEvent(observerOperation ?? "sqlite.write-probe", command.CommandText, ProviderCommandKind.Read, IsProbe: isProbe));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < UserColumns.Count; i++) values[UserColumns[i].Name] = FromSqlite(reader.GetValue(i), UserColumns[i]);
        return new StoredEntry(new StorageValues(values), VersionColumnDefinition is null ? null : Convert.ToInt64(reader.GetValue(UserColumns.Count), CultureInfo.InvariantCulture));
    }

    private void ValidateValues(
        IReadOnlyDictionary<string, object?> values,
        bool requireAllNonNullable,
        bool allowGeneratedLocator = false)
    {
        var known = UserColumns.Select(column => column.Name).ToHashSet(StringComparer.Ordinal);
        var unknown = values.Keys.FirstOrDefault(key => !known.Contains(key));
        if (unknown is not null) throw new ArgumentException($"Column '{unknown}' is not declared by '{Unit.Name}'.", nameof(values));
        foreach (var generated in UserColumns.Where(column => column.Generation == ColumnGeneration.ProviderSequence))
            if (values.ContainsKey(generated.Name) && !allowGeneratedLocator)
                throw new ArgumentException($"ProviderSequence column '{generated.Name}' is assigned by SQLite; it may only be supplied as the locator for Update or Upsert.", nameof(values));
        if (requireAllNonNullable)
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
        if (VersionColumnDefinition is null) return;
        var precondition = options?.Precondition ?? WritePrecondition.Unconditional;
        if (mutation == Mutation.Insert)
        {
            return;
        }
        if (mutation == Mutation.Upsert)
        {
            if (precondition.Kind == WritePreconditionKind.CreateOnly && existing is not null)
                throw new ConcurrencyConflictException(existing.Version);
            if (precondition.Kind == WritePreconditionKind.IfVersion &&
                (existing is null || precondition.Version != existing.Version))
                throw new ConcurrencyConflictException(existing?.Version);
            return;
        }
        if (precondition.Kind == WritePreconditionKind.IfVersion &&
            (existing is null || precondition.Version != existing.Version))
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
        StorageAccessValidation.EnsurePointOperation(Access, "write");
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
    private sealed record AppendExecution(WriteOutcomeStatus Status, IReadOnlyList<WriteOutcome>? Outcomes)
    {
        internal AppendOutcomeReport ToReport() =>
            new(Status, Outcomes ?? throw new InvalidOperationException("GW-APPEND-002: an exact append result was not recorded."));
    }
    private sealed class ConcurrencyConflictException(long? version = null) : Exception { public long? Version { get; } = version; }
}
