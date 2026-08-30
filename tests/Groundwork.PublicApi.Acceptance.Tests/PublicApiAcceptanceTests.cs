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
        var pack = Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = Quote(packScript) + " " + Quote(packageDirectory),
            WorkingDirectory = repository,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        });
        Assert.NotNull(pack);
        var packOutput = pack!.StandardOutput.ReadToEnd();
        var packError = pack.StandardError.ReadToEnd();
        pack.WaitForExit();
        Assert.True(pack.ExitCode == 0, packOutput + Environment.NewLine + packError);
        var script = Path.Combine(repository, "tests", "Groundwork.PublicApi.Acceptance.Tests", "verify-clean-room.sh");
        var result = Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = Quote(script),
            WorkingDirectory = repository,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        });
        Assert.NotNull(result);
        var output = result!.StandardOutput.ReadToEnd();
        var error = result.StandardError.ReadToEnd();
        result.WaitForExit();
        Assert.True(result.ExitCode == 0, output + Environment.NewLine + error);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Groundwork.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the Groundwork repository root.");
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
