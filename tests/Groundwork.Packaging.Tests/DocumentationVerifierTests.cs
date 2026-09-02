using System.Diagnostics;
using Xunit;

namespace Groundwork.Packaging.Tests;

public sealed class DocumentationVerifierTests
{
    [Fact]
    public void Verifier_accepts_scoped_links_anchors_and_a_wired_snippet_fixture()
    {
        using var fixture = Fixture.Create();
        File.WriteAllText(Path.Combine(fixture.Root, "README.md"), "# Root\n\n[Guide](docs/guide.md#guide)\n");
        Directory.CreateDirectory(Path.Combine(fixture.Root, "docs"));
        File.WriteAllText(Path.Combine(fixture.Root, "docs/guide.md"),
            "# Guide\n\n[Root](../README.md#root)\n[Wiki](../wiki-page)\n");
        File.WriteAllText(Path.Combine(fixture.Root, "wiki-page.md"), "# Wiki page\n");
        Directory.CreateDirectory(Path.Combine(fixture.Root, "samples"));
        File.WriteAllText(Path.Combine(fixture.Root, "samples/example.md"),
            "```text\n[ignored](missing.md)\n```\n");
        var manifest = WriteValidManifest(fixture.Root);

        var result = Run(fixture.Root, manifest, offline: true);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Markdown files", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Verifier_reports_source_line_target_and_manifest_contract_failures_deterministically()
    {
        using var fixture = Fixture.Create();
        File.WriteAllText(Path.Combine(fixture.Root, "README.md"), "# Root\n\n[Missing](missing.md#gone)\n");
        var manifest = Path.Combine(fixture.Root, "manifest.json");
        File.WriteAllText(manifest, """
            {
              "version": 1,
              "snippets": [{
                "id": "Bad ID",
                "source": "missing.cs",
                "runner": "missing.sh",
                "workflow": "missing.yml",
                "workflow_job": "bad job",
                "mode": "network",
                "language": "python"
              }]
            }
            """);

        var result = Run(fixture.Root, manifest, offline: true);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("README.md:3", result.Error, StringComparison.Ordinal);
        Assert.Contains("missing.md#gone", result.Error, StringComparison.Ordinal);
        Assert.Contains("manifest snippets[0].id", result.Error, StringComparison.Ordinal);
        Assert.Contains("manifest snippets[0].source", result.Error, StringComparison.Ordinal);
        Assert.Contains("unsupported: 'network'", result.Error, StringComparison.Ordinal);
        Assert.Contains("unsupported: 'python'", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Verifier_enforces_the_versioned_accessible_v2_portal_contract()
    {
        using var fixture = Fixture.Create();
        File.WriteAllText(Path.Combine(fixture.Root, "README.md"), "# Root\n");
        File.WriteAllText(Path.Combine(fixture.Root, "Directory.Build.props"),
            "<Project><PropertyGroup><GroundworkCurrentRelease>0.4.0-preview.5</GroundworkCurrentRelease></PropertyGroup></Project>");
        var manifest = WriteValidManifest(fixture.Root);
        var portal = Directory.CreateDirectory(Path.Combine(fixture.Root, "docs", "wiki")).FullName;
        File.WriteAllText(Path.Combine(portal, "Home.md"), """
            # Home
            Current `0.4.0-preview.5`. Use the wiki **Search** box. On a small screen use this list.
            ## All pages
            Groundwork v1 runtime bridge
            ```
            unlabeled
            ```
            """);
        File.WriteAllText(Path.Combine(portal, "_Footer.md"),
            "0.4.0-preview.5 · [edit portal source](https://example.test/edit) · [report documentation feedback](https://example.test/feedback)\n");
        File.WriteAllText(Path.Combine(portal, "_Sidebar.md"), "**Getting started**\n\n**Reference**\n");
        File.WriteAllText(Path.Combine(portal, "EF-Core-Migration.md"),
            "# Migration\n\nA Groundwork v1 inventory can use a bounded dual-write verification window.\n");

        var result = Run(fixture.Root, manifest, offline: true);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("code fence needs a language", result.Error, StringComparison.Ordinal);
        Assert.Contains("Groundwork v1 compatibility runtime guidance is prohibited", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("EF-Core-Migration.md", result.Error, StringComparison.Ordinal);
    }

    private static string WriteValidManifest(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        var source = Path.Combine(root, "docs", "snippet.cs");
        File.WriteAllText(source, "// snippet\n");
        var runner = Path.Combine(root, "run-snippet.sh");
        File.WriteAllText(runner, "#!/usr/bin/env bash\n# docs/snippet.cs\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(runner, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var workflow = Path.Combine(root, "workflow.yml");
        File.WriteAllText(workflow, "jobs:\n  snippet-job:\n    run: run-snippet.sh\n");
        var manifest = Path.Combine(root, "manifest.json");
        File.WriteAllText(manifest, """
            {
              "version": 1,
              "snippets": [{
                "id": "snippet-example",
                "source": "docs/snippet.cs",
                "runner": "run-snippet.sh",
                "workflow": "workflow.yml",
                "workflow_job": "snippet-job",
                "mode": "local",
                "language": "csharp"
              }]
            }
            """);
        return manifest;
    }

    private static CommandResult Run(string root, string manifest, bool offline)
    {
        var script = Path.Combine(RepositoryRoot.Find(), "eng", "verify-documentation.py");
        var start = new ProcessStartInfo("python3")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(script);
        start.ArgumentList.Add("--root");
        start.ArgumentList.Add(root);
        start.ArgumentList.Add("--manifest");
        start.ArgumentList.Add(manifest);
        if (offline)
            start.ArgumentList.Add("--offline");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start documentation verifier.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return new CommandResult(process.ExitCode, output.Result, error.Result);
    }

    private sealed record CommandResult(int ExitCode, string Output, string Error);

    private sealed class Fixture : IDisposable
    {
        private Fixture(string root) => Root = root;

        internal string Root { get; }

        internal static Fixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "groundwork-docs-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new Fixture(root);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
