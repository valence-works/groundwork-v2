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

    [Fact]
    public async Task Failing_rollback_records_itself_against_the_commit_failure_it_must_not_replace()
    {
        var inner = new FailingUnitOfWork(endsOnCommitFailure: false);
        var unitOfWork = new MongoStoreUnitOfWork(inner, BatchWriteOptions.Exact, exactAvailable: false);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await unitOfWork.CommitWithOutcomesAsync());

        Assert.Same(inner.Failure, failure);
        Assert.True(inner.RolledBack);
        Assert.Contains(
            inner.RollbackFailure.Message,
            Assert.IsType<string>(failure.Data[WriteFailureCleanup.CleanupFailureKey]),
            StringComparison.Ordinal);
        unitOfWork.Dispose();
    }

    /// <summary>
    /// Stands in for the native unit of work after a non-retryable commit failure: the commit
    /// throws, the unit reports whether it has already ended, and its rollback fails too.
    /// </summary>
    private sealed class FailingUnitOfWork(bool endsOnCommitFailure = true)
        : IMongoUnitOfWork, IMongoUnitOfWorkState
    {
        private bool terminal;

        internal InvalidOperationException Failure { get; } = new("the commit failed");

        internal InvalidOperationException RollbackFailure { get; } = new("the rollback failed too");

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
            terminal = endsOnCommitFailure;
            throw Failure;
        }

        public ValueTask CommitAsync(CancellationToken cancellationToken = default)
        {
            terminal = endsOnCommitFailure;
            throw Failure;
        }

        public void Rollback()
        {
            RolledBack = true;
            EnsureActive();
            terminal = true;
            throw RollbackFailure;
        }

        public void Dispose()
        {
        }
    }
}
