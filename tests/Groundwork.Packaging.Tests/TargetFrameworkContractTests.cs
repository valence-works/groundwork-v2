using System.Xml.Linq;
using Xunit;

namespace Groundwork.Packaging.Tests;

/// <summary>
/// Groundwork ships three framework groups, and which group a project belongs to is a decision, not
/// a default. These tests keep the decision in one place: a project that writes a literal framework
/// of its own has silently opted out of the set the rest of the product ships, and nothing else
/// would notice.
/// </summary>
public sealed class TargetFrameworkContractTests
{
    private const string RuntimeFrameworks = "$(GroundworkRuntimeTargetFrameworks)";
    private const string BuildTaskFramework = "$(GroundworkBuildTaskTargetFramework)";

    /// <summary>
    /// Analyzers, source generators, and the portable contract assemblies. netstandard2.0 keeps them
    /// loadable by Roslyn and by build hosts, which is a different requirement from the runtime's.
    /// </summary>
    private static readonly string[] PortableProjects =
    [
        "src/Groundwork.Analyzers/Groundwork.Analyzers.csproj",
        "src/Groundwork.Query.Linq/Groundwork.Query.Linq.csproj",
        "src/Groundwork.Query.Model/Groundwork.Query.Model.csproj",
        "src/Groundwork.Query.Planning/Groundwork.Query.Planning.csproj",
        "src/Groundwork.Schema/Groundwork.Schema.csproj",
        "src/Groundwork.Schema.Generator/Groundwork.Schema.Generator.csproj"
    ];

    /// <summary>
    /// The one package whose framework is not about the consumer's application: its task loads into
    /// the SDK's MSBuild process, and Microsoft.Build 18.x does not support net8.0.
    /// </summary>
    private const string BuildTaskProject = "src/Groundwork.SchemaTool.MSBuild/Groundwork.SchemaTool.MSBuild.csproj";

    /// <summary>
    /// Test suites that deliberately run on one framework, each for a reason that is about the
    /// subject under test rather than about convenience. Everything else must exercise every
    /// framework the runtime packages ship, or the multi-targeting is unproven.
    /// </summary>
    private static readonly Dictionary<string, string> SingleTargetSuites = new(StringComparer.Ordinal)
    {
        ["tests/Groundwork.Packaging.Tests/Groundwork.Packaging.Tests.csproj"] =
            "Inspects repository metadata and packed artifacts, which are the same whichever framework reads them.",
        ["tests/Groundwork.SchemaTool.Tests/Groundwork.SchemaTool.Tests.csproj"] =
            "Instantiates GroundworkVerify, the MSBuild task, which exists only on the build-task framework.",
        ["tests/Groundwork.Analyzers.Tests/Groundwork.Analyzers.Tests.csproj"] =
            "Hosts a netstandard2.0 analyzer inside Roslyn; the framework of the test host is not the analyzer's.",
        ["tests/Groundwork.Schema.Generator.Tests/Groundwork.Schema.Generator.Tests.csproj"] =
            "Hosts a netstandard2.0 source generator inside Roslyn, for the same reason.",
        ["tests/Groundwork.PublicApi.Acceptance.Tests/Groundwork.PublicApi.Acceptance.Tests.csproj"] =
            "Drives the clean-room consumer out of process; the consumer's own framework is what matters, "
            + "and verify-clean-room.sh builds and runs it once per shipped framework.",
        ["tests/Groundwork.Samples.EventLog.Tests/Groundwork.Samples.EventLog.Tests.csproj"] =
            "Exercises a sample application, which targets one framework of its own.",
        ["tests/Groundwork.Samples.Api.Tests/Groundwork.Samples.Api.Tests.csproj"] =
            "Drives the ASP.NET Core sample through WebApplicationFactory, which binds the test host to "
            + "the sample's own framework. Running it twice would test ASP.NET Core, not Groundwork.",
        ["tests/Groundwork.Query.Linq.Fragments/Groundwork.Query.Linq.Fragments.csproj"] =
            "A netstandard2.0 compilation fixture, not a test host.",
        ["tests/Groundwork.Documents.External/Groundwork.Documents.External.csproj"] =
            "Builds Groundwork.Documents from a local package feed to prove the package boundary.",
        ["tests/Groundwork.Documents.External/Groundwork.Documents.Source.csproj"] =
            "The source side of that same package boundary proof."
    };

