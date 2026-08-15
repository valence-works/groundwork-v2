using System.Collections.Immutable;
using Groundwork.Schema;
using Groundwork.Schema.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Groundwork.Schema.Generator.Tests;

public sealed class GeneratorContractTests
{
    [Fact]
    public void Attributes_generate_one_runtime_unit_and_a_matching_assembly_fingerprint()
    {
        const string source = """
            #nullable enable
            using System;
            using Groundwork.Schema;

            [GwTable("tickets")]
            [GwIndex("ix_tickets_status_created", "status ASC, created_at DESC")]
            [GwIndex("ix_tickets_assignee", "assignee ASC", IncludeNulls = false)]
            public partial class Ticket
            {
                [GwKey, GwColumn(Length = 64)] public string Id { get; set; } = "";
                [GwColumn(Length = 32, Folding = TextFolding.AsciiIgnoreCase)] public string Status { get; set; } = "";
                [GwColumn] public DateTimeOffset CreatedAt { get; set; }
                [GwColumn(Precision = 12, Scale = 2)] public decimal Amount { get; set; }
                [GwColumn(Length = 64)] public string? Assignee { get; set; }
            }
            """;

        var result = Run(source);

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(result.OutputCompilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains(result.Generated, generated => generated.Contains("TicketStorageUnit", StringComparison.Ordinal));
        Assert.Contains(result.Generated, generated => generated.Contains("GroundworkSchema", StringComparison.Ordinal));

        var assemblyAttribute = result.OutputCompilation.Assembly.GetAttributes()
            .Single(attribute => attribute.AttributeClass?.ToDisplayString() == typeof(GroundworkSchemaAttribute).FullName);
        var json = (string)assemblyAttribute.ConstructorArguments[0].Value!;
        var fingerprint = (string)assemblyAttribute.ConstructorArguments[1].Value!;
        Assert.Equal(GroundworkSchemaCanonical.Fingerprint(GroundworkSchemaCanonical.Read(json)), fingerprint);
        Assert.Contains("ix_tickets_status_created", json, StringComparison.Ordinal);
        Assert.Contains("\"includeNulls\":false", json, StringComparison.Ordinal);
        Assert.Contains("created_at", json, StringComparison.Ordinal);
        Assert.Contains("\"nullable\":true", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_index_spec_reports_at_the_spec_argument()
    {
        const string source = """
            using Groundwork.Schema;

            [GwTable("tickets")]
            [GwIndex("ix_bad", "missing ASC")]
            public partial class Ticket
            {
                [GwKey, GwColumn] public string Id { get; set; } = "";
            }
            """;

        var result = Run(source);
        var diagnostic = Assert.Single(result.Diagnostics.Where(item => item.Id == "GW_SCHEMA_INDEX_001"));
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("missing", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Equal(4, diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1);
        Assert.True(diagnostic.Location.SourceSpan.Length > 0);
    }

    [Fact]
    public void Additional_file_round_trip_emits_the_same_canonical_fingerprint()
    {
        const string json = "{\"tables\":[{\"name\":\"tickets\",\"columns\":[{\"name\":\"id\",\"type\":\"String\",\"nullable\":false}],\"key\":[\"id\"],\"indexes\":[]}] }";
        var result = Run("public static class Empty { }", new InMemoryAdditionalText("schema/groundwork.json", json));

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(result.OutputCompilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var assemblyAttribute = result.OutputCompilation.Assembly.GetAttributes()
            .Single(attribute => attribute.AttributeClass?.ToDisplayString() == typeof(GroundworkSchemaAttribute).FullName);
        var canonical = (string)assemblyAttribute.ConstructorArguments[0].Value!;
        var fingerprint = (string)assemblyAttribute.ConstructorArguments[1].Value!;
        Assert.Equal(GroundworkSchemaCanonical.Fingerprint(GroundworkSchemaCanonical.Parse(canonical)), fingerprint);
        Assert.Equal(GroundworkSchemaCanonical.Fingerprint(GroundworkSchemaCanonical.Parse(json)), fingerprint);
    }

    [Fact]
    public void Referenced_assembly_schema_is_visible_through_metadata()
    {
        const string source = """
            using Groundwork.Schema;
            [GwTable("tickets")]
            public partial class Ticket
            {
                [GwKey, GwColumn] public string Id { get; set; } = "";
            }
            """;
        var generated = Run(source);
        using var stream = new MemoryStream();
        var emitted = generated.OutputCompilation.Emit(stream);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        stream.Position = 0;
        var reference = MetadataReference.CreateFromStream(stream);

        var consumer = CSharpCompilation.Create(
            "Consumer",
            [CSharpSyntaxTree.ParseText(SourceText.From("public static class Query { }"))],
            References(typeof(GroundworkSchemaAttribute), typeof(object)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var schemas = GroundworkSchemaMetadata.Read(consumer.AddReferences(reference));

        var schema = Assert.Single(schemas);
        Assert.Equal("tickets", Assert.Single(schema.Tables).Name);
    }

    private static GeneratorRunResult Run(string source, params AdditionalText[] additionalFiles)
    {
        var compilation = CSharpCompilation.Create(
            "GeneratorInput",
            [CSharpSyntaxTree.ParseText(SourceText.From(source, encoding: System.Text.Encoding.UTF8))],
            References(typeof(GroundworkSchemaAttribute), typeof(Groundwork.Kernel.StorageUnit), typeof(object)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var driver = CSharpGeneratorDriver.Create(new SchemaGenerator())
            .AddAdditionalTexts(additionalFiles.ToImmutableArray());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        _ = output.GetDiagnostics();
        var run = driver.GetRunResult();
        return new GeneratorRunResult(output, run.Results.SelectMany(result => result.GeneratedSources).Select(source => source.SourceText.ToString()).ToArray(), diagnostics.Concat(run.Diagnostics).GroupBy(diagnostic => diagnostic.ToString(), StringComparer.Ordinal).Select(group => group.First()).ToArray());
    }

    private static IEnumerable<MetadataReference> References(params Type[] types)
    {
        var paths = new HashSet<string>(
            ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);
        foreach (var type in types)
            paths.Add(type.Assembly.Location);
        return paths.Where(File.Exists).Select(path => MetadataReference.CreateFromFile(path));
    }

    private sealed record GeneratorRunResult(
        Compilation OutputCompilation,
        IReadOnlyList<string> Generated,
        IReadOnlyList<Diagnostic> Diagnostics);

    private sealed class InMemoryAdditionalText(string path, string content) : AdditionalText
    {
        public override string Path => path;
        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(content);
    }
}
