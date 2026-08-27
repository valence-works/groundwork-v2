using Groundwork.Kernel;
using Groundwork.Store;
using Xunit;

namespace Groundwork.MongoDb.Tests;

public sealed class MongoUnitOfWorkLifecycleTests
{
    [Fact]
    public async Task Failed_async_commit_surfaces_its_own_error_and_leaves_the_unit_disposable()
    {
        var inner = new FailingUnitOfWork();
        var unitOfWork = new MongoStoreUnitOfWork(inner, BatchWriteOptions.Exact, exactAvailable: false);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await unitOfWork.CommitWithOutcomesAsync());

        Assert.Same(inner.Failure, failure);
        Assert.False(inner.RolledBack);
        unitOfWork.Dispose();
    }

    [Fact]
    public void Failed_sync_commit_surfaces_its_own_error_and_leaves_the_unit_disposable()
    {
        var inner = new FailingUnitOfWork();
        var unitOfWork = new MongoStoreUnitOfWork(inner, BatchWriteOptions.Exact, exactAvailable: false);

        var failure = Assert.Throws<InvalidOperationException>(() => unitOfWork.CommitWithOutcomes());

        Assert.Same(inner.Failure, failure);
        Assert.False(inner.RolledBack);
        unitOfWork.Dispose();
    }

    /// <summary>
    /// Stands in for the native unit of work after a non-retryable commit failure: the commit
    /// throws and the unit has already ended, exactly as <c>MongoUnitOfWork</c> now reports itself.
    /// </summary>
    private sealed class FailingUnitOfWork : IMongoUnitOfWork, IMongoUnitOfWorkState
    {
        private bool terminal;

        internal InvalidOperationException Failure { get; } = new("the commit failed");

        internal bool RolledBack { get; private set; }

        public bool IsActive => !terminal;

        public void EnsureActive()
        {
            if (terminal)
                throw new InvalidOperationException("The unit of work is already terminal.");
        }

        public IMongoStorageSession OpenSession(StorageUnit unit) => throw new NotSupportedException();

        public void Commit()
        {
            terminal = true;
            throw Failure;
        }

        public ValueTask CommitAsync(CancellationToken cancellationToken = default)
        {
            terminal = true;
            throw Failure;
        }

        public void Rollback()
        {
            RolledBack = true;
            EnsureActive();
            terminal = true;
        }

        public void Dispose()
        {
        }
    }
}
