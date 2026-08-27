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
        Assert.Contains(result.Generated, generated => generated.Contains("MaxLength(64)", StringComparison.Ordinal));
        Assert.Contains(result.Generated, generated => generated.Contains("Precision(12, 2)", StringComparison.Ordinal));
        Assert.Contains(result.Generated, generated => generated.Contains("OrdinalIgnoreCase", StringComparison.Ordinal));
        Assert.Contains(result.Generated, generated => generated.Contains(".Descending(\"created_at\")", StringComparison.Ordinal));
        Assert.Contains(result.Generated, generated => generated.Contains("ExcludeMissingValues()", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Generated, generated => generated.Contains("MissingValues =", StringComparison.Ordinal));

        var assemblyAttribute = result.OutputCompilation.Assembly.GetAttributes()
            .Single(attribute => attribute.AttributeClass?.ToDisplayString() == typeof(GroundworkSchemaAttribute).FullName);
        var json = (string)assemblyAttribute.ConstructorArguments[0].Value!;
        var fingerprint = (string)assemblyAttribute.ConstructorArguments[1].Value!;
        Assert.Equal(GroundworkSchemaCanonical.Fingerprint(GroundworkSchemaCanonical.Read(json)), fingerprint);
        Assert.Contains("ix_tickets_status_created", json, StringComparison.Ordinal);
        Assert.Contains("\"includeNulls\":false", json, StringComparison.Ordinal);
        Assert.Contains("created_at", json, StringComparison.Ordinal);
        Assert.Contains("\"nullable\":true", json, StringComparison.Ordinal);

        using var emitted = new MemoryStream();
        Assert.True(result.OutputCompilation.Emit(emitted).Success);
        var runtimeAssembly = System.Reflection.Assembly.Load(emitted.ToArray());
        var definition = (Groundwork.Kernel.StorageUnit)runtimeAssembly
            .GetType("TicketStorageUnit")!
            .GetProperty("Definition")!
            .GetValue(null)!;
        Assert.Equal(json, GroundworkSchemaCanonical.Serialize(new SchemaDocument(
            [new SchemaTable(
                definition.Name,
                definition.Columns.Select(column => new SchemaColumn(
                    column.Name,
                    (SchemaValueType)Enum.Parse(typeof(SchemaValueType), column.Type.ToString()),
                    column.IsNullable,
                    column.MaxLength,
                    column.Precision,
                    column.Scale,
                    column.Collation == Groundwork.Kernel.PortableCollation.OrdinalIgnoreCase
                        ? TextFolding.AsciiIgnoreCase
                        : TextFolding.None,
                    column.Generation == Groundwork.Kernel.ColumnGeneration.ProviderSequence
                        ? SchemaGeneration.ProviderSequence
                        : SchemaGeneration.Supplied)),
                definition.Key.Columns,
                definition.Indexes.Select(index => new SchemaIndex(
                    index.Name,
                    index.Columns.Select(column => new SchemaIndexColumn(
                        column.Column,
                        column.Direction == Groundwork.Kernel.SortDirection.Descending)),
                    index.MissingValues == Groundwork.Kernel.MissingValueBehavior.Included,
                    index.IsUnique)))])));
    }

    [Fact]
    public void Lifecycle_policies_round_trip_from_attributes_to_the_compiled_declaration_in_canonical_order()
    {
        const string source = """
            #nullable enable
            using System;
            using Groundwork.Schema;

            [GwTable("orders", Scope = SchemaScope.Scoped, ConcurrencyToken = "version")]
            [GwIndex("z_orders_status", "status ASC")]
            [GwIndex("a_orders_customer", "customer ASC")]
            [GwRetention(50, "placed_at", Trigger = SchemaRetentionTrigger.OnAppend, PartitionBy = "status")]
            [GwAppendIdempotency("00:10:00")]
            [GwRetentionIdempotency("01:00:00", LedgerName = "orders_retention_ops")]
            [GwAggregate("z_daily", "day bucket_day placed_at, count orders, firstBy newest id placed_at DESC")]
            [GwAggregate("a_by_customer", "group customer, count orders, sum total amount")]
            public partial class Order
            {
                [GwKey, GwColumn(Length = 64)] public string Id { get; set; } = "";
                [GwColumn(Length = 64, Folding = TextFolding.AsciiIgnoreCase)] public string Customer { get; set; } = "";
                [GwColumn] public DateTimeOffset PlacedAt { get; set; }
                [GwColumn(Precision = 12, Scale = 2, Default = "0")] public decimal Amount { get; set; }
                [GwColumn(Length = 16, Default = "pending")] public string Status { get; set; } = "";
            }
            """;

        var result = Run(source);

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(result.OutputCompilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var canonical = (string)result.OutputCompilation.Assembly.GetAttributes()
            .Single(attribute => attribute.AttributeClass?.ToDisplayString() == typeof(GroundworkSchemaAttribute).FullName)
            .ConstructorArguments[0].Value!;

        using var emitted = new MemoryStream();
        Assert.True(result.OutputCompilation.Emit(emitted).Success);
        var generated = (Groundwork.Kernel.StorageUnit)System.Reflection.Assembly.Load(emitted.ToArray())
            .GetType("OrderStorageUnit")!.GetProperty("Definition")!.GetValue(null)!;
        var compiled = SchemaTool.SchemaCompilation.Compile(
            Assert.Single(GroundworkSchemaCanonical.Read(canonical).Tables));

        Assert.Equal(
            new Groundwork.Kernel.Schema.SchemaSubject(compiled).Fingerprint,
            new Groundwork.Kernel.Schema.SchemaSubject(generated).Fingerprint);
        Assert.Equal(Groundwork.Kernel.ScopePolicy.Scoped, generated.Scope);
        Assert.Equal("version", generated.Concurrency.TokenColumn);
        Assert.Equal(Groundwork.Kernel.RetentionTrigger.OnAppend, generated.Retention!.Trigger);
        Assert.Equal(TimeSpan.FromMinutes(10), generated.AppendIdempotency!.Window);
        Assert.Equal("orders_retention_ops", generated.RetentionIdempotency!.LedgerName);
        Assert.Equal("pending", generated.Columns.Single(column => column.Name == "status").Default!.Value);
        Assert.Equal(["a_orders_customer", "z_orders_status"], generated.Indexes.Select(index => index.Name));
        Assert.Equal(["a_by_customer", "z_daily"], generated.AggregationProfiles.Select(profile => profile.Name));
    }

    [Fact]
    public void The_documented_attribute_example_compiles_and_builds_its_unit()
    {
        const string source = """
            #nullable enable
            using Groundwork.Schema;

            [GwTable("orders", Scope = SchemaScope.Scoped, ConcurrencyToken = "version")]
            [GwRetention(1000, "seq", Trigger = SchemaRetentionTrigger.OnAppend, PartitionBy = "status")]
            [GwAppendIdempotency("00:10:00")]
            [GwRetentionIdempotency("1.00:00:00")]
            [GwAggregate("summary", "group status, count n")]
            public sealed class Order
            {
                [GwKey, GwColumn(Length = 64)] public string Id { get; init; } = "";
                [GwColumn(Length = 16, Default = "pending")] public string Status { get; init; } = "";
                [GwColumn(Required = true)] public long Seq { get; init; }
            }
            """;

        var result = Run(source);

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(result.OutputCompilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Equal(TimeSpan.FromDays(1), Definition(result, "OrderStorageUnit").RetentionIdempotency!.Window);
    }

    [Fact]
    public void A_column_only_group_by_expression_generates_compilable_source()
    {
        const string json = """
            {"tables":[{"name":"orders","columns":[{"name":"id","type":"String","nullable":false,"length":64},{"name":"status","type":"String","nullable":false,"length":16}],"key":["id"],"indexes":[],"aggregations":[{"name":"summary","groupByColumns":[],"groupBy":[{"alias":"status","bucket":"None","sourceColumn":null,"widthTicks":0}],"aggregates":[{"kind":"Count","alias":"n","column":null,"orderBy":null,"descending":false,"maxValues":0}]}]}]}
            """;

        var result = Run("public static class Empty { }", new InMemoryAdditionalText("schema/groundwork.json", json));

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(result.OutputCompilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var profile = Assert.Single(Definition(result, "ordersStorageUnit").AggregationProfiles);
        Assert.Equal("status", Assert.Single(profile.GroupByExpressions).Alias);
    }

    [Theory]
    [InlineData("[GwAggregate(\"summary\", \"group status, day d created_at, count n\")]")]
    [InlineData("[GwAggregate(\"summary\", \"group status, count n\")] [GwAggregate(\"summary\", \"group id, count n\")]")]
    public void Aggregation_specs_the_kernel_refuses_are_build_diagnostics(string attributes)
    {
        var result = Run($$"""
            #nullable enable
            using System;
            using Groundwork.Schema;

            [GwTable("orders")]
            {{attributes}}
            public sealed class Order
            {
                [GwKey, GwColumn(Length = 64)] public string Id { get; init; } = "";
                [GwColumn(Length = 16)] public string Status { get; init; } = "";
                [GwColumn] public DateTimeOffset CreatedAt { get; init; }
            }
            """);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "GW_SCHEMA_TABLE_003");
    }

    private static Groundwork.Kernel.StorageUnit Definition(GeneratorRunResult result, string typeName)
    {
        using var emitted = new MemoryStream();
        Assert.True(result.OutputCompilation.Emit(emitted).Success);
        return (Groundwork.Kernel.StorageUnit)System.Reflection.Assembly.Load(emitted.ToArray())
            .GetType(typeName)!.GetProperty("Definition")!.GetValue(null)!;
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
    public void All_variables_in_a_field_declaration_are_generated()
    {
        const string source = "using Groundwork.Schema; [GwTable(\"tickets\")] public partial class Ticket { [GwKey, GwColumn] public string Id = \"\", Other = \"\"; }";

        var result = Run(source);

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains(result.Generated, generated => generated.Contains(".Column(\"other\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Duplicate_column_names_are_diagnosed()
    {
        const string source = "using Groundwork.Schema; [GwTable(\"tickets\")] public partial class Ticket { [GwKey, GwColumn(Name = \"same\")] public string Id { get; set; } = \"\"; [GwColumn(Name = \"same\")] public string Other { get; set; } = \"\"; }";

        var result = Run(source);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "GW_SCHEMA_COLUMN_002");
    }

    [Fact]
    public void Same_clr_type_name_in_different_namespaces_has_unique_generated_sources()
    {
        const string source = "using Groundwork.Schema; namespace A { [GwTable(\"a_tickets\")] public partial class Ticket { [GwKey, GwColumn] public string Id { get; set; } = \"\"; } } namespace B { [GwTable(\"b_tickets\")] public partial class Ticket { [GwKey, GwColumn] public string Id { get; set; } = \"\"; } }";

        var result = Run(source);

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Equal(2, result.Generated.Count(generated => generated.Contains("StorageUnit Definition", StringComparison.Ordinal)));
    }

    [Fact]
    public void Partial_table_declarations_are_combined_into_one_schema()
    {
        const string source = "using Groundwork.Schema; [GwTable(\"tickets\")] public partial class Ticket { [GwKey, GwColumn] public string Id { get; set; } = \"\"; } public partial class Ticket { [GwColumn] public string Status { get; set; } = \"\"; }";

        var result = Run(source);

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains(result.Generated, generated => generated.Contains(".Column(\"status\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Repeated_generator_runs_have_identical_outputs_and_fingerprints()
    {
        const string source = "using Groundwork.Schema; [GwTable(\"tickets\")] public partial class Ticket { [GwKey, GwColumn] public string Id { get; set; } = \"\"; }";

        var first = Run(source);
        var second = Run(source);

        Assert.Equal(first.Generated, second.Generated);
        var firstFingerprint = first.OutputCompilation.Assembly.GetAttributes()
            .Single(attribute => attribute.AttributeClass?.ToDisplayString() == typeof(GroundworkSchemaAttribute).FullName)
            .ConstructorArguments[1].Value;
        var secondFingerprint = second.OutputCompilation.Assembly.GetAttributes()
            .Single(attribute => attribute.AttributeClass?.ToDisplayString() == typeof(GroundworkSchemaAttribute).FullName)
            .ConstructorArguments[1].Value;
        Assert.Equal(firstFingerprint, secondFingerprint);
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
        Assert.Contains(result.Generated, generated => generated.Contains("ticketsStorageUnit", StringComparison.Ordinal));
    }

    [Fact]
    public void Additional_file_does_not_mask_an_invalid_attribute_schema()
    {
        const string json = "{\"tables\":[{\"name\":\"tickets\",\"columns\":[{\"name\":\"id\",\"type\":\"String\",\"nullable\":false}],\"key\":[\"id\"],\"indexes\":[]}] }";
        const string source = "using Groundwork.Schema; [GwTable(\"invalid\")] public partial class Invalid { [GwColumn] public string Id { get; set; } = \"\"; }";

        var result = Run(source, new InMemoryAdditionalText("schema/groundwork.json", json));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "GW_SCHEMA_TABLE_001");
        Assert.DoesNotContain(result.Generated, generated => generated.Contains("ticketsStorageUnit", StringComparison.Ordinal));
    }

    [Fact]
    public void Referenced_schema_fingerprint_is_verified_before_consumption()
    {
        const string source = "using Groundwork.Schema; [assembly: GroundworkSchema(\"{\\\"tables\\\":[]}\", \"stale\")] public static class Empty { }";
        var referenceCompilation = CSharpCompilation.Create(
            "StaleSchema",
            [CSharpSyntaxTree.ParseText(SourceText.From(source))],
            References(typeof(GroundworkSchemaAttribute), typeof(object)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        Assert.True(referenceCompilation.Emit(stream).Success);
        stream.Position = 0;

        var consumer = CSharpCompilation.Create(
            "Consumer",
            [CSharpSyntaxTree.ParseText(SourceText.From("public static class Query { }"))],
            References(typeof(GroundworkSchemaAttribute), typeof(object)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        Assert.Throws<FormatException>(() => GroundworkSchemaMetadata.Read(
            consumer.AddReferences(MetadataReference.CreateFromStream(stream))));
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
