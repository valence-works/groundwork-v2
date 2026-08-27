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
        Assert.StartsWith("Groundwork.Tool ", output.ToString(), StringComparison.Ordinal);

        Assert.Equal(SchemaToolExitCodes.InvalidInvocation, await RunAsync(["unknown", "--output", "json"]));
        using var report = JsonDocument.Parse(output.ToString());
        Assert.Equal("GW-CLI-001", report.RootElement.GetProperty("diagnostics")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task Verify_refuses_a_folded_index_that_exceeds_the_budget_once_its_search_key_is_expanded()
    {
        var folded = Temp("folded.json", """
            {"tables":[{"name":"tickets","columns":[{"name":"id","type":"String","nullable":false,"length":64,"precision":null,"scale":null,"folding":"None","generation":"Supplied"},{"name":"customer","type":"String","nullable":false,"length":200,"precision":null,"scale":null,"folding":"UnicodeOrdinalIgnoreCase","generation":"Supplied"}],"key":["id"],"indexes":[{"name":"by_customer","columns":[{"name":"customer","descending":false}],"includeNulls":true,"unique":false}]}]}
            """);

        Assert.Equal(SchemaToolExitCodes.ValidationFailed,
            await RunAsync(["validate", "--schema", folded, "--provider", "fake", "--offline", "--output", "json"]));
        Assert.Contains("GW-PORT-004", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_provider_physicalization_refusal_keeps_its_code_as_a_validation_error()
    {
        var schema = Temp("refused-schema.json", ValidSchema);

        Assert.Equal(
            SchemaToolExitCodes.ValidationFailed,
            await RunAsync(["plan", "--schema", schema, "--provider", "fake", "--output", "json"],
                _ => new FakeSession(refusal: "GW-PORT-011 at indexes.by_id.physicalName: the composed name is too long.")));
        Assert.Contains("GW-CLI-005", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("GW-PORT-011", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("indexes.by_id.physicalName", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_offset_less_timestamp_default_reads_as_utc_on_any_machine()
    {
        var schema = ValidSchema.Replace(
            "\"generation\":\"Supplied\"",
            "\"generation\":\"Supplied\",\"default\":{\"value\":\"2024-01-01T00:00:00\"}")
            .Replace("\"type\":\"String\"", "\"type\":\"DateTimeOffset\"");

        var value = Assert.IsType<DateTimeOffset>(Assert.Single(
            Assert.Single(GroundworkSchemaCanonical.Read(schema).Tables).Columns).Default!.Value);

        Assert.Equal(TimeSpan.Zero, value.Offset);
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), value);
        Assert.Contains(
            @"2024-01-01T00:00:00.0000000\u002B00:00",
            GroundworkSchemaCanonical.Emit(GroundworkSchemaCanonical.Read(schema)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_default_whose_literal_contradicts_the_column_type_is_a_format_refusal()
    {
        var mistyped = ValidSchema.Replace(
            "\"generation\":\"Supplied\"",
            "\"generation\":\"Supplied\",\"default\":{\"value\":42}");

        var failure = Assert.Throws<FormatException>(() => GroundworkSchemaCanonical.Read(mistyped));
        Assert.Contains("default", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("column")]
    [InlineData("orderBy")]
    public void A_mistyped_aggregate_member_is_refused_rather_than_read_as_absent(string member)
    {
        var aggregation = $$"""
            "aggregations":[{"name":"summary","groupByColumns":["id"],"groupBy":[],"aggregates":[{"kind":"Count","alias":"n","column":null,"orderBy":null,"descending":false,"maxValues":0}]}]
            """.Replace($"\"{member}\":null", $"\"{member}\":7", StringComparison.Ordinal);

        var failure = Assert.Throws<FormatException>(() => GroundworkSchemaCanonical
            .Read(ValidSchema.Replace("\"indexes\":[]", "\"indexes\":[]," + aggregation)));
        Assert.Contains(member, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mistyped_ledger_name_is_refused_rather_than_reverting_to_the_default()
    {
        var mistyped = ValidSchema.Replace(
            "\"indexes\":[]",
            "\"indexes\":[],\"appendIdempotency\":{\"windowTicks\":600000000,\"ledger\":7}");

        var failure = Assert.Throws<FormatException>(() => GroundworkSchemaCanonical.Read(mistyped));
        Assert.Contains("ledger", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("concurrency")]
    [InlineData("retention")]
    [InlineData("appendIdempotency")]
    [InlineData("retentionIdempotency")]
    public void A_wrong_typed_optional_member_is_refused_by_name(string member)
    {
        Assert.Null(Assert.Single(GroundworkSchemaCanonical
            .Read(ValidSchema.Replace("\"indexes\":[]", $"\"indexes\":[],\"{member}\":null"))
            .Tables).Retention);

        var failure = Assert.Throws<FormatException>(() => GroundworkSchemaCanonical
            .Read(ValidSchema.Replace("\"indexes\":[]", $"\"indexes\":[],\"{member}\":5")));
        Assert.Contains(member, failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"type\":\"String\"", "\"type\":\"999\"", "type")]
    [InlineData("\"folding\":\"None\"", "\"folding\":\"999\"", "folding")]
    [InlineData(
        "\"indexes\":[]",
        "\"indexes\":[],\"aggregations\":[{\"name\":\"summary\",\"groupByColumns\":[],"
        + "\"groupBy\":[{\"alias\":\"day\",\"bucket\":\"999\",\"sourceColumn\":\"id\",\"widthTicks\":0}],"
        + "\"aggregates\":[]}]",
        "bucket")]
    public void An_enum_outside_its_declared_members_is_refused_by_name(
        string original, string replacement, string member)
    {
        var undefined = ValidSchema.Replace(original, replacement, StringComparison.Ordinal);

        var failure = Assert.Throws<FormatException>(() => GroundworkSchemaCanonical.Read(undefined));

        Assert.Contains(member, failure.Message, StringComparison.Ordinal);
        Assert.Contains("999", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_null_aggregations_member_is_absent_but_a_wrong_typed_one_is_refused()
    {
        Assert.Empty(Assert.Single(GroundworkSchemaCanonical
            .Read(ValidSchema.Replace("\"indexes\":[]", "\"indexes\":[],\"aggregations\":null"))
            .Tables).Aggregations);

        Assert.Throws<FormatException>(() => GroundworkSchemaCanonical
            .Read(ValidSchema.Replace("\"indexes\":[]", "\"indexes\":[],\"aggregations\":7")));
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

        malformed = Temp("malformed.json", "{");
        Assert.Equal(SchemaToolExitCodes.ValidationFailed,
            await RunAsync(["validate", "--schema", malformed, "--provider", "fake", "--offline", "--output", "json"]));
        Assert.Contains("GW-CLI-005", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_or_invalid_options_are_rejected_without_echoing_values()
    {
        const string secret = "do-not-echo-this";

        Assert.Equal(SchemaToolExitCodes.InvalidInvocation,
            await RunAsync(["validate", "--schema", secret, "--provider", "fake", "--offline", "--output", "json", "--unknown", secret]));
        Assert.DoesNotContain(secret, output.ToString(), StringComparison.Ordinal);

        Assert.Equal(SchemaToolExitCodes.InvalidInvocation,
            await RunAsync(["validate", "--schema", secret, "--provider", "fake", "--offline", "--output", "xml"]));
        Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_has_a_stable_exit_and_never_mutates()
    {
        var schema = Temp("cancel-schema.json", ValidSchema);
        using var session = new FakeSession();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exit = await GroundworkSchemaCli.RunAsync(
            ["apply", "--schema", schema, "--provider", "fake", "--safe", "--output", "json"],
            output,
            error,
            _ => session,
            cancellation.Token);

        Assert.Equal(SchemaToolExitCodes.Cancelled, exit);
        Assert.Empty(session.ExecutorImpl.AppliedOperations);
        Assert.Contains("GW-CLI-009", output.ToString(), StringComparison.Ordinal);
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
    public async Task Multi_target_authorization_is_preflighted_before_any_mutation()
    {
        var secondTable = ValidSchema.Replace("tickets", "users", StringComparison.Ordinal);
        using var second = JsonDocument.Parse(secondTable);
        using var first = JsonDocument.Parse(ValidSchema);
        var combined = "{\"tables\":[" + first.RootElement.GetProperty("tables")[0].GetRawText() + "," +
                       second.RootElement.GetProperty("tables")[0].GetRawText() + "]}";
        var schema = Temp("multi-schema.json", combined);
        using var session = new FakeSession();

        Assert.Equal(SchemaToolExitCodes.PendingChanges,
            await RunAsync(["plan", "--schema", schema, "--provider", "fake", "--output", "json"], _ => session));
        using var report = JsonDocument.Parse(output.ToString());
        var onlyFirstPlan = report.RootElement.GetProperty("targets")[0].GetProperty("planFingerprint").GetString()!;

        Assert.Equal(SchemaToolExitCodes.AuthorizationRequired,
            await RunAsync([
                "apply", "--schema", schema, "--provider", "fake",
                "--expected-plan", onlyFirstPlan
            ], _ => session));
        Assert.Empty(session.ExecutorImpl.AppliedOperations);
    }

    [Fact]
    public async Task Catalog_drift_blocks_apply_before_any_mutation()
    {
        var schema = Temp("drift-schema.json", ValidSchema);
        using var session = new FakeSession();
        session.ExecutorImpl.IsAppliedSchemaValid = false;

        Assert.Equal(SchemaToolExitCodes.ValidationFailed,
            await RunAsync([
                "apply", "--schema", schema, "--provider", "fake", "--safe", "--output", "json"
            ], _ => session));

        Assert.Empty(session.ExecutorImpl.AppliedOperations);
        using var report = JsonDocument.Parse(output.ToString());
        Assert.Equal("blocked", report.RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Raw_provider_failures_are_stable_and_do_not_echo_secrets()
    {
        var schema = Temp("failure-schema.json", ValidSchema);
        const string secret = "provider-secret-do-not-echo";

        Assert.Equal(SchemaToolExitCodes.ExecutionFailed,
            await RunAsync([
                "plan", "--schema", schema, "--provider", "fake", "--output", "json"
            ], _ => throw new InvalidOperationException(secret)));

        Assert.Contains("GW-CLI-010", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Factory_authored_failure_reasons_surface_to_the_operator()
    {
        var schema = Temp("reason-schema.json", ValidSchema);
        const string reason = "the target store is already in use by another process";

        Assert.Equal(SchemaToolExitCodes.ExecutionFailed,
            await RunAsync([
                "plan", "--schema", schema, "--provider", "fake", "--output", "json"
            ], _ => throw new SchemaToolProviderException(reason)));

        Assert.Contains("GW-CLI-010", output.ToString(), StringComparison.Ordinal);
        Assert.Contains(reason, output.ToString(), StringComparison.Ordinal);

        Assert.Equal(SchemaToolExitCodes.InvalidInvocation,
            await RunAsync([
                "plan", "--schema", schema, "--provider", "fake", "--output", "json"
            ], _ => throw new SchemaToolProviderInvocationException("the fake provider requires --connection")));
        Assert.Contains("GW-CLI-001", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("requires --connection", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Destructive_operations_require_the_exact_identity()
    {
        var target = SchemaCompilation.CompileTargets(
            GroundworkSchemaCanonical.Read(ValidSchema), new FakeTargets(new ProviderIdentity("fake", "1")))[0];
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

    /// <summary>
    /// Adoption is a proof, so a provider that cannot compare a deployed catalog to a compiled
    /// target is refused by name rather than through a generic execution failure. Every shipped
    /// provider is a catalog inspector, so the case this guards is a third-party plug-in — which is
    /// exactly what this fake is.
    /// </summary>
    [Fact]
    public async Task Adopt_refuses_a_provider_that_cannot_compare_a_catalog_to_a_target()
    {
        var schema = Temp("adopt-uninspectable.json", ValidSchema);

        Assert.Equal(
            SchemaToolExitCodes.ValidationFailed,
            await RunAsync(
                ["adopt", "--schema", schema, "--provider", "fake", "--safe", "--output", "json"],
                _ => new FakeSession()));
        using var report = JsonDocument.Parse(output.ToString());
        Assert.Equal("GW-CLI-013", report.RootElement.GetProperty("diagnostics")[0].GetProperty("code").GetString());
        Assert.Contains(
            "cannot compare a deployed catalog",
            report.RootElement.GetProperty("diagnostics")[0].GetProperty("message").GetString()!,
            StringComparison.Ordinal);
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

    private sealed class FakeSession(string provider = "fake", string? refusal = null) : ISchemaToolProviderSession
    {
        public FakeExecutor ExecutorImpl { get; } = new();
        public ProviderIdentity Provider { get; } = new(provider, "1");
        public IPhysicalSchemaTargetCompiler Targets => new FakeTargets(Provider, refusal);
        public IPhysicalSchemaExecutor Executor => ExecutorImpl;
        public IPhysicalSchemaHistoryInspector Inspector => ExecutorImpl;
        public void Dispose() { }
    }

    private sealed class FakeTargets(ProviderIdentity provider, string? refusal = null) : IPhysicalSchemaTargetCompiler
    {
        public PhysicalSchemaTarget Compile(StorageUnit declaration) => refusal is null
            ? new(new SchemaSubject(SearchKeyProjection.Expand(declaration)), provider)
            : throw new InvalidOperationException(refusal);
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
        public bool IsAppliedSchemaValid { get; set; } = true;

        public IPhysicalSchemaApplicationLock AcquireApplicationLock(PhysicalSchemaTargetIdentity target) => new FakeLock(target);
        public PhysicalSchemaHistoryState ReadHistory(PhysicalSchemaTargetIdentity target, IPhysicalSchemaApplicationLock applicationLock) =>
            applied is null ? PhysicalSchemaHistoryState.Empty : PhysicalSchemaHistoryState.FromApplied(applied);
        public PhysicalSchemaInspectionResult InspectHistory(PhysicalSchemaTarget target) =>
            new(ReadHistory(target.Identity, new FakeLock(target.Identity)), IsAppliedSchemaValid);
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
