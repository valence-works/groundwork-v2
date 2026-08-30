using System.Diagnostics;
using Xunit;

namespace Groundwork.PublicApi.Acceptance.Tests;

public sealed class PublicApiAcceptanceTests
{
    [Fact]
    public void Clean_room_consumer_runs_from_packed_public_packages()
    {
        var repository = FindRepositoryRoot();
        // A directory of this suite's own: Groundwork.Packaging.Tests reuses whatever it finds at
        // artifacts/packages if the expected files exist there, without checking how they were
        // built. Packing into that same directory here — without ContinuousIntegrationBuild, which
        // this fallback does not set — would hand Packaging.Tests artifacts that fail its Source
        // Link assertions whenever this suite runs first in the same working tree. Always repack:
        // a leftover artifact must never make this acceptance path test a different source tree.
        var packageDirectory = Path.Combine(repository, "artifacts", "acceptance-packages");
        var packScript = Path.Combine(repository, "eng", "pack-public-packages.sh");
        AssertProcessSucceeds(new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = Quote(packScript) + " " + Quote(packageDirectory),
            WorkingDirectory = repository,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        });
        var script = Path.Combine(repository, "tests", "Groundwork.PublicApi.Acceptance.Tests", "verify-clean-room.sh");
        AssertProcessSucceeds(new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = Quote(script),
            WorkingDirectory = repository,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        });
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Groundwork.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the Groundwork repository root.");
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static void AssertProcessSucceeds(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var output = process!.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, output.Result + Environment.NewLine + error.Result);
    }
}
