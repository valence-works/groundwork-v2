using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Groundwork.Documents;
using Groundwork.Documents.Serialization;
using Groundwork.Kernel;
using Groundwork.Records;
using Xunit;

namespace Groundwork.Documents.Tests;

public sealed class DocumentsContractTests
{
    [Fact]
    public void Canonical_json_is_sorted_and_projection_values_are_typed()
    {
        var unit = DocumentUnit.For<Invoice>("invoice", "invoices")
            .Id(invoice => invoice.Id)
            .Project(invoice => invoice.Customer.Name)
            .Project(invoice => invoice.Total)
            .Project(invoice => invoice.Tags)
            .Build();

        var row = unit.ToRowValues(new Invoice(
            Guid.Parse("f4f6b4f9-4ee8-4a2c-9f17-1ee5dcb6db72"),
            new Customer("Ada", null),
            12.50m,
            ["priority", "paid"]));

        Assert.Equal("Ada", row["name"]);
        Assert.Equal(12.50m, row["total"]);
        var tags = Assert.IsType<JsonElement>(row["tags"]);
        Assert.Equal(JsonValueKind.Array, tags.ValueKind);
        Assert.Equal("{\"customer\":{\"name\":\"Ada\",\"phone\":null},\"id\":\"f4f6b4f9-4ee8-4a2c-9f17-1ee5dcb6db72\",\"tags\":[\"priority\",\"paid\"],\"total\":12.50}", row["document"]);
    }

    [Fact]
    public void Json_property_names_and_index_direction_are_part_of_the_public_binding()
    {
        var unit = DocumentUnit.For<NamedDocument>("named", "named")
            .Id(document => document.Id)
            .Project(document => document.DisplayName)
            .Index("by-display", document => document.DisplayName, SortDirection.Descending)
            .Build();

        Assert.Equal("display_name", Assert.Single(unit.Bindings).Path);
        var index = Assert.Single(unit.StorageUnit.Indexes);
        Assert.Equal(SortDirection.Descending, Assert.Single(index.Columns).Direction);
    }

    [Fact]
    public void Enum_projection_matches_the_actual_json_converter_encoding()
    {
        var numeric = DocumentUnit.For<EnumDocument>("enum", "enum_numeric")
            .Id(document => document.Id)
            .Project(document => document.Status)
            .Build();
        var numericRow = numeric.ToRowValues(new EnumDocument(Guid.NewGuid(), OrderStatus.Paid));
        Assert.Equal(PortableType.Int32, Assert.Single(numeric.Bindings).Type);
        Assert.Equal(1, numericRow["status"]);

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        var text = DocumentUnit.For<EnumDocument>("enum", "enum_text")
            .JsonOptions(options)
            .Id(document => document.Id)
            .Project(document => document.Status)
            .Build();
        var textRow = text.ToRowValues(new EnumDocument(Guid.NewGuid(), OrderStatus.Paid));
        Assert.Equal(PortableType.String, Assert.Single(text.Bindings).Type);
        Assert.Equal("paid", textRow["status"]);
    }

    [Fact]
    public void Json_options_are_resolved_at_build_regardless_of_call_order()
    {
        var before = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        before.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        var after = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        after.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

        var optionsFirst = DocumentUnit.For<EnumDocument>("enum", "options_first")
            .JsonOptions(before)
            .Id(document => document.Id)
            .Project(document => document.Status)
            .Build();
        var optionsLast = DocumentUnit.For<EnumDocument>("enum", "options_last")
            .Id(document => document.Id)
            .Project(document => document.Status)
            .JsonOptions(after)
            .Build();

        Assert.Equal(optionsFirst.Bindings, optionsLast.Bindings);
        Assert.Equal(optionsFirst.ToRowValues(new EnumDocument(Guid.NewGuid(), OrderStatus.Paid))["status"],
            optionsLast.ToRowValues(new EnumDocument(Guid.NewGuid(), OrderStatus.Paid))["status"]);
        Assert.Equal(PortableType.String, Assert.Single(optionsLast.Bindings).Type);
    }

    [Fact]
    public void Generic_and_property_enum_converters_match_projection_encoding()
    {
        var genericOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        genericOptions.Converters.Add(new JsonStringEnumConverter<OrderStatus>(JsonNamingPolicy.CamelCase));
        var generic = DocumentUnit.For<EnumDocument>("enum", "generic_enum")
            .JsonOptions(genericOptions)
            .Id(document => document.Id)
            .Project(document => document.Status)
            .Build();

        var attributed = DocumentUnit.For<AttributedEnumDocument>("enum", "attributed_enum")
            .Id(document => document.Id)
            .Project(document => document.Status)
            .Build();

        Assert.Equal(PortableType.String, Assert.Single(generic.Bindings).Type);
        Assert.Equal("paid", generic.ToRowValues(new EnumDocument(Guid.NewGuid(), OrderStatus.Paid))["status"]);
        Assert.Equal(PortableType.String, Assert.Single(attributed.Bindings).Type);
        Assert.Equal("Paid", attributed.ToRowValues(new AttributedEnumDocument(Guid.NewGuid(), OrderStatus.Paid))["status"]);
    }

