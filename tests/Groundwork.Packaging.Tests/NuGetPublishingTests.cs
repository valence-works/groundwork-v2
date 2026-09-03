using System.Text.RegularExpressions;
using Xunit;

namespace Groundwork.Packaging.Tests;

/// <summary>
/// The nuget.org pipeline exists so a release can be published; it must not be able to publish
/// anything on its own. A version on nuget.org is never replaced, so an accidental push is not a
/// mistake that can be corrected — only one that can be listed as unlisted.
/// </summary>
public sealed class NuGetPublishingTests
{
    private static string Workflow() =>
        File.ReadAllText(Path.Combine(RepositoryRoot.Find(), ".github/workflows/publish-nuget.yml"));

    [Fact]
    public void The_nuget_workflow_is_manual_only_and_cannot_be_started_by_repository_activity()
    {
        var workflow = Workflow();
        var triggers = workflow[workflow.IndexOf("\non:", StringComparison.Ordinal)..workflow.IndexOf("\npermissions:", StringComparison.Ordinal)];
        var triggerKeys = Regex.Matches(triggers, @"(?m)^  ([a-z][a-z0-9_-]*):")
            .Select(match => match.Groups[1].Value)
            .ToArray();

        Assert.Equal(new[] { "workflow_dispatch" }, triggerKeys);
    }

    [Fact]
    public void Publishing_is_gated_on_an_explicit_release_a_typed_confirmation_and_a_protected_environment()
    {
        var workflow = Workflow();

        Assert.Contains(
            "if: github.event_name == 'workflow_dispatch' && inputs.publish == true",
            workflow.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
        Assert.Contains("environment: nuget-org", workflow, StringComparison.Ordinal);
        Assert.Contains("default: false", workflow, StringComparison.Ordinal);
        // A manual publish must retype the exact version, so a stray click cannot ship a release.
        Assert.Contains("[[ \"$CONFIRM\" == \"$PACKAGE_VERSION\" ]] ||", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_is_pushed_before_the_full_validation_chain_has_run()
    {
        var workflow = Workflow();
        var tests = workflow.IndexOf("dotnet test Groundwork.slnx", StringComparison.Ordinal);
        var pack = workflow.IndexOf("eng/pack-public-packages.sh", StringComparison.Ordinal);
        var layout = workflow.IndexOf("eng/verify-package-layout.sh", StringComparison.Ordinal);
        var cleanRoom = workflow.IndexOf("verify-clean-room.sh", StringComparison.Ordinal);
        var push = workflow.IndexOf("dotnet nuget push", StringComparison.Ordinal);

        Assert.True(tests >= 0 && pack > tests && layout > pack && cleanRoom > layout && push > cleanRoom,
            "The publish step must come after the tests, the pack, the layout validation, and the clean-room proof.");
        Assert.Contains("needs: package", workflow, StringComparison.Ordinal);
        Assert.Contains("needs: publish", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void The_publish_step_stops_when_no_credential_is_configured()
    {
        var workflow = Workflow();

        Assert.Contains("secrets.NUGET_API_KEY", workflow, StringComparison.Ordinal);
        Assert.Contains("test -n \"$NUGET_ORG_API_KEY\" ||", workflow, StringComparison.Ordinal);
        Assert.Contains("Add a NUGET_API_KEY secret", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void The_feedz_preview_channel_is_release_or_manual_only()
    {
        var root = RepositoryRoot.Find();
        var feedz = File.ReadAllText(Path.Combine(root, ".github/workflows/publish-feedz.yml"));

        // The two channels stay separate: Feedz publishes on a published release or an explicit
        // manual dispatch, while nuget.org remains manual-only.
        var triggers = feedz[feedz.IndexOf("\non:", StringComparison.Ordinal)..feedz.IndexOf("\npermissions:", StringComparison.Ordinal)];
        var triggerKeys = Regex.Matches(triggers, @"(?m)^  ([a-z][a-z0-9_-]*):")
            .Select(match => match.Groups[1].Value)
            .ToArray();

        Assert.Equal(new[] { "release", "workflow_dispatch" }, triggerKeys);
        Assert.Contains("release:\n    types: [published]", feedz, StringComparison.Ordinal);
        Assert.Contains("workflow_dispatch:", feedz, StringComparison.Ordinal);
        Assert.Contains("https://f.feedz.io/valence-works/groundwork/nuget/index.json", feedz, StringComparison.Ordinal);
        Assert.DoesNotContain("api.nuget.org", feedz, StringComparison.Ordinal);
        Assert.DoesNotContain("push:", Workflow(), StringComparison.Ordinal);
    }
}
