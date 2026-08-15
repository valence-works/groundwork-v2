using Groundwork.Kernel;
using Groundwork.Testing;
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

    internal PostgreSqlUnitOfWork(
        PostgreSqlProviderConnection owner,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<StorageUnit> declarations,
        StorageAccess access,
        BatchWriteOptions options)
    {
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
        var session = new PostgreSqlStorageSession(owner, physical, access, connection, transaction);
        sessions.Add(session);
        var batched = new BatchStorageSession(session, batch);
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

    public BatchWriteSummary Commit() => CompleteCommit();

    public BatchWriteSummary CommitWithOutcomes()
    {
        ThrowIfTerminal();
        batch.RequireExactOutcomes();
        return CompleteCommit();
    }

    private BatchWriteSummary CompleteCommit()
    {
        ThrowIfTerminal();
        try
        {
            batch.FlushAll();
            transaction.Commit();
            return new BatchWriteSummary(batch.DrainCompleted());
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

    public ValueTask<BatchWriteSummary> CommitWithOutcomesAsync(CancellationToken cancellationToken = default)
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
        CloseSessions();
        connection.Dispose();
    }

    private void ThrowIfTerminal()
    {
        if (terminal)
            throw new InvalidOperationException("The unit of work is already terminal.");
    }
}