    [Fact]
    public void Materialize_round_trips_a_plain_row_and_keeps_the_kernel_unit_document_free()
    {
        var unit = DocumentUnit.For<Invoice>("invoice", "invoices")
            .Id(invoice => invoice.Id)
            .Project(invoice => invoice.Customer.Name)
            .OptimisticConcurrency()
            .Build();
        var value = new Invoice(Guid.NewGuid(), new Customer("Ada", null), 12.50m, []);

        var materialized = unit.Materialize(unit.ToRowValues(value));

        Assert.Equal(value.Id, materialized.Id);
        Assert.Equal(value.Customer, materialized.Customer);
        Assert.Equal(value.Total, materialized.Total);
        Assert.Equal(value.Tags, materialized.Tags);
        Assert.DoesNotContain(unit.StorageUnit.Columns, column => column.Name.Contains("Document", StringComparison.Ordinal));
        Assert.Equal(PortableType.Json, unit.StorageUnit.Columns.Single(column => column.Name == "document").Type);
        var references = typeof(DocumentUnit<>).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).Where(name => name?.StartsWith("Groundwork.", StringComparison.Ordinal) == true);
        Assert.Equal(["Groundwork.Kernel", "Groundwork.Records"], references.OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void Optimistic_document_writes_return_provider_version_results_without_mapping_the_system_token()
    {
        var unit = DocumentUnit.For<EnumDocument>("enum", "versioned_enum")
            .Id(document => document.Id)
            .Project(document => document.Status)
            .OptimisticConcurrency()
            .Build();
        var store = new CapturingRecordStore(new RecordWriteResult(RecordWriteStatus.Updated, 7));
        var session = unit.Open(store);
        var value = new EnumDocument(Guid.NewGuid(), OrderStatus.Paid);

        var result = session.Update(value, RecordWriteOptions.IfVersion(6));

        Assert.Equal(7, result.Version);
        Assert.DoesNotContain("version", store.LastValues!.Values.Keys);
        Assert.Equal(6, store.LastOptions!.ExpectedVersion);
        var read = unit.Read(store.LastValues, result.Version);
        Assert.Equal(value, read.Value);
        Assert.Equal(7, read.Version);
    }

    [Fact]
    public void Ambiguous_projection_and_unprojected_index_have_actionable_diagnostics()
    {
        var duplicate = Assert.Throws<DocumentDeclarationException>(() =>
            DocumentUnit.For<Invoice>("invoice", "invoices")
                .Id(invoice => invoice.Id)
                .Project(invoice => invoice.Customer.Name)
                .Project(invoice => invoice.Customer.Name)
                .Build());
        Assert.Contains(duplicate.Diagnostics, diagnostic => diagnostic.Code == "GW-DOC-DECL-002");

        var missing = Assert.Throws<DocumentDeclarationException>(() =>
            DocumentUnit.For<Invoice>("invoice", "invoices")
                .Id(invoice => invoice.Id)
                .Index("by-name", invoice => invoice.Customer.Name)
                .Build());
        Assert.Contains(missing.Diagnostics, diagnostic => diagnostic.Code == "GW-DOC-DECL-005");

        var jsonIndex = Assert.Throws<DocumentDeclarationException>(() =>
            DocumentUnit.For<Invoice>("invoice", "invoices")
                .Id(invoice => invoice.Id)
                .Project(invoice => invoice.Tags)
                .Index("by-tags", invoice => invoice.Tags)
                .Build());
        Assert.Contains(jsonIndex.Diagnostics, diagnostic => diagnostic.Code == "GW-DOC-DECL-006");
    }

    [Fact]
    public void Unsupported_unsigned_enum_projection_has_an_actionable_diagnostic()
    {
        var exception = Assert.Throws<DocumentDeclarationException>(() =>
            DocumentUnit.For<UnsignedEnumDocument>("enum", "unsigned_enum")
                .Id(document => document.Id)
                .Project(document => document.Status)
                .Build());

        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "GW-DOC-DECL-007");
    }

