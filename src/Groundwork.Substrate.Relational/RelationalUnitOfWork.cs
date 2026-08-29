using System.Data.Common;
using Groundwork.Kernel;
using Groundwork.Store;

namespace Groundwork.Substrate.Relational;

/// <summary>
/// Owns the provider-neutral relational unit-of-work state machine. Provider packages supply only
/// session construction and the transaction lifetime whose cleanup is local to their driver.
/// </summary>
internal sealed class RelationalUnitOfWork : IUnitOfWork
{
    private readonly IReadOnlyDictionary<StorageUnitId, StorageUnit> units;
    private readonly Func<StorageUnit, RelationalUnitOfWorkSession> sessionFactory;
    private readonly RelationalUnitOfWorkLifetime lifetime;
    private readonly List<RelationalUnitOfWorkSession> sessions = [];
    private readonly BatchContext batch;
    private bool terminal;

    internal RelationalUnitOfWork(
        IEnumerable<StorageUnit> declarations,
        BatchWriteOptions options,
        Func<StorageUnit, RelationalUnitOfWorkSession> sessionFactory,
        RelationalUnitOfWorkLifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(declarations);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sessionFactory);
        ArgumentNullException.ThrowIfNull(lifetime);

        units = declarations.ToDictionary(unit => unit.Id);
        this.sessionFactory = sessionFactory;
        this.lifetime = lifetime;
        batch = new BatchContext(options);
    }

    public IStorageSession OpenSession(StorageUnit unit)
    {
        ThrowIfTerminal();
        ArgumentNullException.ThrowIfNull(unit);
        if (!units.TryGetValue(unit.Id, out var declaration))
            throw new InvalidOperationException($"Storage unit '{unit.Id.Value}' was not declared for this unit of work.");

        var owned = sessionFactory(declaration);
        sessions.Add(owned);
        var batched = BatchStorageSession.Create(owned.Session, batch);
        batch.Register(batched);
        return batched;
    }

    public void Stage(RowWrite write)
    {
        ArgumentNullException.ThrowIfNull(write);
        ThrowIfTerminal();
        if (!units.ContainsKey(write.Unit.Id))
            throw new InvalidOperationException($"Storage unit '{write.Unit.Id.Value}' was not declared for this unit of work.");
        if (!sessions.Any(session => session.Session.Unit.Id == write.Unit.Id))
            _ = OpenSession(write.Unit);
        batch.Stage(write);
        if (batch.ReachedCap)
            batch.FlushAll();
    }

    public BatchWriteSummary Commit() =>
        BatchWriteSummary.FromOutcomes(CompleteCommit(RelationalExecution.Synchronous).GetAwaiter().GetResult());

    public async ValueTask<BatchWriteSummary> CommitAsync(CancellationToken cancellationToken = default) =>
        BatchWriteSummary.FromOutcomes(await CompleteCommit(lifetime.Execution(cancellationToken)).ConfigureAwait(false));

    public BatchWriteReport CommitWithOutcomes()
    {
        ThrowIfTerminal();
        batch.RequireExactOutcomes();
        return new BatchWriteReport(CompleteCommit(RelationalExecution.Synchronous).GetAwaiter().GetResult());
    }

    public async ValueTask<BatchWriteReport> CommitWithOutcomesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfTerminal();
        batch.RequireExactOutcomes();
        return new BatchWriteReport(await CompleteCommit(lifetime.Execution(cancellationToken)).ConfigureAwait(false));
    }

    private async ValueTask<IReadOnlyList<RowWriteOutcome>> CompleteCommit(RelationalExecution execution)
    {
        ThrowIfTerminal();
        try
        {
            if (execution.IsAsync)
                await batch.FlushAllAsync(execution.CancellationToken).ConfigureAwait(false);
            else
                batch.FlushAll();
            await lifetime.Commit(execution).ConfigureAwait(false);
            return batch.DrainCompleted();
        }
        catch (Exception failure)
        {
            await WriteFailureCleanup.Run(failure, async () =>
            {
                try { await lifetime.Rollback(execution).ConfigureAwait(false); }
                finally { Complete(); }
            }).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (!terminal)
                Complete();
        }
    }

    public void Rollback()
    {
        ThrowIfTerminal();
        try { lifetime.Rollback(RelationalExecution.Synchronous).GetAwaiter().GetResult(); }
        finally { Complete(); }
    }

    public void Dispose()
    {
        if (!terminal)
            Rollback();
    }

    private void Complete()
    {
        terminal = true;
        WriteFailureCleanup.RunAll(
            () =>
            {
                foreach (var session in sessions)
                    session.Close();
            },
            lifetime.Dispose);
    }

    private void ThrowIfTerminal()
    {
        if (terminal)
            throw new InvalidOperationException("The unit of work is already terminal.");
    }
}

internal sealed record RelationalUnitOfWorkSession(IStorageSession Session, Action Close);

/// <summary>Contains the driver-local transaction cleanup decisions for a relational unit of work.</summary>
internal sealed class RelationalUnitOfWorkLifetime : IDisposable
{
    private readonly DbConnection connection;
    private readonly DbTransaction transaction;
    private readonly Action? rollback;
    private readonly bool disposeTransaction;
    private readonly bool supportsAsync;

    internal RelationalUnitOfWorkLifetime(
        DbConnection connection,
        DbTransaction transaction,
        bool supportsAsync,
        bool disposeTransaction,
        Action? rollback = null)
    {
        this.connection = connection;
        this.transaction = transaction;
        this.supportsAsync = supportsAsync;
        this.disposeTransaction = disposeTransaction;
        this.rollback = rollback;
    }

    internal RelationalExecution Execution(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return supportsAsync
            ? RelationalExecution.Asynchronous(cancellationToken)
            : RelationalExecution.Synchronous;
    }

    internal ValueTask Commit(RelationalExecution execution) => execution.Commit(transaction);

    internal ValueTask Rollback(RelationalExecution execution)
    {
        if (rollback is null)
            return execution.Rollback(transaction);
        rollback();
        return default;
    }

    public void Dispose()
    {
        if (disposeTransaction)
            WriteFailureCleanup.RunAll(transaction.Dispose, connection.Dispose);
        else
            connection.Dispose();
    }
}
