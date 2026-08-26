using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Groundwork.Packaging.Tests;

/// <summary>
/// <c>&lt;GroundworkCurrentRelease&gt;</c> in <c>Directory.Build.props</c> is the single source of
/// truth for the most recently published release. This test keeps the repository consistent with
/// it: the declared release must have release notes, the development version must be strictly
/// ahead of it, the clean-room consumer's default package version must name it, and every
/// documentation pin of the current version — package references and <c>--version</c> install
/// commands — must name it exactly. Release notes under <c>docs/v2/releases/</c> are immutable
/// archives and are excluded from the pin scan, so a release bump touches one declaration and
/// this test then points at every stale pin.
/// </summary>
public sealed class VersionIntegrityTests
{
    private static readonly Regex SemanticVersionPattern =
        new(@"^(\d+)\.(\d+)\.(\d+)(?:-([0-9a-z.-]+))?$", RegexOptions.Compiled);

    // A "pin" installs or references packages at the current release, with or without a prerelease
    // suffix. Prose naming a historical release boundary (e.g. the SQLite catalog reset) is not a pin.
    private static readonly Regex DocumentationPinPattern =
        new(@"(?:Version=""|--version\s+)(?<version>\d+\.\d+\.\d+(?:-[0-9a-z.-]+)?)", RegexOptions.Compiled);

    [Fact]
    public void Documentation_and_build_props_agree_on_the_current_release()
    {
        var root = RepositoryRoot.Find();
        var props = XDocument.Load(Path.Combine(root, "Directory.Build.props"));
        var current = RequiredProperty(props, "GroundworkCurrentRelease");
        var prefix = RequiredProperty(props, "VersionPrefix");
        var suffix = Property(props, "VersionSuffix");
        var development = suffix is null ? prefix : $"{prefix}-{suffix}";

        Assert.True(
            File.Exists(Path.Combine(root, "docs", "v2", "releases", current + ".md")),
            $"docs/v2/releases/{current}.md must exist for the declared current release '{current}'.");
        Assert.True(
            Compare(development, current) > 0,
            $"Development version '{development}' must be strictly ahead of the current release '{current}'.");

        var consumer = Path.Combine(root, "tests", "Groundwork.PublicApi.Acceptance.Tests", "Consumer", "Groundwork.PublicApi.Consumer.csproj");
        Assert.True(
            string.Equals(XDocument.Load(consumer).Descendants("GroundworkVersion").Single().Value.Trim(), current, StringComparison.Ordinal),
            $"The clean-room consumer's default GroundworkVersion must be the current release '{current}'.");

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

    /// <summary>SemVer precedence for '<c>major.minor.patch[-prerelease]</c>' versions.</summary>
    private static int Compare(string left, string right)
    {
        var (leftMatch, rightMatch) = (Parse(left), Parse(right));
        for (var group = 1; group <= 3; group++)
        {
            var comparison = int.Parse(leftMatch.Groups[group].Value).CompareTo(int.Parse(rightMatch.Groups[group].Value));
            if (comparison != 0)
                return comparison;
        }

        var (leftPre, rightPre) = (leftMatch.Groups[4], rightMatch.Groups[4]);
        if (!leftPre.Success || !rightPre.Success)
            return (leftPre.Success ? 0 : 1) - (rightPre.Success ? 0 : 1); // a stable version outranks any prerelease

        var leftIds = leftPre.Value.Split('.');
        var rightIds = rightPre.Value.Split('.');
        for (var index = 0; index < Math.Min(leftIds.Length, rightIds.Length); index++)
        {
            var comparison = (long.TryParse(leftIds[index], out var leftNumber), long.TryParse(rightIds[index], out var rightNumber)) switch
            {
                (true, true) => leftNumber.CompareTo(rightNumber),
                (true, false) => -1, // numeric identifiers rank below alphanumeric ones
                (false, true) => 1,
                _ => string.CompareOrdinal(leftIds[index], rightIds[index])
            };
            if (comparison != 0)
                return Math.Sign(comparison);
        }

        return leftIds.Length.CompareTo(rightIds.Length);
    }

    private static Match Parse(string version)
    {
        var match = SemanticVersionPattern.Match(version);
        Assert.True(match.Success, $"Version '{version}' is not a lowercase '<major>.<minor>.<patch>[-prerelease]' version.");
        return match;
    }

    private static string RequiredProperty(XDocument props, string name)
    {
        var value = Property(props, name);
        Assert.False(string.IsNullOrEmpty(value), $"Directory.Build.props must declare a non-empty <{name}>.");
        return value!;
    }

    private static string? Property(XDocument props, string name) =>
        props.Descendants(name).SingleOrDefault()?.Value.Trim();
}
