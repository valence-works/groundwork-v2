using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Groundwork.Packaging.Tests;

public sealed class PackagingContractTests
{
    private static readonly string[] ExpectedPackageProjects =
    [
        "src/Groundwork.Analyzers/Groundwork.Analyzers.csproj",
        "src/Groundwork.Diagnostics/Groundwork.Diagnostics.csproj",
        "src/Groundwork.Documents/Groundwork.Documents.csproj",
        "src/Groundwork.EntityFrameworkCore/Groundwork.EntityFrameworkCore.csproj",
        "src/Groundwork.Extensions.DependencyInjection/Groundwork.Extensions.DependencyInjection.csproj",
        "src/Groundwork.Kernel/Groundwork.Kernel.csproj",
        "src/Groundwork.MongoDb/Groundwork.MongoDb.csproj",
        "src/Groundwork.MySql/Groundwork.MySql.csproj",
        "src/Groundwork.PostgreSql/Groundwork.PostgreSql.csproj",
        "src/Groundwork.Query.Linq/Groundwork.Query.Linq.csproj",
        "src/Groundwork.Query.Linq.Execution/Groundwork.Query.Linq.Execution.csproj",
        "src/Groundwork.Query.Linq.Sqlite/Groundwork.Query.Linq.Sqlite.csproj",
        "src/Groundwork.Query.Model/Groundwork.Query.Model.csproj",
        "src/Groundwork.Query.Planning/Groundwork.Query.Planning.csproj",
        "src/Groundwork.Records/Groundwork.Records.csproj",
        "src/Groundwork.Records.Store/Groundwork.Records.Store.csproj",
        "src/Groundwork.Schema/Groundwork.Schema.csproj",
        "src/Groundwork.Schema.Generator/Groundwork.Schema.Generator.csproj",
        "src/Groundwork.SchemaTool/Groundwork.SchemaTool.csproj",
        "src/Groundwork.SchemaTool.MSBuild/Groundwork.SchemaTool.MSBuild.csproj",
        "src/Groundwork.SqlServer/Groundwork.SqlServer.csproj",
        "src/Groundwork.Sqlite/Groundwork.Sqlite.csproj",
        "src/Groundwork.Store/Groundwork.Store.csproj",
        "src/Groundwork.Substrate.Mongo/Groundwork.Substrate.Mongo.csproj",
        "src/Groundwork.Substrate.Relational/Groundwork.Substrate.Relational.csproj",
        "src/Groundwork.Testing/Groundwork.Testing.csproj"
    ];

    [Fact]
    public void Public_package_allowlist_is_explicit_and_excludes_non_release_projects()
    {
        var root = RepositoryRoot.Find();
        var allowlist = File.ReadAllLines(Path.Combine(root, "eng", "public-packages.txt"))
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
            .Select(line => line.Split('|', 2)[1])
            .ToArray();

        Assert.Equal(ExpectedPackageProjects, allowlist);
        Assert.DoesNotContain(allowlist, project => project.Contains("samples/", StringComparison.Ordinal));
        Assert.DoesNotContain(allowlist, project => project.Contains("benchmarks/", StringComparison.Ordinal));
    }

