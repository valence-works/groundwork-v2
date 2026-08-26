using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Groundwork.Packaging.Tests;

/// <summary>
/// <c>&lt;GroundworkCurrentRelease&gt;</c> in <c>Directory.Build.props</c> is the single source of
/// truth for the most recently published release. This test keeps the repository consistent with
/// it: the declared release must have release notes, the development version must be strictly
/// ahead of it, and every documentation pin of the current version — package references and
/// <c>--version</c> install commands — must name it exactly. Release notes under
/// <c>docs/v2/releases/</c> are immutable archives and are excluded from the pin scan, so a
/// release bump touches one declaration and this test then points at every stale pin.
/// </summary>
public sealed class VersionIntegrityTests
{
    private static readonly Regex ReleaseVersionPattern =
        new(@"^(\d+)\.(\d+)\.(\d+)-preview\.(\d+)$", RegexOptions.Compiled);

    // A "pin" installs or references packages at the current release. Prose naming a historical
    // release boundary (e.g. the SQLite catalog reset) is not a pin.
    private static readonly Regex DocumentationPinPattern =
        new(@"(?:Version=""|--version\s+)(?<version>\d+\.\d+\.\d+-[0-9a-z.]+)", RegexOptions.Compiled);

    [Fact]
    public void Documentation_and_build_props_agree_on_the_current_release()
    {
        var root = FindRepositoryRoot();
        var props = XDocument.Load(Path.Combine(root, "Directory.Build.props"));
        var current = Property(props, "GroundworkCurrentRelease");
        var development = $"{Property(props, "VersionPrefix")}-{Property(props, "VersionSuffix")}";

        Assert.True(
            File.Exists(Path.Combine(root, "docs", "v2", "releases", current + ".md")),
            $"docs/v2/releases/{current}.md must exist for the declared current release '{current}'.");
        Assert.True(
            Order(development) > Order(current),
            $"Development version '{development}' must be strictly ahead of the current release '{current}'.");

        var pins = DocumentationFiles(root)
            .SelectMany(file => File.ReadLines(file).Select((line, index) => (file, line, index)))
            .SelectMany(entry => DocumentationPinPattern.Matches(entry.line).Select(match =>
                (Location: $"{Path.GetRelativePath(root, entry.file)}:{entry.index + 1}",
                 Version: match.Groups["version"].Value)))
            .ToArray();

        Assert.True(pins.Length > 0, "No documentation version pins were found; the pin pattern no longer matches anything and this check has gone blind.");
        Assert.True(
            pins.All(pin => pin.Version == current),
            string.Join(Environment.NewLine, pins.Where(pin => pin.Version != current)
                .Select(pin => $"{pin.Location} pins '{pin.Version}' but the current release is '{current}'.")));
    }

    private static IEnumerable<string> DocumentationFiles(string root)
    {
        yield return Path.Combine(root, "README.md");

        var releases = Path.Combine(root, "docs", "v2", "releases") + Path.DirectorySeparatorChar;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md", SearchOption.AllDirectories))
        {
            if (!file.StartsWith(releases, StringComparison.Ordinal))
                yield return file;
        }
    }

    /// <summary>Orders '<c>major.minor.patch-preview.n</c>' versions numerically.</summary>
    private static long Order(string version)
    {
        var match = ReleaseVersionPattern.Match(version);
        Assert.True(match.Success, $"Version '{version}' does not match the required '<major>.<minor>.<patch>-preview.<n>' format.");
        return match.Groups.Cast<Group>().Skip(1)
            .Aggregate(0L, (order, group) => order * 100_000 + long.Parse(group.Value));
    }

    private static string Property(XDocument props, string name)
    {
        var value = props.Descendants(name).Single().Value.Trim();
        Assert.False(string.IsNullOrEmpty(value), $"Directory.Build.props must declare a non-empty <{name}>.");
        return value;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Groundwork.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the Groundwork repository root.");
    }
}
