using System.Data.Common;
using Groundwork.Kernel;
using Groundwork.Query.Model;
using Groundwork.Store;

namespace Groundwork.Substrate.Relational;

/// <summary>
/// Public provider-authoring base for the required relational storage-session surface. It owns the
/// shared execution, point-read, query, aggregation, CRUD, append, and retention state machines;
/// a provider supplies only driver-shaped commands and explicitly implements any optional
/// capability interfaces.
/// </summary>
public abstract class RelationalStorageSessionBase : IStorageSession
{
    private readonly RelationalSessionExecution execution;
    private readonly RelationalSessionPointReads pointReads;
    private readonly RelationalSessionCrud crud;
    private readonly RelationalSessionQueries queries;
    private readonly RelationalSessionAggregations aggregations;
    private readonly RelationalSessionSetMutations setMutations;
    private readonly RelationalSessionAppends appends;
    private readonly RelationalSessionRetention? retention;
    private readonly object onAppendRetentionOwner;

    protected RelationalStorageSessionBase(
        StorageUnit unit,
        StorageAccess access,
        RelationalStorageSessionAdapter adapter,
        RelationalAppendAdapter appendAdapter,
        RelationalRetentionAdapter? retentionAdapter = null,
        object? onAppendRetentionOwner = null,
        DbTransaction? transaction = null,
        bool ownsConnection = false,
        IProviderCommandObserver? observer = null,
        string? operationPrefix = null)
    {
        Unit = unit ?? throw new ArgumentNullException(nameof(unit));
        Access = access ?? throw new ArgumentNullException(nameof(access));
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(appendAdapter);
        if (unit.Retention is not null && retentionAdapter is null)
        {
            throw new ArgumentException(
                $"Storage unit '{unit.Name}' declares retention, so its session requires a relational retention adapter.",
                nameof(retentionAdapter));
        }
        operationPrefix ??= adapter.Dialect.ProviderName;

        execution = new RelationalSessionExecution(
            access,
            transaction,
            ownsConnection,
            adapter,
            GetType().Name);
        adapter.AttachTransactionAccessor(() => execution.Transaction);
        var userColumns = unit.Columns
            .Where(column => column.Name is not ProviderOwnedColumns.Scope and
                not ProviderOwnedColumns.Version and
                not ProviderOwnedColumns.Action)
            .ToArray();
        var sequenceColumn = userColumns.FirstOrDefault(
            column => column.Generation == ColumnGeneration.ProviderSequence);
        var versionColumn = unit.Columns.FirstOrDefault(
            column => column.Name == ProviderOwnedColumns.Version);
        pointReads = new RelationalSessionPointReads(
            unit,
            access,
            userColumns,
            versionColumn,
            sql => adapter.CreateCommand(sql, execution.Transaction),
            adapter,
            observer,
            operationPrefix);
        crud = new RelationalSessionCrud(
            unit,
            userColumns,
            sequenceColumn,
            versionColumn,
            adapter.Dialect.ProviderName,
            (key, mode) => pointReads.Read(key, mode),
            adapter);
        var physicalIndexNames = adapter.PhysicalIndexNames(unit);
        queries = new RelationalSessionQueries(
            unit,
            access,
            adapter.Connection,
            adapter.QueryRenderer,
            () => physicalIndexNames,
            adapter.Decode,
            adapter.AssertExplainPlan,
            observer,
            operationPrefix);
        aggregations = new RelationalSessionAggregations(
            unit,
            access,
            adapter.Connection,
            adapter.Dialect,
            adapter.Decode,
            observer,
            operationPrefix + ".aggregate");
        setMutations = new RelationalSessionSetMutations(
            unit,
            access,
            adapter.QueryRenderer,
            versionColumn?.Name,
            adapter.CreateCommand,
            adapter.Bind,
            observer,
            operationPrefix);
        appends = new RelationalSessionAppends(unit, access, appendAdapter);
        retention = retentionAdapter is null
            ? null
            : new RelationalSessionRetention(unit, access, retentionAdapter);
        this.onAppendRetentionOwner = onAppendRetentionOwner ?? adapter.Connection;
    }

    public StorageUnit Unit { get; }

    public StorageAccess Access { get; }

    /// <summary>Whether this session has been closed by its owner.</summary>
    protected bool IsClosed => execution.IsReleased;

