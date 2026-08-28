using Microsoft.Data.Sqlite;
using System.Reflection;
using Groundwork.Kernel;
using Groundwork.Sqlite;
using Groundwork.Store;
using Xunit;

namespace Groundwork.Sqlite.Tests;

/// <summary>
/// Regression coverage for the pool-poisoning guard in <see cref="SqliteTransactionCleanup"/>,
/// shared by <c>SqliteUnitOfWork</c> and <c>SqliteStorageSession</c>'s single-write path.
/// </summary>
public sealed class SqliteTransactionCleanupTests
{
    /// <summary>
    /// Proves the guard by observing the driver's own pooling behavior rather than Groundwork state.
    /// A <c>TEMP</c> table is tied to one specific native SQLite connection for as long as that
    /// connection stays open — including while it sits idle in Microsoft.Data.Sqlite's pool, since
    /// pooling keeps the same underlying <c>sqlite3*</c> handle alive rather than closing it. (A
    /// custom SQL function would not serve as the same probe: Microsoft.Data.Sqlite's own
    /// <c>Deactivate()</c> unregisters every collation, function, and aggregate whenever a connection
    /// returns to the pool, pooled or not, so a missing function proves nothing about pooling.) If
    /// the driver hands the same native handle back out of its pool, the temp table is still there;
    /// if the handle was discarded instead, a brand new native connection never saw it.
    /// <see cref="SqliteTransactionCleanup.RollbackOrClearPool"/> is exercised through a transaction
    /// whose <c>Rollback()</c> call is forced to fail (by having already completed via a prior
    /// <c>Commit()</c>) while the connection remains fully open and otherwise healthy — the same
    /// shape Microsoft.Data.Sqlite's own <c>SqliteTransaction.Dispose</c> produces when the ROLLBACK
    /// statement it issues under real contention fails but the wrapper still marks the transaction
    /// "completed" regardless.
    /// </summary>
    [Fact]
    public void RollbackOrClearPool_discards_the_connection_instead_of_returning_it_to_the_pool()
    {
        using var store = TemporaryStore.Create();

        using (var connection = new SqliteConnection(store.ConnectionString))
        {
            connection.Open();
            CreateMarker(connection);

            var transaction = connection.BeginTransaction();
            transaction.Commit();

            // The transaction is already completed, so Rollback() throws "TransactionCompleted" here —
            // standing in for a ROLLBACK statement that failed under contention while the connection
            // itself remained open and otherwise usable.
            Assert.Throws<InvalidOperationException>(
                () => SqliteTransactionCleanup.RollbackOrClearPool(transaction, connection));
        } // Dispose(): without the guard this returns a perfectly healthy connection to the pool.

        AssertMarkerMissing(store.ConnectionString);
    }

    [Fact]
    public void RollbackOrClearPool_does_not_intervene_when_rollback_succeeds()
    {
        using var store = TemporaryStore.Create();

        using (var connection = new SqliteConnection(store.ConnectionString))
        {
            connection.Open();
            CreateMarker(connection);

            var transaction = connection.BeginTransaction();
            SqliteTransactionCleanup.RollbackOrClearPool(transaction, connection);
        } // Dispose(): a clean rollback leaves pooling untouched, so this connection is recyclable.

        AssertMarkerPresent(store.ConnectionString);
    }

    /// <summary>
    /// Exercises the guard through the public <see cref="SqliteProviderConnection.BeginUnitOfWork"/>
    /// and <see cref="IUnitOfWork.Rollback"/> path rather than calling
    /// <see cref="SqliteTransactionCleanup.RollbackOrClearPool"/> directly. The private provider
    /// objects are inspected only to put the real transaction into a deterministic failed-rollback
    /// state; the operation under test is still the production unit-of-work entry point. Deleting or
    /// miswiring the forwarding call at that call site must make this test reuse the TEMP marker.
    /// </summary>
    [Fact]
    public void SqliteUnitOfWork_Rollback_discards_the_connection_instead_of_returning_it_to_the_pool()
    {
        using var store = TemporaryStore.Create();
        var unit = Unit();

        using var provider = new SqliteProviderConnection(store.ConnectionString);
        provider.Schema.Apply(unit);

        using (var unitOfWork = provider.BeginUnitOfWork(StorageAccess.Global, unit))
        {
            var connection = PrivateField<SqliteConnection>(unitOfWork, "connection");
            var transaction = PrivateField<SqliteTransaction>(unitOfWork, "transaction");
            CreateMarker(connection, transaction);

            transaction.Commit();

            // The transaction is already completed, so the public Rollback() call this unit of work
            // issues internally fails the same way a ROLLBACK statement can fail under contention.
            Assert.Throws<InvalidOperationException>(unitOfWork.Rollback);
        } // Rollback() already completed and disposed the production connection.

        AssertMarkerMissing(store.ConnectionString);
    }

