using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Groundwork.Packaging.Tests;

/// <summary>
/// The allowlisted public packages, and the one packed output every artifact assertion reads.
/// </summary>
/// <remarks>
/// The layering, readme, symbol, and Source Link expectations are claims about what consumers
/// actually receive, so they are asserted against real packed artifacts rather than against the
/// project files that were supposed to produce them. Packing is expensive, so it happens once for
/// the whole suite and reuses artifacts already packed at this version if there are any.
/// </remarks>
public sealed class PublicPackageSet
{
    public sealed record PublicPackage(string PackageId, string ProjectPath);

    public PublicPackageSet()
    {
        Root = RepositoryRoot.Find();
        Packages = Allowlist();
        Version = ReadVersion(Path.Combine(Root, "Directory.Build.props"));
        Directory = PackOnce();
    }

    /// <summary>
    /// The allowlist alone, without packing anything. Test discovery reads this to name its cases,
    /// which must not drag the pack along with it.
    /// </summary>
    public static IReadOnlyList<PublicPackage> Allowlist() =>
        File.ReadAllLines(Path.Combine(RepositoryRoot.Find(), "eng", "public-packages.txt"))
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
            .Select(line => line.Split('|', 2))
            .Select(parts => new PublicPackage(parts[0], parts[1]))
            .ToArray();

    public string Root { get; }

    public string Version { get; }

    public string Directory { get; }

    public IReadOnlyList<PublicPackage> Packages { get; }

    public string PackagePath(string packageId) => Path.Combine(Directory, $"{packageId}.{Version}.nupkg");

    public string SymbolPackagePath(string packageId) => Path.Combine(Directory, $"{packageId}.{Version}.snupkg");

    private static string ReadVersion(string propsPath)
    {
        var props = File.ReadAllText(propsPath);
        var prefix = Match(props, "VersionPrefix");
        var suffix = Match(props, "VersionSuffix");
        return string.IsNullOrEmpty(suffix) ? prefix : $"{prefix}-{suffix}";

        static string Match(string text, string element)
        {
            var match = Regex.Match(text, $"<{element}>([^<]*)</{element}>");
            return match.Success ? match.Groups[1].Value : string.Empty;
        }
    }

    private string PackOnce()
    {
        // A release run has already packed the allowlist by the time these assertions matter, so
        // read those artifacts rather than spending a minute reproducing them.
        var released = Path.Combine(Root, "artifacts", "packages");
        if (IsComplete(released))
            return released;

        // Otherwise pack into a directory of this suite's own. eng/pack-public-packages.sh clears
        // its output first, and Groundwork.PublicApi.Acceptance.Tests packs into artifacts/packages,
        // so sharing that directory would let either suite delete the other's artifacts mid-run.
        var own = Path.Combine(Root, "artifacts", "packaging-tests");
        if (IsComplete(own))
            return own;

        Run("dotnet", "restore Groundwork.slnx --nologo -m:1 -nodeReuse:false");
        Run("/bin/bash", $"eng/pack-public-packages.sh {own}");
        return own;
    }

    private bool IsComplete(string directory) =>
        Packages.All(package => File.Exists(Path.Combine(directory, $"{package.PackageId}.{Version}.nupkg")) &&
                                File.Exists(Path.Combine(directory, $"{package.PackageId}.{Version}.snupkg")));

    private void Run(string fileName, string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        // Reused MSBuild worker nodes outlive the command that started them and keep the inherited
        // pipe handles open, so reading to end would block long after the build finished. One
        // in-process node, not reused, keeps the redirected streams closing when the child exits.
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        startInfo.Environment["MSBUILDNODECONNECTIONTIMEOUT"] = "1000";

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"'{fileName} {arguments}' failed:{Environment.NewLine}{output}{Environment.NewLine}{error}");
    }
}
