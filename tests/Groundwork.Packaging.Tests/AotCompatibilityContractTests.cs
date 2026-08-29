using System.Xml.Linq;
using Xunit;

namespace Groundwork.Packaging.Tests;

/// <summary>
/// Native-AOT compatibility is a package and execution contract, not merely a successful managed
/// build. These checks keep the annotated runtime-library set and the exact-head native proof
/// visible in repository metadata.
/// </summary>
public sealed class AotCompatibilityContractTests
{
    private const string RuntimeFrameworks = "$(GroundworkRuntimeTargetFrameworks)";

    [Fact]
    public void Every_multi_target_runtime_library_declares_aot_compatibility()
    {
        var root = RepositoryRoot.Find();
        var offenders = RuntimeLibraryProjects(root)
            .Where(project => !DeclaresAotCompatibility(Path.Combine(root, project)))
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Every multi-target runtime library must declare IsAotCompatible for net8.0 or later:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void Aot_analyzer_warnings_are_errors_for_annotated_projects()
    {
        var targets = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "Directory.Build.targets"));

        Assert.Contains("'$(IsAotCompatible)' == 'true'", targets, StringComparison.Ordinal);
        foreach (var diagnostic in new[] { "IL2026", "IL2055", "IL2060", "IL2072", "IL2087", "IL2090", "IL3050" })
            Assert.Contains(diagnostic, targets, StringComparison.Ordinal);
    }

    [Fact]
    public void Native_aot_workflow_publishes_and_runs_the_exact_head_conformance_executable()
    {
        var root = RepositoryRoot.Find();
        var workflowPath = Path.Combine(root, ".github", "workflows", "aot.yml");
        Assert.True(File.Exists(workflowPath));
        var workflow = File.ReadAllText(workflowPath).ReplaceLineEndings("\n");

        Assert.Contains("name: Native AOT conformance", workflow, StringComparison.Ordinal);
        Assert.Contains("workflow_dispatch:", workflow, StringComparison.Ordinal);
        Assert.Contains("branches: [main]", workflow, StringComparison.Ordinal);
        Assert.Contains("if: ${{ vars.GROUNDWORK_CI_PAUSED != 'true' }}", workflow, StringComparison.Ordinal);
        Assert.Contains("bash eng/verify-exact-head.sh \"$EXPECTED_REF\"", workflow, StringComparison.Ordinal);
        Assert.Contains("eng/pack-public-packages.sh artifacts/aot-packages", workflow, StringComparison.Ordinal);
        Assert.Contains("verify-native-aot.sh artifacts/aot-packages linux-x64", workflow, StringComparison.Ordinal);

        Assert.True(File.Exists(Path.Combine(root, "tests", "Groundwork.Aot.Conformance", "Program.cs")));
        var project = File.ReadAllText(Path.Combine(root, "tests", "Groundwork.Aot.Conformance", "Groundwork.Aot.Conformance.csproj"));
        Assert.Contains("<PackageReference Include=\"Groundwork.Testing\"", project, StringComparison.Ordinal);
        Assert.DoesNotContain("<ProjectReference", project, StringComparison.Ordinal);

        var verification = File.ReadAllText(Path.Combine(root, "tests", "Groundwork.Aot.Conformance", "verify-native-aot.sh"));
        Assert.Contains("-p:PublishAot=true", verification, StringComparison.Ordinal);
        Assert.Contains("--self-contained true", verification, StringComparison.Ordinal);
        Assert.Contains("file \"$binary\"", verification, StringComparison.Ordinal);
        Assert.Contains("\n\"$binary\"\n", verification.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    private static IEnumerable<string> RuntimeLibraryProjects(string root) =>
        File.ReadAllLines(Path.Combine(root, "eng", "public-packages.txt"))
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
            .Select(line => line.Split('|', 2)[1])
            .Where(project =>
            {
                var document = XDocument.Load(Path.Combine(root, project));
                var frameworks = document.Descendants()
                    .Single(element => element.Name.LocalName is "TargetFramework" or "TargetFrameworks")
                    .Value;
                var outputType = document.Descendants("OutputType").SingleOrDefault()?.Value;
                return frameworks == RuntimeFrameworks && !string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase);
            });

    private static bool DeclaresAotCompatibility(string projectPath)
    {
        var declaration = XDocument.Load(projectPath).Descendants("IsAotCompatible").SingleOrDefault();
        return declaration?.Value == "true" &&
               declaration.Attribute("Condition")?.Value.Contains("IsTargetFrameworkCompatible", StringComparison.Ordinal) == true;
    }
}
