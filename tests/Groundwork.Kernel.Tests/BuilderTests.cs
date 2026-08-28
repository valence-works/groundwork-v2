using System.Collections;
using System.Text.Json;
using Groundwork.Kernel;
using Groundwork.Records;
using KernelStorageUnit = Groundwork.Kernel.StorageUnit;
using Xunit;

namespace Groundwork.Kernel.Tests;

public sealed class BuilderTests
{
    [Fact]
    public void Fluent_customer_declaration_builds_a_plain_storage_unit()
    {
        var customer = Groundwork.Records.StorageUnit
            .Declare("customer", "customers")
            .Guid("id", column => column.Required())
            .String("name", 200, column => column.Required())
            .String("email", 320, column => column.Required())
            .Timestamp("createdAt", column => column.Required())
            .Boolean("isActive", column => column.Required().Default(true))
            .Decimal("balance", 19, 4, column => column.Required())
            .Key("id")
            .UniqueIndex("by_email", "email")
            .Index("by_created", index => index.Descending("createdAt"))
            .Build();

        Assert.Equal("customer", customer.Id.Value);
        Assert.Equal("customers", customer.Name);
        Assert.Equal(["id"], customer.Key.Columns);
        Assert.Equal(6, customer.Columns.Count);
        Assert.Equal(2, customer.Indexes.Count);
        Assert.Equal(PortableType.Guid, customer.Columns[0].Type);
        Assert.False(customer.Columns[0].IsNullable);
        Assert.Equal(true, customer.Columns[4].Default!.Value);
        Assert.Equal(SortDirection.Descending, customer.Indexes[1].Columns.Single().Direction);
    }

    [Fact]
    public void Fluent_index_can_exclude_missing_values_while_defaulting_to_included()
    {
        var definition = Groundwork.Kernel.StorageUnit
            .Declare("sparse", "sparse")
            .String("id", 32, column => column.Required())
            .String("email", 320)
            .Key("id")
            .UniqueIndex("by_email", index => index
                .Column("email")
                .ExcludeMissingValues())
            .Index("by_id", "id")
            .Build();

        Assert.Equal(MissingValueBehavior.Excluded, definition.Indexes[0].MissingValues);
        Assert.Equal(MissingValueBehavior.Included, definition.Indexes[1].MissingValues);
    }

