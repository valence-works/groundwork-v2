using System.Collections.Immutable;
using Groundwork.Schema;
using Groundwork.Schema.Generator;
using Groundwork.Query.Linq;
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
        Assert.Contains(result.Generated, generated => generated.Contains("GwGeneratedRows.Register", StringComparison.Ordinal));
        Assert.Contains(result.Generated, generated => generated.Contains("static value => value.Status", StringComparison.Ordinal));
        Assert.Contains(result.Generated, generated => generated.Contains("\"CreatedAt\", \"created_at\"", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Generated, generated => generated.Contains("GetMember(", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Generated, generated => generated.Contains(".Compile()", StringComparison.Ordinal));
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
    public void Positional_record_columns_generate_from_symbols_with_exact_schema_column_names()
    {
        const string source = """
            using System;
            using Groundwork.Schema;

            [GwTable("events")]
            public sealed record Event(
                [property: GwKey, GwColumn(Name = "event_id", Required = true)] Guid Id,
                [property: GwColumn] DateTimeOffset CreatedAt);
            """;

        var result = Run(source);

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(result.OutputCompilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains(result.Generated, generated => generated.Contains("\"Id\", \"event_id\"", StringComparison.Ordinal));
        Assert.Contains(result.Generated, generated => generated.Contains("\"CreatedAt\", \"created_at\"", StringComparison.Ordinal));
        Assert.Contains(result.Generated, generated => generated.Contains("new global::Event(", StringComparison.Ordinal));

        var schema = result.OutputCompilation.Assembly.GetAttributes()
            .Single(attribute => attribute.AttributeClass?.ToDisplayString() == typeof(GroundworkSchemaAttribute).FullName);
        var json = (string)schema.ConstructorArguments[0].Value!;
        Assert.Contains("\"name\":\"event_id\"", json, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"created_at\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Init_only_table_members_generate_an_object_initializer_materializer()
    {
        const string source = """
            using Groundwork.Schema;

            [GwTable("tickets")]
            public sealed class Ticket
            {
                [GwKey, GwColumn(Name = "ticket_id", Required = true)]
                public string Id { get; init; } = "";

                [GwColumn]
                public required string Status { get; init; }
            }
            """;

        var result = Run(source);

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(result.OutputCompilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains(result.Generated, generated => generated.Contains("new global::Ticket()", StringComparison.Ordinal));
        Assert.Contains(result.Generated, generated => generated.Contains("Id = global::Groundwork.Query.Linq.GwGeneratedRowValue.Read<string>", StringComparison.Ordinal));
        Assert.Contains(result.Generated, generated => generated.Contains("Status = global::Groundwork.Query.Linq.GwGeneratedRowValue.Read<string>", StringComparison.Ordinal));
    }

    [Fact]
    public void Schema_only_consumers_do_not_receive_runtime_row_registrations()
    {
        const string source = "using Groundwork.Schema; [GwTable(\"tickets\")] public sealed class Ticket { [GwKey, GwColumn] public string Id { get; set; } = \"\"; }";

        var result = RunCore(source, Array.Empty<AdditionalText>(), includeGeneratedRows: false);

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(result.OutputCompilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(result.Generated, generated => generated.Contains("GwGeneratedRows", StringComparison.Ordinal));
    }

    [Fact]
    public void Inaccessible_schema_types_keep_the_compatibility_path_without_breaking_generation()
    {
        const string source = "using Groundwork.Schema; public sealed class Owner { [GwTable(\"tickets\")] private sealed class Ticket { [GwKey, GwColumn] public string Id { get; set; } = \"\"; } }";

        var result = Run(source);

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(result.OutputCompilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(result.Generated, generated => generated.Contains("GwGeneratedRows.Register", StringComparison.Ordinal));
    }

    [Fact]
    public void Select_lambdas_register_scalar_named_record_and_anonymous_projection_factories()
    {
        const string source = """
            #nullable enable
            using Groundwork.Query.Linq;
            using Groundwork.Schema;

            [GwTable("tickets")]
            public sealed class Ticket
            {
                [GwKey, GwColumn] public string Id { get; init; } = "";
                [GwColumn] public string? Status { get; init; }
                [GwColumn] public int Amount { get; init; }
            }

            public sealed class StatusView
            {
                public StatusView(string? status) => Status = status;
                public string? Status { get; }
            }

            public sealed class StatusInitView
            {
                public string? Status { get; init; }
            }

            public sealed record StatusRecord(string? Status);

            public sealed class StatusTargetView
            {
                public StatusTargetView(string? status) => Status = status;
                public string? Status { get; }
            }

            public sealed record DirectTableView(int Amount);

            public static class Queries
            {
                public static void Use(IGwQueryable<Ticket> query)
                {
                    _ = query.Select(ticket => ticket.Status);
                    _ = query.Select(ticket => new StatusView(ticket.Status));
                    _ = query.Select(ticket => new StatusInitView { Status = ticket.Status });
                    _ = query.Select(ticket => new StatusRecord(ticket.Status));
                    _ = query.Select<StatusTargetView>(ticket => new(ticket.Status));
                    _ = query.Select(ticket => new { ticket.Status, ticket.Amount });
                }

                public static void UseDirectTable(GwQueryTable<Ticket> table) =>
                    _ = table.Select(ticket => new DirectTableView(ticket.Amount));
            }
            """;

        var result = Run(source);

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(result.OutputCompilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains(result.Generated, generated => generated.Contains("GwGeneratedRows.RegisterProjection", StringComparison.Ordinal));
        Assert.Contains(result.Generated, generated => generated.Contains("GwGeneratedRowValue.ReadProjection<string>(values, columns, 0)", StringComparison.Ordinal));
        Assert.Contains(result.Generated, generated => generated.Contains("new global::StatusView(", StringComparison.Ordinal));
        Assert.Contains(result.Generated, generated => generated.Contains("new global::StatusInitView() { Status =", StringComparison.Ordinal));
        Assert.Contains(result.Generated, generated => generated.Contains("new global::StatusRecord(", StringComparison.Ordinal));
        Assert.Contains(result.Generated, generated => generated.Contains("new global::StatusTargetView(", StringComparison.Ordinal));
        Assert.Contains(result.Generated, generated => generated.Contains("new { Status =", StringComparison.Ordinal));
        Assert.Contains(result.Generated, generated => generated.Contains("ReadProjection<int>(values, columns, 1)", StringComparison.Ordinal));
        Assert.Contains(result.Generated, generated => generated.Contains("new global::DirectTableView(", StringComparison.Ordinal));
    }

    [Fact]
    public void Select_lambdas_register_a_declared_navigation_member_by_its_terminal_column()
    {
        const string source = """
            using Groundwork.Query.Linq;
            using Groundwork.Schema;

            [GwTable("tickets")]
            public sealed class Ticket
            {
                [GwKey, GwColumn] public string Id { get; init; } = "";
                public Customer Customer { get; init; } = new();
            }

            [GwTable("customers")]
            public sealed class Customer
            {
                [GwKey, GwColumn] public string Id { get; init; } = "";
                [GwColumn] public string Name { get; init; } = "";
            }

            public static class Queries
            {
                public static void Use(IGwQueryable<Ticket> query) => _ = query.Select(ticket => ticket.Customer.Name);
            }
            """;

        var result = Run(source);

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(result.OutputCompilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains(result.Generated, generated => generated.Contains("RegisterProjection(typeof(string), 1", StringComparison.Ordinal));
        Assert.Contains(result.Generated, generated => generated.Contains("ReadProjection<string>(values, columns, 0)", StringComparison.Ordinal));
    }

    [Fact]
    public void Select_lambdas_skip_ambiguous_inaccessible_generic_and_computed_shapes()
    {
        const string source = """
            using Groundwork.Query.Linq;
            using Groundwork.Schema;

            [GwTable("tickets")]
            public sealed class Ticket
            {
                [GwKey, GwColumn] public string Id { get; init; } = "";
                [GwColumn] public string Status { get; init; } = "";
            }

            public sealed class AmbiguousView
            {
                public AmbiguousView() { }
                public AmbiguousView(string status) => Status = status;
                public string Status { get; init; } = "";
            }

            public sealed class GenericView<T>
            {
                public GenericView(T status) => Status = status;
                public T Status { get; }
            }

            public static class Queries
            {
                private sealed class HiddenView
                {
                    public HiddenView(string status) => Status = status;
                    public string Status { get; }
                }

                public static void Use(IGwQueryable<Ticket> query)
                {
                    _ = query.Select(ticket => new AmbiguousView(ticket.Status));
                    _ = query.Select(ticket => new AmbiguousView { Status = ticket.Status });
                    _ = query.Select(ticket => new GenericView<string>(ticket.Status));
                    _ = query.Select(ticket => new HiddenView(ticket.Status));
                    _ = query.Select(ticket => ticket.Status.ToString());
                }
            }
            """;

        var result = Run(source);

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(result.OutputCompilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(result.Generated, generated => generated.Contains("GwGeneratedRows.RegisterProjection", StringComparison.Ordinal));
    }

    [Fact]
    public void A_generated_named_projection_factory_materializes_by_result_ordinal()
    {
        const string source = """
            using Groundwork.Query.Linq;
            using Groundwork.Schema;

            [GwTable("tickets")]
            public sealed class Ticket
            {
                [GwKey, GwColumn] public string Id { get; init; } = "";
                [GwColumn] public string Status { get; init; } = "";
            }

            public sealed record StatusView(string Status);

            public static class Queries
            {
                public static void Use(IGwQueryable<Ticket> query) => _ = query.Select(ticket => new StatusView(ticket.Status));
            }
            """;

        var result = Run(source);

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(result.OutputCompilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        using var emitted = new MemoryStream();
        Assert.True(result.OutputCompilation.Emit(emitted).Success);
        var assembly = System.Reflection.Assembly.Load(emitted.ToArray());
        var projectedType = assembly.GetType("StatusView")!;
        var lookup = typeof(GwGeneratedRows).GetMethods()
            .Single(method => method.Name == nameof(GwGeneratedRows.TryGetProjection) && method.IsGenericMethodDefinition)
            .MakeGenericMethod(projectedType);
        var arguments = new object?[] { 1, null };
        Assert.True((bool)lookup.Invoke(null, arguments)!);
        var materializer = (Delegate)arguments[1]!;
        var value = materializer.DynamicInvoke(
            new Dictionary<string, object?> { ["unrelated"] = "open" },
            new[] { "unrelated" });
        Assert.Equal("open", projectedType.GetProperty("Status")!.GetValue(value));
    }

    [Fact]
    public void A_generated_anonymous_projection_factory_uses_the_source_anonymous_type()
    {
        const string source = """
            using Groundwork.Query.Linq;
            using Groundwork.Schema;

            [GwTable("tickets")]
            public sealed class Ticket
            {
                [GwKey, GwColumn] public string Id { get; init; } = "";
                [GwColumn] public string Status { get; init; } = "";
                [GwColumn] public int Amount { get; init; }
            }

            public static class Queries
            {
                public static void Register(IGwQueryable<Ticket> query) => _ = query.Select(ticket => new { ticket.Status, ticket.Amount });
                public static object Create(Ticket ticket) => new { ticket.Status, ticket.Amount };
            }
            """;

        var result = Run(source);

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(result.OutputCompilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        using var emitted = new MemoryStream();
        Assert.True(result.OutputCompilation.Emit(emitted).Success);
        var assembly = System.Reflection.Assembly.Load(emitted.ToArray());
        var ticketType = assembly.GetType("Ticket")!;
        var ticket = Activator.CreateInstance(ticketType)!;
        ticketType.GetProperty("Status")!.SetValue(ticket, "open");
        ticketType.GetProperty("Amount")!.SetValue(ticket, 7);
        var anonymous = assembly.GetType("Queries")!.GetMethod("Create")!.Invoke(null, [ticket])!;
        var anonymousType = anonymous.GetType();
        var lookup = typeof(GwGeneratedRows).GetMethods()
            .Single(method => method.Name == nameof(GwGeneratedRows.TryGetProjection) && method.IsGenericMethodDefinition)
            .MakeGenericMethod(anonymousType);
        var arguments = new object?[] { 2, null };
        Assert.True((bool)lookup.Invoke(null, arguments)!);
        var value = ((Delegate)arguments[1]!).DynamicInvoke(
            new Dictionary<string, object?> { ["status"] = "closed", ["amount"] = 9 },
            new[] { "status", "amount" })!;
        Assert.Equal("closed", anonymousType.GetProperty("Status")!.GetValue(value));
        Assert.Equal(9, anonymousType.GetProperty("Amount")!.GetValue(value));
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

    [Fact]
    public void A_json_default_generates_source_that_a_nullable_disabled_consumer_compiles_cleanly()
    {
        const string json = """
            {"tables":[{"name":"orders","columns":[{"name":"id","type":"String","nullable":false,"length":64},{"name":"payload","type":"Json","nullable":true,"default":{"value":{"items":[1,"two"],"active":true}}}],"key":["id"],"indexes":[]}]}
            """;

        var result = Run("public static class Empty { }", new InMemoryAdditionalText("schema/groundwork.json", json));

        Assert.DoesNotContain(result.OutputCompilation.GetDiagnostics(), diagnostic => diagnostic.Id == "CS8669");
        Assert.DoesNotContain(result.OutputCompilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var payload = Assert.IsType<Dictionary<string, object?>>(
            Definition(result, "ordersStorageUnit").Columns.Single(column => column.Name == "payload").Default!.Value);
        Assert.True((bool)payload["active"]!);
    }

    [Theory]
    [InlineData("""[GwTable("")]""")]
    [InlineData("""[GwTable("orders", ConcurrencyToken = "")]""")]
    [InlineData("""[GwTable("orders")] [GwRetention(50, "")]""")]
    [InlineData("""[GwTable("orders")] [GwAppendIdempotency("00:10:00", LedgerName = "")]""")]
    [InlineData("""[GwTable("orders")] [GwAggregate("", "group status, count n")]""")]
    [InlineData("""[GwTable("orders")] [GwIndex("", "status ASC")]""")]
    public void An_empty_lifecycle_value_is_a_build_diagnostic_not_a_generator_fault(string attributes)
    {
        var result = Run($$"""
            #nullable enable
            using Groundwork.Schema;

            {{attributes}}
            public sealed class Order
            {
                [GwKey, GwColumn(Length = 64)] public string Id { get; init; } = "";
                [GwColumn(Length = 16)] public string Status { get; init; } = "";
            }
            """);

        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Id is "GW_SCHEMA_TABLE_003" or "GW_SCHEMA_INDEX_001");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Id == "CS8785");
    }

    [Fact]
    public void An_empty_column_name_is_a_build_diagnostic_not_a_generator_fault()
    {
        var result = Run("""
            #nullable enable
            using Groundwork.Schema;

            [GwTable("orders")]
            public sealed class Order
            {
                [GwKey, GwColumn(Name = "", Length = 64)] public string Id { get; init; } = "";
            }
            """);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "GW_SCHEMA_TABLE_003");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Id == "CS8785");
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

    /// <summary>
    /// A declared logical id has to reach both consumers of the canonical artifact, or a renamed
    /// column would deploy under one identity and be admitted at runtime under another.
    /// </summary>
    [Fact]
    public void A_declared_logical_id_reaches_the_canonical_document_and_the_generated_unit()
    {
        const string source = """
            using Groundwork.Schema;

            [GwTable("purchase_orders", Id = "orders")]
            public partial class Order
            {
                [GwKey, GwColumn(Length = 64)] public string Id { get; set; } = "";
                [GwColumn(Name = "buyer", Id = "customer", Length = 64)] public string Buyer { get; set; } = "";
            }
            """;

        var result = Run(source);

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var canonical = (string)result.OutputCompilation.Assembly.GetAttributes()
            .Single(attribute => attribute.AttributeClass?.ToDisplayString() == typeof(GroundworkSchemaAttribute).FullName)
            .ConstructorArguments[0].Value!;
        var table = Assert.Single(GroundworkSchemaCanonical.Read(canonical).Tables);
        Assert.Equal("orders", table.LogicalId);
        Assert.Equal("purchase_orders", table.Name);
        Assert.Equal("customer", table.Columns.Single(column => column.Name == "buyer").LogicalId);

        using var emitted = new MemoryStream();
        Assert.True(result.OutputCompilation.Emit(emitted).Success);
        var generated = (Groundwork.Kernel.StorageUnit)System.Reflection.Assembly.Load(emitted.ToArray())
            .GetType("OrderStorageUnit")!.GetProperty("Definition")!.GetValue(null)!;
        Assert.Equal("orders", generated.Id.Value);
        Assert.Equal("customer", generated.Columns.Single(column => column.Name == "buyer").LogicalId);
        // The tool-compiled target and the runtime's expected target stay one value.
        Assert.Equal(
            new Groundwork.Kernel.Schema.SchemaSubject(SchemaTool.SchemaCompilation.Compile(table)).Fingerprint,
            new Groundwork.Kernel.Schema.SchemaSubject(generated).Fingerprint);
    }

    /// <summary>
    /// Adding logical ids must not restate every already-emitted schema. A declaration that never
    /// renames anything has to serialize to exactly the bytes it did before, or every deployed
    /// catalog would hit a persisted schema boundary for a feature it does not use.
    /// </summary>
    [Fact]
    public void An_undeclared_logical_id_leaves_the_canonical_document_byte_identical()
    {
        const string json =
            "{\"tables\":[{\"name\":\"tickets\",\"columns\":[{\"name\":\"id\",\"type\":\"String\"," +
            "\"nullable\":false,\"length\":64,\"precision\":null,\"scale\":null,\"folding\":\"None\"," +
            "\"generation\":\"Supplied\",\"default\":null}],\"key\":[\"id\"],\"indexes\":[],\"scope\":\"Global\"," +
            "\"concurrency\":null,\"timestamps\":\"None\",\"retention\":null,\"appendIdempotency\":null," +
            "\"retentionIdempotency\":null,\"aggregations\":[]}]}";

        Assert.Equal(json, GroundworkSchemaCanonical.Serialize(GroundworkSchemaCanonical.Parse(json)));
    }

    /// <summary>
    /// The foreign-column policy travels in the schema document, so the deployment tool and the
    /// host reach the same verdict about an undeclared deployed column from one declaration. It is
    /// emitted only once it diverges from the default, for the same reason logical ids are.
    /// </summary>
    [Fact]
    public void The_foreign_column_policy_round_trips_and_stays_absent_at_its_default()
    {
        const string prefix =
            "{\"tables\":[{\"name\":\"tickets\",\"columns\":[{\"name\":\"id\",\"type\":\"String\"," +
            "\"nullable\":false,\"length\":64,\"precision\":null,\"scale\":null,\"folding\":\"None\"," +
            "\"generation\":\"Supplied\",\"default\":null}],\"key\":[\"id\"],\"indexes\":[],\"scope\":\"Global\"," +
            "\"concurrency\":null,\"timestamps\":\"None\",\"retention\":null,\"appendIdempotency\":null," +
            "\"retentionIdempotency\":null,\"aggregations\":[]";
        const string strict = prefix + "}]}";
        const string tolerant = prefix + ",\"foreignColumns\":\"TolerateDatabaseSupplied\"}]}";

        Assert.Equal(strict, GroundworkSchemaCanonical.Serialize(GroundworkSchemaCanonical.Parse(strict)));
        Assert.Equal(tolerant, GroundworkSchemaCanonical.Serialize(GroundworkSchemaCanonical.Parse(tolerant)));
        Assert.Equal(
            SchemaForeignColumns.TolerateDatabaseSupplied,
            GroundworkSchemaCanonical.Parse(tolerant).Tables[0].ForeignColumns);
        Assert.Equal(
            Groundwork.Kernel.ForeignColumnPolicy.TolerateDatabaseSupplied,
            SchemaTool.SchemaCompilation.Compile(GroundworkSchemaCanonical.Parse(tolerant).Tables[0]).ForeignColumns);

        // Tolerance is not part of the physical target, so it does not move the fingerprint the
        // deployed catalog is admitted against.
        Assert.Equal(
            new Groundwork.Kernel.Schema.SchemaSubject(
                SchemaTool.SchemaCompilation.Compile(GroundworkSchemaCanonical.Parse(strict).Tables[0])).Fingerprint,
            new Groundwork.Kernel.Schema.SchemaSubject(
                SchemaTool.SchemaCompilation.Compile(GroundworkSchemaCanonical.Parse(tolerant).Tables[0])).Fingerprint);
    }

    /// <summary>
    /// The attribute surface reaches the same declaration the document does, so a source-generated
    /// schema can opt in without hand-writing JSON.
    /// </summary>
    [Fact]
    public void The_foreign_column_policy_survives_the_generator()
    {
        const string source = """
            using Groundwork.Schema;
            [GwTable("tickets", ForeignColumns = SchemaForeignColumns.TolerateDatabaseSupplied)]
            public partial class Ticket { [GwKey][GwColumn(Length = 64)] public string Id { get; set; } = ""; }
            """;
        var result = Run(source);

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var canonical = (string)result.OutputCompilation.Assembly.GetAttributes()
            .Single(attribute => attribute.AttributeClass?.ToDisplayString() == typeof(GroundworkSchemaAttribute).FullName)
            .ConstructorArguments[0].Value!;
        Assert.Contains("\"foreignColumns\":\"TolerateDatabaseSupplied\"", canonical, StringComparison.Ordinal);
        Assert.Contains(
            result.Generated,
            generated => generated.Contains(".TolerateForeignColumns()", StringComparison.Ordinal));
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
    public void Additional_file_references_reach_the_generated_runtime_unit()
    {
        const string json = """
            {"tables":[
              {"name":"customers","columns":[{"name":"id","type":"Guid","nullable":false}],"key":["id"],"indexes":[]},
              {"name":"orders","columns":[{"name":"id","type":"Guid","nullable":false},{"name":"customer_id","type":"Guid","nullable":false}],"key":["id"],"indexes":[{"name":"by_customer","columns":[{"name":"customer_id","descending":false}],"includeNulls":true,"unique":false}],"references":[{"name":"customer","target":"customers","columns":["customer_id"]}]}
            ]}
            """;

        var result = Run("public static class Empty { }", new InMemoryAdditionalText("schema/groundwork.json", json));

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(result.OutputCompilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var reference = Assert.Single(Definition(result, "ordersStorageUnit").References);
        Assert.Equal("customer", reference.Name);
        Assert.Equal("customers", reference.TargetUnitId.Value);
        Assert.Equal(["customer_id"], reference.Columns);
        Assert.Contains(result.Generated, generated => generated.Contains(".Reference(\"customer\"", StringComparison.Ordinal));
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
        => RunCore(source, additionalFiles, includeGeneratedRows: true);

    private static GeneratorRunResult RunCore(
        string source,
        IReadOnlyList<AdditionalText> additionalFiles,
        bool includeGeneratedRows)
    {
        var references = References(includeGeneratedRows
            ? new[] { typeof(GroundworkSchemaAttribute), typeof(Groundwork.Kernel.StorageUnit), typeof(GwGeneratedRows), typeof(object) }
            : new[] { typeof(GroundworkSchemaAttribute), typeof(Groundwork.Kernel.StorageUnit), typeof(object) });
        if (!includeGeneratedRows)
            references = references.Where(reference => !string.Equals(reference.Display, typeof(GwGeneratedRows).Assembly.Location, StringComparison.OrdinalIgnoreCase));
        var compilation = CSharpCompilation.Create(
            "GeneratorInput",
            [CSharpSyntaxTree.ParseText(SourceText.From(source, encoding: System.Text.Encoding.UTF8))],
            references,
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
