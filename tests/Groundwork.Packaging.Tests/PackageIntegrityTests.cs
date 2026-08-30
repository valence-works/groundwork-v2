using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace Groundwork.Packaging.Tests;

public sealed class PackageIntegrityTests
{
    [Fact]
    public void Manifest_verification_refuses_tampered_missing_and_extra_packages()
    {
        var root = RepositoryRoot.Find();
        var script = Path.Combine(root, "eng", "verify-package-integrity.sh");
        var packages = Path.Combine(Path.GetTempPath(), "groundwork-package-integrity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packages);
        try
        {
            var package = Path.Combine(packages, "Groundwork.Sample.1.2.3.nupkg");
            var symbols = Path.Combine(packages, "Groundwork.Sample.1.2.3.snupkg");
            File.WriteAllText(package, "package");
            File.WriteAllText(symbols, "symbols");

            AssertSuccess(Run(script, "create", packages, "1.2.3"));
            AssertSuccess(Run(script, "verify", packages, "1.2.3"));
            var digest = Run(script, "digest", packages);
            AssertSuccess(digest);
            Assert.Matches(new Regex("^[0-9a-f]{64}\\n?$", RegexOptions.CultureInvariant), digest.Output);

            File.AppendAllText(package, "tampered");
            AssertFailure(Run(script, "verify", packages, "1.2.3"), "digest mismatch");

            AssertSuccess(Run(script, "create", packages, "1.2.3"));
            var extra = Path.Combine(packages, "Groundwork.Extra.1.2.3.nupkg");
            File.WriteAllText(extra, "extra");
            AssertFailure(Run(script, "verify", packages, "1.2.3"), "does not exactly match");

            File.Delete(extra);
            File.Delete(symbols);
            AssertFailure(Run(script, "verify", packages, "1.2.3"), "does not exactly match");
        }
        finally
        {
            Directory.Delete(packages, recursive: true);
        }
    }

    [Fact]
    public void Manifest_verification_refuses_package_symlinks()
    {
        var root = RepositoryRoot.Find();
        var script = Path.Combine(root, "eng", "verify-package-integrity.sh");
        var packages = Path.Combine(Path.GetTempPath(), "groundwork-package-integrity-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(Path.GetTempPath(), "groundwork-package-integrity-target-" + Guid.NewGuid().ToString("N") + ".nupkg");
        Directory.CreateDirectory(packages);
        try
        {
            File.WriteAllText(Path.Combine(packages, "Groundwork.Sample.1.2.3.nupkg"), "package");
            AssertSuccess(Run(script, "create", packages, "1.2.3"));

            var symlink = Path.Combine(packages, "Groundwork.Extra.1.2.3.nupkg");
            File.WriteAllText(target, "external package");
            try
            {
                File.CreateSymbolicLink(symlink, target);
            }
            catch (PlatformNotSupportedException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            AssertFailure(Run(script, "verify", packages, "1.2.3"), "not a regular file");
        }
        finally
        {
            File.Delete(target);
            Directory.Delete(packages, recursive: true);
        }
    }

    private static CommandResult Run(string script, params string[] arguments)
    {
        var start = new ProcessStartInfo("/bin/bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(script);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start package integrity verifier.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return new CommandResult(process.ExitCode, output.Result, error.Result);
    }

    private static void AssertSuccess(CommandResult result) =>
        Assert.True(result.ExitCode == 0, result.Output + Environment.NewLine + result.Error);

    private static void AssertFailure(CommandResult result, string message)
    {
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(message, result.Error, StringComparison.Ordinal);
    }

    private sealed record CommandResult(int ExitCode, string Output, string Error);
}