    public StoredEntry? Read(StorageKey key) =>
        ReadCore(key, RelationalExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask<StoredEntry?> ReadAsync(
        StorageKey key,
        CancellationToken cancellationToken = default) =>
        ReadCore(key, RelationalExecution.Asynchronous(cancellationToken));

    public QueryMaterializedResult Query(QueryRequest request, QueryRenderOptions? options = null) =>
        QueryCore(request, options, RelationalExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask<QueryMaterializedResult> QueryAsync(
        QueryRequest request,
        QueryRenderOptions? options = null,
        CancellationToken cancellationToken = default) =>
        QueryCore(request, options, RelationalExecution.Asynchronous(cancellationToken));

    public AggregationResult Aggregate(AggregationQuery query) =>
        AggregateCore(query, RelationalExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask<AggregationResult> AggregateAsync(
        AggregationQuery query,
        CancellationToken cancellationToken = default) =>
        AggregateCore(query, RelationalExecution.Asynchronous(cancellationToken));

    public WriteOutcome Insert(StorageValues values, WriteOptions? options = null) =>
        Mutate(values, options, RelationalCrudKind.Insert, RelationalExecution.Synchronous)
            .GetAwaiter().GetResult();

    public ValueTask<WriteOutcome> InsertAsync(
        StorageValues values,
        WriteOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Mutate(values, options, RelationalCrudKind.Insert, RelationalExecution.Asynchronous(cancellationToken));

    public WriteOutcome Update(StorageValues values, WriteOptions? options = null) =>
        Mutate(values, options, RelationalCrudKind.Update, RelationalExecution.Synchronous)
            .GetAwaiter().GetResult();

    public ValueTask<WriteOutcome> UpdateAsync(
        StorageValues values,
        WriteOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Mutate(values, options, RelationalCrudKind.Update, RelationalExecution.Asynchronous(cancellationToken));

    public WriteOutcome Upsert(StorageValues values, WriteOptions? options = null) =>
        Mutate(values, options, RelationalCrudKind.Upsert, RelationalExecution.Synchronous)
            .GetAwaiter().GetResult();

    public ValueTask<WriteOutcome> UpsertAsync(
        StorageValues values,
        WriteOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Mutate(values, options, RelationalCrudKind.Upsert, RelationalExecution.Asynchronous(cancellationToken));

    public WriteOutcome Delete(StorageKey key, WriteOptions? options = null) =>
        DeleteCore(key, options, RelationalExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask<WriteOutcome> DeleteAsync(
        StorageKey key,
        WriteOptions? options = null,
        CancellationToken cancellationToken = default) =>
        DeleteCore(key, options, RelationalExecution.Asynchronous(cancellationToken));

    public WriteOutcome Append(OperationId operationId, IReadOnlyList<StorageValues> values) =>
        AppendCore(operationId, values, exactOutcomes: false, RelationalExecution.Synchronous)
            .GetAwaiter().GetResult().Outcome;

    public ValueTask<WriteOutcome> AppendAsync(
        OperationId operationId,
        IReadOnlyList<StorageValues> values,
        CancellationToken cancellationToken = default) =>
        AppendOutcome(operationId, values, RelationalExecution.Asynchronous(cancellationToken));

    /// <summary>Shared exact-outcome append runner for sessions that opt into that capability.</summary>
    protected AppendOutcomeReport AppendWithOutcomesCore(
        OperationId operationId,
        IReadOnlyList<StorageValues> values) =>
        AppendCore(operationId, values, exactOutcomes: true, RelationalExecution.Synchronous)
            .GetAwaiter().GetResult().Report!;

    protected async ValueTask<AppendOutcomeReport> AppendWithOutcomesCoreAsync(
        OperationId operationId,
        IReadOnlyList<StorageValues> values,
        CancellationToken cancellationToken = default) =>
        (await AppendCore(
            operationId,
            values,
            exactOutcomes: true,
            RelationalExecution.Asynchronous(cancellationToken)).ConfigureAwait(false)).Report!;

    /// <summary>Shared privileged cross-scope query runner for sessions that opt into that capability.</summary>
    protected CrossScopeQueryResult QueryAcrossScopesCore(
        QueryRequest request,
        QueryRenderOptions? options = null) =>
        QueryAcrossScopesCore(request, options, RelationalExecution.Synchronous)
            .GetAwaiter().GetResult();

    protected ValueTask<CrossScopeQueryResult> QueryAcrossScopesCoreAsync(
        QueryRequest request,
        QueryRenderOptions? options = null,
        CancellationToken cancellationToken = default) =>
        QueryAcrossScopesCore(request, options, RelationalExecution.Asynchronous(cancellationToken));

    /// <summary>Shared set-update runner for sessions that opt into that capability.</summary>
    protected SetMutationResult UpdateWhereCore(
        Predicate where,
        IReadOnlyDictionary<string, object?> assignments) =>
        UpdateWhereCore(where, assignments, RelationalExecution.Synchronous).GetAwaiter().GetResult();

    protected ValueTask<SetMutationResult> UpdateWhereCoreAsync(
        Predicate where,
        IReadOnlyDictionary<string, object?> assignments,
        CancellationToken cancellationToken = default) =>
        UpdateWhereCore(where, assignments, RelationalExecution.Asynchronous(cancellationToken));

    /// <summary>Shared set-delete runner for sessions that opt into that capability.</summary>
    protected SetMutationResult DeleteWhereCore(Predicate where) =>
        DeleteWhereCore(where, RelationalExecution.Synchronous).GetAwaiter().GetResult();

    protected ValueTask<SetMutationResult> DeleteWhereCoreAsync(
        Predicate where,
        CancellationToken cancellationToken = default) =>
        DeleteWhereCore(where, RelationalExecution.Asynchronous(cancellationToken));

    /// <summary>Shared retention runner for sessions that opt into that capability.</summary>
    protected RetentionResult ApplyRetentionCore(RetentionExecutionOptions? options = null) =>
        ApplyRetentionCore(options, RelationalExecution.Synchronous).GetAwaiter().GetResult();

    protected ValueTask<RetentionResult> ApplyRetentionCoreAsync(
        RetentionExecutionOptions? options = null) =>
        ApplyRetentionCore(
            options,
            RelationalExecution.Asynchronous(options?.CancellationToken ?? CancellationToken.None));

    /// <summary>Shared exact-retention runner for sessions that opt into that capability.</summary>
    protected RetentionOperationResult ApplyExactRetentionCore(
        OperationId operationId,
        RetentionExecutionOptions? options = null) =>
        ApplyExactRetentionCore(operationId, options, RelationalExecution.Synchronous)
            .GetAwaiter().GetResult();

    protected ValueTask<RetentionOperationResult> ApplyExactRetentionCoreAsync(
        OperationId operationId,
        RetentionExecutionOptions? options = null) =>
        ApplyExactRetentionCore(
            operationId,
            options,
            RelationalExecution.Asynchronous(options?.CancellationToken ?? CancellationToken.None));

    /// <summary>
    /// Runs one provider-native optional write capability through this session's shared gate,
    /// transaction, cancellation, cleanup, and closed-state policy.
    /// </summary>
    protected T ExecuteProviderWriteCore<T>(Func<RelationalExecution, ValueTask<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var mode = RelationalExecution.Synchronous;
        return execution.ExecuteWrite(() => operation(mode), mode).GetAwaiter().GetResult();
    }

    /// <summary>Asynchronous counterpart of <see cref="ExecuteProviderWriteCore{T}"/>.</summary>
    protected ValueTask<T> ExecuteProviderWriteCoreAsync<T>(
        Func<RelationalExecution, ValueTask<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var mode = RelationalExecution.Asynchronous(cancellationToken);
        return execution.ExecuteWrite(() => operation(mode), mode);
    }

    /// <summary>
    /// Runs a provider-native conditional upsert through the shared write and OnAppend-retention
    /// lifecycle while preserving its exact Inserted/Updated outcome.
    /// </summary>
    protected WriteOutcome ExecuteProviderConditionalUpsertCore(
        Func<RelationalExecution, ValueTask<WriteOutcome>> operation) =>
        ExecuteProviderConditionalUpsert(operation, RelationalExecution.Synchronous)
            .GetAwaiter().GetResult();

    /// <summary>Asynchronous counterpart of <see cref="ExecuteProviderConditionalUpsertCore"/>.</summary>
    protected ValueTask<WriteOutcome> ExecuteProviderConditionalUpsertCoreAsync(
        Func<RelationalExecution, ValueTask<WriteOutcome>> operation,
        CancellationToken cancellationToken = default) =>
        ExecuteProviderConditionalUpsert(operation, RelationalExecution.Asynchronous(cancellationToken));

    /// <summary>Closes this non-owning view; the resource owner decides whether to dispose its connection.</summary>
    public void Close() => execution.Close();

    private ValueTask<StoredEntry?> ReadCore(StorageKey key, RelationalExecution mode)
    {
        pointReads.ValidatePublicRead();
        return execution.Execute(() => pointReads.ReadPublic(key, mode), mode);
    }

    private ValueTask<QueryMaterializedResult> QueryCore(
        QueryRequest request,
        QueryRenderOptions? options,
        RelationalExecution mode) =>
        execution.Execute(() => queries.Query(request, options, execution.Transaction, mode), mode);

    private ValueTask<AggregationResult> AggregateCore(AggregationQuery query, RelationalExecution mode) =>
        execution.Execute(() => aggregations.Aggregate(query, execution.Transaction, mode), mode);

    private async ValueTask<WriteOutcome> AppendOutcome(
        OperationId operationId,
        IReadOnlyList<StorageValues> values,
        RelationalExecution mode) =>
        (await AppendCore(operationId, values, exactOutcomes: false, mode).ConfigureAwait(false)).Outcome;

    private async ValueTask<(WriteOutcome Outcome, AppendOutcomeReport? Report)> AppendCore(
        OperationId operationId,
        IReadOnlyList<StorageValues> values,
        bool exactOutcomes,
        RelationalExecution mode)
    {
        ArgumentNullException.ThrowIfNull(values);
        var operation = appends.Prepare(operationId, values, exactOutcomes);
        var onAppend = Unit.Retention?.Trigger == RetentionTrigger.OnAppend;
        var registration = BeginOnAppend(onAppend);
        RelationalAppendResult result;
        try
        {
            result = await execution.ExecuteWrite(() => appends.Append(operation, mode), mode)
                .ConfigureAwait(false);
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
        return (new WriteOutcome(result.Status), exactOutcomes ? result.ToReport() : null);
    }

    private async ValueTask<WriteOutcome> Mutate(
        StorageValues values,
        WriteOptions? options,
        RelationalCrudKind kind,
        RelationalExecution mode)
    {
        var operation = crud.PrepareMutation(values, options, kind);
        var onAppend = Unit.Retention?.Trigger == RetentionTrigger.OnAppend &&
            kind is RelationalCrudKind.Insert or RelationalCrudKind.Upsert;
        var registration = BeginOnAppend(onAppend);
        WriteOutcome outcome;
        try
        {
            outcome = await execution.ExecuteWrite(() => crud.Mutate(operation, mode), mode)
                .ConfigureAwait(false);
        }
        catch
        {
            await CompleteOnAppend(registration, cleanupRequired: false, mode).ConfigureAwait(false);
            throw;
        }
        await CompleteOnAppend(registration, onAppend && outcome.Succeeded, mode).ConfigureAwait(false);
        return outcome;
    }

    private ValueTask<WriteOutcome> DeleteCore(
        StorageKey key,
        WriteOptions? options,
        RelationalExecution mode)
    {
        var operation = crud.PrepareDelete(key, options);
        return execution.ExecuteWrite(() => crud.Delete(operation, mode), mode);
    }

    private ValueTask<CrossScopeQueryResult> QueryAcrossScopesCore(
        QueryRequest request,
        QueryRenderOptions? options,
        RelationalExecution mode) =>
        execution.Execute(() => queries.QueryAcrossScopes(request, options, mode), mode);

    private ValueTask<SetMutationResult> UpdateWhereCore(
        Predicate where,
        IReadOnlyDictionary<string, object?> assignments,
        RelationalExecution mode)
    {
        var operation = setMutations.PrepareUpdateWhere(where, assignments);
        return execution.ExecuteWrite(() => operation(mode), mode);
    }

    private ValueTask<SetMutationResult> DeleteWhereCore(Predicate where, RelationalExecution mode)
    {
        var operation = setMutations.PrepareDeleteWhere(where);
        return execution.ExecuteWrite(() => operation(mode), mode);
    }

    private ValueTask<RetentionResult> ApplyRetentionCore(
        RetentionExecutionOptions? options,
        RelationalExecution mode)
    {
        var engine = RequireRetention();
        var operation = engine.Prepare(options);
        return execution.ExecuteWrite(() => engine.Apply(operation, mode), mode);
    }

    private ValueTask<RetentionOperationResult> ApplyExactRetentionCore(
        OperationId operationId,
        RetentionExecutionOptions? options,
        RelationalExecution mode)
    {
        var engine = RequireRetention();
        var operation = engine.PrepareExact(operationId, options);
        return execution.ExecuteWrite(() => engine.ApplyExact(operation, mode), mode);
    }

    private async ValueTask<WriteOutcome> ExecuteProviderConditionalUpsert(
        Func<RelationalExecution, ValueTask<WriteOutcome>> operation,
        RelationalExecution mode)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var onAppend = Unit.Retention?.Trigger == RetentionTrigger.OnAppend;
        var registration = BeginOnAppend(onAppend);
        WriteOutcome outcome;
        try
        {
            outcome = await execution.ExecuteWrite(() => operation(mode), mode).ConfigureAwait(false);
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

    private OnAppendRetentionCoordinator.AppendRegistration? BeginOnAppend(bool eligible)
    {
        execution.EnsureOpen();
        StorageAccessValidation.EnsurePointOperation(Access, "write");
        return eligible && execution.Transaction is null
            ? OnAppendRetentionCoordinator.Begin(
                onAppendRetentionOwner,
                Unit,
                Access.Scope?.Value)
            : null;
    }

    private ValueTask CompleteOnAppend(
        OnAppendRetentionCoordinator.AppendRegistration? registration,
        bool cleanupRequired,
        RelationalExecution mode)
    {
        async ValueTask Cleanup()
        {
            var engine = RequireRetention();
            var options = new RetentionExecutionOptions { CancellationToken = mode.CancellationToken };
            var operation = engine.Prepare(options);
            await execution.ExecuteWrite(() => engine.Apply(operation, mode), mode).ConfigureAwait(false);
        }

        if (registration is not null)
            return registration.Complete(cleanupRequired, Cleanup);
        if (!cleanupRequired)
            return ValueTask.CompletedTask;
        return execution.Transaction is null
            ? OnAppendRetentionCoordinator.Run(
                onAppendRetentionOwner,
                Unit,
                Access.Scope?.Value,
                Cleanup)
            : Cleanup();
    }

    private RelationalSessionRetention RequireRetention() =>
        retention ?? throw new InvalidOperationException(
            $"Storage unit '{Unit.Name}' requires a relational retention adapter.");
}

/// <summary>
/// Driver-shaped hooks consumed by <see cref="RelationalStorageSessionBase"/>. The adapter is one
/// deliberate public seam; the individual provider-neutral state machines remain internal.
/// </summary>
public abstract class RelationalStorageSessionAdapter :
    IRelationalSessionExecutionAdapter,
    IRelationalPointReadAdapter,
    IRelationalCrudAdapter
{
    private readonly SemaphoreSlim gate;
    private Func<DbTransaction?> transaction = static () => null;
    private bool attached;

    /// <param name="gate">
    /// The serialization gate shared by every non-owning session that uses
    /// <paramref name="connection"/>. Omit it only when this adapter has exclusive ownership of
    /// the connection.
    /// </param>
    /// <param name="connection">The provider connection used by this session.</param>
    /// <param name="dialect">The relational command dialect.</param>
    protected RelationalStorageSessionAdapter(
        DbConnection connection,
        RelationalDialect dialect,
        SemaphoreSlim? gate = null)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        Dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
        this.gate = gate ?? new SemaphoreSlim(1, 1);
    }

    public DbConnection Connection { get; }

    public RelationalDialect Dialect { get; }

    /// <summary>The active direct-write or ambient unit-of-work transaction.</summary>
    public DbTransaction? Transaction => transaction();

    /// <summary>Creates a native command already attached to <see cref="Transaction"/>.</summary>
    public DbCommand CreateCommand(string sql) => CreateCommand(sql, Transaction);

    public virtual RelationalQueryRenderer QueryRenderer => Dialect.CreateQueryRenderer();

    public virtual bool SerializeAmbientReads => false;

    public virtual IReadOnlyDictionary<string, string> PhysicalIndexNames(StorageUnit unit) =>
        unit.Indexes.ToDictionary(index => index.Name, index => index.Name, StringComparer.Ordinal);

    public virtual object? Decode(object value, ColumnDefinition column) =>
        Dialect.ReadValue(value, column);

    public virtual ValueTask AssertExplainPlan(
        RelationalQueryCommand query,
        QueryRenderOptions options,
        RelationalExecution execution) => default;

    public virtual void EnsureUsable() { }

    protected virtual string Equality(
        ColumnDefinition column,
        string parameter,
        bool exactStringKeys) =>
        $"{Dialect.QuoteIdentifier(column.Name)}={parameter}";

    protected virtual string LockingClause(bool forUpdate) => string.Empty;

    protected abstract void BindParameter(
        DbCommand command,
        string parameter,
        object? value,
        ColumnDefinition column);

    protected abstract ValueTask<WriteOutcome> Insert(
        StorageValues values,
        WriteOutcomeStatus status,
        RelationalExecution execution);

    protected abstract ValueTask<WriteOutcome> Update(
        StorageValues values,
        StorageKey key,
        StoredEntry? existing,
        WriteOptions? options,
        RelationalExecution execution);

    protected abstract ValueTask<WriteOutcome> Upsert(
        StorageValues values,
        StorageKey key,
        StoredEntry? existing,
        WriteOptions? options,
        RelationalExecution execution);

    protected abstract ValueTask<WriteOutcome> Delete(
        StorageKey key,
        StoredEntry? existing,
        WriteOptions? options,
        RelationalExecution execution);

    protected virtual ValueTask<DbTransaction> BeginWrite(RelationalExecution execution) =>
        Dialect.BeginTransaction(Connection, execution);

    protected virtual ValueTask Rollback(DbTransaction transaction, RelationalExecution execution) =>
        execution.Rollback(transaction);

    internal void AttachTransactionAccessor(Func<DbTransaction?> accessor)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        if (attached)
            throw new InvalidOperationException("A relational session adapter can belong to only one session.");
        transaction = accessor;
        attached = true;
    }

    internal void Bind(
        DbCommand command,
        string parameter,
        object? value,
        ColumnDefinition column) => BindParameter(command, parameter, value, column);

    internal DbCommand CreateCommand(string sql, DbTransaction? transaction)
    {
        var command = Connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    ValueTask<IDisposable> IRelationalSessionExecutionAdapter.EnterGate(RelationalExecution execution)
    {
        if (execution.IsAsync)
            return EnterGateAsync(execution.CancellationToken);
        gate.Wait(execution.CancellationToken);
        return ValueTask.FromResult<IDisposable>(new GateLease(gate));
    }

    ValueTask<DbTransaction> IRelationalSessionExecutionAdapter.BeginWrite(RelationalExecution execution) =>
        BeginWrite(execution);

    ValueTask IRelationalSessionExecutionAdapter.Rollback(
        DbTransaction transaction,
        RelationalExecution execution) => Rollback(transaction, execution);

    string IRelationalPointReadAdapter.QuoteIdentifier(string identifier) =>
        Dialect.QuoteIdentifier(identifier);

    string IRelationalPointReadAdapter.Equality(
        ColumnDefinition column,
        string parameter,
        bool exactStringKeys) => Equality(column, parameter, exactStringKeys);

    void IRelationalPointReadAdapter.Bind(
        DbCommand command,
        string parameter,
        object? value,
        ColumnDefinition column) => BindParameter(command, parameter, value, column);

    object? IRelationalPointReadAdapter.Decode(object value, ColumnDefinition column) =>
        Decode(value, column);

    string IRelationalPointReadAdapter.LockingClause(bool forUpdate) => LockingClause(forUpdate);

    ValueTask<WriteOutcome> IRelationalCrudAdapter.Insert(
        StorageValues values,
        WriteOutcomeStatus status,
        RelationalExecution execution) => Insert(values, status, execution);

    ValueTask<WriteOutcome> IRelationalCrudAdapter.Update(
        StorageValues values,
        StorageKey key,
        StoredEntry? existing,
        WriteOptions? options,
        RelationalExecution execution) => Update(values, key, existing, options, execution);

    ValueTask<WriteOutcome> IRelationalCrudAdapter.Upsert(
        StorageValues values,
        StorageKey key,
        StoredEntry? existing,
        WriteOptions? options,
        RelationalExecution execution) => Upsert(values, key, existing, options, execution);

    ValueTask<WriteOutcome> IRelationalCrudAdapter.Delete(
        StorageKey key,
        StoredEntry? existing,
        WriteOptions? options,
        RelationalExecution execution) => Delete(key, existing, options, execution);

    private async ValueTask<IDisposable> EnterGateAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new GateLease(gate);
    }

    private sealed class GateLease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? remaining = gate;

        public void Dispose() => Interlocked.Exchange(ref remaining, null)?.Release();
    }
}
