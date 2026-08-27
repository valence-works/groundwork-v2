using System.Data;
using System.Data.Common;

namespace Groundwork.Substrate.Relational;

/// <summary>Closes one provider resource on the surface the caller selected.</summary>
public readonly struct RelationalDisposal(IDisposable resource, bool isAsync) : IAsyncDisposable
{
    public ValueTask DisposeAsync()
    {
        if (isAsync && resource is IAsyncDisposable asynchronous)
            return asynchronous.DisposeAsync();
        resource.Dispose();
        return default;
    }
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

    public ValueTask<DbDataReader> ExecuteReader(DbCommand command) => IsAsync
        ? new(command.ExecuteReaderAsync(CancellationToken))
        : new(command.ExecuteReader());

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

    public ValueTask Dispose(DbTransaction transaction) => new RelationalDisposal(transaction, IsAsync).DisposeAsync();

    /// <summary>
    /// Scopes a provider resource whose close can still talk to the server — a data reader — so
    /// the asynchronous surface closes it asynchronously and the synchronous surface does not
    /// block the caller on a pending close.
    /// </summary>
    public RelationalDisposal Scope(IDisposable resource) => new(resource, IsAsync);
}
