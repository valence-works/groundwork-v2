using System.Data;
using System.Data.Common;

namespace Groundwork.Substrate.Relational;

/// <summary>
/// An open data reader bound to the surface that opened it. Closing a reader still talks to the
/// server whenever its result set is not drained, so this scope is asynchronously disposable and
/// nothing else: a <c>using</c> declaration over it does not compile, and an asynchronously opened
/// reader therefore cannot be closed with blocking I/O by forgetting an idiom.
/// </summary>
public readonly struct RelationalReader : IAsyncDisposable
{
    private readonly bool isAsync;

    internal RelationalReader(DbDataReader reader, bool isAsync)
    {
        Reader = reader;
        this.isAsync = isAsync;
    }

    /// <summary>The reader, open until this scope is disposed.</summary>
    public DbDataReader Reader { get; }

    public ValueTask DisposeAsync() => RelationalExecution.Close(Reader, isAsync);
}

/// <summary>
/// Selects the synchronous or the asynchronous ADO.NET surface for one shared command body, so a
/// provider keeps a single implementation of every operation rather than two that can drift.
/// </summary>
public readonly struct RelationalExecution
{
    private RelationalExecution(bool isAsync, CancellationToken cancellationToken)
    {
        IsAsync = isAsync;
        CancellationToken = cancellationToken;
    }

    /// <summary>Runs the body on the calling thread; every returned task is already completed.</summary>
    public static RelationalExecution Synchronous { get; } = new(false, CancellationToken.None);

    public static RelationalExecution Asynchronous(CancellationToken cancellationToken) =>
        new(true, cancellationToken);

    public bool IsAsync { get; }

    public CancellationToken CancellationToken { get; }

    public ValueTask<int> ExecuteNonQuery(DbCommand command) => IsAsync
        ? new(command.ExecuteNonQueryAsync(CancellationToken))
        : new(command.ExecuteNonQuery());

    public ValueTask<object?> ExecuteScalar(DbCommand command) => IsAsync
        ? new(command.ExecuteScalarAsync(CancellationToken))
        : new(command.ExecuteScalar());

    /// <summary>
    /// Opens a reader inside a scope that closes it on the same surface. A command needs no such
    /// scope because disposing one talks to nobody.
    /// </summary>
    public ValueTask<RelationalReader> ExecuteReader(DbCommand command) => IsAsync
        ? Open(command.ExecuteReaderAsync(CancellationToken))
        : new(new RelationalReader(command.ExecuteReader(), false));

    public ValueTask<bool> Read(DbDataReader reader) => IsAsync
        ? new(reader.ReadAsync(CancellationToken))
        : new(reader.Read());

    public ValueTask<bool> NextResult(DbDataReader reader) => IsAsync
        ? new(reader.NextResultAsync(CancellationToken))
        : new(reader.NextResult());

    public ValueTask<DbTransaction> BeginTransaction(DbConnection connection, IsolationLevel isolation) => IsAsync
        ? connection.BeginTransactionAsync(isolation, CancellationToken)
        : new(connection.BeginTransaction(isolation));

    public ValueTask Commit(DbTransaction transaction)
    {
        if (IsAsync)
            return new(transaction.CommitAsync(CancellationToken));
        transaction.Commit();
        return default;
    }

    /// <summary>
    /// Rolls back without observing the ambient token: a rollback must still run when the caller
    /// has already cancelled the operation it is undoing.
    /// </summary>
    public ValueTask Rollback(DbTransaction transaction)
    {
        if (IsAsync)
            return new(transaction.RollbackAsync(CancellationToken.None));
        transaction.Rollback();
        return default;
    }

    public ValueTask Dispose(DbTransaction transaction) => Close(transaction, IsAsync);

    internal static ValueTask Close(IAsyncDisposable resource, bool isAsync)
    {
        if (isAsync)
            return resource.DisposeAsync();
        ((IDisposable)resource).Dispose();
        return default;
    }

    private static async ValueTask<RelationalReader> Open(Task<DbDataReader> pending) =>
        new(await pending.ConfigureAwait(false), true);
}
