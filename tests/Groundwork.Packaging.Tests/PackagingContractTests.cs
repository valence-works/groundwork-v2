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
    public void Native_aot_sample_local_package_sources_exist_in_a_clean_checkout()
    {
        var sampleRoot = Path.Combine(RepositoryRoot.Find(), "samples", "Groundwork.Samples.NativeAotApi");
        var config = XDocument.Load(Path.Combine(sampleRoot, "NuGet.Config"));
        var localSources = config
            .Descendants("packageSources")
            .Elements("add")
            .Select(source => source.Attribute("value")?.Value)
            .OfType<string>()
            .Where(source => !Uri.TryCreate(source, UriKind.Absolute, out _))
            .ToArray();

        Assert.NotEmpty(localSources);
        Assert.All(
            localSources,
            source => Assert.True(
                Directory.Exists(Path.GetFullPath(Path.Combine(sampleRoot, source))),
                $"The local package source '{source}' must exist in a clean checkout."));
    }

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
        Assert.Contains("<GenerateDocumentationFile>true</GenerateDocumentationFile>", targets, StringComparison.Ordinal);
        Assert.Contains("GroundworkPackApiDocumentation", targets, StringComparison.Ordinal);
        Assert.Contains("$(DocumentationFile)", targets, StringComparison.Ordinal);

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
        Assert.Contains("| MySQL 8.4.6 | **Production-supported** |", matrix, StringComparison.Ordinal);
        Assert.Contains("| MariaDB 11.4.13+ | **Compatibility-only** |", matrix, StringComparison.Ordinal);
        Assert.Contains("| MongoDB replica set | **Production-supported** |", matrix, StringComparison.Ordinal);
        Assert.Contains("| MongoDB sharded cluster | **Compatibility-only** |", matrix, StringComparison.Ordinal);
        Assert.DoesNotContain("MySQL/MariaDB | **Production-supported**", matrix, StringComparison.Ordinal);
        Assert.DoesNotContain("replica set or sharded cluster reached", matrix, StringComparison.Ordinal);
        Assert.Contains("Groundwork maintainers", operations, StringComparison.Ordinal);
        Assert.Contains("deployment owner", operations, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("## SQLite: single-writer file", operations, StringComparison.Ordinal);
        Assert.Contains("## PostgreSQL: writable primary", operations, StringComparison.Ordinal);
        Assert.Contains("## SQL Server: writable primary database", operations, StringComparison.Ordinal);
        Assert.Contains("## MySQL 8.4.6: InnoDB writable primary", operations, StringComparison.Ordinal);
        Assert.Contains("## MongoDB: replica-set deployment", operations, StringComparison.Ordinal);
    }

    [Fact]
    public void Documented_mongodb_fixture_has_a_safe_descriptor_budget_and_recovery_guidance()
    {
        var root = RepositoryRoot.Find();
        var testing = File.ReadAllText(Path.Combine(root, "docs", "wiki", "Testing.md"));
        var troubleshooting = File.ReadAllText(Path.Combine(root, "docs", "wiki", "Troubleshooting.md"));
        const string limit = "--ulimit nofile=64000:64000";

        Assert.Contains(limit, testing, StringComparison.Ordinal);
        Assert.Contains(limit, troubleshooting, StringComparison.Ordinal);
        Assert.Contains("docker restart", testing, StringComparison.Ordinal);
        Assert.Contains("Too many open files", troubleshooting, StringComparison.Ordinal);
        Assert.Contains("Restarting that same container preserves the bad limit", troubleshooting, StringComparison.Ordinal);
    }

    [Fact]
    public void Wiki_support_guidance_defers_to_the_canonical_policy_instead_of_repeating_topology_promises()
    {
        var root = RepositoryRoot.Find();
        var canonicalMatrix = "https://github.com/valence-works/groundwork-v2/blob/main/docs/v2/support-matrix.md";
        var canonicalRunbook = "https://github.com/valence-works/groundwork-v2/blob/main/docs/v2/production-operations.md";
        var providers = File.ReadAllText(Path.Combine(root, "docs", "wiki", "Providers.md"));
        var versioning = File.ReadAllText(Path.Combine(root, "docs", "wiki", "Versioning-and-Support.md"));
        var faq = File.ReadAllText(Path.Combine(root, "docs", "wiki", "FAQ.md"));
        var operations = File.ReadAllText(Path.Combine(root, "docs", "wiki", "Production-Operations.md"));

        Assert.Contains(canonicalMatrix, providers, StringComparison.Ordinal);
        Assert.Contains(canonicalMatrix, versioning, StringComparison.Ordinal);
        Assert.Contains(canonicalMatrix, faq, StringComparison.Ordinal);
        Assert.Contains(canonicalRunbook, operations, StringComparison.Ordinal);

        Assert.DoesNotContain("| Provider | Tier | Supported topology |", providers, StringComparison.Ordinal);
        Assert.DoesNotContain("| Component / provider | Tier | Supported topology |", versioning, StringComparison.Ordinal);
        Assert.DoesNotContain("writable-primary MySQL/MariaDB", faq, StringComparison.Ordinal);
        Assert.DoesNotContain("replica set or sharded cluster", faq, StringComparison.Ordinal);
        Assert.DoesNotContain("## SQLite", operations, StringComparison.Ordinal);
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
        Assert.Contains("groundwork\" apply --schema", cleanRoomVerifier, StringComparison.Ordinal);
        Assert.Contains("--provider sqlite", cleanRoomVerifier, StringComparison.Ordinal);
        Assert.Contains("apply-second.json", cleanRoomVerifier, StringComparison.Ordinal);
        Assert.Contains("groundwork\" plan --schema", cleanRoomVerifier, StringComparison.Ordinal);
        Assert.Contains("Usage: groundwork apply", cleanRoomVerifier, StringComparison.Ordinal);
    }

    [Fact]
    public void Documentation_evidence_workflow_is_manual_independent_and_bounded()
    {
        var workflow = File.ReadAllText(Path.Combine(
            RepositoryRoot.Find(), ".github/workflows/documentation-evidence.yml"));
        var triggers = workflow[workflow.IndexOf("\non:", StringComparison.Ordinal)..
                               workflow.IndexOf("\npermissions:", StringComparison.Ordinal)];

        Assert.Contains("workflow_dispatch:", triggers, StringComparison.Ordinal);
        Assert.DoesNotContain("push:", triggers, StringComparison.Ordinal);
        Assert.DoesNotContain("pull_request:", triggers, StringComparison.Ordinal);
        Assert.DoesNotContain("release:", triggers, StringComparison.Ordinal);
        Assert.DoesNotContain("actions/checkout@v4", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("actions/setup-dotnet@v4", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("actions/upload-artifact@v4", workflow, StringComparison.Ordinal);
        Assert.Equal(4, Regex.Matches(workflow,
            "actions/checkout@11d5960a326750d5838078e36cf38b85af677262").Count);
        Assert.Equal(3, Regex.Matches(workflow,
            "actions/setup-dotnet@67a3573c9a986a3f9c594539f4ab511d57bb3ce9").Count);
        Assert.Equal(4, Regex.Matches(workflow,
            "actions/upload-artifact@ea165f8d65b6e75b540449e92b4886f43607fa02").Count);
        Assert.Contains("GroundworkCurrentRelease", workflow, StringComparison.Ordinal);
        Assert.Contains("portal-product:", workflow, StringComparison.Ordinal);
        Assert.Contains("sample-provider-matrix:", workflow, StringComparison.Ordinal);
        Assert.Contains("feedz-clean-room:", workflow, StringComparison.Ordinal);
        Assert.Contains("newcomer-sqlite:", workflow, StringComparison.Ordinal);
        foreach (var provider in new[] { "sqlite", "postgresql", "sqlserver", "mongodb", "mysql", "inmemory" })
            Assert.Contains(provider, workflow, StringComparison.Ordinal);
        Assert.Contains("expected executed passing tests only", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("needs: feedz-clean-room", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("needs: newcomer-sqlite", workflow, StringComparison.Ordinal);
        Assert.True(Regex.Matches(workflow, "if: always\\(\\)").Count >= 2);
        Assert.Equal(4, Regex.Matches(workflow, "retention-days: 7").Count);
        Assert.Contains("verify-published-portal.py", workflow, StringComparison.Ordinal);
        Assert.Contains("verify-published-packages.sh \"$FEEDZ_NUGET_SOURCE\" \"$VERSION\"", workflow, StringComparison.Ordinal);
        Assert.Contains("GROUNDWORK_PUBLIC_API_REMOTE_ONLY: \"true\"", workflow, StringComparison.Ordinal);
        Assert.Contains("verify-newcomer-sqlite.sh \"$FEEDZ_NUGET_SOURCE\" \"$version\"", workflow, StringComparison.Ordinal);
        Assert.Contains("if: always()\n        env:\n          VERSION: ${{ steps.version.outputs.version }}\n          INPUT_VERSION: ${{ inputs.version }}", workflow, StringComparison.Ordinal);
        Assert.Contains("evidence-manifest.md", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("package-verification.log", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("clean-room.log", workflow, StringComparison.Ordinal);
        Assert.Contains("artifacts/docs-evidence/newcomer/report.md", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Documentation_evidence_scripts_preserve_local_hash_mode_and_support_remote_mode()
    {
        var root = RepositoryRoot.Find();
        var published = File.ReadAllText(Path.Combine(root, "eng/verify-published-packages.sh"));
        Assert.Contains("[expected-package-directory]", published, StringComparison.Ordinal);
        Assert.Contains("[[ \"$expected_packages\" == \"-\" ]] && expected_packages=\"\"", published, StringComparison.Ordinal);
        Assert.Contains("[[ -n \"$expected_packages\" ]] || return 0", published, StringComparison.Ordinal);
        Assert.Contains("Artifact hash mismatch", published, StringComparison.Ordinal);

        var cleanRoom = File.ReadAllText(Path.Combine(
            root, "tests/Groundwork.PublicApi.Acceptance.Tests/verify-clean-room.sh"));
        Assert.Contains("GROUNDWORK_PUBLIC_API_REMOTE_ONLY", cleanRoom, StringComparison.Ordinal);
        Assert.Contains("GROUNDWORK_PUBLIC_API_FEEDZ_SOURCE", cleanRoom, StringComparison.Ordinal);
        Assert.Contains("GroundworkCurrentRelease", cleanRoom, StringComparison.Ordinal);
        Assert.Contains("Remote clean-room version", cleanRoom, StringComparison.Ordinal);
        Assert.Contains("groundwork-local|value=", cleanRoom, StringComparison.Ordinal);

        var newcomer = File.ReadAllText(Path.Combine(root, "eng/verify-newcomer-sqlite.sh"));
        Assert.Contains("GroundworkCurrentRelease", newcomer, StringComparison.Ordinal);
        Assert.Contains("index-covered customer query", newcomer, StringComparison.Ordinal);
        Assert.Contains("declared customer aggregation", newcomer, StringComparison.Ordinal);
        Assert.Contains("Groundwork newcomer SQLite evidence", newcomer, StringComparison.Ordinal);
        Assert.Contains("groundwork-feedz", newcomer, StringComparison.Ordinal);
        Assert.Contains("Published portal snapshot SHA-256", newcomer, StringComparison.Ordinal);
        Assert.Contains("raw.githubusercontent.com/valence-works/groundwork-v2/$source_sha", newcomer, StringComparison.Ordinal);
        Assert.Contains("## Commands", newcomer, StringComparison.Ordinal);
        Assert.Contains("Started (UTC)", newcomer, StringComparison.Ordinal);
        Assert.Contains("Checkout SHA", newcomer, StringComparison.Ordinal);
        Assert.Contains("raw command output are intentionally not retained", newcomer, StringComparison.Ordinal);
    }

    [Fact]
    public void Publication_workflows_gate_and_retain_the_exact_package_api_reference()
    {
        var root = RepositoryRoot.Find();
        foreach (var workflowName in new[] { "publish-feedz.yml", "publish-nuget.yml" })
        {
            var workflow = File.ReadAllText(Path.Combine(root, ".github/workflows", workflowName));
            var layout = workflow.IndexOf("verify-package-layout.sh", StringComparison.Ordinal);
            var apiReference = workflow.IndexOf("verify-api-reference.py", StringComparison.Ordinal);
            Assert.True(layout >= 0 && apiReference > layout, $"{workflowName} must validate package layout before API references.");
            Assert.Contains("artifacts/packages \"$PACKAGE_VERSION\" artifacts/api-reference", workflow, StringComparison.Ordinal);
            Assert.Contains("api-reference", workflow, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Documentation_verification_is_path_scoped_and_does_not_run_solution_lanes()
    {
        var root = RepositoryRoot.Find();
        var workflow = File.ReadAllText(Path.Combine(root, ".github/workflows/documentation-verification.yml"));
        var triggers = workflow[workflow.IndexOf("\non:", StringComparison.Ordinal)..
                               workflow.IndexOf("\npermissions:", StringComparison.Ordinal)];

        Assert.Contains("push:", triggers, StringComparison.Ordinal);
        Assert.Contains("pull_request:", triggers, StringComparison.Ordinal);
        foreach (var path in new[]
        {
            "README.md", "docs/**", "samples/**", "eng/verify-documentation.py",
            "eng/generate-provider-matrices.sh", "eng/provider-matrix/**", "eng/verify-api-reference.py",
            "eng/api-documentation-baseline.json",
            ".github/workflows/documentation-evidence.yml"
        })
            Assert.Contains(path, triggers, StringComparison.Ordinal);
        Assert.Contains("python3 eng/verify-documentation.py", workflow, StringComparison.Ordinal);
        Assert.Contains("bash eng/generate-provider-matrices.sh check", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet test", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("Groundwork.slnx", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("concurrency.yml", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("performance.yml", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("actions/checkout@v4", workflow, StringComparison.Ordinal);
        Assert.Contains("actions/checkout@11d5960a326750d5838078e36cf38b85af677262", workflow, StringComparison.Ordinal);
        Assert.Contains("actions/setup-dotnet@67a3573c9a986a3f9c594539f4ab511d57bb3ce9", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet-version: |", workflow, StringComparison.Ordinal);
        Assert.Contains("8.0.x", workflow, StringComparison.Ordinal);
        Assert.Contains("10.0.x", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Executable_snippet_manifest_wires_the_first_public_fixture()
    {
        var root = RepositoryRoot.Find();
        var manifest = File.ReadAllText(Path.Combine(root, "docs/v2/executable-snippets.json"));

        Assert.Contains("\"id\": \"newcomer-sqlite\"", manifest, StringComparison.Ordinal);
        Assert.Contains("\"source\": \"docs/v2/newcomer-sqlite/Program.cs\"", manifest, StringComparison.Ordinal);
        Assert.Contains("\"runner\": \"eng/verify-newcomer-sqlite.sh\"", manifest, StringComparison.Ordinal);
        Assert.Contains("\"mode\": \"feedz\"", manifest, StringComparison.Ordinal);
        Assert.Contains("\"language\": \"csharp\"", manifest, StringComparison.Ordinal);
        Assert.Contains("executable-snippets.json", File.ReadAllText(Path.Combine(root, "eng/verify-documentation.py")), StringComparison.Ordinal);
        var verifier = File.ReadAllText(Path.Combine(root, "eng/verify-documentation.py"));
        Assert.Contains("urlopen", verifier, StringComparison.Ordinal);
        Assert.Contains("GET_ONLY_HOSTS", verifier, StringComparison.Ordinal);
        Assert.Contains("RETRYABLE_STATUS_CODES", verifier, StringComparison.Ordinal);
        Assert.Contains("--offline", verifier, StringComparison.Ordinal);
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
        Assert.Equal(2, Regex.Matches(
            workflow,
            "actions/upload-artifact@ea165f8d65b6e75b540449e92b4886f43607fa02").Count);
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
