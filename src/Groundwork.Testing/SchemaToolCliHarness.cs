using System.Text.Json;
using Groundwork.Kernel;

namespace Groundwork.Testing;

/// <summary>
/// Minimal end-to-end harness for driving the groundwork schema CLI against one provider plug-in:
/// a scratch directory for schema files, canonical schema documents, and a JSON-report runner. The
/// CLI entry point is passed as a delegate so this assembly stays independent of the tool.
/// </summary>
public sealed class SchemaToolCliHarness : IDisposable
{
    private readonly Func<IReadOnlyList<string>, TextWriter, TextWriter, Task<int>> run;
    private readonly string providerAlias;
    private readonly string providerAssembly;

    public SchemaToolCliHarness(
        Func<IReadOnlyList<string>, TextWriter, TextWriter, Task<int>> run,
        string providerAlias,
        string providerAssembly)
    {
        this.run = run ?? throw new ArgumentNullException(nameof(run));
        this.providerAlias = providerAlias ?? throw new ArgumentNullException(nameof(providerAlias));
        this.providerAssembly = providerAssembly ?? throw new ArgumentNullException(nameof(providerAssembly));
        Root = Path.Combine(Path.GetTempPath(), "groundwork-schema-tool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    // This assembly stays independent of the tool, so the two exit codes that carry a usable
    // plan — success and pending changes — are named here rather than referenced.
    private const int SuccessExitCode = 0;
    private const int PendingChangesExitCode = 2;

    public static string InitialSchema(string table = "tickets") =>
        $$"""
        {"tables":[{"name":"{{table}}","columns":[{"name":"id","type":"String","nullable":false,"length":64,"precision":null,"scale":null,"folding":"None","generation":"Supplied"}],"key":["id"],"indexes":[]}]}
        """;

    /// <summary>Two tables in one document, for which the tool reports no single plan fingerprint.</summary>
    public const string MultiTargetSchema =
        """
        {"tables":[{"name":"tickets","columns":[{"name":"id","type":"String","nullable":false,"length":64,"precision":null,"scale":null,"folding":"None","generation":"Supplied"}],"key":["id"],"indexes":[]},{"name":"orders","columns":[{"name":"id","type":"String","nullable":false,"length":64,"precision":null,"scale":null,"folding":"None","generation":"Supplied"}],"key":["id"],"indexes":[]}]}
        """;

    public static string EvolvedSchema(string table = "tickets") =>
        $$"""
        {"tables":[{"name":"{{table}}","columns":[{"name":"id","type":"String","nullable":false,"length":64,"precision":null,"scale":null,"folding":"None","generation":"Supplied"},{"name":"priority","type":"Int32","nullable":true,"length":null,"precision":null,"scale":null,"folding":"None","generation":"Supplied"}],"key":["id"],"indexes":[{"name":"by_priority","columns":[{"name":"priority","descending":false}],"includeNulls":true,"unique":false}]}]}
        """;

    /// <summary>
    /// One unit expressed as a canonical schema document. <see cref="ParityDeclaration"/> expresses
    /// the same unit through the fluent kernel builder, so a test can prove that a tool-applied
    /// target and the runtime's expected target are the same value. Both deliberately name their
    /// indexes in an order the other does not use, which subject identity treats as one set.
    /// </summary>
    public static string ParitySchema(string table = "parity_orders") =>
        $$$"""
        {"tables":[{"name":"{{{table}}}","columns":[{"name":"id","type":"String","nullable":false,"length":64,"precision":null,"scale":null,"folding":"None","generation":"Supplied","default":null},{"name":"customer","type":"String","nullable":false,"length":64,"precision":null,"scale":null,"folding":"AsciiIgnoreCase","generation":"Supplied","default":null},{"name":"status","type":"String","nullable":false,"length":16,"precision":null,"scale":null,"folding":"None","generation":"Supplied","default":{"value":"pending"}}],"key":["id"],"indexes":[{"name":"z_parity_customer","columns":[{"name":"customer","descending":false}],"includeNulls":true,"unique":false},{"name":"a_parity_status","columns":[{"name":"status","descending":false}],"includeNulls":true,"unique":false}],"scope":"Scoped","concurrency":{"token":"version"},"timestamps":"None","retention":null,"appendIdempotency":null,"retentionIdempotency":null,"aggregations":[]}]}
        """;

    public static StorageUnit ParityDeclaration(string table = "parity_orders") =>
        StorageUnit.Declare(table, table)
            .String("id", 64, column => column.Required())
            .String("customer", 64, column => column.Required().Collation(PortableCollation.OrdinalIgnoreCase))
            .String("status", 16, column => column.Required().Default("pending"))
            .Key("id")
            .Index("z_parity_customer", "customer")
            .Index("a_parity_status", "status")
            .Scoped()
            .OptimisticConcurrency()
            .Build();

    public string Temp(string name, string contents)
    {
        var path = Path.Combine(Root, name);
        File.WriteAllText(path, contents);
        return path;
    }

    /// <summary>Runs 'schema emit', which is provider-independent and takes no provider options.</summary>
    public Task<SchemaToolCliRun> EmitAsync(string input, string file) =>
        InvokeAsync(["schema", "emit", "--input", input, "--file", file, "--output", "json"]);

    /// <summary>
    /// Plans, then applies under exact-plan authorization, naming every target's plan fingerprint
    /// and every destructive and semantic identity the plan reported. Apply stays refused unless
    /// the plan is still the current one. A plan that failed, or that named no target fingerprint
    /// to authorize, is returned as-is so a caller sees the plan's own reason.
    /// </summary>
    public async Task<SchemaToolCliRun> ApplyAuthorizedAsync(string schemaFile, string connection)
    {
        var plan = await RunAsync(["plan", "--schema", schemaFile], connection);
        if (plan.ExitCode is not (SuccessExitCode or PendingChangesExitCode))
            return plan;
        // The report's top-level planFingerprint is null for a multi-target schema, so authorize
        // each target by its own fingerprint rather than the summary.
        var fingerprints = plan.Report.RootElement.GetProperty("targets").EnumerateArray()
            .Select(target => target.TryGetProperty("planFingerprint", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null)
            .ToArray();
        if (fingerprints.Length == 0 || Array.Exists(fingerprints, fingerprint => fingerprint is null))
            return plan;

        var authorization = plan.Report.RootElement.GetProperty("authorization");
        var arguments = new List<string> { "apply", "--schema", schemaFile };
        foreach (var fingerprint in fingerprints)
            arguments.AddRange(["--expected-plan", fingerprint!]);
        foreach (var identity in authorization.GetProperty("destructiveOperationsRequired").EnumerateArray())
            arguments.AddRange(["--allow-destructive", identity.GetString()!]);
        foreach (var identity in authorization.GetProperty("semanticRequired").EnumerateArray())
            arguments.AddRange(["--allow-semantic", identity.GetString()!]);
        return await RunAsync(arguments, connection);
    }

    public Task<SchemaToolCliRun> RunAsync(IReadOnlyList<string> arguments, string? connection = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var composed = new List<string>(arguments) { "--provider", providerAlias };
        if (connection is not null)
            composed.AddRange(["--connection", connection]);
        composed.AddRange(["--provider-assembly", providerAssembly, "--output", "json"]);
        return InvokeAsync(composed);
    }

    private async Task<SchemaToolCliRun> InvokeAsync(IReadOnlyList<string> arguments)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exitCode = await run(arguments, output, error);
        var text = output.ToString();
        return new SchemaToolCliRun(exitCode, JsonDocument.Parse(text), text, error.ToString());
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}

public sealed record SchemaToolCliRun(int ExitCode, JsonDocument Report, string Output, string Error)
{
    /// <summary>
    /// The exit code together with the sanitized reasons the tool reported, so a failed exit-code
    /// assertion names the refusal instead of leaving an operator with a bare number.
    /// </summary>
    public string Reason
    {
        get
        {
            var diagnostics = Report.RootElement.TryGetProperty("diagnostics", out var reported) &&
                              reported.ValueKind == JsonValueKind.Array
                ? reported.EnumerateArray()
                    .Select(diagnostic => string.Join(' ', new[] { "code", "message", "target" }
                        .Select(name => diagnostic.TryGetProperty(name, out var part) ? part.GetString() : null)
                        .Where(part => !string.IsNullOrEmpty(part))))
                    .Where(text => text.Length != 0)
                    .ToArray()
                : [];
            return $"exit {ExitCode}: " + (diagnostics.Length == 0
                ? Output.Trim()
                : string.Join(Environment.NewLine, diagnostics));
        }
    }
}
