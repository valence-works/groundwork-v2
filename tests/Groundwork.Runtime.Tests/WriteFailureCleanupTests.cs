using Groundwork.Store;
using Xunit;

namespace Groundwork.Runtime.Tests;

/// <summary>
/// Cleanup must not lose information. A rollback or a disposal that fails while a write failure is
/// already in flight has to be recorded rather than raised, and a step that fails must not take the
/// steps after it down with it — releasing a connection is not optional because disposing its
/// transaction threw. A connection abandoned mid-transaction returns to the driver's pool carrying
/// that state, and the next caller to open it meets a refusal about something it never asked for.
/// </summary>
public sealed class WriteFailureCleanupTests
{
    [Fact]
    public void A_failing_cleanup_step_does_not_stop_the_steps_after_it()
    {
        var ran = new List<string>();

        var thrown = Assert.Throws<InvalidOperationException>(() => WriteFailureCleanup.RunAll(
            () => { ran.Add("sessions"); throw new InvalidOperationException("closing the sessions failed"); },
            () => { ran.Add("transaction"); throw new InvalidOperationException("disposing the transaction failed"); },
            () => ran.Add("connection")));

        // The connection step is the one that matters: it runs last and both steps before it threw.
        Assert.Equal(["sessions", "transaction", "connection"], ran);
        Assert.Equal("closing the sessions failed", thrown.Message);
        Assert.Contains(
            "disposing the transaction failed",
            Assert.IsType<string>(thrown.Data[WriteFailureCleanup.CleanupFailureKey]),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Every_later_failure_is_recorded_rather_than_the_last_one_overwriting_the_rest()
    {
        var thrown = Assert.Throws<InvalidOperationException>(() => WriteFailureCleanup.RunAll(
            () => throw new InvalidOperationException("first"),
            () => throw new InvalidOperationException("second"),
            () => throw new InvalidOperationException("third")));

        var recorded = Assert.IsType<string>(thrown.Data[WriteFailureCleanup.CleanupFailureKey]);
        Assert.Contains("second", recorded, StringComparison.Ordinal);
        Assert.Contains("third", recorded, StringComparison.Ordinal);
    }

    [Fact]
    public void The_thrown_step_failure_keeps_the_stack_it_was_thrown_from()
    {
        var thrown = Assert.Throws<InvalidOperationException>(
            () => WriteFailureCleanup.RunAll(ThrowFromAKnownFrame));

        // Rethrowing with `throw first` would reset this to RunAll's own line and lose the frame
        // that says which disposal actually failed.
        Assert.Contains(nameof(ThrowFromAKnownFrame), thrown.StackTrace, StringComparison.Ordinal);
    }

    [Fact]
    public void Steps_that_all_succeed_throw_nothing()
    {
        var ran = 0;
        WriteFailureCleanup.RunAll(() => ran++, () => ran++);
        Assert.Equal(2, ran);
    }

    [Fact]
    public void A_cleanup_that_fails_under_an_original_failure_is_recorded_against_it()
    {
        var original = new InvalidOperationException("the commit failed");

        // RunAll throws, Run catches: the caller still gets the commit failure it has to act on.
        WriteFailureCleanup.Run(original, () => WriteFailureCleanup.RunAll(
            () => throw new InvalidOperationException("the rollback failed too")));

        Assert.Contains(
            "the rollback failed too",
            Assert.IsType<string>(original.Data[WriteFailureCleanup.CleanupFailureKey]),
            StringComparison.Ordinal);
    }

    private static void ThrowFromAKnownFrame() => throw new InvalidOperationException("from a known frame");
}
