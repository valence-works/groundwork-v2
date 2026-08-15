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
    private bool terminal;

    internal PostgreSqlUnitOfWork(
        PostgreSqlProviderConnection owner,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<StorageUnit> declarations,
        StorageAccess access)
    {
        this.owner = owner;
        this.connection = connection;
        this.transaction = transaction;
        this.access = access;
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
        return session;
    }

    public void Commit()
    {
        ThrowIfTerminal();
        try
        {
            transaction.Commit();
            terminal = true;
            CloseSessions();
        }
        finally
        {
            connection.Dispose();
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

    private void ThrowIfTerminal()
    {
        if (terminal)
            throw new InvalidOperationException("The unit of work is already terminal.");
    }
}