    [Fact]
    public void Unsupported_enum_converter_output_has_an_actionable_diagnostic()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new BooleanEnumConverter());

        var exception = Assert.Throws<DocumentDeclarationException>(() =>
            DocumentUnit.For<EnumDocument>("enum", "unsupported_converter")
                .JsonOptions(options)
                .Id(document => document.Id)
                .Project(document => document.Status)
                .Build());

        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "GW-DOC-DECL-008");
    }

    [Fact]
    public void Materialize_rejects_a_row_without_the_required_schema_stamp()
    {
        var unit = DocumentUnit.For<EnumDocument>("enum", "missing_stamp")
            .Id(document => document.Id)
            .Build();

        var row = new RowValues(new Dictionary<string, object?>
        {
            ["id"] = Guid.NewGuid(),
            ["document"] = "{\"id\":\"00000000-0000-0000-0000-000000000000\",\"status\":0}"
        });

        var exception = Assert.Throws<DocumentSchemaVersionException>(() => unit.Materialize(row));

        Assert.Equal(DocumentSchemaVersionFailure.MalformedStamp, exception.Failure);
        Assert.Contains("schemaVersion", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Upcasters_are_applied_contiguously_and_future_content_is_rejected()
    {
        var codec = new VersionedJsonDocumentCodec(
            [new DocumentSchemaVersionPolicy("invoice", 1, 3)],
            [
                new Rename("invoice", 1, "oldName", "name"),
                new Rename("invoice", 2, "name", "display_name")
            ],
            new DocumentSchemaVersionFormat(
                (_, stamp) => stamp.StartsWith('v') && int.TryParse(stamp.AsSpan(1), out var version) ? version : null,
                (_, version) => "v" + version),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var value = codec.Deserialize<NamedDocument>(new VersionedJsonPayload(
            "invoice", "v1", "{\"oldName\":\"Ada\",\"id\":\"00000000-0000-0000-0000-000000000000\"}"));

        Assert.Equal("Ada", value.DisplayName);
        var future = Assert.Throws<DocumentSchemaVersionException>(() => codec.Deserialize<NamedDocument>(new VersionedJsonPayload(
            "invoice", "v4", "{}")));
        Assert.Equal(DocumentSchemaVersionFailure.Future, future.Failure);
    }

    private sealed record Invoice(Guid Id, Customer Customer, decimal Total, IReadOnlyList<string> Tags);
    private sealed record Customer(string Name, string? Phone);
    private sealed record EnumDocument(Guid Id, OrderStatus Status);
    private sealed record AttributedEnumDocument(
        Guid Id,
        [property: JsonConverter(typeof(JsonStringEnumConverter))] OrderStatus Status);
    private sealed record UnsignedEnumDocument(Guid Id, UnsignedOrderStatus Status);
    private enum OrderStatus { Pending, Paid }
    private enum UnsignedOrderStatus : uint { Pending, Paid }

    private sealed class BooleanEnumConverter : JsonConverter<OrderStatus>
    {
        public override OrderStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetBoolean() ? OrderStatus.Paid : OrderStatus.Pending;

        public override void Write(Utf8JsonWriter writer, OrderStatus value, JsonSerializerOptions options) =>
            writer.WriteBooleanValue(value == OrderStatus.Paid);
    }

    private sealed record NamedDocument(
        Guid Id,
        [property: JsonPropertyName("display_name")] string DisplayName);

    private sealed class Rename(string kind, int from, string source, string target) : IDocumentJsonUpcaster
    {
        public string DocumentKind { get; } = kind;
        public int FromVersion { get; } = from;
        public JsonObject Upcast(JsonObject content)
        {
            content[target] = content[source]?.DeepClone();
            content.Remove(source);
            return content;
        }
    }

    private sealed class CapturingRecordStore(RecordWriteResult result) : IRecordStore
    {
        public RowValues? LastValues { get; private set; }
        public RecordWriteOptions? LastOptions { get; private set; }

        public RecordWriteResult Insert(Groundwork.Kernel.StorageUnit unit, RowValues values, RecordWriteOptions? options = null) => Capture(values, options);
        public RecordWriteResult Update(Groundwork.Kernel.StorageUnit unit, RowValues values, RecordWriteOptions? options = null) => Capture(values, options);
        public RecordWriteResult Upsert(Groundwork.Kernel.StorageUnit unit, RowValues values, RecordWriteOptions? options = null) => Capture(values, options);
        public RecordWriteResult Delete(Groundwork.Kernel.StorageUnit unit, RowValues key, RecordWriteOptions? options = null) => Capture(key, options);
        public RecordQueryResult Query(Groundwork.Query.Model.QueryRequest request, Groundwork.Query.Model.QueryRenderOptions? options = null) => new([]);

        private RecordWriteResult Capture(RowValues values, RecordWriteOptions? options)
        {
            LastValues = values;
            LastOptions = options;
            return result;
        }
    }
}
