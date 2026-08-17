using System.Text.Json;
using Xunit;

namespace Groundwork.Docs.Acceptance.Tests;

public sealed class DocumentationContractTests
{
    [Fact]
    public void Api_reference_covers_every_public_package_project()
    {
        var repository = FindRepositoryRoot();
        var allowlist = File.ReadAllLines(Path.Combine(repository, "eng", "public-packages.txt"))
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
            .Select(line => line.Split('|', 2)[1])
            .Select(Path.GetFileNameWithoutExtension)
            .Order(StringComparer.Ordinal)
            .ToArray();

        using var config = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(repository, "docs", "portal", "docfx.json")));
        var documented = config.RootElement
            .GetProperty("metadata")[0]
            .GetProperty("src")[0]
            .GetProperty("files")
            .EnumerateArray()
            .Select(item => Path.GetFileNameWithoutExtension(item.GetString()))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(allowlist, documented);
    }

    [Fact]
    public void Installation_is_feedz_only_for_groundwork_and_pins_exact_previews()
    {
        var repository = FindRepositoryRoot();
        var installation = File.ReadAllText(Path.Combine(
            repository, "docs", "portal", "v0.1", "getting-started", "install.md"));

        Assert.Contains("https://f.feedz.io/valence-works/groundwork/nuget/index.json", installation, StringComparison.Ordinal);
        Assert.Contains("<package pattern=\"Groundwork.*\" />", installation, StringComparison.Ordinal);
        foreach (var command in installation.Split('\n')
                     .Where(line => line.StartsWith("dotnet add package Groundwork.", StringComparison.Ordinal)))
            Assert.Contains("--version 0.1.0-preview.1", command, StringComparison.Ordinal);
    }

    [Fact]
    public void Portal_has_versioned_navigation_search_providers_and_compiled_sample()
    {
        var repository = FindRepositoryRoot();
        var portal = Path.Combine(repository, "docs", "portal");
        var config = File.ReadAllText(Path.Combine(portal, "docfx.json"));
        var quickstart = File.ReadAllText(Path.Combine(
            portal, "v0.1", "getting-started", "quickstart.md"));

        Assert.Contains("\"_enableSearch\": true", config, StringComparison.Ordinal);
        Assert.Contains("\"dest\": \"v0.1/api\"", config, StringComparison.Ordinal);
        Assert.Contains("Groundwork.Samples.Quickstart/Program.cs", quickstart, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(portal, "versions.md")));
        foreach (var provider in new[] { "sqlite.md", "postgresql.md", "sql-server.md", "mongodb.md" })
            Assert.True(File.Exists(Path.Combine(portal, "v0.1", "providers", provider)), provider);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Groundwork.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the Groundwork repository root.");
    }
}