    [Fact]
    public void Fluent_build_reports_all_portability_failures_together()
    {
        var exception = Assert.Throws<StorageDeclarationException>(() =>
            Groundwork.Records.StorageUnit
                .Declare("invalid", "invalid")
                .String("name", column => column.Required())
                .Decimal("amount")
                .Key("name")
                .Index("by_name", "name")
                .Build());

        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "GW-PORT-002");
        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "GW-PORT-003");
        Assert.Equal(2, exception.Diagnostics.Count);
    }

    [Fact]
    public void Fluent_build_refuses_a_default_with_the_wrong_clr_type()
    {
        var exception = Assert.Throws<StorageDeclarationException>(() =>
            Groundwork.Records.StorageUnit
                .Declare("invalid-default", "invalid_default")
                .Guid("id", column => column.Required())
                .Int64("attempts", column => column.Default(1.5d))
                .Key("id")
                .Build());

        var diagnostic = Assert.Single(exception.Diagnostics, item => item.Code == "GW-PORT-013");
        Assert.Equal("columns.attempts.default", diagnostic.Path);
        Assert.Contains("Int64", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("Double", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Fluent_build_reports_a_mutable_default_type_mismatch_at_the_declaration_boundary()
    {
        var exception = Assert.Throws<StorageDeclarationException>(() =>
            Groundwork.Records.StorageUnit
                .Declare("invalid-mutable-default", "invalid_mutable_default")
                .Guid("id", column => column.Required())
                .Int64("attempts", column => column.Default(new List<int> { 1 }))
                .Key("id")
                .Build());

        var diagnostic = Assert.Single(exception.Diagnostics, item => item.Code == "GW-PORT-013");
        Assert.Equal("columns.attempts.default", diagnostic.Path);
        Assert.Contains("Int64", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("List", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Fluent_build_snapshots_json_document_defaults()
    {
        using var document = JsonDocument.Parse("{\"state\":\"pending\",\"items\":[true,null]}");

        var definition = Groundwork.Records.StorageUnit
            .Declare("json-document-default", "json_document_default")
            .Guid("id", column => column.Required())
            .Json("payload", column => column.Default(document))
            .Key("id")
            .Build();

        var payload = Assert.IsType<Dictionary<string, object?>>(definition.Columns.Single(column => column.Name == "payload").Default!.Value);
        Assert.Equal("pending", payload["state"]);
        Assert.Equal([true, null], Assert.IsType<List<object?>>(payload["items"]));
    }

    [Fact]
    public void Typed_record_table_infers_columns_and_exposes_plain_definition()
    {
        var table = RecordTable.For<Customer>("customers")
            .Key(customer => customer.Id)
            .Column(customer => customer.Name, column => column.MaxLength(200))
            .Column(customer => customer.Email, column => column.MaxLength(320))
            .Column(customer => customer.Balance, column => column.Precision(19, 4))
            .UniqueIndex("by_email", customer => customer.Email)
            .Index("by_created", customer => customer.CreatedAt, SortDirection.Descending)
            .Build();

        KernelStorageUnit definition = table.Definition;
        Assert.Equal("customers", definition.Id.Value);
        Assert.Equal("customers", definition.Name);
        Assert.Equal(["id"], definition.Key.Columns);
        Assert.Equal(
            ["id", "name", "email", "createdAt", "isActive", "balance"],
            definition.Columns.Select(column => column.Name));
        Assert.Equal(PortableType.Guid, definition.Columns[0].Type);
        Assert.False(definition.Columns[0].IsNullable);
        Assert.Equal(PortableType.Decimal, definition.Columns[5].Type);
        Assert.Equal(19, definition.Columns[5].Precision);
        Assert.Equal(4, definition.Columns[5].Scale);
        Assert.DoesNotContain("RecordTable", definition.GetType().AssemblyQualifiedName, StringComparison.Ordinal);
    }

    [Fact]
    public void Typed_build_reports_portability_failures()
    {
        var exception = Assert.Throws<StorageDeclarationException>(() =>
            RecordTable.For<InvalidCustomer>("invalid")
                .Key(customer => customer.Id)
                .Column(customer => customer.Name)
                .UniqueIndex("by_name", customer => customer.Name)
                .Build());

        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "GW-PORT-003");
        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "GW-PORT-001");
    }

    [Fact]
    public void Builder_preserves_invalid_decimal_precision_for_K2_to_report()
    {
        var exception = Assert.Throws<StorageDeclarationException>(() =>
            Groundwork.Records.StorageUnit
                .Declare("invalid", "invalid")
                .Decimal("amount", 39, 0)
                .Key("amount")
                .Index("by_amount", "amount")
                .Build());

        Assert.Contains(exception.Diagnostics, diagnostic =>
            diagnostic.Code == "GW-PORT-004" && diagnostic.Message.Contains("precision 39", StringComparison.Ordinal));
    }

    [Fact]
    public void Built_definition_snapshots_are_not_backed_by_builder_mutation()
    {
        var builder = Groundwork.Records.StorageUnit
            .Declare("values", "values")
            .String("value", 10)
            .Key("value");

        var first = builder.Build();
        builder.String("other", 10);

        Assert.Single(first.Columns);
        Assert.Equal("value", first.Columns[0].Name);
    }

    [Fact]
    public void Built_definition_snapshots_mutable_binary_and_json_defaults()
    {
        var bytes = new byte[] { 1, 2 };
        var nested = new Dictionary<string, object?>
        {
            ["items"] = new List<object?>
            {
                new Dictionary<string, object?> { ["value"] = 1 }
            }
        };

        var definition = Groundwork.Records.StorageUnit
            .Declare("defaults", "defaults")
            .Int32("id", column => column.Required())
            .Binary("payload", 2, column => column.Default(bytes))
            .Json("metadata", column => column.Default(nested))
            .Key("id")
            .Build();

        bytes[0] = 9;
        ((List<object?>)nested["items"]!)[0] = new Dictionary<string, object?> { ["value"] = 9 };

        Assert.Equal(new byte[] { 1, 2 }, definition.Columns[1].Default!.Value);
        var storedMetadata = Assert.IsType<Dictionary<string, object?>>(definition.Columns[2].Default!.Value);
        var storedItems = Assert.IsType<List<object?>>(storedMetadata["items"]);
        var storedItem = Assert.IsType<Dictionary<string, object?>>(storedItems[0]);
        Assert.Equal(1, storedItem["value"]);
    }

    [Fact]
    public void Unsupported_or_cyclic_json_defaults_are_rejected_at_the_declaration_boundary()
    {
        var cyclic = new Dictionary<string, object?>();
        cyclic["self"] = cyclic;

        var exception = Assert.Throws<StorageDeclarationException>(() => Groundwork.Records.StorageUnit
            .Declare("cyclic", "cyclic")
            .Int32("id", column => column.Required())
            .Json("metadata", column => column.Default(cyclic))
            .Key("id")
            .Build());

        var diagnostic = Assert.Single(exception.Diagnostics, item => item.Code == "GW-PORT-013");
        Assert.Equal("columns.metadata.default", diagnostic.Path);
        Assert.Contains("Json", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("Dictionary", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Exception_snapshots_one_shot_diagnostics_once()
    {
        var source = new List<GroundworkDiagnostic>
        {
            new("GW-DECL-001", "first failure", "columns.first"),
            new("GW-DECL-002", "second failure", "columns.second")
        };
        var exception = new StorageDeclarationException(new OneShotDiagnostics(source));

        source[0] = new GroundworkDiagnostic("GW-DECL-999", "changed", "changed");

        Assert.Equal(2, exception.Diagnostics.Count);
        Assert.Equal("GW-DECL-001", exception.Diagnostics[0].Code);
        Assert.Contains("GW-DECL-001", exception.Message, StringComparison.Ordinal);
        Assert.Contains("GW-DECL-002", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_aggregates_declaration_and_portability_failures()
    {
        var exception = Assert.Throws<StorageDeclarationException>(() => Groundwork.Records.StorageUnit
            .Declare("invalid", "invalid")
            .Decimal("amount")
            .Build());

        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "GW-DECL-KEY-001");
        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "GW-PORT-002");
    }

    [Fact]
    public void Build_reports_missing_and_duplicate_key_and_index_columns()
    {
        var exception = Assert.Throws<StorageDeclarationException>(() => Groundwork.Records.StorageUnit
            .Declare("invalid", "invalid")
            .Int32("id")
            .Int32("value")
            .Key("id", "id", "missing")
            .Index("by_value", "missing", "value", "value")
            .Build());

        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "GW-DECL-KEY-002");
        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "GW-DECL-KEY-003");
        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "GW-DECL-INDEX-001");
        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "GW-DECL-INDEX-002");
    }

    [Fact]
    public void Fluent_build_refuses_regular_and_unique_indexes_over_portable_json()
    {
        var regular = Assert.Throws<StorageDeclarationException>(() => Groundwork.Records.StorageUnit
            .Declare("bad-json-index", "bad_json_index")
            .Int32("id", column => column.Required())
            .Json("payload")
            .Key("id")
            .Index("by_payload", "payload")
            .Build());
        var unique = Assert.Throws<StorageDeclarationException>(() => Groundwork.Records.StorageUnit
            .Declare("bad-unique-json-index", "bad_unique_json_index")
            .Int32("id", column => column.Required())
            .Json("payload")
            .Key("id")
            .UniqueIndex("by_payload", "payload")
            .Build());

        Assert.All(new[] { regular, unique }, error => Assert.Contains(
            error.Diagnostics,
            diagnostic => diagnostic.Code == "GW-DECL-INDEX-003" &&
                diagnostic.Path == "indexes.by_payload.columns[0]"));
    }

    [Fact]
    public void Fluent_build_allows_portable_json_when_it_is_not_indexed()
    {
        var definition = Groundwork.Records.StorageUnit
            .Declare("json-values", "json_values")
            .Int32("id", column => column.Required())
            .Json("payload")
            .Key("id")
            .Build();

        Assert.Equal(PortableType.Json, definition.Columns.Single(column => column.Name == "payload").Type);
        Assert.Empty(definition.Indexes);
    }

    [Fact]
    public void Committed_customer_sample_stays_within_the_issue_line_targets()
    {
        Assert.Equal("customers", BuilderCustomerSample.Customer.Name);
        Assert.Equal("customers", BuilderCustomerSample.Customers.Definition.Name);
        Assert.True(BuilderCustomerSample.FluentDeclarationLineCount <= 12);
        Assert.True(BuilderCustomerSample.TypedDeclarationLineCount <= 9);
    }

    public sealed record Customer(
        Guid Id,
        string Name,
        string Email,
        DateTimeOffset CreatedAt,
        bool IsActive,
        decimal Balance);

    public sealed record InvalidCustomer(Guid Id, string? Name);

    private sealed class OneShotDiagnostics : IEnumerable<GroundworkDiagnostic>
    {
        private readonly IReadOnlyList<GroundworkDiagnostic> diagnostics;
        private bool consumed;

        public OneShotDiagnostics(IReadOnlyList<GroundworkDiagnostic> diagnostics) => this.diagnostics = diagnostics;

        public IEnumerator<GroundworkDiagnostic> GetEnumerator()
        {
            if (consumed)
                throw new InvalidOperationException("This diagnostic sequence can only be enumerated once.");

            consumed = true;
            return diagnostics.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
