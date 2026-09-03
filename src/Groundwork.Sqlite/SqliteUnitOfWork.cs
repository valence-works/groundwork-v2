using Groundwork.Store;
using Groundwork.Substrate.Relational;
using Groundwork.Kernel;

namespace Groundwork.Sqlite;

/// <summary>Releases SQLite's provider gate only after the transaction reaches a terminal state.</summary>
internal sealed class SqliteUnitOfWork : IUnitOfWork
{
    private readonly RelationalUnitOfWork inner;
    private IDisposable? gateLease;

    internal SqliteUnitOfWork(RelationalUnitOfWork inner, IDisposable gateLease)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.gateLease = gateLease ?? throw new ArgumentNullException(nameof(gateLease));
    }

    public IStorageSession OpenSession(StorageUnit unit) => inner.OpenSession(unit);

    public void Stage(RowWrite write) => inner.Stage(write);

    public BatchWriteSummary Commit()
    {
        try
        {
            return inner.Commit();
        }
        finally
        {
            ReleaseIfTerminal();
        }
    }

    public BatchWriteReport CommitWithOutcomes()
    {
        try
        {
            return inner.CommitWithOutcomes();
        }
        finally
        {
            ReleaseIfTerminal();
        }
    }

    public async ValueTask<BatchWriteReport> CommitWithOutcomesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await inner.CommitWithOutcomesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseIfTerminal();
        }
    }

    public async ValueTask<BatchWriteSummary> CommitAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await inner.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseIfTerminal();
        }
    }

    public void Rollback()
    {
        try
        {
            inner.Rollback();
        }
        finally
        {
            ReleaseIfTerminal();
        }
    }

    public void Dispose()
    {
        try
        {
            inner.Dispose();
        }
        finally
        {
            ReleaseIfTerminal();
        }
    }

    private void ReleaseIfTerminal()
    {
        if (inner.IsTerminal)
            Interlocked.Exchange(ref gateLease, null)?.Dispose();
    }
}