    [Fact]
    public void Release_contract_has_version_source_link_and_symbols()
    {
        var root = RepositoryRoot.Find();
        var props = File.ReadAllText(Path.Combine(root, "Directory.Build.props"));
        Assert.Matches(@"<VersionPrefix>\d+\.\d+\.\d+</VersionPrefix>", props);
        Assert.Matches(@"<VersionSuffix>[0-9a-z.-]+</VersionSuffix>", props);
        Assert.Contains("<PublishRepositoryUrl>true</PublishRepositoryUrl>", props, StringComparison.Ordinal);
        Assert.Contains("<EmbedUntrackedSources>true</EmbedUntrackedSources>", props, StringComparison.Ordinal);
        Assert.Contains("<SymbolPackageFormat>snupkg</SymbolPackageFormat>", props, StringComparison.Ordinal);

        // Source Link and the readme are wired once for every packable project rather than repeated
        // per project. That the wiring actually reaches the artifacts is asserted by
        // PackedArtifactTests against real packed output; a project property is not evidence.
        var targets = File.ReadAllText(Path.Combine(root, "Directory.Build.targets"));
        Assert.Contains("Microsoft.SourceLink.GitHub", targets, StringComparison.Ordinal);
        Assert.Contains("docs/v2/package-readmes/$(PackageId).md", targets, StringComparison.Ordinal);

        Assert.True(File.Exists(Path.Combine(root, "docs/v2/versioning.md")));
        Assert.True(File.Exists(Path.Combine(root, "docs/v2/support-matrix.md")));
        Assert.True(File.Exists(Path.Combine(root, "docs/v2/production-operations.md")));
        Assert.True(File.Exists(Path.Combine(root, ".github/workflows/publish-feedz.yml")));
        Assert.True(File.Exists(Path.Combine(root, ".github/workflows/publish-nuget.yml")));
    }

