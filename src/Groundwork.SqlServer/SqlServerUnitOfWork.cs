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
    private bool terminal;

    internal SqlServerUnitOfWork(SqlServerProviderConnection owner, SqlConnection connection, SqlTransaction transaction,
        IEnumerable<StorageUnit> units, StorageAccess access)
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
        SqlServerSchemaCoordinator.ValidateAccess(unit, access);
        var session = new SqlServerStorageSession(owner, SqlServerSchemaCoordinator.Physicalize(unit), access, connection, transaction);
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