    [Fact]
    public void The_target_framework_sets_are_declared_once_in_the_repository_root()
    {
        var props = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "Directory.Build.props"));

        Assert.Contains("<GroundworkRuntimeTargetFrameworks>net8.0;net10.0</GroundworkRuntimeTargetFrameworks>", props, StringComparison.Ordinal);
        Assert.Contains("<GroundworkBuildTaskTargetFramework>net10.0</GroundworkBuildTaskTargetFramework>", props, StringComparison.Ordinal);
    }

    [Fact]
    public void The_sdk_is_pinned_so_installing_an_older_runtime_cannot_change_which_sdk_builds()
    {
        var global = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "global.json"));

        Assert.Contains("\"version\": \"10.0.", global, StringComparison.Ordinal);
        Assert.Contains("\"rollForward\": \"latestFeature\"", global, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_public_package_project_names_a_shared_framework_set_rather_than_a_literal()
    {
        var root = RepositoryRoot.Find();
        var offenders = new List<string>();

        foreach (var project in PublicProjects(root))
        {
            var declared = DeclaredFrameworks(Path.Combine(root, project));
            var expected = project == BuildTaskProject
                ? BuildTaskFramework
                : PortableProjects.Contains(project, StringComparer.Ordinal)
                    ? "netstandard2.0"
                    : RuntimeFrameworks;
            if (declared != expected)
                offenders.Add($"{project}: expected {expected}, found {declared}");
        }

        Assert.True(offenders.Count == 0,
            "Every public package project declares one of the shared framework sets:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void Analyzers_and_source_generators_stay_netstandard()
    {
        var root = RepositoryRoot.Find();

        Assert.Equal("netstandard2.0", DeclaredFrameworks(Path.Combine(root, "src/Groundwork.Analyzers/Groundwork.Analyzers.csproj")));
        Assert.Equal("netstandard2.0", DeclaredFrameworks(Path.Combine(root, "src/Groundwork.Schema.Generator/Groundwork.Schema.Generator.csproj")));
    }

    [Fact]
    public void Every_test_suite_runs_on_every_shipped_runtime_framework_unless_it_names_a_reason()
    {
        var root = RepositoryRoot.Find();
        var offenders = new List<string>();

        foreach (var project in Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.csproj", SearchOption.AllDirectories)
                     .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            // The clean-room consumer is not a test host: verify-clean-room.sh copies it outside the
            // repository and builds it against packed artifacts, once for every framework the runtime
            // packages ship. It is checked there, not here.
            if (project == "tests/Groundwork.PublicApi.Acceptance.Tests/Consumer/Groundwork.PublicApi.Consumer.csproj")
            {
                Assert.Equal(RuntimeFrameworks.Replace("$(GroundworkRuntimeTargetFrameworks)", "net8.0;net10.0", StringComparison.Ordinal),
                    DeclaredFrameworks(Path.Combine(root, project)));
                continue;
            }

            if (SingleTargetSuites.ContainsKey(project))
            {
                if (DeclaredFrameworks(Path.Combine(root, project)) == RuntimeFrameworks)
                    offenders.Add($"{project} is multi-targeted; remove its single-target exemption.");
                continue;
            }

            var declared = DeclaredFrameworks(Path.Combine(root, project));
            if (declared != RuntimeFrameworks)
                offenders.Add($"{project}: expected {RuntimeFrameworks}, found {declared}");
        }

        Assert.True(offenders.Count == 0,
            "A suite that runs on one framework proves the runtime packages only on that framework. " +
            "Multi-target it, or add it to SingleTargetSuites with the reason:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void Continuous_integration_installs_every_runtime_a_test_host_will_need()
    {
        var root = RepositoryRoot.Find();
        var workflows = Directory.EnumerateFiles(Path.Combine(root, ".github", "workflows"), "*.yml");

        foreach (var workflow in workflows)
        {
            var text = File.ReadAllText(workflow);
            if (!text.Contains("actions/setup-dotnet", StringComparison.Ordinal))
                continue;
            // A net8.0 test host is framework-dependent: on a runner with only .NET 10 it does not
            // start, and the framework the suites are supposed to prove goes untested.
            Assert.DoesNotContain("dotnet-version: 10.0.x", text, StringComparison.Ordinal);
            Assert.Contains("8.0.x", text, StringComparison.Ordinal);
            Assert.Contains("10.0.x", text, StringComparison.Ordinal);
        }
    }

    private static IEnumerable<string> PublicProjects(string root) =>
        File.ReadAllLines(Path.Combine(root, "eng", "public-packages.txt"))
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
            .Select(line => line.Split('|', 2)[1]);

    private static string DeclaredFrameworks(string projectPath) =>
        XDocument.Load(projectPath)
            .Descendants()
            .Single(element => element.Name.LocalName is "TargetFramework" or "TargetFrameworks")
            .Value;
}
