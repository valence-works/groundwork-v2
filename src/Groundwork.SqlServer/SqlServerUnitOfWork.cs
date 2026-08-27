using Microsoft.Data.SqlClient;
using Groundwork.Kernel;
using Groundwork.Store;
using Groundwork.Diagnostics;

namespace Groundwork.SqlServer;

internal sealed class SqlServerUnitOfWork : IUnitOfWork
{
    private readonly SqlServerProviderConnection owner;
    private readonly SqlConnection connection;
    private readonly SqlTransaction transaction;
    private readonly StorageAccess access;
    private readonly HashSet<StorageUnitId> units;
    private readonly List<SqlServerStorageSession> sessions = [];
    private readonly BatchContext batch;
    private bool terminal;
    private readonly IProviderCommandObserver? commandObserver;

    internal SqlServerUnitOfWork(SqlServerProviderConnection owner, SqlConnection connection, SqlTransaction transaction,
        IEnumerable<StorageUnit> units, StorageAccess access, BatchWriteOptions options,
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
        SqlServerSchemaCoordinator.ValidateAccess(unit, access);
        var session = new SqlServerStorageSession(owner, SqlServerSchemaCoordinator.Physicalize(unit), access, connection, transaction, commandObserver);
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

    public BatchWriteSummary Commit() =>
        BatchWriteSummary.FromOutcomes(CompleteCommit(isAsync: false, CancellationToken.None).GetAwaiter().GetResult());

    public async ValueTask<BatchWriteSummary> CommitAsync(CancellationToken cancellationToken = default) =>
        BatchWriteSummary.FromOutcomes(await CompleteCommit(isAsync: true, cancellationToken).ConfigureAwait(false));

    public BatchWriteReport CommitWithOutcomes()
    {
        ThrowIfTerminal();
        batch.RequireExactOutcomes();
        return new BatchWriteReport(CompleteCommit(isAsync: false, CancellationToken.None).GetAwaiter().GetResult());
    }

    public async ValueTask<BatchWriteReport> CommitWithOutcomesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfTerminal();
        batch.RequireExactOutcomes();
        return new BatchWriteReport(await CompleteCommit(isAsync: true, cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask<IReadOnlyList<RowWriteOutcome>> CompleteCommit(bool isAsync, CancellationToken cancellationToken)
    {
        ThrowIfTerminal();
        try
        {
            if (isAsync)
            {
                await batch.FlushAllAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                batch.FlushAll();
                transaction.Commit();
            }
            return batch.DrainCompleted();
        }
        catch (Exception failure)
        {
            await WriteFailureCleanup.Run(failure, async () =>
            {
                try
                {
                    if (isAsync)
                        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    else
                        transaction.Rollback();
                }
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
