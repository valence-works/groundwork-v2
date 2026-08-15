using System.Collections.Immutable;
using System.Text;
using Groundwork.Analyzers;
using Groundwork.Schema;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Groundwork.Analyzers.Tests;

public sealed class CoverageAnalyzerTests
{
    [Fact]
    public async Task Uncovered_query_reports_the_q3_code_and_suggested_index()
    {
        var diagnostics = await Analyze(WithSchema(SchemaWithIndex("ix_other", "other ASC")) + QuerySource("var result = db.Table<Ticket>().Where(t => t.Status == status).QueryAsync();"));

        var diagnostic = Assert.Single(diagnostics.Where(item => item.Id == "GW_COVER_006"));
        Assert.Contains("GwIndex", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("status", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.True(diagnostic.Location.GetLineSpan().StartLinePosition.Line > 0);
    }

    [Fact]
    public async Task WhereIf_enumerates_shapes_and_the_all_filters_absent_shape_fails()
    {
        var diagnostics = await Analyze(WithSchema(SchemaWithIndex("ix_status", "status ASC")) + QuerySource("var result = db.Table<Ticket>().WhereIf(enabled, t => t.Status == status).QueryAsync();"));

        var diagnostic = Assert.Single(diagnostics.Where(item => item.Id == "GW_COVER_005"));
        Assert.Contains("shape 1 of 2", diagnostic.GetMessage(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("all filters absent", diagnostic.GetMessage(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhereIf_with_an_index_for_each_shape_is_clean()
    {
        var diagnostics = await Analyze(WithSchema(
            SchemaWithIndex("ix_status", "status ASC", "ix_status_created", "status ASC, created_at DESC")) +
            QuerySource("var result = db.Table<Ticket>().Where(t => t.Status == status).WhereIf(enabled, t => t.CreatedAt >= from).QueryAsync();"));

        Assert.DoesNotContain(diagnostics, item => item.Id.StartsWith("GW_COVER_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reassignment_dataflow_enumerates_conditional_where()
    {
        var diagnostics = await Analyze(WithSchema(SchemaWithIndex("ix_status", "status ASC")) + QueryInfrastructure + " public static class Reassignment { public static void Run() { var q = db.Table<Ticket>(); if (enabled) q = q.Where(t => t.Status == status); var result = q.QueryAsync(); } }");

        Assert.True(diagnostics.Any(item => item.Id == "GW_COVER_005"), string.Join(Environment.NewLine, diagnostics));
        var diagnostic = Assert.Single(diagnostics.Where(item => item.Id == "GW_COVER_005"));
        Assert.Contains("shape 1 of 2", diagnostic.GetMessage(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reassignment_that_escapes_into_a_collection_is_unresolvable()
    {
        var source = WithSchema(SchemaWithIndex("ix_status", "status ASC")) + QueryInfrastructure +
                     " using System.Collections.Generic; public static class Escape { public static void Run() { var q = db.Table<Ticket>(); var values = new List<object>(); values.Add(q); var result = q.QueryAsync(); } }";

        var diagnostics = await Analyze(source);

        var diagnostic = Assert.Single(diagnostics.Where(item => item.Id == "GW_COVER_900"));
        Assert.Contains("escapes", diagnostic.GetMessage(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task More_than_six_conditional_filters_are_unresolvable_by_the_blessed_bound()
    {
        var filters = string.Join("", Enumerable.Range(0, 7).Select(index => $".WhereIf(c{index}, t => t.Status == status)"));
        var diagnostics = await Analyze(WithSchema(SchemaWithIndex("ix_status", "status ASC")) + QuerySource($"var result = db.Table<Ticket>(){filters}.QueryAsync();"));

        var diagnostic = Assert.Single(diagnostics.Where(item => item.Id == "GW_COVER_900"));
        Assert.Contains("six", diagnostic.GetMessage(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Additional_file_schema_fallback_is_consumed()
    {
        var diagnostics = await Analyze(QuerySource("var result = db.Table<Ticket>().Where(t => t.Status == status).QueryAsync();"),
            new InMemoryAdditionalText("groundwork.schema.json", SchemaWithIndex("ix_other", "other ASC")));

        Assert.Contains(diagnostics, item => item.Id == "GW_COVER_006");
    }

    [Fact]
    public async Task Referenced_package_schema_metadata_is_consumed()
    {
        using var stream = new MemoryStream();
        var referenceCompilation = CSharpCompilation.Create(
            "SchemaPackage",
            [CSharpSyntaxTree.ParseText(WithSchema(SchemaWithIndex("ix_other", "other ASC")))],
            References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.True(referenceCompilation.Emit(stream).Success);
        stream.Position = 0;

        var diagnostics = await Analyze(QuerySource("var result = db.Table<Ticket>().Where(t => t.Status == status).QueryAsync();"),
            references: [MetadataReference.CreateFromStream(stream)]);

        Assert.Contains(diagnostics, item => item.Id == "GW_COVER_006");
    }

    [Fact]
    public async Task Unknown_query_root_is_advisory_error_900()
    {
        var diagnostics = await Analyze(WithSchema(SchemaWithIndex("ix_status", "status ASC")) + "var result = GetQuery<Ticket>().QueryAsync();\n" + QueryInfrastructure);

        var diagnostic = Assert.Single(diagnostics.Where(item => item.Id == "GW_COVER_900"));
        Assert.Contains("statically", diagnostic.GetMessage(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unresolvable_reassignment_has_a_working_where_if_code_fix()
    {
        var source = WithSchema(SchemaWithIndex("ix_status", "status ASC")) + QueryInfrastructure +
                     " public static class Reassignment { public static void Run() { var q = db.Table<Ticket>(); for (var i = 0; i < 1; i++) if (enabled) q = q.Where(t => t.Status == status); var result = q.QueryAsync(); } }";
        var allDiagnostics = await Analyze(source);
        Assert.True(allDiagnostics.Any(item => item.Id == "GW_COVER_900"), string.Join(Environment.NewLine, allDiagnostics));
        var diagnostic = Assert.Single(allDiagnostics.Where(item => item.Id == "GW_COVER_900"));

        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("CodeFix", LanguageNames.CSharp);
        var document = project.AddDocument("Query.cs", SourceText.From(source));
        var actions = new List<CodeAction>();
        var context = new CodeFixContext(document, diagnostic, (action, _) => actions.Add(action), CancellationToken.None);
        await new CoverageCodeFixProvider().RegisterCodeFixesAsync(context);

        Assert.True(actions.Count > 0, $"span={diagnostic.Location.SourceSpan}; text={source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length)}");
        var action = Assert.Single(actions);
        var operations = await action.GetOperationsAsync(CancellationToken.None);
        var apply = Assert.Single(operations.OfType<ApplyChangesOperation>());
        var changed = apply.ChangedSolution.GetDocument(document.Id)!;
        var rewritten = (await changed.GetTextAsync()).ToString();
        Assert.Contains("q = q.WhereIf(enabled, t => t.Status == status);", rewritten, StringComparison.Ordinal);
    }

    private static async Task<ImmutableArray<Diagnostic>> Analyze(
        string source,
        AdditionalText? additional = null,
        IEnumerable<MetadataReference>? references = null)
    {
        var compilation = CSharpCompilation.Create(
            "CoverageInput",
            [CSharpSyntaxTree.ParseText(SourceText.From(source, Encoding.UTF8))],
            References().Concat(references ?? Array.Empty<MetadataReference>()),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var options = new AnalyzerOptions(additional is null
            ? ImmutableArray<AdditionalText>.Empty
            : [additional]);
        var result = await compilation.WithAnalyzers(
            [new CoverageAnalyzer()],
            new CompilationWithAnalyzersOptions(options, onAnalyzerException: null, concurrentAnalysis: false, logAnalyzerExecutionTime: true))
            .GetAnalyzerDiagnosticsAsync();
        return result;
    }

    private static IEnumerable<MetadataReference> References()
    {
        var paths = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        return paths
            .Concat([typeof(GroundworkSchemaAttribute).Assembly.Location])
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path));
    }

    private static string WithSchema(string schema)
    {
        var document = GroundworkSchemaCanonical.Parse(schema);
        var fingerprint = GroundworkSchemaCanonical.Fingerprint(document);
        return "using Groundwork.Schema; [assembly: GroundworkSchema(" + Literal(schema) + ", " + Literal(fingerprint) + ")]\n";
    }

    private static string QuerySource(string query) => "#nullable enable\n" + QueryInfrastructure + "\n" + query + "\n";

    private static string SchemaWithIndex(params string[] values)
    {
        var indexes = new StringBuilder();
        for (var index = 0; index < values.Length; index += 2)
        {
            if (index != 0) indexes.Append(',');
            indexes.Append("{\"name\":\"").Append(values[index]).Append("\",\"columns\":[");
            var columns = values[index + 1].Split(',');
            for (var column = 0; column < columns.Length; column++)
            {
                if (column != 0) indexes.Append(',');
                var parts = columns[column].Trim().Split(' ');
                indexes.Append("{\"name\":\"").Append(parts[0]).Append("\",\"descending\":").Append(parts[1] == "DESC" ? "true" : "false").Append('}');
            }
            indexes.Append("],\"includeNulls\":true,\"unique\":false}");
        }

        return "{\"tables\":[{\"name\":\"tickets\",\"columns\":[" +
            "{\"name\":\"id\",\"type\":\"String\",\"nullable\":false}," +
            "{\"name\":\"status\",\"type\":\"String\",\"nullable\":true}," +
            "{\"name\":\"other\",\"type\":\"String\",\"nullable\":true}," +
            "{\"name\":\"created_at\",\"type\":\"DateTimeOffset\",\"nullable\":false}]," +
            "\"key\":[\"id\"],\"indexes\":[" + indexes + "]}]}";
    }

    private static string Literal(string value) => Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(value, quote: true);

    private const string QueryInfrastructure = """
        using System;
        using System.Threading.Tasks;
        using Groundwork.Schema;
        using static QueryHost;
        [GwTable("tickets")] public sealed class Ticket { public string Status { get; set; } = ""; public string Other { get; set; } = ""; public DateTimeOffset CreatedAt { get; set; } }
        public sealed class Db { public Query<T> Table<T>() => new Query<T>(); }
        public sealed class Query<T>
        {
            public Query<T> Where(Func<T, bool> predicate) => this;
            public Query<T> WhereIf(bool condition, Func<T, bool> predicate) => this;
            public Query<T> OrderByDescending<TKey>(Func<T, TKey> selector) => this;
            public Query<T> Take(int count) => this;
            public Task QueryAsync() => Task.CompletedTask;
        }
        public static class QueryHost { public static Db db = new Db(); public static bool enabled; public static bool c0, c1, c2, c3, c4, c5, c6; public static string status = "open"; public static DateTimeOffset from; }
        """;

    private sealed class InMemoryAdditionalText(string path, string content) : AdditionalText
    {
        public override string Path => path;
        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(content);
    }
}
