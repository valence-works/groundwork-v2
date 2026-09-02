using Microsoft.Data.Sqlite;

namespace Groundwork.Sqlite;

/// <summary>
/// Shared rollback-failure guard for every SQLite write path that opens its own transaction
/// (the SQLite unit-of-work path and <see cref="SqliteStorageSession"/>'s single-write path).
/// </summary>
/// <remarks>
/// <para>
/// Microsoft.Data.Sqlite's <c>SqliteTransaction.Rollback</c>/<c>Dispose</c> issue a native
/// <c>ROLLBACK;</c> and, in a <c>finally</c> block, mark the transaction "completed" and null the
/// connection's <c>Transaction</c> property <em>regardless of whether that ROLLBACK statement
/// itself succeeded</em>. If the rollback fails — for example under write contention — the ADO.NET
/// wrapper still reports the connection as clean. Disposed afterward, that connection is returned
/// to the driver's connection-string-keyed pool as if nothing were wrong, while the native SQLite
/// handle may still hold an open transaction. The next caller to draw that pooled handle meets
/// "cannot start a transaction within a transaction" or a PRAGMA refused for running inside one —
/// attributed to whatever unrelated work it was attempting.
/// </para>
/// <para>
/// <see cref="SqliteConnection.ClearPool(SqliteConnection)"/> defends against exactly this: verified
/// against the pinned Microsoft.Data.Sqlite 10.0.10 source, <c>SqliteConnectionPool.Clear()</c> calls
/// <c>DoNotPool()</c> on every <c>SqliteConnectionInternal</c> the pool has ever handed out —
/// including the one still checked out under this failing rollback, not only idle ones — so that
/// connection's own subsequent <c>Dispose</c> discards its native handle instead of recycling it.
/// </para>
/// <para>
/// <b>Blast radius:</b> clearing acts on the whole pool group for this connection string, in this
/// process. Every other idle connection sharing that connection string is torn down immediately,
/// and every other connection currently checked out — by other sessions, other units of work, on
/// other threads — is marked non-poolable too, so each pays a fresh native open (and re-registers
/// its collations, functions, and PRAGMAs) the next time it completes, instead of being recycled.
/// That is a deliberate trade on a failure path that is otherwise rare: one writer's failed rollback
/// pays for every neighbor's next connection open, so that no neighbor risks inheriting a
/// still-open transaction.
/// </para>
/// </remarks>
internal static class SqliteTransactionCleanup
{
    /// <summary>
    /// Rolls back <paramref name="transaction"/>. If the ROLLBACK itself fails, clears the
    /// connection-string pool group <paramref name="connection"/> belongs to before rethrowing, so
    /// the connection this transaction lives on is torn down rather than recirculated once disposed.
    /// </summary>
    public static void RollbackOrClearPool(SqliteTransaction transaction, SqliteConnection connection)
    {
        try
        {
            transaction.Rollback();
        }
        catch
        {
            SqliteConnection.ClearPool(connection);
            throw;
        }
    }
}
