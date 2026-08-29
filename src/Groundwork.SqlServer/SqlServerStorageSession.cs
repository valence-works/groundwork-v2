using System.Data;
using System.Data.Common;
using System.Data.SqlTypes;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Groundwork.Kernel;
using Groundwork.Query.Model;
using Groundwork.Substrate.Relational;
using Groundwork.Store;
using Groundwork.Diagnostics;

namespace Groundwork.SqlServer;

internal class SqlServerStorageSession : IStorageSession, IProviderBoundStorageSession, IExactAppendStorageSession, IConcurrencyStorageSession, ICompareAndDeleteStorageSession, IBatchedStorageSession, IRetentionStorageSession, IStorageInspectionSession, IExactRetentionStorageSession, IPrivilegedCrossScopeQuerySession, ISetMutationStorageSession
{
    private readonly SqlServerProviderConnection owner;
    private readonly SqlConnection connection;
    private readonly SqlTransaction? transaction;
    private readonly RelationalSessionExecution execution;
    private readonly RelationalSessionPointReads pointReads;
    private readonly RelationalSessionCrud crud;
    private readonly RelationalSessionQueries queries;
    private readonly RelationalSessionAggregations aggregations;
    private readonly RelationalSessionSetMutations setMutations;
    private readonly RelationalSessionAppends appends;
    private readonly SqlServerDialect dialect = new();

    /// <summary>
    /// True when opened through <c>OpenOwnedSession</c>, so disposal returns this session's connection.
    /// A view from <c>OpenSession</c> and a session from a unit of work both belong to someone else.
    /// </summary>
    private readonly bool ownsConnection;

    internal SqlServerStorageSession(SqlServerProviderConnection owner, StorageUnit unit, StorageAccess access,
        SqlConnection connection, SqlTransaction? transaction,
        IProviderCommandObserver? observer = null,
        bool ownsConnection = false)
    {
        this.ownsConnection = ownsConnection;
        commandObserver = observer;
        this.owner = owner;
        Unit = unit;
        Access = access;
        this.connection = connection;
        this.transaction = transaction;
        execution = new RelationalSessionExecution(
            access,
            transaction,
            ownsConnection,
            new SqlServerSessionExecutionAdapter(owner, connection),
            nameof(SqlServerStorageSession));
        pointReads = new RelationalSessionPointReads(
            unit,
            access,
            UserColumns,
            VersionColumnDefinition,
            Command,
            new SqlServerPointReadAdapter(),
            observer,
            "sqlserver");
        crud = new RelationalSessionCrud(
            unit,
            UserColumns,
            SequenceColumnDefinition,
            VersionColumnDefinition,
            "SQL Server",
            (key, mode) => ReadCore(key, mode),
            new SqlServerCrudAdapter(this));
        queries = new RelationalSessionQueries(
            unit,
            access,
            connection,
            new SqlServerQueryRenderer(),
            PhysicalIndexNames,
            FromSqlServer,
            AssertExplainPlan,
            observer,
            "sqlserver");
        aggregations = new RelationalSessionAggregations(
            unit,
            access,
            connection,
            dialect,
            FromSqlServer,
            observer,
            "sqlserver.aggregate");
        setMutations = new RelationalSessionSetMutations(
            unit,
            access,
            new SqlServerQueryRenderer(),
            unit.Columns.FirstOrDefault(column => column.Name == SqlServerSchemaCoordinator.VersionColumn)?.Name,
            Command,
            (command, name, value, column) => SqlServerProviderConnection.AddParameter(
                (SqlCommand)command,
                "@" + name,
                value,
                column),
            observer,
            "sqlserver");
        appends = new RelationalSessionAppends(unit, access, new SqlServerAppendAdapter(this));
    }

    /// <summary>
    /// Counts every provider command this session issues. It belongs to the session because the session is
    /// what issues commands; it used to be read off an individual write's options, so a batch observed only
    /// whatever happened to be staged first.
    /// </summary>
    private readonly IProviderCommandObserver? commandObserver;

    public StorageUnit Unit { get; }
    public StorageAccess Access { get; }

    IStorageProviderConnection IProviderBoundStorageSession.ProviderConnection => owner;

    /// <summary>Maps every declared logical index name to the physical name the catalog carries.</summary>
    private IReadOnlyDictionary<string, string> PhysicalIndexNames() => Unit.Indexes.ToDictionary(
        index => index.Name,
        index => SqlServerDialect.PhysicalIndexName(Unit.Name, index.Name),
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
        using (var enable = Command("SET STATISTICS XML ON"))
            await mode.ExecuteNonQuery(enable).ConfigureAwait(false);
        string rawPlan;
        try
        {
            using var explain = Command(query.CommandText);
            RelationalQueryResultReader.AddParameters(explain, query);
            await using var readerScope = await mode.ExecuteReader(explain).ConfigureAwait(false);
            var reader = readerScope.Reader;
            var plans = new List<string>();
            do
            {
                while (await mode.Read(reader).ConfigureAwait(false))
                for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
                {
                    if (!reader.GetName(ordinal).Contains("XML Showplan", StringComparison.OrdinalIgnoreCase) &&
                        reader.GetFieldType(ordinal) != typeof(SqlXml))
                        continue;
                    var content = reader.GetValue(ordinal) switch
                    {
                        SqlXml xml when !xml.IsNull => xml.Value,
                        SqlString text when !text.IsNull => text.Value,
                        string text => text,
                        _ => null
                    };
                    if (!string.IsNullOrWhiteSpace(content)) plans.Add(content);
                }
            } while ((await mode.NextResult(reader).ConfigureAwait(false)));
            rawPlan = string.Join(Environment.NewLine, plans);
        }
        finally
        {
            using var disable = Command("SET STATISTICS XML OFF");
            await mode.ExecuteNonQuery(disable).ConfigureAwait(false);
        }
        ExplainAssertionMode.AssertChosenIndex(
            "SQL Server", logicalIndex, physicalIndex, query.IndexHintApplied, rawPlan,
            SqlServerExplainPlanInspector.ChoseIndex(rawPlan, physicalIndex));
    }

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
        WritePreconditionValidator.ValidateWrittenValues(Unit, values.Values);
        WritePreconditionValidator.Validate(Unit, WriteOperation.ConditionalUpsert, options);
        var onAppend = Unit.Retention?.Trigger == RetentionTrigger.OnAppend;
        var registration = BeginOnAppend(onAppend);
        WriteOutcome outcome;
        try
        {
            outcome = await Execute(() => ConditionalUpsertCore(values, options, mode), mode).ConfigureAwait(false);
        }
        catch
        {
            await CompleteOnAppend(registration, cleanupRequired: false, mode).ConfigureAwait(false);
            throw;
        }
        await CompleteOnAppend(registration, onAppend && outcome.Status == WriteOutcomeStatus.Inserted, mode)
            .ConfigureAwait(false);
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
        var registration = BeginOnAppend(nativeOnAppend);
        IReadOnlyList<RowWriteOutcome> outcomes;
        try
        {
            outcomes = await ExecuteWrite(() => ApplyBatchCore(writes, mode), mode).ConfigureAwait(false);
        }
        catch
        {
            await CompleteOnAppend(registration, cleanupRequired: false, mode).ConfigureAwait(false);
            throw;
        }
        var succeeded = nativeOnAppend && OnAppendRetentionCoordinator.ContainsAppend(outcomes);
        await CompleteOnAppend(registration, succeeded, mode).ConfigureAwait(false);
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
            var (where, parameters) = KeyPredicate(canonicalKey.Values, exactStringKeys: true);
            foreach (var pair in expected)
            {
                if (pair.Value is null)
                {
                    where += $" AND {Quote(pair.Key)} IS NULL";
                }
                else
                {
                    var parameter = "@compare_" + pair.Key;
                    var columnPredicate = $"{Quote(pair.Key)}={parameter}";
                    if (Column(pair.Key).Type == PortableType.String)
                    {
                        columnPredicate = $"DATALENGTH({Quote(pair.Key)})=DATALENGTH({parameter}) AND {columnPredicate}";
                    }
                    where += $" AND {columnPredicate}";
                    parameters[parameter] = (pair.Value, Column(pair.Key));
                }
            }
            if (VersionColumnDefinition is not null && options?.Precondition.Kind == WritePreconditionKind.IfVersion)
            {
                where += $" AND {Quote(VersionColumnDefinition.Name)}=@expected";
                parameters["@expected"] = (options.Precondition.Version!.Value, VersionColumnDefinition);
            }

