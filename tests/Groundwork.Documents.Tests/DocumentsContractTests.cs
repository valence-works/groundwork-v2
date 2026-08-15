using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Groundwork.Documents;
using Groundwork.Documents.Serialization;
using Groundwork.Documents.Store;
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

        var value = codec.Deserialize<NamedDocument>(new DocumentEnvelope(
            "invoice", "id-1", "v1", 4, "{\"oldName\":\"Ada\",\"id\":\"00000000-0000-0000-0000-000000000000\"}", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));

        Assert.Equal("Ada", value.DisplayName);
        var future = Assert.Throws<DocumentSchemaVersionException>(() => codec.Deserialize<NamedDocument>(new DocumentEnvelope(
            "invoice", "id-1", "v4", 4, "{}", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)));
        Assert.Equal(DocumentSchemaVersionFailure.Future, future.Failure);
    }

    private sealed record Invoice(Guid Id, Customer Customer, decimal Total, IReadOnlyList<string> Tags);
    private sealed record Customer(string Name, string? Phone);

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
}