    [Fact]
    public void Production_support_policy_names_tiers_topologies_ownership_and_runbooks()
    {
        var root = RepositoryRoot.Find();
        var matrix = File.ReadAllText(Path.Combine(root, "docs", "v2", "support-matrix.md"));
        var operations = File.ReadAllText(Path.Combine(root, "docs", "v2", "production-operations.md"));

        Assert.Contains("**Production-supported**", matrix, StringComparison.Ordinal);
        Assert.Contains("**Compatibility-only**", matrix, StringComparison.Ordinal);
        Assert.Contains("**Development/reference-only**", matrix, StringComparison.Ordinal);
        Assert.Contains("one application writer process per database file", matrix, StringComparison.Ordinal);
        Assert.Contains("transaction-capable replica set or sharded cluster", matrix, StringComparison.Ordinal);
        Assert.Contains("Groundwork maintainers", operations, StringComparison.Ordinal);
        Assert.Contains("deployment owner", operations, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("## SQLite: single-writer file", operations, StringComparison.Ordinal);
        Assert.Contains("## PostgreSQL: writable primary", operations, StringComparison.Ordinal);
        Assert.Contains("## SQL Server: writable primary database", operations, StringComparison.Ordinal);
        Assert.Contains("## MySQL/MariaDB: InnoDB writable primary", operations, StringComparison.Ordinal);
        Assert.Contains("## MongoDB: transaction-capable deployment", operations, StringComparison.Ordinal);
    }

    [Fact]
    public void V1_contract_policy_is_enforceable_and_defines_the_final_preview_transition()
    {
        var root = RepositoryRoot.Find();
        var policy = File.ReadAllText(Path.Combine(root, "docs", "v2", "versioning.md"));

        Assert.Contains("## Frozen 1.0 contract", policy, StringComparison.Ordinal);
        Assert.Contains("eng/public-api-v1-net8.0.txt", policy, StringComparison.Ordinal);
        Assert.Contains("eng/public-api-v1-net10.0.txt", policy, StringComparison.Ordinal);
        Assert.Contains("eng/diagnostic-codes-v1.txt", policy, StringComparison.Ordinal);
        Assert.Matches(@"never\s+reassigned or reused during 1\.x", policy);
        Assert.Matches(@"removal waits for the next major\s+version", policy);
        Assert.Contains("## Final preview-to-1.0 transition", policy, StringComparison.Ordinal);
        Assert.Contains("groundwork adopt", policy, StringComparison.Ordinal);
        Assert.Contains("data migration", policy, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("recreate", policy, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Public_tool_identity_is_groundwork_tool_while_project_identity_stays_schema_tool()
    {
        var root = RepositoryRoot.Find();
        var project = XDocument.Load(Path.Combine(root, "src/Groundwork.SchemaTool/Groundwork.SchemaTool.csproj"));
        var packageId = project.Descendants("PackageId").Single().Value;
        Assert.Equal("Groundwork.Tool", packageId);
        Assert.Contains("<ToolCommandName>groundwork</ToolCommandName>", project.ToString(), StringComparison.Ordinal);

        var cli = File.ReadAllText(Path.Combine(root, "src/Groundwork.SchemaTool/GroundworkSchemaCli.cs"));
        Assert.Contains("Groundwork.Tool {version}", cli, StringComparison.Ordinal);
    }

    [Fact]
    public void Publication_workflow_requires_a_release_key_and_never_publishes_unvalidated_packages()
    {
        var root = RepositoryRoot.Find();
        var workflow = File.ReadAllText(Path.Combine(root, ".github/workflows/publish-feedz.yml"));
        Assert.Contains("FEEDZ_API_KEY", workflow, StringComparison.Ordinal);
        Assert.Contains("https://f.feedz.io/valence-works/groundwork/nuget/index.json", workflow, StringComparison.Ordinal);
        Assert.Contains("environment: feedz", workflow, StringComparison.Ordinal);
        Assert.Contains("needs: package", workflow, StringComparison.Ordinal);
        Assert.Contains("verify-package-layout.sh", workflow, StringComparison.Ordinal);
        Assert.Contains("release", workflow, StringComparison.Ordinal);
        Assert.Contains("[0-9a-z.-]+", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("A-Za-z", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("vars.FEEDZ_NUGET_SOURCE", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("NUGET_API_KEY", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("api.nuget.org/v3/index.json", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("find src/Groundwork", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("Groundwork.SchemaTool --version", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Publication_workflow_runs_the_exact_clean_room_proof_after_layout_validation()
    {
        var root = RepositoryRoot.Find();
        var workflow = File.ReadAllText(Path.Combine(root, ".github/workflows/publish-feedz.yml"));
        var layout = workflow.IndexOf("eng/verify-package-layout.sh", StringComparison.Ordinal);
        var cleanRoom = workflow.IndexOf("tests/Groundwork.PublicApi.Acceptance.Tests/verify-clean-room.sh", StringComparison.Ordinal);
        var upload = workflow.IndexOf("actions/upload-artifact@v4", StringComparison.Ordinal);

        Assert.True(layout >= 0);
        Assert.True(cleanRoom > layout);
        Assert.True(upload > cleanRoom);
        Assert.Contains("GROUNDWORK_PUBLIC_API_PACKAGES: artifacts/packages", workflow, StringComparison.Ordinal);
        Assert.Contains("GROUNDWORK_PUBLIC_API_VERSION: ${{ steps.version.outputs.version }}", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Publication_workflow_verifies_the_exact_version_from_feedz_after_push()
    {
        var root = RepositoryRoot.Find();
        var workflow = File.ReadAllText(Path.Combine(root, ".github/workflows/publish-feedz.yml"));
        var push = workflow.IndexOf("dotnet nuget push", StringComparison.Ordinal);
        var verifyJob = workflow.IndexOf("verify-feed:", StringComparison.Ordinal);
        var remoteProof = workflow.IndexOf("eng/verify-published-packages.sh", StringComparison.Ordinal);

        Assert.True(push >= 0);
        Assert.True(verifyJob > push);
        Assert.True(remoteProof > verifyJob);
        Assert.Contains("needs: publish", workflow, StringComparison.Ordinal);
        Assert.Contains("needs.publish.outputs.version", workflow, StringComparison.Ordinal);
        Assert.Contains("\"$FEEDZ_NUGET_SOURCE\" \"$PACKAGE_VERSION\" artifacts/packages", workflow, StringComparison.Ordinal);
        Assert.Contains("Push symbol packages to Feedz with retry", workflow, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(root, "eng", "verify-published-packages.sh")));

        var remoteVerifier = File.ReadAllText(Path.Combine(root, "eng", "verify-published-packages.sh"));
        Assert.Contains(".nupkg.sha512", remoteVerifier, StringComparison.Ordinal);
        Assert.Contains("Artifact hash mismatch", remoteVerifier, StringComparison.Ordinal);

        var packer = File.ReadAllText(Path.Combine(root, "eng", "pack-public-packages.sh"));
        Assert.Contains("-p:PackageVersion=$package_version", packer, StringComparison.Ordinal);
        Assert.Contains("-p:Version=$package_version", packer, StringComparison.Ordinal);

        var cleanRoomVerifier = File.ReadAllText(Path.Combine(root, "tests", "Groundwork.PublicApi.Acceptance.Tests", "verify-clean-room.sh"));
        Assert.Contains("Groundwork.Tool $version", cleanRoomVerifier, StringComparison.Ordinal);
    }

    [Fact]
    public void Nuget_publication_workflow_pins_actions_and_gates_credentials_on_the_package_manifest()
    {
        var root = RepositoryRoot.Find();
        var workflow = File.ReadAllText(Path.Combine(root, ".github/workflows/publish-nuget.yml"));

        Assert.DoesNotContain("actions/setup-dotnet@v4", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("actions/download-artifact@v4", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("actions/checkout@v4", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("actions/upload-artifact@v4", workflow, StringComparison.Ordinal);
        Assert.Equal(3, Regex.Matches(
            workflow,
            "actions/checkout@11d5960a326750d5838078e36cf38b85af677262").Count);
        Assert.Equal(3, Regex.Matches(
            workflow,
            "actions/setup-dotnet@67a3573c9a986a3f9c594539f4ab511d57bb3ce9").Count);
        Assert.Equal(2, Regex.Matches(
            workflow,
            "actions/download-artifact@d3f86a106a0bac45b974a628896c90dbdf5c8093").Count);
        Assert.Single(Regex.Matches(
            workflow,
            "actions/upload-artifact@ea165f8d65b6e75b540449e92b4886f43607fa02"));
        Assert.Contains("# v4.3.1", workflow, StringComparison.Ordinal);
        Assert.Contains("# v4.3.0", workflow, StringComparison.Ordinal);
        Assert.Contains("# v4.4.0", workflow, StringComparison.Ordinal);
        Assert.Contains("# v4.6.2", workflow, StringComparison.Ordinal);

        var manifest = workflow.IndexOf("eng/verify-package-integrity.sh", StringComparison.Ordinal);
        var upload = workflow.IndexOf(
            "actions/upload-artifact@ea165f8d65b6e75b540449e92b4886f43607fa02", StringComparison.Ordinal);
        var manifestDigest = workflow.IndexOf(
            "actual_manifest_sha256=\"$(eng/verify-package-integrity.sh digest artifacts/packages)\"",
            StringComparison.Ordinal);
        var verification = workflow.IndexOf(
            "eng/verify-package-integrity.sh verify artifacts/packages", StringComparison.Ordinal);
        var expectedDigest = workflow.IndexOf(
            "EXPECTED_MANIFEST_SHA256: ${{ needs.package.outputs.manifest_sha256 }}",
            StringComparison.Ordinal);
        var credential = workflow.IndexOf(
            "NUGET_ORG_API_KEY: ${{ secrets.NUGET_API_KEY }}", StringComparison.Ordinal);
        var push = workflow.IndexOf("dotnet nuget push", StringComparison.Ordinal);

        Assert.True(manifest >= 0);
        Assert.True(upload > manifest);
        Assert.True(manifestDigest > upload);
        Assert.True(expectedDigest > upload);
        Assert.True(verification > manifestDigest);
        Assert.True(credential > verification);
        Assert.True(push > verification);
        Assert.Contains("manifest_sha256: ${{ steps.manifest.outputs.sha256 }}", workflow, StringComparison.Ordinal);
        Assert.Contains("needs.package.outputs.manifest_sha256", workflow, StringComparison.Ordinal);
        Assert.Contains("EXPECTED_MANIFEST_SHA256", workflow, StringComparison.Ordinal);
        Assert.Contains("package-sha256sums.txt", File.ReadAllText(
            Path.Combine(root, "eng", "verify-package-integrity.sh")), StringComparison.Ordinal);
    }

}
