using System.IO.Compression;
using System.Reflection.Metadata;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Groundwork.Packaging.Tests;

/// <summary>
/// What a consumer restoring from nuget.org actually receives: the assemblies, the readme, the
/// symbols, and the debugging metadata. Every assertion here reads a real packed artifact, because
/// a project property that was supposed to produce a package layout is not evidence that it did.
/// </summary>
public sealed class PackedArtifactTests(PublicPackageSet packages) : IClassFixture<PublicPackageSet>
{
    /// <summary>The custom debug information kind that carries the Source Link document map.</summary>
    private static readonly Guid SourceLinkDebugInformation = new("CC110556-A091-4D38-9FEC-25AB9A351A6A");

    public static TheoryData<string> PublicPackageIds()
    {
        var data = new TheoryData<string>();
        foreach (var package in PublicPackageSet.Allowlist())
            data.Add(package.PackageId);
        return data;
    }

    [Theory]
    [MemberData(nameof(PublicPackageIds))]
    public void Every_public_package_ships_its_own_readme(string packageId)
    {
        using var package = ZipFile.OpenRead(packages.PackagePath(packageId));

        var nuspec = XDocument.Parse(ReadText(package, $"{packageId}.nuspec"));
        var declared = nuspec.Descendants().Single(element => element.Name.LocalName == "readme").Value;
        Assert.Equal("README.md", declared);

        var shipped = ReadText(package, "README.md");
        Assert.Equal(
            File.ReadAllText(Path.Combine(packages.Root, "docs", "v2", "package-readmes", $"{packageId}.md")),
            shipped);
        // A listing that opens with another package's name is a listing nobody can act on.
        Assert.StartsWith($"# {packageId}\n", shipped.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void No_two_public_packages_share_a_readme()
    {
        var readmes = packages.Packages
            .Select(package => File.ReadAllText(
                Path.Combine(packages.Root, "docs", "v2", "package-readmes", $"{package.PackageId}.md")))
            .ToArray();

        Assert.Equal(readmes.Length, readmes.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [MemberData(nameof(PublicPackageIds))]
    public void Every_public_package_ships_symbols_with_source_link_and_deterministic_paths(string packageId)
    {
        using var symbols = ZipFile.OpenRead(packages.SymbolPackagePath(packageId));
        var portablePdbs = symbols.Entries
            .Where(entry => entry.FullName.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.NotEmpty(portablePdbs);
        foreach (var entry in portablePdbs)
        {
            using var provider = OpenPortablePdb(entry);
            var reader = provider.GetMetadataReader();

            var sourceLink = reader.CustomDebugInformation
                .Select(reader.GetCustomDebugInformation)
                .Where(information => reader.GetGuid(information.Kind) == SourceLinkDebugInformation)
                .Select(information => Encoding.UTF8.GetString(reader.GetBlobBytes(information.Value)))
                .ToArray();
            var map = Assert.Single(sourceLink);
            Assert.Contains("https://raw.githubusercontent.com/valence-works/groundwork-v2/", map, StringComparison.Ordinal);

            // ContinuousIntegrationBuild rewrites the build machine's paths to a repository-relative
            // root. A document still naming a local directory means the package is not reproducible
            // and the Source Link map cannot resolve it.
            var documents = reader.Documents
                .Select(handle => reader.GetString(reader.GetDocument(handle).Name))
                .ToArray();
            Assert.NotEmpty(documents);
            Assert.All(documents, document =>
                Assert.StartsWith("/_/", document, StringComparison.Ordinal));
        }
    }

    [Theory]
    [MemberData(nameof(PublicPackageIds))]
    public void Every_public_package_records_the_commit_it_was_built_from(string packageId)
    {
        using var package = ZipFile.OpenRead(packages.PackagePath(packageId));
        var nuspec = XDocument.Parse(ReadText(package, $"{packageId}.nuspec"));

        var repository = nuspec.Descendants().Single(element => element.Name.LocalName == "repository");
        Assert.Equal("git", repository.Attribute("type")!.Value);
        Assert.Equal("https://github.com/valence-works/groundwork-v2.git", repository.Attribute("url")!.Value);
        Assert.Matches("^[0-9a-f]{40}$", repository.Attribute("commit")?.Value ?? string.Empty);
        Assert.Equal(
            "MIT",
            nuspec.Descendants().Single(element => element.Name.LocalName == "license").Value);
    }

    [Theory]
    [MemberData(nameof(PublicPackageIds))]
    public void Every_public_package_ships_the_frameworks_its_project_declares(string packageId)
    {
        var project = packages.Packages.Single(package => package.PackageId == packageId);
        var expected = DeclaredTargetFrameworks(Path.Combine(packages.Root, project.ProjectPath));

        using var package = ZipFile.OpenRead(packages.PackagePath(packageId));
        var shipped = package.Entries
            .Select(entry => Regex.Match(entry.FullName, @"^(?:lib|tools)/([^/]+)/"))
            .Where(match => match.Success)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(framework => framework, StringComparer.Ordinal)
            .ToArray();

        // Analyzers and source generators ship under analyzers/dotnet/cs with no framework folder at
        // all, so they are exempt from the framework layout rather than exempt from being checked.
        if (shipped.Length == 0)
        {
            Assert.Contains(package.Entries, entry => entry.FullName.StartsWith("analyzers/dotnet/cs/", StringComparison.Ordinal));
            Assert.Equal(new[] { "netstandard2.0" }, expected);
            return;
        }

        Assert.Equal(expected, shipped);
    }

    private static string[] DeclaredTargetFrameworks(string projectPath)
    {
        var project = XDocument.Load(projectPath);
        var declaration = project.Descendants()
            .Single(element => element.Name.LocalName is "TargetFramework" or "TargetFrameworks")
            .Value;
        var properties = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["$(GroundworkRuntimeTargetFrameworks)"] = "net8.0;net10.0",
            ["$(GroundworkBuildTaskTargetFramework)"] = "net10.0"
        };
        foreach (var (property, value) in properties)
            declaration = declaration.Replace(property, value, StringComparison.Ordinal);
        return declaration.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .OrderBy(framework => framework, StringComparer.Ordinal)
            .ToArray();
    }

    private static MetadataReaderProvider OpenPortablePdb(ZipArchiveEntry entry)
    {
        using var compressed = entry.Open();
        var buffer = new MemoryStream();
        compressed.CopyTo(buffer);
        buffer.Position = 0;
        return MetadataReaderProvider.FromPortablePdbStream(buffer);
    }

    private static string ReadText(ZipArchive package, string entryName)
    {
        var entry = package.GetEntry(entryName)
            ?? throw new InvalidOperationException($"The package has no '{entryName}' entry.");
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
