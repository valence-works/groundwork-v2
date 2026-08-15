using Microsoft.Data.Sqlite;
using Groundwork.Kernel;
using Groundwork.Testing;

namespace Groundwork.Sqlite;

internal sealed class SqliteUnitOfWork : IUnitOfWork
{
    private readonly SqliteProviderConnection owner;
    private readonly SqliteConnection connection;
    private readonly SqliteTransaction transaction;
    private readonly StorageAccess access;
    private readonly HashSet<StorageUnitId> units;
    private readonly List<SqliteStorageSession> sessions = [];
    private bool terminal;

    internal SqliteUnitOfWork(
        SqliteProviderConnection owner,
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<StorageUnit> units,
        StorageAccess access)
    {
        this.owner = owner;
        this.connection = connection;
        this.transaction = transaction;
        this.units = units.Select(unit => unit.Id).ToHashSet();
        this.access = access;
    }

    public IStorageSession OpenSession(StorageUnit unit)
    {
        ThrowIfTerminal();
        ArgumentNullException.ThrowIfNull(unit);
        if (!units.Contains(unit.Id))
            throw new InvalidOperationException($"Storage unit '{unit.Id.Value}' was not declared for this unit of work.");
        SqliteSchemaCoordinator.ValidateAccess(unit, access);
        var session = new SqliteStorageSession(owner, SqliteSchemaCoordinator.Physicalize(unit), access, connection, transaction);
        sessions.Add(session);
        return session;
    }

    public void Commit()
    {
        ThrowIfTerminal();
        try { transaction.Commit(); }
        finally { Complete(); }
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
