using Microsoft.Data.SqlClient;
using Groundwork.Kernel;
using Groundwork.Testing;

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

    internal SqlServerUnitOfWork(SqlServerProviderConnection owner, SqlConnection connection, SqlTransaction transaction,
        IEnumerable<StorageUnit> units, StorageAccess access, BatchWriteOptions options)
    {
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
        var session = new SqlServerStorageSession(owner, SqlServerSchemaCoordinator.Physicalize(unit), access, connection, transaction);
        sessions.Add(session);
        var batched = new BatchStorageSession(session, batch);
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
        catch
        {
            try { transaction.Rollback(); }
            finally { Complete(); }
            throw;
        }
        finally
        {
            if (!terminal)
                Complete();
        }
    }

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
        foreach (var session in sessions) session.Close();
        transaction.Dispose();
        connection.Dispose();
    }

    private void ThrowIfTerminal()
    {
        if (terminal) throw new InvalidOperationException("The unit of work is already terminal.");
    }
}
