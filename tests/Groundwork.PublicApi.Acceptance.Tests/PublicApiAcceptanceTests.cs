using System.Diagnostics;
using Xunit;

namespace Groundwork.PublicApi.Acceptance.Tests;

public sealed class PublicApiAcceptanceTests
{
    [Fact]
    public void Clean_room_consumer_runs_from_packed_public_packages()
    {
        var repository = FindRepositoryRoot();
        var packageDirectory = Path.Combine(repository, "artifacts", "packages");
        if (!Directory.Exists(packageDirectory) || !Directory.EnumerateFiles(packageDirectory, "Groundwork.Documents.*.nupkg").Any())
        {
            var pack = Process.Start(new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "pack Groundwork.slnx --configuration Release --output artifacts/packages --nologo -m:1 -v:q",
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
        }
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