            var output = VersionColumnDefinition is null ? string.Empty : $" OUTPUT deleted.{Quote(VersionColumnDefinition.Name)}";
            using var command = Command($"DELETE FROM {Quote(Unit.Name)}{output} WHERE {where};");
            AddParameters(command, parameters);
            commandObserver?.Observe(new ProviderCommandEvent("sqlserver.compare-and-delete", command.CommandText, ProviderCommandKind.Write, IsProbe: false));
            if (VersionColumnDefinition is not null)
            {
                await using (var readerScope = await mode.ExecuteReader(command).ConfigureAwait(false))
                {
                    var reader = readerScope.Reader;
                    if (await mode.Read(reader).ConfigureAwait(false))
                        return new WriteOutcome(WriteOutcomeStatus.Deleted, Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture));
                }
            }
            else if ((await mode.ExecuteNonQuery(command).ConfigureAwait(false)) != 0)
            {
                return new WriteOutcome(WriteOutcomeStatus.Deleted);
            }

            var existing = await ReadCore(canonicalKey, mode, "sqlserver.compare-and-delete-read", exactStringKeys: true).ConfigureAwait(false);
            if (existing is null)
                return new WriteOutcome(WriteOutcomeStatus.NotFound);
            if (options?.Precondition.Kind == WritePreconditionKind.IfVersion &&
                options.Precondition.Version != existing.Version)
                return new WriteOutcome(WriteOutcomeStatus.ConcurrencyConflict, existing.Version);
            return RelationalSessionPolicy.MatchesExpected(Unit, existing, expected)
                ? new WriteOutcome(WriteOutcomeStatus.ConcurrencyConflict, existing.Version)
                : new WriteOutcome(WriteOutcomeStatus.ComparisonMismatch, existing.Version);
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

    private ValueTask<RetentionResult> ApplyRetention(RetentionExecutionOptions? options, RelationalExecution mode) =>
        ExecuteWrite(() => ApplyRetentionCore(options ?? new RetentionExecutionOptions(), mode), mode);

    private async ValueTask<RetentionResult> ApplyRetentionCore(RetentionExecutionOptions options, RelationalExecution mode)
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
        var scope = Unit.Columns.Any(column => column.Name == SqlServerSchemaCoordinator.ScopeColumn)
            ? $" WHERE {Quote(SqlServerSchemaCoordinator.ScopeColumn)}=@__groundwork_scope"
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
                $"SELECT TOP (@limit) {keys} FROM ranked WHERE __groundwork_retention_rank > @keep) " +
                $"DELETE target FROM {Quote(Unit.Name)} AS target INNER JOIN victims AS victim ON {equality};");
            SqlServerProviderConnection.AddParameter(command, "@keep", keepNewest,
                new ColumnDefinition { Name = "keep", Type = PortableType.Int32, IsNullable = false });
            SqlServerProviderConnection.AddParameter(command, "@limit", options.MaxRowsPerBatch,
                new ColumnDefinition { Name = "limit", Type = PortableType.Int32, IsNullable = false });
            if (Unit.Columns.Any(column => column.Name == SqlServerSchemaCoordinator.ScopeColumn))
                SqlServerProviderConnection.AddParameter(command, "@__groundwork_scope", Access.Scope!.Value,
                    new ColumnDefinition { Name = SqlServerSchemaCoordinator.ScopeColumn, Type = PortableType.String, MaxLength = 128, IsNullable = false });
            var affected = await mode.ExecuteNonQuery(command).ConfigureAwait(false);
            commandObserver?.Observe(new ProviderCommandEvent("sqlserver.retention-delete", command.CommandText, ProviderCommandKind.Write, IsProbe: false));
            if (affected == 0)
                break;
            deleted += affected;
            batches++;
            if (affected < options.MaxRowsPerBatch)
                break;
        }
        return new RetentionResult(deleted, batches);
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
        AddLedgerParameter(command, "unit", Unit.Id.Value);
        AddLedgerParameter(command, "scope", Access.Scope?.Value ?? string.Empty);
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
        var declaration = Unit.RetentionIdempotency ?? throw new InvalidOperationException(
            $"Storage unit '{Unit.Name}' does not declare retention idempotency; declare RetentionIdempotency before using operation-identified retention.");
        declaration.Validate(Unit);
        options ??= new RetentionExecutionOptions();
        RetentionSessionExtensions.ValidateExecutionOptions(options);
        RetentionOperationCodec.ValidateOperation(operationId);
        return ExecuteWrite(() => ApplyExactRetentionCore(operationId, declaration, options, mode), mode);
    }

    private async ValueTask<RetentionOperationResult> ApplyExactRetentionCore(
        OperationId operationId,
        RetentionIdempotencyDeclaration declaration,
        RetentionExecutionOptions options,
        RelationalExecution mode)
    {
        await EnsureLedgerTable(declaration.LedgerName, mode).ConfigureAwait(false);
        var providerNow = await ProviderNow(mode).ConfigureAwait(false);
        var scope = Access.Scope?.Value ?? string.Empty;
        var fingerprint = RetentionOperationCodec.Fingerprint(Unit, options);
        var cutoff = IdempotencyRules.ReclamationCutoff(providerNow, declaration.Window);
        using (var reclaim = Command($"WITH expired AS (SELECT TOP (128) * FROM {Quote(declaration.LedgerName)} WHERE {Quote(LedgerUnit)}=@reclaim_unit AND {Quote(LedgerCommittedAt)} <= @cutoff) DELETE FROM expired;"))
        {
            AddLedgerParameter(reclaim, "reclaim_unit", Unit.Id.Value);
            AddLedgerParameter(reclaim, "cutoff", FormatLedgerTime(cutoff));
            await mode.ExecuteNonQuery(reclaim).ConfigureAwait(false);
        }

        var existing = await ReadRetentionLedger(declaration.LedgerName, operationId, scope, mode).ConfigureAwait(false);
        if (existing is not null)
        {
            var (committedAt, storedFingerprint, storedResult) = existing.Value;
            if (IdempotencyRules.IsWithinWindow(committedAt, providerNow, declaration.Window))
            {
                if (string.IsNullOrEmpty(storedFingerprint) || string.IsNullOrEmpty(storedResult))
                    throw new InvalidOperationException("GW-RETENTION-002: an existing exact retention ledger entry has no exact result; use a new operation nonce.");
                if (!string.Equals(storedFingerprint, fingerprint, StringComparison.Ordinal))
                    throw new RetentionIdempotencyConflictException(Unit.Id.Value, scope, operationId.Nonce, storedFingerprint, fingerprint);
                return RetentionOperationCodec.DeserializeResult(storedResult) with { Status = RetentionOperationStatus.Replayed };
            }

            using var deleteExpired = Command($"DELETE FROM {Quote(declaration.LedgerName)} WHERE {Quote(LedgerUnit)}=@unit AND {Quote(LedgerScope)}=@scope AND {Quote(LedgerNonce)}=@nonce;");
            AddLedgerParameters(deleteExpired, Unit.Id.Value, scope, operationId.Nonce);
            await mode.ExecuteNonQuery(deleteExpired).ConfigureAwait(false);
        }

        using (var insertLedger = Command($"INSERT INTO {Quote(declaration.LedgerName)} ({Quote(LedgerUnit)}, {Quote(LedgerScope)}, {Quote(LedgerNonce)}, {Quote(LedgerCommittedAt)}, {Quote(LedgerFingerprint)}, {Quote(LedgerResult)}) SELECT @unit, @scope, @nonce, @committed_at, @fingerprint, @result WHERE NOT EXISTS (SELECT 1 FROM {Quote(declaration.LedgerName)} WITH (UPDLOCK, HOLDLOCK) WHERE {Quote(LedgerUnit)}=@unit AND {Quote(LedgerScope)}=@scope AND {Quote(LedgerNonce)}=@nonce);"))
        {
            AddLedgerParameters(insertLedger, Unit.Id.Value, scope, operationId.Nonce);
            AddLedgerParameter(insertLedger, "committed_at", FormatLedgerTime(providerNow));
            AddLedgerParameter(insertLedger, "fingerprint", fingerprint);
            AddLedgerParameter(insertLedger, "result", string.Empty);
            if ((await mode.ExecuteNonQuery(insertLedger).ConfigureAwait(false)) == 0)
            {
                var raced = await ReadRetentionLedger(declaration.LedgerName, operationId, scope, mode).ConfigureAwait(false);
                if (raced is null || string.IsNullOrEmpty(raced.Value.storedFingerprint) || string.IsNullOrEmpty(raced.Value.storedResult))
                    throw new InvalidOperationException("GW-RETENTION-002: an existing exact retention ledger entry has no exact result; use a new operation nonce.");
                if (!string.Equals(raced.Value.storedFingerprint, fingerprint, StringComparison.Ordinal))
                    throw new RetentionIdempotencyConflictException(Unit.Id.Value, scope, operationId.Nonce, raced.Value.storedFingerprint, fingerprint);
                return RetentionOperationCodec.DeserializeResult(raced.Value.storedResult) with { Status = RetentionOperationStatus.Replayed };
            }
        }

        var retention = await ApplyRetentionCore(options, mode).ConfigureAwait(false);
        var result = new RetentionOperationResult(RetentionOperationStatus.Executed, retention.DeletedRows, retention.Batches, retention.Completed);
        using var complete = Command($"UPDATE {Quote(declaration.LedgerName)} SET {Quote(LedgerResult)}=@result WHERE {Quote(LedgerUnit)}=@unit AND {Quote(LedgerScope)}=@scope AND {Quote(LedgerNonce)}=@nonce;");
        AddLedgerParameters(complete, Unit.Id.Value, scope, operationId.Nonce);
        AddLedgerParameter(complete, "result", RetentionOperationCodec.SerializeResult(result));
        await mode.ExecuteNonQuery(complete).ConfigureAwait(false);
        return result;
    }

    private async ValueTask<(DateTimeOffset committedAt, string? storedFingerprint, string? storedResult)?> ReadRetentionLedger(
        string table,
        OperationId operationId,
        string scope,
        RelationalExecution mode)
    {
        using var command = Command($"SELECT {Quote(LedgerCommittedAt)}, {Quote(LedgerFingerprint)}, {Quote(LedgerResult)} FROM {Quote(table)} WHERE {Quote(LedgerUnit)}=@unit AND {Quote(LedgerScope)}=@scope AND {Quote(LedgerNonce)}=@nonce;");
        AddLedgerParameters(command, Unit.Id.Value, scope, operationId.Nonce);
        await using var readerScope = await mode.ExecuteReader(command).ConfigureAwait(false);
        var reader = readerScope.Reader;
        if (!(await mode.Read(reader).ConfigureAwait(false)))
            return null;
        return (
            DateTimeOffset.Parse(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.IsDBNull(1) ? null : Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture),
            reader.IsDBNull(2) ? null : Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture));
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
                operation, mode).ConfigureAwait(false)).ToReport(), mode)
                .ConfigureAwait(false);
        }
        catch
        {
            await CompleteOnAppend(registration, cleanupRequired: false, mode).ConfigureAwait(false);
            throw;
        }
        await CompleteOnAppend(registration,
            onAppend && outcome.Status is WriteOutcomeStatus.Inserted or WriteOutcomeStatus.Replayed, mode)
            .ConfigureAwait(false);
        return outcome;
    }

    private async ValueTask<RowWriteOutcome> InsertAppendSequence(RowWrite write, RelationalExecution mode)
    {
        var values = new StorageValues(SearchKeyProjection.Populate(Unit, write.Values!.Values));
        RelationalSessionPolicy.ValidateValues(Unit, UserColumns, "SQL Server", values.Values, requireAllNonNullable: true);
        return new RowWriteOutcome(write, await InsertCore(values.Values, mode, WriteOutcomeStatus.Inserted).ConfigureAwait(false));
    }

    private async ValueTask EnsureLedgerTable(string table, RelationalExecution mode)
    {
        using var command = Command($"BEGIN TRY IF OBJECT_ID(N'{table.Replace("'", "''", StringComparison.Ordinal)}', N'U') IS NULL BEGIN CREATE TABLE {Quote(table)} (" +
            $"{Quote(LedgerUnit)} nvarchar(450) COLLATE {BinaryIdentityCollation} NOT NULL, " +
            $"{Quote(LedgerScope)} nvarchar(128) COLLATE {BinaryIdentityCollation} NOT NULL, " +
            $"{Quote(LedgerNonce)} nvarchar(256) COLLATE {BinaryIdentityCollation} NOT NULL, " +
            $"{Quote(LedgerCommittedAt)} nvarchar(64) NOT NULL, " +
            $"{Quote(LedgerFingerprint)} nvarchar(128) NULL, " +
            $"{Quote(LedgerResult)} nvarchar(max) NULL, " +
            // The tuple is 1,668 bytes at its declared maxima. A clustered key is capped at
            // 900 bytes, while SQL Server's nonclustered key budget is 1,700 bytes.
            $"PRIMARY KEY NONCLUSTERED ({Quote(LedgerUnit)}, {Quote(LedgerScope)}, {Quote(LedgerNonce)})); END; END TRY BEGIN CATCH IF ERROR_NUMBER() <> 2714 THROW; END CATCH;");
        await mode.ExecuteNonQuery(command).ConfigureAwait(false);

        await EnsureLedgerColumn(table, LedgerFingerprint, "nvarchar(128)", mode).ConfigureAwait(false);
        await EnsureLedgerColumn(table, LedgerResult, "nvarchar(max)", mode).ConfigureAwait(false);
        await EnsureBinaryIdentityColumns(table, [LedgerUnit, LedgerScope, LedgerNonce], mode).ConfigureAwait(false);

        using var cleanupIndex = Command($"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{IdempotencyRules.CleanupIndexName(table)}' AND object_id = OBJECT_ID(N'{table.Replace("'", "''", StringComparison.Ordinal)}')) " +
            $"CREATE INDEX {Quote(IdempotencyRules.CleanupIndexName(table))} ON {Quote(table)} ({Quote(LedgerUnit)}, {Quote(LedgerCommittedAt)});");
        await mode.ExecuteNonQuery(cleanupIndex).ConfigureAwait(false);
    }

    private async ValueTask EnsureLedgerColumn(string table, string column, string type, RelationalExecution mode)
    {
        var escapedTable = table.Replace("'", "''", StringComparison.Ordinal);
        using var alter = Command($"IF COL_LENGTH(N'{escapedTable}', N'{column}') IS NULL ALTER TABLE {Quote(table)} ADD {Quote(column)} {type} NULL;");
        try
        {
            await mode.ExecuteNonQuery(alter).ConfigureAwait(false);
        }
        catch (SqlException exception) when (exception.Number == 2705)
        {
            // Another session may have passed the COL_LENGTH check concurrently.
            // Duplicate-column means the additive upgrade is already complete.
        }
    }

    private async ValueTask EnsureHighWaterTable(RelationalExecution mode)
    {
        using var command = Command($"BEGIN TRY IF OBJECT_ID(N'{HighWaterTable}', N'U') IS NULL BEGIN CREATE TABLE {Quote(HighWaterTable)} (" +
            $"{Quote(LedgerUnit)} nvarchar(450) COLLATE {BinaryIdentityCollation} NOT NULL, " +
            $"{Quote(LedgerScope)} nvarchar(128) COLLATE {BinaryIdentityCollation} NOT NULL, " +
            $"{Quote(HighWaterValue)} bigint NOT NULL, " +
            $"PRIMARY KEY NONCLUSTERED ({Quote(LedgerUnit)}, {Quote(LedgerScope)})); END; END TRY BEGIN CATCH IF ERROR_NUMBER() <> 2714 THROW; END CATCH;");
        await mode.ExecuteNonQuery(command).ConfigureAwait(false);
        await EnsureBinaryIdentityColumns(HighWaterTable, [LedgerUnit, LedgerScope], mode).ConfigureAwait(false);
    }

    private async ValueTask EnsureBinaryIdentityColumns(string table, IReadOnlyList<string> columns, RelationalExecution mode)
    {
        var escapedTable = table.Replace("'", "''", StringComparison.Ordinal);
        using var command = Command($"SELECT c.name, c.collation_name FROM sys.columns c " +
            $"WHERE c.object_id = OBJECT_ID(N'{escapedTable}', N'U') AND c.name IN ({string.Join(", ", columns.Select(column => "N'" + column.Replace("'", "''", StringComparison.Ordinal) + "'"))});");
        await using var readerScope = await mode.ExecuteReader(command).ConfigureAwait(false);
        var reader = readerScope.Reader;
        var collations = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        while (await mode.Read(reader).ConfigureAwait(false))
            collations[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);

        if (columns.Any(column => !collations.TryGetValue(column, out var collation) ||
                                  !string.Equals(collation, BinaryIdentityCollation, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"{LifecycleSchemaDiagnosticCode}: SQL Server lifecycle table '{table}' must use " +
                $"{BinaryIdentityCollation} on identity columns ({string.Join(", ", columns)}). " +
                "Recreate or migrate the table under the current Groundwork lifecycle schema before retrying.");
        }
    }

    private async ValueTask RecordHighWater(object? generatedValue, RelationalExecution mode)
    {
        if (SequenceColumnDefinition is null || generatedValue is null)
            return;
        await EnsureHighWaterTable(mode).ConfigureAwait(false);
        using var command = Command($"MERGE {Quote(HighWaterTable)} WITH (HOLDLOCK) AS target " +
            $"USING (SELECT @unit AS {Quote(LedgerUnit)}, @scope AS {Quote(LedgerScope)}, @value AS {Quote(HighWaterValue)}) AS source " +
            $"ON target.{Quote(LedgerUnit)}=source.{Quote(LedgerUnit)} AND target.{Quote(LedgerScope)}=source.{Quote(LedgerScope)} " +
            $"WHEN MATCHED THEN UPDATE SET {Quote(HighWaterValue)}=CASE WHEN target.{Quote(HighWaterValue)} < source.{Quote(HighWaterValue)} THEN source.{Quote(HighWaterValue)} ELSE target.{Quote(HighWaterValue)} END " +
            $"WHEN NOT MATCHED THEN INSERT ({Quote(LedgerUnit)}, {Quote(LedgerScope)}, {Quote(HighWaterValue)}) VALUES (source.{Quote(LedgerUnit)}, source.{Quote(LedgerScope)}, source.{Quote(HighWaterValue)}); ");
        AddLedgerParameter(command, "unit", Unit.Id.Value);
        AddLedgerParameter(command, "scope", Access.Scope?.Value ?? string.Empty);
        AddLedgerParameter(command, "value", Convert.ToInt64(generatedValue, CultureInfo.InvariantCulture));
        await mode.ExecuteNonQuery(command).ConfigureAwait(false);
    }

    private static void AddLedgerParameters(SqlCommand command, string unit, string scope, string nonce)
    {
        AddLedgerParameter(command, "unit", unit);
        AddLedgerParameter(command, "scope", scope);
        AddLedgerParameter(command, "nonce", nonce);
    }

    private static void AddLedgerParameter(SqlCommand command, string name, object? value) =>
        command.Parameters.AddWithValue("@" + name, value);

    private static string FormatLedgerTime(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private async ValueTask<DateTimeOffset> ProviderNow(RelationalExecution mode)
    {
        using var command = Command("SELECT SYSUTCDATETIME();");
        var value = (DateTime)(await mode.ExecuteScalar(command).ConfigureAwait(false))!;
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
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

    private async ValueTask<IReadOnlyList<RowWriteOutcome>> ApplyBatchCore(IReadOnlyList<RowWrite> writes, RelationalExecution mode)
    {
        ArgumentNullException.ThrowIfNull(writes);
        if (writes.Count == 0)
            return [];
        if (SequenceColumnDefinition is not null)
            return await ApplyBatchFallback(writes, mode).ConfigureAwait(false);
        if (writes.Any(write => write.Options.Precondition.Kind != WritePreconditionKind.Unconditional))
            return await ApplyBatchFallback(writes, mode).ConfigureAwait(false);
        if (RelationalSessionPolicy.HasSecondaryUniqueIndex(writes[0].Unit))
            return await ApplyBatchFallback(writes, mode).ConfigureAwait(false);
        if (writes[0].Mode is not (RowWriteMode.Insert or RowWriteMode.Upsert))
            return await ApplyBatchFallback(writes, mode).ConfigureAwait(false);

        var physicalWrites = writes.Select(write => write.PopulateSearchKeyValues()).ToArray();

        var columns = PhysicalBatchColumns(physicalWrites[0]);
        foreach (var write in physicalWrites)
        {
            RelationalSessionPolicy.ValidateValues(Unit, UserColumns, "SQL Server", write.Values!.Values, requireAllNonNullable: write.Mode == RowWriteMode.Insert);
            if (!PhysicalBatchColumns(write).Select(column => column.Name).SequenceEqual(columns.Select(column => column.Name), StringComparer.Ordinal))
                return await ApplyBatchFallback(writes, mode).ConfigureAwait(false);
        }

        return await ApplyMergeBatch(physicalWrites, columns, mode).ConfigureAwait(false);
    }

    private bool IsNativeAppendBatch(IReadOnlyList<RowWrite> writes) =>
        Unit.Retention?.Trigger == RetentionTrigger.OnAppend &&
        writes.Count != 0 &&
        SequenceColumnDefinition is null &&
        writes.All(write => write.Options.Precondition.Kind == WritePreconditionKind.Unconditional) &&
        !RelationalSessionPolicy.HasSecondaryUniqueIndex(writes[0].Unit) &&
        writes.Select(write => write.ColumnSet).Distinct(StringComparer.Ordinal).Count() == 1 &&
        writes[0].Mode is RowWriteMode.Insert or RowWriteMode.Upsert;

    private IReadOnlyList<ColumnDefinition> PhysicalBatchColumns(RowWrite write)
    {
        var columns = UserColumns.Where(column => write.Values!.Values.ContainsKey(column.Name)).ToList();
        if (VersionColumnDefinition is not null)
            columns.Add(VersionColumnDefinition);
        if (ScopeColumnDefinition is not null)
            columns.Add(ScopeColumnDefinition);
        return columns;
    }

    private async ValueTask<IReadOnlyList<RowWriteOutcome>> ApplyMergeBatch(
        IReadOnlyList<RowWrite> writes,
        IReadOnlyList<ColumnDefinition> columns,
        RelationalExecution mode)
    {
        try
        {
            return await ApplyMergeBatchTableValued(writes, columns, mode).ConfigureAwait(false);
        }
        catch (SqlException exception) when (exception.Message.Contains("table type", StringComparison.OrdinalIgnoreCase) ||
                                              exception.Message.Contains("type name", StringComparison.OrdinalIgnoreCase))
        {
            // Existing installations can be upgraded before the provider definition is
            // materialized. Preserve a VALUES fallback while the durable TVP catches up.
            return await ApplyMergeBatchValues(writes, columns, mode).ConfigureAwait(false);
        }
    }

    private async ValueTask<IReadOnlyList<RowWriteOutcome>> ApplyMergeBatchTableValued(
        IReadOnlyList<RowWrite> writes,
        IReadOnlyList<ColumnDefinition> columns,
        RelationalExecution mode)
    {
        using var command = Command(string.Empty);
        var table = new DataTable();
        foreach (var column in Unit.Columns)
            table.Columns.Add(column.Name, ClrType(column.Type));
        foreach (var write in writes)
        {
            var row = table.NewRow();
            foreach (var column in Unit.Columns)
            {
                row[column.Name] = column.Name == SqlServerSchemaCoordinator.VersionColumn
                    ? 1L
                    : column.Name == SqlServerSchemaCoordinator.ScopeColumn
                        ? Access.Scope?.Value ?? (object)DBNull.Value
                        : write.Values!.Values.TryGetValue(column.Name, out var value)
                            ? SqlServerProviderConnection.ToSqlServerValue(value, column) ?? DBNull.Value
                            : DBNull.Value;
            }
            table.Rows.Add(row);
        }
        var parameter = command.Parameters.Add("@rows", SqlDbType.Structured);
        parameter.TypeName = $"dbo.{SqlServerSchemaCoordinator.BatchTypeName(writes[0].Unit)}";
        parameter.Value = table;

        var match = string.Join(" AND ", Unit.Key.Columns.Select(column =>
            $"target.{Quote(column)}=source.{Quote(column)}"));
        var sql = $"MERGE {Quote(Unit.Name)} WITH (HOLDLOCK) AS target USING @rows AS source ON {match} ";
        if (writes[0].Mode == RowWriteMode.Upsert)
        {
            var updates = columns
                .Where(column => !Unit.Key.Columns.Contains(column.Name, StringComparer.Ordinal) &&
                                 column.Name != SqlServerSchemaCoordinator.ScopeColumn &&
                                 column.Name != "createdAt" &&
                                 column.Name != SqlServerSchemaCoordinator.VersionColumn)
                .Select(column => $"target.{Quote(column.Name)}=source.{Quote(column.Name)}")
                .ToList();
            if (VersionColumnDefinition is not null)
                updates.Add($"target.{Quote(VersionColumnDefinition.Name)}=target.{Quote(VersionColumnDefinition.Name)}+1");
            if (updates.Count == 0)
                updates.Add($"target.{Quote(Unit.Key.Columns[0])}=target.{Quote(Unit.Key.Columns[0])}");
            sql += $"WHEN MATCHED THEN UPDATE SET {string.Join(", ", updates)} ";
        }
        sql += $"WHEN NOT MATCHED BY TARGET THEN INSERT ({string.Join(", ", columns.Select(column => Quote(column.Name)))}) VALUES ({string.Join(", ", columns.Select(column => $"source.{Quote(column.Name)}"))}) OUTPUT $action, {string.Join(", ", Unit.Key.Columns.Select(column => $"inserted.{Quote(column)}"))}{(VersionColumnDefinition is null ? string.Empty : $", inserted.{Quote(VersionColumnDefinition.Name)}")};";
        command.CommandText = sql;
        commandObserver?.Observe(new ProviderCommandEvent("sqlserver.batch-merge-tvp", "SQL Server MERGE table-valued parameter", ProviderCommandKind.Write, IsProbe: false));
        try
        {
            var returned = await ReadMergeOutcomes(command, writes[0].Unit, mode).ConfigureAwait(false);
            return MapMergeOutcomes(writes, returned);
        }
        catch (SqlException exception) when (dialect.TryMapUniqueViolation(exception, out var indexName))
        {
            return writes.Select(write => new RowWriteOutcome(write,
                new WriteOutcome(WriteOutcomeStatus.UniqueViolation, null, LogicalIndexName(indexName)))).ToArray();
        }
    }

    private async ValueTask<IReadOnlyList<RowWriteOutcome>> ApplyMergeBatchValues(
        IReadOnlyList<RowWrite> writes,
        IReadOnlyList<ColumnDefinition> columns,
        RelationalExecution mode)
    {
        var maxRows = Math.Max(1, 2_000 / columns.Count);
        if (writes.Count > maxRows)
        {
            var chunked = new List<RowWriteOutcome>(writes.Count);
            foreach (var chunk in writes.Chunk(maxRows))
                chunked.AddRange(await ApplyMergeBatchValues(chunk, columns, mode).ConfigureAwait(false));
            return chunked;
        }

        using var command = Command(string.Empty);
        var rows = new List<string>(writes.Count);
        for (var row = 0; row < writes.Count; row++)
        {
            var values = writes[row].Values!.Values;
            var parameters = new List<string>(columns.Count);
            foreach (var column in columns)
            {
                var name = $"@r{row}_{column.Name}";
                parameters.Add(name);
                SqlServerProviderConnection.AddParameter(command, name,
                    column.Name == SqlServerSchemaCoordinator.VersionColumn
                        ? 1L
                        : column.Name == SqlServerSchemaCoordinator.ScopeColumn
                            ? Access.Scope!.Value
                            : values[column.Name], column);
            }
            rows.Add($"({string.Join(", ", parameters)})");
        }

        var sourceColumns = string.Join(", ", columns.Select(column => Quote(column.Name)));
        var match = string.Join(" AND ", Unit.Key.Columns.Select(column =>
            $"target.{Quote(column)}=source.{Quote(column)}"));
        var sql = $"MERGE {Quote(Unit.Name)} WITH (HOLDLOCK) AS target USING (VALUES {string.Join(", ", rows)}) AS source ({sourceColumns}) ON {match} ";
        if (writes[0].Mode == RowWriteMode.Upsert)
        {
            var updates = columns
                .Where(column => !Unit.Key.Columns.Contains(column.Name, StringComparer.Ordinal) &&
                                 column.Name != SqlServerSchemaCoordinator.ScopeColumn &&
                                 column.Name != "createdAt" &&
                                 column.Name != SqlServerSchemaCoordinator.VersionColumn)
                .Select(column => $"target.{Quote(column.Name)}=source.{Quote(column.Name)}")
                .ToList();
            if (VersionColumnDefinition is not null)
                updates.Add($"target.{Quote(VersionColumnDefinition.Name)}=target.{Quote(VersionColumnDefinition.Name)}+1");
            if (updates.Count == 0)
                updates.Add($"target.{Quote(Unit.Key.Columns[0])}=target.{Quote(Unit.Key.Columns[0])}");
            sql += $"WHEN MATCHED THEN UPDATE SET {string.Join(", ", updates)} ";
        }
        sql += $"WHEN NOT MATCHED BY TARGET THEN INSERT ({string.Join(", ", columns.Select(column => Quote(column.Name)))}) VALUES ({string.Join(", ", columns.Select(column => $"source.{Quote(column.Name)}"))}) OUTPUT $action, {string.Join(", ", Unit.Key.Columns.Select(column => $"inserted.{Quote(column)}"))}{(VersionColumnDefinition is null ? string.Empty : $", inserted.{Quote(VersionColumnDefinition.Name)}")};";
        command.CommandText = sql;
        commandObserver?.Observe(new ProviderCommandEvent("sqlserver.batch-merge", "SQL Server MERGE batch", ProviderCommandKind.Write, IsProbe: false));
        try
        {
            var returned = await ReadMergeOutcomes(command, writes[0].Unit, mode).ConfigureAwait(false);
            return writes.Select(write =>
            {
                if (!returned.TryGetValue(write.Identity, out var result))
                    return new RowWriteOutcome(write, new WriteOutcome(WriteOutcomeStatus.UniqueViolation));
                var status = string.Equals(result.Action, "INSERT", StringComparison.Ordinal)
                    ? WriteOutcomeStatus.Inserted
                    : WriteOutcomeStatus.Updated;
                return new RowWriteOutcome(write, new WriteOutcome(status, result.Version));
            }).ToArray();
        }
        catch (SqlException exception) when (dialect.TryMapUniqueViolation(exception, out var indexName))
        {
            return writes.Select(write => new RowWriteOutcome(write,
                new WriteOutcome(WriteOutcomeStatus.UniqueViolation, null, LogicalIndexName(indexName)))).ToArray();
        }
    }

    private async ValueTask<Dictionary<string, (string Action, long? Version)>> ReadMergeOutcomes(
        SqlCommand command,
        StorageUnit logicalUnit,
        RelationalExecution mode)
    {
        var returned = new Dictionary<string, (string, long?)>(StringComparer.Ordinal);
        await using var readerScope = await mode.ExecuteReader(command).ConfigureAwait(false);
        var reader = readerScope.Reader;
        while (await mode.Read(reader).ConfigureAwait(false))
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var index = 0; index < Unit.Key.Columns.Count; index++)
            {
                var column = Unit.Key.Columns[index];
                if (column == SqlServerSchemaCoordinator.ScopeColumn)
                    values[column] = Access.Scope!.Value;
                else
                    values[column] = FromSqlServer(reader.GetValue(index + 1), Column(column));
            }
            var versionOrdinal = Unit.Key.Columns.Count + 1;
            var version = VersionColumnDefinition is null || reader.IsDBNull(versionOrdinal)
                ? (long?)null
                : Convert.ToInt64(reader.GetValue(versionOrdinal), CultureInfo.InvariantCulture);
            returned[RowWrite.IdentityFor(logicalUnit, values)] = (reader.GetString(0), version);
        }
        return returned;
    }

    private IReadOnlyList<RowWriteOutcome> MapMergeOutcomes(
        IReadOnlyList<RowWrite> writes,
        IReadOnlyDictionary<string, (string Action, long? Version)> returned) =>
        writes.Select(write =>
        {
            if (!returned.TryGetValue(write.Identity, out var result))
                return new RowWriteOutcome(write, new WriteOutcome(WriteOutcomeStatus.UniqueViolation));
            var status = string.Equals(result.Action, "INSERT", StringComparison.Ordinal)
                ? WriteOutcomeStatus.Inserted
                : WriteOutcomeStatus.Updated;
            return new RowWriteOutcome(write, new WriteOutcome(status, result.Version));
        }).ToArray();

    [return: DynamicallyAccessedMembers(
        DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)]
    private static Type ClrType(PortableType type) => type switch
    {
        PortableType.String or PortableType.Json => typeof(string),
        PortableType.Int32 => typeof(int),
        PortableType.Int64 => typeof(long),
        PortableType.Decimal => typeof(decimal),
        PortableType.Boolean => typeof(bool),
        PortableType.DateTimeOffset => typeof(DateTimeOffset),
        PortableType.Guid => typeof(Guid),
        PortableType.Binary => typeof(byte[]),
        PortableType.Double => typeof(double),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    private async ValueTask<IReadOnlyList<RowWriteOutcome>> ApplyBatchFallback(IReadOnlyList<RowWrite> writes, RelationalExecution mode)
    {
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
        var onAppend = Unit.Retention?.Trigger == RetentionTrigger.OnAppend &&
            operation.Kind is RelationalCrudKind.Insert or RelationalCrudKind.Upsert;
        var registration = BeginOnAppend(onAppend);
        WriteOutcome outcome;
        try
        {
            outcome = await ExecuteWrite(() => crud.Mutate(operation, mode), mode).ConfigureAwait(false);
        }
        catch
        {
            await CompleteOnAppend(registration, cleanupRequired: false, mode).ConfigureAwait(false);
            throw;
        }
        await CompleteOnAppend(registration, onAppend && outcome.Succeeded, mode).ConfigureAwait(false);
        return outcome;
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
        async ValueTask Cleanup()
        {
            owner.ThrowIfDisposed();
            if (transaction is not null)
            {
                await ApplyRetentionCore(new RetentionExecutionOptions(), mode).ConfigureAwait(false);
                return;
            }

            // On-append cleanup runs after the append transaction has released the gate. Re-enter
            // through ExecuteWrite so a concurrent caller cannot issue a command on this shared session
            // while the retention scan/delete is in flight.
            await ExecuteWrite(
                () => ApplyRetentionCore(new RetentionExecutionOptions(), mode), mode).ConfigureAwait(false);
        }
        if (registration is not null)
            return registration.Complete(cleanupRequired, Cleanup);
        if (!cleanupRequired)
            return ValueTask.CompletedTask;
        return transaction is null
            ? OnAppendRetentionCoordinator.Run(owner, Unit, Access.Scope?.Value, Cleanup)
            : Cleanup();
    }

    private async ValueTask<WriteOutcome> InsertCore(
        IReadOnlyDictionary<string, object?> values,
        RelationalExecution mode,
        WriteOutcomeStatus status = WriteOutcomeStatus.Upserted)
    {
        var supplied = UserColumns.Where(column => values.ContainsKey(column.Name)).ToArray();
        var columns = supplied.ToList();
        if (VersionColumnDefinition is not null) columns.Add(VersionColumnDefinition);
        if (ScopeColumnDefinition is not null) columns.Add(ScopeColumnDefinition);
        var parameters = BuildParameters(values, supplied);
        if (VersionColumnDefinition is not null) parameters["@__groundwork_version"] = (1L, VersionColumnDefinition);
        if (ScopeColumnDefinition is not null) parameters["@__groundwork_scope"] = (Access.Scope!.Value, ScopeColumnDefinition);
        var output = SequenceColumnDefinition is null ? string.Empty : $" OUTPUT INSERTED.{Quote(SequenceColumnDefinition.Name)}";
        var sql = columns.Count == 0
            ? $"INSERT INTO {Quote(Unit.Name)}{output} DEFAULT VALUES;"
            : $"INSERT INTO {Quote(Unit.Name)} ({string.Join(", ", columns.Select(column => Quote(column.Name)))}){output} VALUES ({string.Join(", ", columns.Select(column => "@" + column.Name))});";
        using var command = Command(sql);
        AddParameters(command, parameters);
        commandObserver?.Observe(new ProviderCommandEvent("sqlserver.insert", sql, ProviderCommandKind.Write, IsProbe: false));
        try
        {
            if (SequenceColumnDefinition is null)
            {
                await mode.ExecuteNonQuery(command).ConfigureAwait(false);
                return new WriteOutcome(status, VersionColumnDefinition is null ? null : 1);
            }

            var generated = await mode.ExecuteScalar(command).ConfigureAwait(false);
            var generatedValue = FromSqlServer(generated!, SequenceColumnDefinition);
            await RecordHighWater(generatedValue, mode).ConfigureAwait(false);
            return new WriteOutcome(
                status,
                VersionColumnDefinition is null ? null : 1,
                generatedValues: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [SequenceColumnDefinition.Name] = generatedValue
                });
        }
        catch (SqlException exception) when (dialect.TryMapUniqueViolation(exception, out _)) { return new WriteOutcome(WriteOutcomeStatus.UniqueViolation); }
    }

    private async ValueTask<WriteOutcome> UpsertNoneCore(IReadOnlyDictionary<string, object?> values, WriteOptions? options, RelationalExecution mode)
    {
        var supplied = UserColumns.Where(column => values.ContainsKey(column.Name)).ToArray();
        var columns = supplied.ToList();
        var parameters = BuildParameters(values, supplied);
        if (ScopeColumnDefinition is not null)
        {
            columns.Add(ScopeColumnDefinition);
            parameters["@__groundwork_scope"] = (Access.Scope!.Value, ScopeColumnDefinition);
        }

        var sql = RenderNoneUpsertSql(Unit, columns);
        using var command = Command(sql);
        AddParameters(command, parameters);
        commandObserver?.Observe(new ProviderCommandEvent("sqlserver.upsert", sql, ProviderCommandKind.Write, IsProbe: false));
        try
        {
            await mode.ExecuteNonQuery(command).ConfigureAwait(false);
            return new WriteOutcome(WriteOutcomeStatus.Upserted);
        }
        catch (SqlException exception) when (dialect.TryMapUniqueViolation(exception, out _))
        {
            return new WriteOutcome(WriteOutcomeStatus.UniqueViolation);
        }
    }

    internal static string RenderNoneUpsertSql(
        StorageUnit unit,
        IReadOnlyList<ColumnDefinition> columns)
    {
        var sourceColumns = string.Join(", ", columns.Select(column => Quote(column.Name)));
        var match = string.Join(" AND ", unit.Key.Columns.Select(column =>
            $"target.{Quote(column)}=source.{Quote(column)}"));
        var updates = columns
            .Where(column => !unit.Key.Columns.Contains(column.Name, StringComparer.Ordinal) &&
                             column.Name != SqlServerSchemaCoordinator.ScopeColumn &&
                             column.Name != "createdAt" &&
                             column.Name != SqlServerSchemaCoordinator.VersionColumn)
            .Select(column => $"target.{Quote(column.Name)}=source.{Quote(column.Name)}")
            .ToList();
        if (updates.Count == 0)
            updates.Add($"target.{Quote(unit.Key.Columns[0])}=target.{Quote(unit.Key.Columns[0])}");

        return $"MERGE {Quote(unit.Name)} WITH (HOLDLOCK) AS target " +
               $"USING (VALUES ({string.Join(", ", columns.Select(column => "@" + column.Name))})) AS source ({sourceColumns}) ON {match} " +
               $"WHEN MATCHED THEN UPDATE SET {string.Join(", ", updates)} " +
               $"WHEN NOT MATCHED BY TARGET THEN INSERT ({sourceColumns}) VALUES ({string.Join(", ", columns.Select(column => $"source.{Quote(column.Name)}"))});";
    }

    private async ValueTask<WriteOutcome> DeleteCore(
        StorageKey key,
        StoredEntry? existing,
        WriteOptions? options,
        RelationalExecution mode)
    {
        var (where, parameters) = KeyPredicate(key.Values);
        if (VersionColumnDefinition is not null && options?.Precondition.Kind == WritePreconditionKind.IfVersion)
        {
            where += $" AND {Quote(VersionColumnDefinition.Name)}=@expected";
            parameters["@expected"] = (options.Precondition.Version!.Value, VersionColumnDefinition);
        }
        using var command = Command($"DELETE FROM {Quote(Unit.Name)} WHERE {where};");
        commandObserver?.Observe(new ProviderCommandEvent(
            "sqlserver.delete",
            command.CommandText,
            ProviderCommandKind.Write,
            IsProbe: false));
        AddParameters(command, parameters);
        var affected = await mode.ExecuteNonQuery(command).ConfigureAwait(false);
        if (affected != 0)
            return new WriteOutcome(WriteOutcomeStatus.Deleted, existing?.Version);
        return new WriteOutcome(
            Unit.Concurrency.IsNone ? WriteOutcomeStatus.NotFound : WriteOutcomeStatus.ConcurrencyConflict,
            existing?.Version);
    }

    private async ValueTask<WriteOutcome> UpdateCore(
        IReadOnlyDictionary<string, object?> values,
        StorageKey key,
        StoredEntry? existing,
        WriteOptions? options,
        RelationalExecution mode)
    {
        var supplied = UserColumns.Where(column => values.ContainsKey(column.Name) && !Unit.Key.Columns.Contains(column.Name, StringComparer.Ordinal)).ToArray();
        var sets = supplied.Select(column => $"{Quote(column.Name)}=@{column.Name}").ToList();
        var parameters = BuildParameters(values, supplied);
        var (where, keyParameters) = KeyPredicate(key.Values);
        foreach (var pair in keyParameters) parameters[pair.Key] = pair.Value;
        if (VersionColumnDefinition is not null)
        {
            sets.Add($"{Quote(VersionColumnDefinition.Name)}={Quote(VersionColumnDefinition.Name)}+1");
            if (options?.Precondition.Kind == WritePreconditionKind.IfVersion)
            {
                where += $" AND {Quote(VersionColumnDefinition.Name)}=@expected";
                parameters["@expected"] = (options.Precondition.Version!.Value, VersionColumnDefinition);
            }
        }
        if (sets.Count == 0)
        {
            var noOpColumn = LogicalKeyColumns[0];
            sets.Add($"{Quote(noOpColumn)}={Quote(noOpColumn)}");
        }
        var sql = $"UPDATE {Quote(Unit.Name)} SET {string.Join(", ", sets)} WHERE {where};";
        using var command = Command(sql);
        AddParameters(command, parameters);
        if (Unit.Concurrency.IsNone)
            commandObserver?.Observe(new ProviderCommandEvent("sqlserver.update", sql, ProviderCommandKind.Write, IsProbe: false));
        try
        {
            if ((await mode.ExecuteNonQuery(command).ConfigureAwait(false)) == 0)
                return new WriteOutcome(Unit.Concurrency.IsNone
                    ? WriteOutcomeStatus.NotFound
                    : WriteOutcomeStatus.ConcurrencyConflict, existing?.Version);
            return new WriteOutcome(WriteOutcomeStatus.Updated, VersionColumnDefinition is null ? null : existing!.Version + 1);
        }
        catch (SqlException exception) when (dialect.TryMapUniqueViolation(exception, out _))
        {
            return new WriteOutcome(WriteOutcomeStatus.UniqueViolation, existing?.Version);
        }
    }

    private ValueTask<WriteOutcome> ConditionalUpsertCore(StorageValues values, WriteOptions? options, RelationalExecution mode)
    {
        ArgumentNullException.ThrowIfNull(values);
        values = new StorageValues(SearchKeyProjection.Populate(Unit, values.Values));
        RelationalSessionPolicy.ValidateValues(Unit, UserColumns, "SQL Server", values.Values, requireAllNonNullable: false);
        if (options?.Precondition.Kind == WritePreconditionKind.IfVersion && VersionColumnDefinition is null)
            throw new InvalidOperationException($"Storage unit '{Unit.Name}' does not declare version machinery.");

        var key = new StorageKey(LogicalKeyColumns.ToDictionary(
            column => column,
            column => values.Values.TryGetValue(column, out var value)
                ? value
                : throw new ArgumentException($"Key column '{column}' is required.", nameof(values)),
            StringComparer.Ordinal));
        return ExecuteConditionalBatch(values, options, key, mode);
    }

    private async ValueTask<WriteOutcome> ExecuteConditionalBatch(
        StorageValues values,
        WriteOptions? options,
        StorageKey key,
        RelationalExecution mode)
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
            parameters["@expected"] = (options?.Precondition.Version, VersionColumnDefinition);

        var where = string.Join(" AND ", LogicalKeyColumns.Select(column =>
            $"target.{Quote(column)}=@key_{column}"));
        if (ScopeColumnDefinition is not null)
            where += $" AND target.{Quote(ScopeColumnDefinition.Name)}=@__groundwork_scope";
        var updateCondition = VersionColumnDefinition is null || options?.Precondition.Kind == WritePreconditionKind.Unconditional
            ? "1=1"
            : options?.Precondition.Kind == WritePreconditionKind.CreateOnly
                ? "1=0"
                : $"target.{Quote(VersionColumnDefinition.Name)}=@expected";
        var insertCondition = VersionColumnDefinition is null || options?.Precondition.Kind != WritePreconditionKind.IfVersion ? "1=1" : "1=0";
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
        var operation = $"DECLARE @result TABLE ([operation] nvarchar(6) NOT NULL, [version] bigint NULL); " +
            $"UPDATE target WITH (UPDLOCK, SERIALIZABLE) SET {string.Join(", ", updates)} " +
            $"OUTPUT N'UPDATE', {outputVersion} INTO @result ([operation], [version]) " +
            $"FROM {Quote(Unit.Name)} AS target WHERE {where} AND ({updateCondition}); " +
            $"IF @@ROWCOUNT = 0 AND ({insertCondition}) BEGIN " +
            $"INSERT INTO {Quote(Unit.Name)} ({string.Join(", ", insertColumns.Select(column => Quote(column.Name)))}) " +
            $"OUTPUT N'INSERT', {(VersionColumnDefinition is null ? "CONVERT(bigint, NULL)" : "CONVERT(bigint, 1)")} " +
            $"INTO @result ([operation], [version]) VALUES ({string.Join(", ", insertColumns.Select(column => "@" + column.Name))}); END; " +
            "SELECT [operation], [version] FROM @result;";
        // A range lock must span the UPDATE and conditional INSERT, but opening and
        // committing a SqlTransaction from the client would add two network round
        // trips. When the caller did not supply a transaction, keep the transaction
        // boundary inside this one submitted batch. XACT_ABORT plus the catch block
        // guarantees that a failed insert does not strand an open transaction.
        var sql = transaction is not null
            ? operation
            : "SET XACT_ABORT ON; BEGIN TRANSACTION; BEGIN TRY " + operation +
              " COMMIT TRANSACTION; END TRY BEGIN CATCH IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION; THROW; END CATCH;";
        using var command = Command(sql);
        AddParameters(command, parameters);
        commandObserver?.Observe(new ProviderCommandEvent("sqlserver.conditional-upsert", sql, ProviderCommandKind.Write, IsProbe: false));
        try
        {
            await using var readerScope = await mode.ExecuteReader(command).ConfigureAwait(false);
            var reader = readerScope.Reader;
            if (await mode.Read(reader).ConfigureAwait(false))
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
        catch (SqlException exception) when (dialect.TryMapUniqueViolation(exception, out var indexName))
        {
            return IsPrimaryKeyViolation(indexName)
                ? DeferredConflict(key)
                : new WriteOutcome(WriteOutcomeStatus.UniqueViolation, null, LogicalIndexName(indexName));
        }

        return DeferredConflict(key);
    }

    private bool IsPrimaryKeyViolation(string indexName) =>
        indexName.Contains("__groundwork_pk_", StringComparison.OrdinalIgnoreCase) ||
        indexName.Contains("PRIMARY KEY", StringComparison.OrdinalIgnoreCase);

    private string? LogicalIndexName(string? physicalName)
    {
        if (string.IsNullOrWhiteSpace(physicalName))
            return physicalName;

        return Unit.Indexes.FirstOrDefault(index =>
            string.Equals(
                SqlServerDialect.PhysicalIndexName(Unit.Name, index.Name),
                physicalName,
                StringComparison.OrdinalIgnoreCase))?.Name ?? physicalName;
    }

    private WriteOutcome DeferredConflict(StorageKey key) =>
        WriteOutcome.Deferred(
            WriteOutcomeStatus.ConcurrencyConflict,
            null,
            () =>
            {
                var existing = ReadCore(key, RelationalExecution.Synchronous).GetAwaiter().GetResult();
                return existing is null
                    ? new WriteOutcomeDetail(WriteOutcomeStatus.NotFound)
                    : new WriteOutcomeDetail(WriteOutcomeStatus.ConcurrencyConflict, existing.Version);
            });

    private async ValueTask<StoredEntry?> ReadCore(
        StorageKey key,
        RelationalExecution mode,
        string? observerOperation = null,
        bool exactStringKeys = false,
        bool isProbe = true) => await pointReads.Read(
            key,
            mode,
            forUpdate: false,
            observerOperation,
            exactStringKeys,
            isProbe).ConfigureAwait(false);

    private (string Predicate, Dictionary<string, (object? Value, ColumnDefinition Definition)> Parameters) KeyPredicate(
        IReadOnlyDictionary<string, object?> values,
        bool exactStringKeys = false)
    {
        var clauses = new List<string>();
        var parameters = new Dictionary<string, (object?, ColumnDefinition)>(StringComparer.Ordinal);
        foreach (var column in LogicalKeyColumns)
        {
            if (!values.TryGetValue(column, out var value)) throw new ArgumentException($"Key column '{column}' is required.", nameof(values));
            var parameter = "@key_" + column;
            var definition = Column(column);
            var predicate = $"{Quote(column)}={parameter}";
            if (exactStringKeys && definition.Type == PortableType.String)
                predicate = $"DATALENGTH({Quote(column)})=DATALENGTH({parameter}) AND {predicate}";
            clauses.Add(predicate);
            parameters[parameter] = (value, definition);
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
        execution.EnsureOpen();
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = (SqlTransaction?)execution.Transaction;
        return command;
    }

    private static void AddParameters(SqlCommand command, IReadOnlyDictionary<string, (object? Value, ColumnDefinition Definition)> parameters)
    {
        foreach (var pair in parameters) SqlServerProviderConnection.AddParameter(command, pair.Key, pair.Value.Value, pair.Value.Definition);
    }

    private ValueTask<T> Execute<T>(Func<ValueTask<T>> operation, RelationalExecution mode) =>
        execution.Execute(operation, mode);

    private ValueTask<T> ExecuteWrite<T>(Func<ValueTask<T>> operation, RelationalExecution mode) =>
        execution.ExecuteWrite(operation, mode);

    private ColumnDefinition Column(string name) => UserColumns.First(column => column.Name == name);
    private IReadOnlyList<ColumnDefinition> UserColumns => Unit.Columns.Where(column => column.Name is not SqlServerSchemaCoordinator.ScopeColumn and not SqlServerSchemaCoordinator.VersionColumn).ToArray();
    private IReadOnlyList<string> LogicalKeyColumns => Unit.Key.Columns.Where(column => column != SqlServerSchemaCoordinator.ScopeColumn).ToArray();
    private ColumnDefinition? ScopeColumnDefinition => Unit.Columns.FirstOrDefault(column => column.Name == SqlServerSchemaCoordinator.ScopeColumn);
    private ColumnDefinition? VersionColumnDefinition => Unit.Columns.FirstOrDefault(column => column.Name == SqlServerSchemaCoordinator.VersionColumn);
    private const string LedgerUnit = "unit";
    private const string LedgerScope = "scope";
    private const string LedgerNonce = "nonce";
    private const string LedgerCommittedAt = "committed_at";
    private const string LedgerFingerprint = "input_fingerprint";
    private const string LedgerResult = "exact_result";
    private const string HighWaterTable = "__groundwork_sequence_high_waters";
    private const string HighWaterValue = "high_water";
    private const string BinaryIdentityCollation = "Latin1_General_100_BIN2";
    private const string LifecycleSchemaDiagnosticCode = "GW-SQLSERVER-LIFECYCLE-001";
    private ColumnDefinition? SequenceColumnDefinition => UserColumns.FirstOrDefault(column => column.Generation == ColumnGeneration.ProviderSequence);
    private static string Quote(string value) => SqlServerProviderConnection.QuoteIdentifier(value);

    private static object? FromSqlServer(object value, ColumnDefinition definition) =>
        SqlServerDialect.ReadPortableValue(value, definition);

    private sealed class SqlServerAppendAdapter(SqlServerStorageSession session) : IRelationalAppendAdapter
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
                $"WITH expired AS (SELECT TOP (128) * FROM {Quote(operation.Declaration.LedgerName)} " +
                $"WHERE {Quote(LedgerUnit)}=@reclaim_unit AND {Quote(LedgerCommittedAt)} <= @cutoff) " +
                "DELETE FROM expired;");
            AddLedgerParameter(reclaim, "reclaim_unit", operation.Unit.Id.Value);
            AddLedgerParameter(reclaim, "cutoff", FormatLedgerTime(cutoff));
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
            AddLedgerParameters(existing, operation.Unit.Id.Value, operation.Scope, operation.OperationId.Nonce);
            await using var readerScope = await execution.ExecuteReader(existing).ConfigureAwait(false);
            var reader = readerScope.Reader;
            if (!await execution.Read(reader).ConfigureAwait(false))
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
            AddLedgerParameters(delete, operation.Unit.Id.Value, operation.Scope, operation.OperationId.Nonce);
            AddLedgerParameter(delete, "observed_committed_at", FormatLedgerTime(existing.CommittedAt));
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
                "SELECT @unit, @scope, @nonce, @committed_at, @fingerprint, @result WHERE NOT EXISTS " +
                $"(SELECT 1 FROM {Quote(operation.Declaration.LedgerName)} WITH (UPDLOCK, HOLDLOCK) " +
                $"WHERE {Quote(LedgerUnit)}=@unit AND {Quote(LedgerScope)}=@scope " +
                $"AND {Quote(LedgerNonce)}=@nonce);");
            AddLedgerParameters(insert, operation.Unit.Id.Value, operation.Scope, operation.OperationId.Nonce);
            AddLedgerParameter(insert, "committed_at", FormatLedgerTime(providerNow));
            AddLedgerParameter(insert, "fingerprint", operation.Fingerprint);
            AddLedgerParameter(insert, "result", string.Empty);
            return await execution.ExecuteNonQuery(insert).ConfigureAwait(false) != 0;
        }

        public async ValueTask<RelationalAppendReplayEntry?> ReadClaimWinner(
            RelationalAppendOperation operation,
            RelationalExecution execution)
        {
            using var replay = session.Command(
                $"SELECT {Quote(LedgerFingerprint)}, {Quote(LedgerResult)} " +
                $"FROM {Quote(operation.Declaration.LedgerName)} WHERE {Quote(LedgerUnit)}=@unit " +
                $"AND {Quote(LedgerScope)}=@scope AND {Quote(LedgerNonce)}=@nonce;");
            AddLedgerParameters(replay, operation.Unit.Id.Value, operation.Scope, operation.OperationId.Nonce);
            await using var readerScope = await execution.ExecuteReader(replay).ConfigureAwait(false);
            var reader = readerScope.Reader;
            if (!await execution.Read(reader).ConfigureAwait(false))
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
                SqlServerSchemaCoordinator.ScopeColumn);
            var writes = operation.Values
                .Select(value => RowWrite.Insert(logicalUnit, value))
                .ToArray();
            if (session.SequenceColumnDefinition is null)
                return await session.ApplyBatchCore(writes, execution).ConfigureAwait(false);

            var outcomes = new List<RowWriteOutcome>(writes.Length);
            foreach (var write in writes)
                outcomes.Add(await session.InsertAppendSequence(write, execution).ConfigureAwait(false));
            return outcomes;
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
            AddLedgerParameters(complete, operation.Unit.Id.Value, operation.Scope, operation.OperationId.Nonce);
            AddLedgerParameter(complete, "result", serializedOutcomes);
            return await execution.ExecuteNonQuery(complete).ConfigureAwait(false) == 1;
        }
    }

    private sealed class SqlServerSessionExecutionAdapter(
        SqlServerProviderConnection owner,
        SqlConnection connection) : IRelationalSessionExecutionAdapter
    {
        public bool SerializeAmbientReads => false;

        public void EnsureUsable() => owner.ThrowIfDisposed();

        public ValueTask<IDisposable> EnterGate(RelationalExecution execution) =>
            owner.EnterGate(execution);

        public ValueTask<DbTransaction> BeginWrite(RelationalExecution execution) =>
            execution.BeginTransaction(connection, IsolationLevel.Serializable);

        public ValueTask Rollback(DbTransaction transaction, RelationalExecution execution) =>
            execution.Rollback(transaction);
    }

    private sealed class SqlServerPointReadAdapter : IRelationalPointReadAdapter
    {
        public string QuoteIdentifier(string identifier) => Quote(identifier);

        public string Equality(ColumnDefinition column, string parameter, bool exactStringKeys)
        {
            var equality = $"{Quote(column.Name)}={parameter}";
            return exactStringKeys && column.Type == PortableType.String
                ? $"DATALENGTH({Quote(column.Name)})=DATALENGTH({parameter}) AND {equality}"
                : equality;
        }

        public void Bind(DbCommand command, string parameter, object? value, ColumnDefinition column) =>
            SqlServerProviderConnection.AddParameter((SqlCommand)command, parameter, value, column);

        public object? Decode(object value, ColumnDefinition column) => FromSqlServer(value, column);

        public string LockingClause(bool forUpdate) => string.Empty;
    }

    private sealed class SqlServerCrudAdapter(
        SqlServerStorageSession session) : IRelationalCrudAdapter
    {
        public ValueTask<WriteOutcome> Insert(
            StorageValues values,
            WriteOutcomeStatus status,
            RelationalExecution execution) =>
            session.InsertCore(values.Values, execution, status);

        public ValueTask<WriteOutcome> Update(
            StorageValues values,
            StorageKey key,
            StoredEntry? existing,
            WriteOptions? options,
            RelationalExecution execution) =>
            session.UpdateCore(values.Values, key, existing, options, execution);

        public ValueTask<WriteOutcome> Upsert(
            StorageValues values,
            StorageKey key,
            StoredEntry? existing,
            WriteOptions? options,
            RelationalExecution execution) =>
            session.SequenceColumnDefinition is not null && values.Values.ContainsKey(session.SequenceColumnDefinition.Name)
                ? session.UpdateCore(values.Values, key, existing, options, execution)
                : session.Unit.Concurrency.IsNone
                    ? session.UpsertNoneCore(values.Values, options, execution)
                    : existing is null
                        ? session.InsertCore(values.Values, execution)
                        : session.UpdateCore(values.Values, key, existing, options, execution);

        public ValueTask<WriteOutcome> Delete(
            StorageKey key,
            StoredEntry? existing,
            WriteOptions? options,
            RelationalExecution execution) =>
            session.DeleteCore(key, existing, options, execution);
    }
}

internal sealed class OwnedSqlServerStorageSession : SqlServerStorageSession, IOwnedStorageSession
{
    internal OwnedSqlServerStorageSession(
        SqlServerProviderConnection owner,
        StorageUnit unit,
        StorageAccess access,
        SqlConnection connection,
        IProviderCommandObserver? observer = null)
        : base(owner, unit, access, connection, null, observer, ownsConnection: true)
    {
    }
}
