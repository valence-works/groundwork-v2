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
        "src/Groundwork.Kernel/Groundwork.Kernel.csproj",
        "src/Groundwork.MongoDb/Groundwork.MongoDb.csproj",
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
        Assert.Contains("Microsoft.SourceLink.GitHub", File.ReadAllText(Path.Combine(root, "Directory.Packages.props")), StringComparison.Ordinal);

        Assert.True(File.Exists(Path.Combine(root, "docs/v2/versioning.md")));
        Assert.True(File.Exists(Path.Combine(root, "docs/v2/support-matrix.md")));
        Assert.True(File.Exists(Path.Combine(root, ".github/workflows/publish-feedz.yml")));
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

}
