using Groundwork.Testing;
using Groundwork.Store;
using Groundwork.Diagnostics;

namespace Groundwork.Testing.SelfTests;

public sealed class ExplainAssertTestModeTests
{
    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData(null, false)]
    [InlineData("0", false)]
    [InlineData("yes", false)]
    public void Mode_is_enabled_only_by_the_documented_flag_values(string? value, bool expected) =>
        Assert.Equal(expected, ExplainAssertTestMode.IsEnabled(value));

    [Fact]
    public void Verification_retains_raw_plan_and_distinguishes_free_selection()
    {
        using var directory = new TemporaryDirectory();
        var messages = new List<string>();
        var artifact = ExplainAssertTestMode.Verify(
            "PostgreSQL", "ix_logical", "ix_physical", hinted: false, "RAW PLAN", chosen: true,
            directory.Path, messages.Add);

        Assert.Equal("RAW PLAN", File.ReadAllText(artifact));
        Assert.Contains("optimizer-selected", Assert.Single(messages), StringComparison.Ordinal);
    }

    [Fact]
    public void Misdeclared_index_fails_after_retaining_the_raw_hinted_plan()
    {
        using var directory = new TemporaryDirectory();
        var exception = Assert.Throws<ExplainAssertionException>(() => ExplainAssertTestMode.Verify(
            "SQL Server", "ix_wrong", "ix_wrong_physical", hinted: true, "RAW XML", chosen: false,
            directory.Path, _ => { }));

        Assert.Contains("hinted", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ix_wrong_physical", exception.Message, StringComparison.Ordinal);
        Assert.Equal("RAW XML", File.ReadAllText(exception.ArtifactPath));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory() =>
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "groundwork-explain-tests", Guid.NewGuid().ToString("N"));

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
