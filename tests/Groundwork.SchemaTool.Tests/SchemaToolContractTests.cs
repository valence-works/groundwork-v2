using System.Text.Json;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Schema;
using Groundwork.SchemaTool.MSBuild;
using Microsoft.Build.Framework;
using Xunit;

namespace Groundwork.SchemaTool.Tests;

public sealed class SchemaToolContractTests
{
    private const string ValidSchema = """
        {"tables":[{"name":"tickets","columns":[{"name":"id","type":"String","nullable":false,"length":64,"precision":null,"scale":null,"folding":"None","generation":"Supplied"}],"key":["id"],"indexes":[]}]}
        """;

    [Fact]
    public async Task Help_version_and_invalid_invocation_have_stable_exit_codes()
    {
        Assert.Equal(SchemaToolExitCodes.Success, await RunAsync(["--help"]));
        Assert.Contains("Usage: groundwork", output.ToString(), StringComparison.Ordinal);

        Assert.Equal(SchemaToolExitCodes.Success, await RunAsync(["--version"]));
        Assert.StartsWith("Groundwork.SchemaTool ", output.ToString(), StringComparison.Ordinal);

        Assert.Equal(SchemaToolExitCodes.InvalidInvocation, await RunAsync(["unknown", "--output", "json"]));
        using var report = JsonDocument.Parse(output.ToString());
        Assert.Equal("GW-CLI-001", report.RootElement.GetProperty("diagnostics")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task Invalid_schema_and_portability_violations_are_validation_failures()
    {
        var malformed = Temp("invalid.json", "{}");
        Assert.Equal(SchemaToolExitCodes.ValidationFailed,
            await RunAsync(["validate", "--schema", malformed, "--provider", "fake", "--offline", "--output", "json"]));

        var nonPortable = Temp("non-portable.json", ValidSchema.Replace(
            "\"type\":\"String\",\"nullable\":false,\"length\":64",
            "\"type\":\"Decimal\",\"nullable\":false,\"length\":null"));
        Assert.Equal(SchemaToolExitCodes.ValidationFailed,
            await RunAsync(["validate", "--schema", nonPortable, "--provider", "fake", "--offline", "--output", "json"]));
        Assert.Contains("GW-PORT-002", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Emit_writes_the_canonical_schema_and_fingerprint()
    {
        var source = Temp("source.json", "  " + ValidSchema);
        var destination = Path.Combine(directory, "canonical.json");

        Assert.Equal(SchemaToolExitCodes.Success,
            await RunAsync(["schema", "emit", "--input", source, "--file", destination, "--output", "json"]));

        var document = GroundworkSchemaCanonical.Read(ValidSchema);
        Assert.Equal(GroundworkSchemaCanonical.Emit(document), await File.ReadAllTextAsync(destination));
        Assert.Contains(GroundworkSchemaCanonical.Fingerprint(document), output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Default_host_discovers_a_loaded_provider_factory()
    {
        var schema = Temp("discovery-schema.json", ValidSchema);

        var exit = await GroundworkSchemaCli.RunAsync(
            ["plan", "--schema", schema, "--provider", DiscoveredFactory.ProviderAlias],
            output,
            error);

        Assert.Equal(SchemaToolExitCodes.PendingChanges, exit);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task Apply_is_inspect_only_until_explicitly_authorized()
    {
        var schema = Temp("schema.json", ValidSchema);
        using var session = new FakeSession();

        Assert.Equal(SchemaToolExitCodes.PendingChanges,
            await RunAsync(["plan", "--schema", schema, "--provider", "fake", "--output", "json"], _ => session));
        Assert.Empty(session.ExecutorImpl.AppliedOperations);
        using var plan = JsonDocument.Parse(output.ToString());
        Assert.Equal("1", plan.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal("fake", plan.RootElement.GetProperty("provider").GetProperty("name").GetString());
        Assert.Equal("1", plan.RootElement.GetProperty("provider").GetProperty("version").GetString());
        Assert.Equal("pending", plan.RootElement.GetProperty("outcome").GetString());
        Assert.True(plan.RootElement.GetProperty("pendingOperations").GetArrayLength() > 0);
        Assert.False(plan.RootElement.GetProperty("authorization").GetProperty("destructiveRequired").GetBoolean());
        var fingerprint = plan.RootElement.GetProperty("planFingerprint").GetString()!;

        Assert.Equal(SchemaToolExitCodes.InvalidInvocation,
            await RunAsync(["apply", "--schema", schema, "--provider", "fake"], _ => session));
        Assert.Empty(session.ExecutorImpl.AppliedOperations);

        Assert.Equal(SchemaToolExitCodes.Success,
            await RunAsync(["apply", "--schema", schema, "--provider", "fake", "--expected-plan", fingerprint], _ => session));
        Assert.NotEmpty(session.ExecutorImpl.AppliedOperations);

        Assert.Equal(SchemaToolExitCodes.Success,
            await RunAsync(["status", "--schema", schema, "--provider", "fake"], _ => session));

        Assert.Equal(SchemaToolExitCodes.Success,
            await RunAsync(["validate", "--schema", schema, "--provider", "fake", "--output", "json"], _ => session));
        using var validation = JsonDocument.Parse(output.ToString());
        Assert.Equal("live", validation.RootElement.GetProperty("inspectionMode").GetString());
        Assert.Equal("ready", validation.RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public void Destructive_operations_require_the_exact_identity()
    {
        var target = SchemaCompilation.CompileTargets(
            GroundworkSchemaCanonical.Read(ValidSchema), new ProviderIdentity("fake", "1"))[0];
        target = new PhysicalSchemaTarget(
            new SchemaSubject(target.Subject.Definition, new SchemaEvolutionMetadata(true, "reclassify-v2")),
            target.Provider);
        var plan = PhysicalSchemaDiffPlanner.Plan(target, PhysicalSchemaHistoryState.Empty, DateTimeOffset.UnixEpoch);
        var protection = PhysicalSchemaPlanProtection.Inspect(plan.Operations);
        Assert.NotEmpty(protection.DestructiveOperationIdentities);
        Assert.Contains("reclassify-v2", protection.SemanticMigrationIdentities);
        var identities = protection.DestructiveOperationIdentities.ToHashSet(StringComparer.Ordinal);

        Assert.False(SchemaToolAuthorization.Evaluate(plan, true).IsAuthorized);
        Assert.False(SchemaToolAuthorization.Evaluate(plan, true, new HashSet<string> { "wrong" }).IsAuthorized);
        Assert.True(SchemaToolAuthorization.Evaluate(
            plan,
            true,
            identities,
            new HashSet<string> { "reclassify-v2" }).IsAuthorized);
    }

    [Fact]
    public void Runtime_auto_apply_is_disabled_by_default() =>
        Assert.False(new GroundworkRuntimeSchemaAdmissionOptions().AutoApplyOnStartup);

    [Fact]
    public void Msbuild_task_fails_the_build_for_nonportable_schema()
    {
        var schema = Temp("msbuild-schema.json", ValidSchema.Replace(
            "\"type\":\"String\",\"nullable\":false,\"length\":64",
            "\"type\":\"Decimal\",\"nullable\":false,\"length\":null"));
        var engine = new RecordingBuildEngine();
        var task = new GroundworkVerify { SchemaFile = schema, BuildEngine = engine };

        Assert.False(task.Execute());
        Assert.Contains(engine.Errors, item => item.Code == "GW-PORT-002");
    }

    [Fact]
    public void Msbuild_task_fails_the_build_for_uncovered_query()
    {
        var schema = Temp("coverage-schema.json", ValidSchema);
        var inventory = Temp("coverage.json", """
            {"queries":[{"name":"tickets-by-id","table":"tickets","equal":["id"]}]}
            """);
        var engine = new RecordingBuildEngine();
        var task = new GroundworkVerify { SchemaFile = schema, CoverageFile = inventory, BuildEngine = engine };

        Assert.False(task.Execute());
        Assert.NotEmpty(engine.Errors);
    }

    private readonly string directory = Path.Combine(Path.GetTempPath(), "groundwork-schema-tool-" + Guid.NewGuid().ToString("N"));
    private readonly StringWriter output = new();
    private readonly StringWriter error = new();

    public SchemaToolContractTests() => Directory.CreateDirectory(directory);

    private string Temp(string name, string contents)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, contents);
        return path;
    }

    private Task<int> RunAsync(string[] arguments, Func<string, ISchemaToolProviderSession?>? resolver = null)
    {
        output.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        return GroundworkSchemaCli.RunAsync(arguments, output, error, resolver ?? (_ => null));
    }

    private sealed class FakeSession(string provider = "fake") : ISchemaToolProviderSession
    {
        public FakeExecutor ExecutorImpl { get; } = new();
        public ProviderIdentity Provider { get; } = new(provider, "1");
        public IPhysicalSchemaExecutor Executor => ExecutorImpl;
        public IPhysicalSchemaHistoryInspector Inspector => ExecutorImpl;
        public void Dispose() { }
    }

    public sealed class DiscoveredFactory : ISchemaToolProviderSessionFactory
    {
        public const string ProviderAlias = "schema-tool-test-discovered";
        public string Alias => ProviderAlias;
        public ISchemaToolProviderSession Open(SchemaToolProviderOptions options) => new FakeSession(ProviderAlias);
    }

    private sealed class FakeExecutor : IPhysicalSchemaExecutor, IPhysicalSchemaHistoryInspector
    {
        private PhysicalSchemaAppliedState? applied;
        public List<string> AppliedOperations { get; } = [];

        public IPhysicalSchemaApplicationLock AcquireApplicationLock(PhysicalSchemaTargetIdentity target) => new FakeLock(target);
        public PhysicalSchemaHistoryState ReadHistory(PhysicalSchemaTargetIdentity target, IPhysicalSchemaApplicationLock applicationLock) =>
            applied is null ? PhysicalSchemaHistoryState.Empty : PhysicalSchemaHistoryState.FromApplied(applied);
        public PhysicalSchemaInspectionResult InspectHistory(PhysicalSchemaTarget target) =>
            new(ReadHistory(target.Identity, new FakeLock(target.Identity)), true);
        public PhysicalSchemaOperationAcknowledgement ApplyOperation(PhysicalSchemaTargetIdentity target, PhysicalSchemaOperation operation, IPhysicalSchemaApplicationLock applicationLock)
        {
            AppliedOperations.Add(operation.Identity);
            return new PhysicalSchemaOperationAcknowledgement(operation.Identity, operation.Fingerprint, DateTimeOffset.UnixEpoch);
        }
        public void PublishAppliedState(PhysicalSchemaAppliedState state, string? expectedAppliedTargetFingerprint, IPhysicalSchemaApplicationLock applicationLock) => applied = state;

        private sealed class FakeLock(PhysicalSchemaTargetIdentity target) : IPhysicalSchemaApplicationLock
        {
            public PhysicalSchemaTargetIdentity Target { get; } = target;
            public void Dispose() { }
        }
    }

    private sealed class RecordingBuildEngine : IBuildEngine
    {
        public List<BuildErrorEventArgs> Errors { get; } = [];
        public bool ContinueOnError => false;
        public int LineNumberOfTaskNode => 0;
        public int ColumnNumberOfTaskNode => 0;
        public string ProjectFileOfTaskNode => "test";
        public void LogErrorEvent(BuildErrorEventArgs e) => Errors.Add(e);
        public void LogWarningEvent(BuildWarningEventArgs e) { }
        public void LogMessageEvent(BuildMessageEventArgs e) { }
        public void LogCustomEvent(CustomBuildEventArgs e) { }
        public bool BuildProjectFile(string projectFileName, string[] targetNames, System.Collections.IDictionary globalProperties, System.Collections.IDictionary targetOutputs) => true;
    }
}
