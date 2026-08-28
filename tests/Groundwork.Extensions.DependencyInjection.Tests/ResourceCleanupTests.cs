using Groundwork.Store;
using Xunit;

namespace Groundwork.Extensions.DependencyInjection.Tests;

public sealed class ResourceCleanupTests
{
    [Fact]
    public void Synchronous_cleanup_attempts_every_step_and_preserves_the_first_failure()
    {
        var attempted = new List<string>();
        var first = new InvalidOperationException("first cleanup failed");
        var later = new InvalidOperationException("later cleanup failed");

        var thrown = Assert.Throws<InvalidOperationException>(() => ResourceCleanup.RunAll(
        [
            () => { attempted.Add("first"); throw first; },
            () => attempted.Add("middle"),
            () => { attempted.Add("last"); throw later; }
        ]));

        Assert.Same(first, thrown);
        Assert.Equal(["first", "middle", "last"], attempted);
        Assert.Contains(later.Message,
            Assert.IsType<string>(thrown.Data[WriteFailureCleanup.CleanupFailureKey]),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Asynchronous_cleanup_attempts_every_step_and_preserves_the_first_failure()
    {
        var attempted = new List<string>();
        var first = new InvalidOperationException("first async cleanup failed");
        var later = new InvalidOperationException("later async cleanup failed");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => ResourceCleanup.RunAllAsync(
        [
            () => Fail("first", first),
            () => Complete("middle"),
            () => Fail("last", later)
        ]).AsTask());

        Assert.Same(first, thrown);
        Assert.Equal(["first", "middle", "last"], attempted);
        Assert.Contains(later.Message,
            Assert.IsType<string>(thrown.Data[WriteFailureCleanup.CleanupFailureKey]),
            StringComparison.Ordinal);

        ValueTask Complete(string name)
        {
            attempted.Add(name);
            return ValueTask.CompletedTask;
        }

        ValueTask Fail(string name, Exception failure)
        {
            attempted.Add(name);
            return ValueTask.FromException(failure);
        }
    }
}