    /// <summary>
    /// Exercises the guard through the public <see cref="SqliteProviderConnection.OpenSession"/>'s
    /// standalone single-write path
    /// (<c>ExecuteWrite</c>'s catch block) rather than calling
    /// <see cref="SqliteTransactionCleanup.RollbackOrClearPool"/> directly. A custom observer is
    /// attached through the public API and closes the session's own production connection the instant
    /// it is told a write is about to run — before the INSERT statement reaches the driver — so the
    /// insert fails on a closed connection and the session's own subsequent <c>Rollback()</c> call
    /// fails too, the same shape a ROLLBACK failing under contention would produce.
    /// </summary>
    [Fact]
    public void SqliteStorageSession_write_path_discards_the_connection_instead_of_returning_it_to_the_pool()
    {
        using var store = TemporaryStore.Create();
        var unit = Unit();
        var observer = new CloseConnectionOnWrite();

        using var providerConnection = new SqliteProviderConnection(store.ConnectionString);
        providerConnection.Schema.Apply(unit);

        var session = providerConnection.OpenSession(unit, StorageAccess.Global, observer);
        var sessionConnection = PrivateField<SqliteConnection>(session, "connection");
        observer.Attach(sessionConnection);
        CreateMarker(sessionConnection);

        var values = new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "row-1",
            ["value"] = "value-1"
        });

        Assert.ThrowsAny<Exception>(() => session.Insert(values));

        AssertMarkerMissing(store.ConnectionString);
    }

    /// <summary>
    /// Uses SQLite's native authorizer to reject the actual ROLLBACK statement after a public
    /// session write has failed. This proves that cleanup retires the checked-out connection and
    /// makes the same session unusable, rather than merely marking its managed transaction inactive.
    /// </summary>
    [Fact]
    public void SqliteStorageSession_native_rollback_failure_retires_the_session()
    {
        using var store = TemporaryStore.Create();
        var unit = Unit();
        var observer = new ThrowOnWrite();

        using var providerConnection = new SqliteProviderConnection(store.ConnectionString);
        providerConnection.Schema.Apply(unit);

        var session = providerConnection.OpenSession(unit, StorageAccess.Global, observer);
        var sessionConnection = PrivateField<SqliteConnection>(session, "connection");
        CreateMarker(sessionConnection);

        // SQLite authorizers run inside the native engine, so this rejects the real ROLLBACK
        // statement rather than throwing from the managed helper before the provider is reached.
        SQLitePCL.strdelegate_authorizer denyRollback =
            (_, actionCode, param0, _, _, _) => actionCode == SQLitePCL.raw.SQLITE_TRANSACTION &&
                string.Equals(param0, "ROLLBACK", StringComparison.OrdinalIgnoreCase)
                    ? SQLitePCL.raw.SQLITE_DENY
                    : SQLitePCL.raw.SQLITE_OK;
        Assert.Equal(SQLitePCL.raw.SQLITE_OK,
            SQLitePCL.raw.sqlite3_set_authorizer(sessionConnection.Handle, denyRollback, null));

        var values = new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "row-1",
            ["value"] = "value-1"
        });

        var original = Assert.Throws<InvalidOperationException>(() => session.Insert(values));
        Assert.Equal(ThrowOnWrite.Message, original.Message);
        var cleanupFailure = Assert.IsType<string>(original.Data[WriteFailureCleanup.CleanupFailureKey]);
        Assert.Contains("not authorized", cleanupFailure, StringComparison.OrdinalIgnoreCase);

        // The native transaction is stranded by the denied rollback, so a second write must be
        // refused by the session lifecycle before it can touch a retired connection.
        var unusable = Assert.Throws<ObjectDisposedException>(() => session.Insert(values));
        Assert.Equal("SqliteStorageSession", unusable.ObjectName);
        AssertMarkerMissing(store.ConnectionString);
    }

    private static void CreateMarker(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "CREATE TEMP TABLE marker (id INTEGER);";
        command.ExecuteNonQuery();
    }

    private static T PrivateField<T>(object instance, string name) =>
        (T)(instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance)
            ?? throw new InvalidOperationException($"Production field '{name}' was not found."));

    private static void AssertMarkerMissing(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM marker;";

        // A pooled native handle would still carry the temp table. A cleared pool gives us a new
        // native connection that never saw the marker, so the query fails.
        var failure = Assert.Throws<SqliteException>(() => command.ExecuteScalar());
        Assert.Contains("no such table", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertMarkerPresent(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM marker;";

        // A successful rollback does not clear the pool, so the same native handle comes back out.
        Assert.Equal(0L, command.ExecuteScalar());
    }

    private static StorageUnit Unit() => new()
    {
        Id = new StorageUnitId("cleanup"),
        Name = "cleanup",
        Columns =
        [
            new() { Name = "id", Type = PortableType.String, IsNullable = false },
            new() { Name = "value", Type = PortableType.String }
        ],
        Key = new KeyDefinition { Columns = ["id"] }
    };

    /// <summary>
    /// Closes its connection the instant a write is about to run, so the write fails on a closed
    /// connection before it reaches the driver, and the session's own recovery rollback fails too.
    /// </summary>
    private sealed class CloseConnectionOnWrite : IProviderCommandObserver
    {
        private SqliteConnection? connection;

        public void Attach(SqliteConnection sessionConnection) => connection = sessionConnection;

        public void Observe(ProviderCommandEvent command)
        {
            if (command.Kind == ProviderCommandKind.Write)
                connection!.Close();
        }
    }

    private sealed class ThrowOnWrite : IProviderCommandObserver
    {
        public const string Message = "forced write failure";

        public void Observe(ProviderCommandEvent command)
        {
            if (command.Kind == ProviderCommandKind.Write)
                throw new InvalidOperationException(Message);
        }
    }

    private sealed class TemporaryStore : IDisposable
    {
        private readonly string directory;

        private TemporaryStore(string directory)
        {
            this.directory = directory;
            ConnectionString = $"Data Source={Path.Combine(directory, "cleanup.db")}";
        }

        public string ConnectionString { get; }

        public static TemporaryStore Create()
        {
            var path = Path.Combine(Path.GetTempPath(), "groundwork-sqlite-cleanup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryStore(path);
        }

        public void Dispose()
        {
            using var connection = new SqliteConnection(ConnectionString);
            SqliteConnection.ClearPool(connection);
            Directory.Delete(directory, recursive: true);
        }
    }
}
