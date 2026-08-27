using System.Text.Json;

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

    public static string InitialSchema(string table = "tickets") =>
        $$"""
        {"tables":[{"name":"{{table}}","columns":[{"name":"id","type":"String","nullable":false,"length":64,"precision":null,"scale":null,"folding":"None","generation":"Supplied"}],"key":["id"],"indexes":[]}]}
        """;

    public static string EvolvedSchema(string table = "tickets") =>
        $$"""
        {"tables":[{"name":"{{table}}","columns":[{"name":"id","type":"String","nullable":false,"length":64,"precision":null,"scale":null,"folding":"None","generation":"Supplied"},{"name":"priority","type":"Int32","nullable":true,"length":null,"precision":null,"scale":null,"folding":"None","generation":"Supplied"}],"key":["id"],"indexes":[{"name":"by_priority","columns":[{"name":"priority","descending":false}],"includeNulls":true,"unique":false}]}]}
        """;

    public string Temp(string name, string contents)
    {
        var path = Path.Combine(Root, name);
        File.WriteAllText(path, contents);
        return path;
    }

    public async Task<SchemaToolCliRun> RunAsync(IReadOnlyList<string> arguments, string? connection = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var output = new StringWriter();
        var error = new StringWriter();
        var composed = new List<string>(arguments) { "--provider", providerAlias };
        if (connection is not null)
            composed.AddRange(["--connection", connection]);
        composed.AddRange(["--provider-assembly", providerAssembly, "--output", "json"]);
        var exitCode = await run(composed, output, error);
        var text = output.ToString();
        return new SchemaToolCliRun(exitCode, JsonDocument.Parse(text), text, error.ToString());
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}

public sealed record SchemaToolCliRun(int ExitCode, JsonDocument Report, string Output, string Error);
