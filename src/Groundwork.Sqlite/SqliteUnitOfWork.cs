using Microsoft.Data.Sqlite;
using Groundwork.Kernel;
using Groundwork.Store;
using Groundwork.Diagnostics;

namespace Groundwork.Sqlite;

internal sealed class SqliteUnitOfWork : IUnitOfWork
{
    private readonly SqliteProviderConnection owner;
    private readonly SqliteConnection connection;
    private readonly SqliteTransaction transaction;
    private readonly StorageAccess access;
    private readonly HashSet<StorageUnitId> units;
    private readonly List<SqliteStorageSession> sessions = [];
    private readonly BatchContext batch;
    private bool terminal;
    private readonly IProviderCommandObserver? commandObserver;

    internal SqliteUnitOfWork(
        SqliteProviderConnection owner,
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<StorageUnit> units,
        StorageAccess access,
        BatchWriteOptions options,
        IProviderCommandObserver? observer = null)
    {
        commandObserver = observer;
        this.owner = owner;
        this.connection = connection;
        this.transaction = transaction;
        this.units = units.Select(unit => unit.Id).ToHashSet();
        this.access = access;
        batch = new BatchContext(options);
    }

    public IStorageSession OpenSession(StorageUnit unit)
    {
        ThrowIfTerminal();
        ArgumentNullException.ThrowIfNull(unit);
        if (!units.Contains(unit.Id))
            throw new InvalidOperationException($"Storage unit '{unit.Id.Value}' was not declared for this unit of work.");
        SqliteSchemaCoordinator.ValidateAccess(unit, access);
        var session = new SqliteStorageSession(owner, SqliteSchemaCoordinator.Physicalize(unit), access, connection, transaction, commandObserver);
        sessions.Add(session);
        var batched = BatchStorageSession.Create(session, batch);
        batch.Register(batched);
        return batched;
    }

    public void Stage(RowWrite write)
    {
        ArgumentNullException.ThrowIfNull(write);
        ThrowIfTerminal();
        if (!units.Contains(write.Unit.Id))
            throw new InvalidOperationException($"Storage unit '{write.Unit.Id.Value}' was not declared for this unit of work.");
        if (!sessions.Any(session => session.Unit.Id == write.Unit.Id))
            _ = OpenSession(write.Unit);
        batch.Stage(write);
        if (batch.ReachedCap)
            batch.FlushAll();
    }

    public BatchWriteSummary Commit() => BatchWriteSummary.FromOutcomes(CompleteCommit());

    public BatchWriteReport CommitWithOutcomes()
    {
        ThrowIfTerminal();
        batch.RequireExactOutcomes();
        return new BatchWriteReport(CompleteCommit());
    }

    private IReadOnlyList<RowWriteOutcome> CompleteCommit()
    {
        ThrowIfTerminal();
        try
        {
            batch.FlushAll();
            transaction.Commit();
            return batch.DrainCompleted();
        }
        catch (Exception failure)
        {
            WriteFailureCleanup.Run(failure, () =>
            {
                try { transaction.Rollback(); }
                finally { Complete(); }
            });
            throw;
        }
        finally
        {
            if (!terminal)
                Complete();
        }
    }

    /// <summary>
    /// Microsoft.Data.Sqlite completes its asynchronous surface synchronously and this provider
    /// serializes commands on a gate a suspended continuation cannot hold, so an asynchronous
    /// commit flushes and commits on the calling thread; see docs/sqlite-provider.md.
    /// </summary>
    public ValueTask<BatchWriteReport> CommitWithOutcomesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(CommitWithOutcomes());
    }

    public ValueTask<BatchWriteSummary> CommitAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Commit());
    }

    public void Rollback()
    {
        ThrowIfTerminal();
        try { transaction.Rollback(); }
        finally { Complete(); }
    }

    public void Dispose()
    {
        if (!terminal) Rollback();
    }

    private void Complete()
    {
        terminal = true;
        WriteFailureCleanup.RunAll(
            () => { foreach (var session in sessions) session.Close(); },
            transaction.Dispose,
            connection.Dispose);
    }

    private void ThrowIfTerminal()
    {
        if (terminal) throw new InvalidOperationException("The unit of work is already terminal.");
    }
}
