using Groundwork.Kernel;
using Groundwork.Store;
using Groundwork.Diagnostics;
using Npgsql;

namespace Groundwork.PostgreSql;

internal sealed class PostgreSqlUnitOfWork : IUnitOfWork
{
    private readonly PostgreSqlProviderConnection owner;
    private readonly NpgsqlConnection connection;
    private readonly NpgsqlTransaction transaction;
    private readonly IReadOnlyDictionary<StorageUnitId, StorageUnit> units;
    private readonly StorageAccess access;
    private readonly List<PostgreSqlStorageSession> sessions = [];
    private readonly BatchContext batch;
    private bool terminal;
    private readonly IProviderCommandObserver? commandObserver;

    internal PostgreSqlUnitOfWork(
        PostgreSqlProviderConnection owner,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<StorageUnit> declarations,
        StorageAccess access,
        BatchWriteOptions options,
        IProviderCommandObserver? observer = null)
    {
        commandObserver = observer;
        this.owner = owner;
        this.connection = connection;
        this.transaction = transaction;
        this.access = access;
        batch = new BatchContext(options);
        units = declarations.ToDictionary(unit => unit.Id, owner.Resolve);
    }

    public IStorageSession OpenSession(StorageUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ThrowIfTerminal();
        if (!units.TryGetValue(unit.Id, out var physical))
            throw new InvalidOperationException($"Storage unit '{unit.Id.Value}' was not declared for this unit of work.");
        var session = new PostgreSqlStorageSession(owner, physical, access, connection, transaction, commandObserver);
        sessions.Add(session);
        var batched = BatchStorageSession.Create(session, batch);
        batch.Register(batched);
        return batched;
    }

    public void Stage(RowWrite write)
    {
        ArgumentNullException.ThrowIfNull(write);
        ThrowIfTerminal();
        if (!units.ContainsKey(write.Unit.Id))
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
        try
        {
            transaction.Rollback();
            terminal = true;
            CloseSessions();
        }
        finally
        {
            connection.Dispose();
        }
    }

    public void Dispose()
    {
        if (!terminal)
            Rollback();
    }

    private void CloseSessions()
    {
        foreach (var session in sessions)
            session.Close();
    }

    private void Complete()
    {
        terminal = true;
        WriteFailureCleanup.RunAll(CloseSessions, connection.Dispose);
    }

    private void ThrowIfTerminal()
    {
        if (terminal)
            throw new InvalidOperationException("The unit of work is already terminal.");
    }
}
