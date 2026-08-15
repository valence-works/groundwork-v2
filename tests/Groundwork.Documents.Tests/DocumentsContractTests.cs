using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Groundwork.Documents;
using Groundwork.Documents.Serialization;
using Groundwork.Kernel;
using Groundwork.Records;
using Groundwork.Store;
using Groundwork.Sqlite;
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
    public void Explicit_default_json_options_preserve_pascal_case_paths_and_values()
    {
        var unit = DocumentUnit.For<Invoice>("invoice", "pascal_options")
            .JsonOptions(new JsonSerializerOptions())
            .Id(invoice => invoice.Id)
            .Project(invoice => invoice.Customer.Name)
            .Build();

        var row = unit.ToRowValues(new Invoice(
            Guid.Parse("f4f6b4f9-4ee8-4a2c-9f17-1ee5dcb6db72"),
            new Customer("Ada", null),
            12.50m,
            []));

        Assert.Equal("Customer.Name", Assert.Single(unit.Bindings).Path);
        Assert.Equal("Ada", row[Assert.Single(unit.Bindings).Column]);
        Assert.Contains("\"Customer\":{\"Name\":\"Ada\"", (string)row["document"]!);
    }

    [Fact]
    public void Selected_fields_require_the_effective_serializer_field_policy()
    {
        var rejected = Assert.Throws<DocumentDeclarationException>(() =>
            DocumentUnit.For<FieldDocument>("field", "field_rejected")
                .Id(document => document.Id)
                .Project(document => document.Name)
                .Build());
        Assert.Contains(rejected.Diagnostics, diagnostic => diagnostic.Code == "GW-DOC-DECL-009");

        var options = new JsonSerializerOptions { IncludeFields = true };
        var unit = DocumentUnit.For<FieldDocument>("field", "field_included")
            .JsonOptions(options)
            .Id(document => document.Id)
            .Project(document => document.Name)
            .Build();

        var row = unit.ToRowValues(new FieldDocument { Id = Guid.NewGuid(), Name = "Ada" });

        Assert.Equal("Name", Assert.Single(unit.Bindings).Path);
        Assert.Equal("Ada", row[Assert.Single(unit.Bindings).Column]);
    }

    [Fact]
    public void Json_include_fields_are_serializable_without_global_field_inclusion()
    {
        var unit = DocumentUnit.For<IncludedFieldDocument>("field", "field_attribute")
            .JsonOptions(new JsonSerializerOptions())
            .Id(document => document.Id)
            .Project(document => document.Name)
            .Build();

        var row = unit.ToRowValues(new IncludedFieldDocument { Id = Guid.NewGuid(), Name = "Ada" });

        Assert.Equal("Name", Assert.Single(unit.Bindings).Path);
        Assert.Equal("Ada", row[Assert.Single(unit.Bindings).Column]);
    }

    [Fact]
    public void Ignored_identity_and_projection_members_fail_with_actionable_diagnostics()
    {
        var ignoredId = Assert.Throws<DocumentDeclarationException>(() =>
            DocumentUnit.For<IgnoredIdDocument>("ignored", "ignored_id")
                .Id(document => document.Id)
                .Build());
        Assert.Contains(ignoredId.Diagnostics, diagnostic => diagnostic.Code == "GW-DOC-DECL-009");

        var ignoredProjection = Assert.Throws<DocumentDeclarationException>(() =>
            DocumentUnit.For<IgnoredProjectionDocument>("ignored", "ignored_projection")
                .Id(document => document.Id)
                .Project(document => document.Name)
                .Build());
        Assert.Contains(ignoredProjection.Diagnostics, diagnostic => diagnostic.Code == "GW-DOC-DECL-009");

        var explicitlyIncluded = DocumentUnit.For<ExplicitlyIncludedDocument>("ignored", "ignored_never")
            .Id(document => document.Id)
            .Project(document => document.Name)
            .Build();
        Assert.Equal("included", explicitlyIncluded.ToRowValues(new ExplicitlyIncludedDocument
        {
            Id = Guid.NewGuid(),
            Name = "included"
        })[Assert.Single(explicitlyIncluded.Bindings).Column]);
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
        Assert.Equal(["Groundwork.Kernel", "Groundwork.Records", "Groundwork.Store"], references.OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void Document_mutations_are_ordinary_row_writes_with_typed_values_and_exact_preconditions()
    {
        var unit = DocumentUnit.For<EnumDocument>("enum", "versioned_enum")
            .Id(document => document.Id)
            .Project(document => document.Status)
            .OptimisticConcurrency()
            .Build();
        var value = new EnumDocument(Guid.NewGuid(), OrderStatus.Paid);

        var write = unit.Update(value, WriteOptions.IfVersion(6));

        Assert.Equal(RowWriteMode.Update, write.Mode);
        Assert.Equal(WritePreconditionKind.IfVersion, write.Options.Precondition.Kind);
        Assert.Equal(6, write.Options.Precondition.Version);
        Assert.Equal(value.Id, write.Values!.Values["id"]);
        Assert.Equal(1, write.Values.Values["status"]);
        Assert.DoesNotContain("version", write.Values.Values.Keys);

        var delete = unit.Delete(value, WriteOptions.IfVersion(6));
        Assert.Equal(RowWriteMode.Delete, delete.Mode);
        Assert.Null(delete.Values);
        Assert.Equal(value.Id, delete.Key!.Values["id"]);
    }

    [Fact]
    public void Document_execution_uses_the_same_row_write_contract_as_a_record()
    {
        var unit = DocumentUnit.For<EnumDocument>("enum", "executed_enum")
            .Id(document => document.Id)
            .OptimisticConcurrency()
            .Build();
        var session = new CapturingStorageSession(unit.StorageUnit, new WriteOutcome(WriteOutcomeStatus.Updated, 7));
        var connection = new CapturingStorageConnection(session);
        var value = new EnumDocument(Guid.NewGuid(), OrderStatus.Paid);

        var outcome = unit.Execute(connection, unit.Update(value, WriteOptions.IfVersion(6)));

        Assert.Equal(WriteOutcomeStatus.Updated, outcome.Status);
        Assert.Equal(RowWriteMode.Update, session.LastMode);
        Assert.Equal(value.Id, session.LastValues!.Values["id"]);
        Assert.Equal(WritePreconditionKind.IfVersion, session.LastOptions!.Precondition.Kind);
        Assert.Equal(6, session.LastOptions.Precondition.Version);
    }

    [Fact]
    public void Execute_rejects_a_write_from_another_same_id_declaration_before_opening_provider_state()
    {
        var first = DocumentUnit.For<EnumDocument>("first", "same_storage")
            .Id(document => document.Id)
            .Build();
        var other = DocumentUnit.For<EnumDocument>("other", "same_storage")
            .Id(document => document.Id)
            .Build();
        var session = new CapturingStorageSession(first.StorageUnit, new WriteOutcome(WriteOutcomeStatus.Updated, 1));
        var connection = new CapturingStorageConnection(session);

        Assert.Throws<ArgumentException>(() => first.Execute(
            connection,
            other.Update(new EnumDocument(Guid.NewGuid(), OrderStatus.Paid))));
        Assert.Equal(0, connection.OpenCount);
    }

    [Fact]
    public void Execute_passes_explicit_scoped_access_unchanged_to_the_provider_connection()
    {
        var unit = DocumentUnit.For<EnumDocument>("scoped", "scoped_documents")
            .Id(document => document.Id)
            .Scoped()
            .Build();
        var session = new CapturingStorageSession(unit.StorageUnit, new WriteOutcome(WriteOutcomeStatus.Inserted, 1));
        var connection = new CapturingStorageConnection(session);
        var access = StorageAccess.Scoped(new StorageScope("scope-a"));

        _ = unit.Execute(connection, unit.Insert(new EnumDocument(Guid.NewGuid(), OrderStatus.Paid)), access);

        Assert.Equal(1, connection.OpenCount);
        Assert.Same(access, connection.LastAccess);
    }

    [Fact]
    public void SQLite_cannot_distinguish_a_document_upsert_from_an_equivalent_row_upsert()
    {
        var unit = DocumentUnit.For<EnumDocument>("enum", "document_equivalence")
            .Id(document => document.Id)
            .Project(document => document.Status)
            .Build();
        using var store = TemporarySqliteStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        connection.Schema.Apply(unit.StorageUnit);
        var value = new EnumDocument(Guid.NewGuid(), OrderStatus.Paid);
        var documentObserver = new WritePathObserver();
        var documentWrite = unit.Upsert(value, new WriteOptions { Observer = documentObserver });

        var documentOutcome = unit.Execute(connection, documentWrite);

        var equivalentObserver = new WritePathObserver();
        var equivalentWrite = RowWrite.Upsert(
            unit.StorageUnit,
            new StorageValues(documentWrite.Values!.Values),
            new WriteOptions { Observer = equivalentObserver });
        var equivalentOutcome = connection.OpenSession(unit.StorageUnit, StorageAccess.Global)
            .Upsert(equivalentWrite.Values!, equivalentWrite.Options);

        Assert.True(documentOutcome.Succeeded);
        Assert.True(equivalentOutcome.Succeeded);
        Assert.Equal(RowWriteMode.Upsert, equivalentWrite.Mode);
        Assert.Equal(documentWrite.Values!.Values, equivalentWrite.Values!.Values);
        Assert.Equal(documentWrite.Options.Precondition.Kind, equivalentWrite.Options.Precondition.Kind);
        Assert.Single(documentObserver.Commands);
        Assert.Single(equivalentObserver.Commands);
        Assert.Equal(documentObserver.Commands[0], equivalentObserver.Commands[0]);
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
    public void Shared_kind_units_use_a_composite_discriminator_key_and_reject_cross_kind_rows()
    {
        var first = DocumentUnit.For<EnumDocument>("kind-a", "shared_documents")
            .Id(document => document.Id)
            .SharedKind()
            .Build();
        var second = DocumentUnit.For<EnumDocument>("kind-b", "shared_documents")
            .Id(document => document.Id)
            .SharedKind()
            .Build();
        var value = new EnumDocument(Guid.NewGuid(), OrderStatus.Paid);

        Assert.Equal(["kind", "id"], first.StorageUnit.Key.Columns);
        var firstDelete = first.Delete(value);
        var secondDelete = second.Delete(value);
        Assert.Equal("kind-a", firstDelete.Key!.Values["kind"]);
        Assert.Equal("kind-b", secondDelete.Key!.Values["kind"]);

        var crossKind = new RowValues(first.ToRowValues(value).Values.ToDictionary(
            pair => pair.Key,
            pair => pair.Key == "kind" ? "kind-b" : pair.Value,
            StringComparer.Ordinal));
        var exception = Assert.Throws<DocumentMaterializationException>(() => first.Materialize(crossKind));
        Assert.Equal("GW-DOC-MAT-004", exception.Code);
    }

    [Fact]
    public void Materialize_rejects_missing_and_mismatched_typed_identity_columns()
    {
        var guidUnit = DocumentUnit.For<EnumDocument>("enum", "identity_guid")
            .Id(document => document.Id)
            .Build();
        var value = new EnumDocument(Guid.NewGuid(), OrderStatus.Paid);
        var guidValues = guidUnit.ToRowValues(value).Values;

        var missing = new RowValues(guidValues
            .Where(pair => pair.Key != "id")
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
        Assert.Equal("GW-DOC-MAT-002", Assert.Throws<DocumentMaterializationException>(() => guidUnit.Materialize(missing)).Code);

        var mismatched = new RowValues(guidValues.ToDictionary(
            pair => pair.Key,
            pair => pair.Key == "id" ? Guid.NewGuid() : pair.Value,
            StringComparer.Ordinal));
        Assert.Equal("GW-DOC-MAT-003", Assert.Throws<DocumentMaterializationException>(() => guidUnit.Materialize(mismatched)).Code);

        var stringUnit = DocumentUnit.For<StringIdDocument>("string", "identity_string")
            .Id(document => document.Id)
            .Build();
        var stringValues = stringUnit.ToRowValues(new StringIdDocument("expected", "value")).Values;
        var stringMismatch = new RowValues(stringValues.ToDictionary(
            pair => pair.Key,
            pair => pair.Key == "id" ? "actual" : pair.Value,
            StringComparer.Ordinal));
        Assert.Equal("GW-DOC-MAT-003", Assert.Throws<DocumentMaterializationException>(() => stringUnit.Materialize(stringMismatch)).Code);

        var binaryUnit = DocumentUnit.For<BinaryIdDocument>("binary", "identity_binary")
            .Id(document => document.Id)
            .Build();
        var binaryValue = new BinaryIdDocument([1, 2, 3], "value");
        var binaryMaterialized = binaryUnit.Materialize(binaryUnit.ToRowValues(binaryValue));
        Assert.Equal(binaryValue.Id, binaryMaterialized.Id);
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
    private sealed class FieldDocument
    {
        public Guid Id;
        public string Name = string.Empty;
    }
    private sealed class IncludedFieldDocument
    {
        [JsonInclude] public Guid Id;
        [JsonInclude] public string Name = string.Empty;
    }
    private sealed class IgnoredIdDocument
    {
        [JsonIgnore] public Guid Id { get; init; }
    }
    private sealed class IgnoredProjectionDocument
    {
        public Guid Id { get; init; }
        [JsonIgnore] public string Name { get; init; } = string.Empty;
    }
    private sealed class ExplicitlyIncludedDocument
    {
        public Guid Id { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string Name { get; init; } = string.Empty;
    }
    private sealed record StringIdDocument(string Id, string Value);
    private sealed record BinaryIdDocument(byte[] Id, string Value);
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

    private sealed class CapturingStorageConnection(CapturingStorageSession session) : IStorageProviderConnection
    {
        public int OpenCount { get; private set; }
        public StorageAccess? LastAccess { get; private set; }
        public IProviderCatalog Catalog => throw new NotSupportedException();
        public ISchemaCoordinator Schema => throw new NotSupportedException();
        public IReadOnlyList<CapabilityDescriptor> Capabilities => [];
        public IStorageSession OpenSession(Groundwork.Kernel.StorageUnit unit, StorageAccess access)
        {
            OpenCount++;
            LastAccess = access;
            return session;
        }
        public IUnitOfWork BeginUnitOfWork(StorageAccess access, params Groundwork.Kernel.StorageUnit[] units) => throw new NotSupportedException();
        public IUnitOfWork BeginUnitOfWork(StorageAccess access, BatchWriteOptions options, params Groundwork.Kernel.StorageUnit[] units) => throw new NotSupportedException();
        public void Dispose() { }
    }

    private sealed class CapturingStorageSession(Groundwork.Kernel.StorageUnit unit, WriteOutcome result) : IStorageSession
    {
        public Groundwork.Kernel.StorageUnit Unit { get; } = unit;
        public StorageAccess Access => StorageAccess.Global;
        public RowWriteMode? LastMode { get; private set; }
        public StorageValues? LastValues { get; private set; }
        public WriteOptions? LastOptions { get; private set; }
        public StoredEntry? Read(StorageKey key) => throw new NotSupportedException();
        public Groundwork.Query.Model.QueryMaterializedResult Query(Groundwork.Query.Model.QueryRequest request, Groundwork.Query.Model.QueryRenderOptions? options = null) => throw new NotSupportedException();
        public AggregationResult Aggregate(Groundwork.Kernel.AggregationQuery query) => throw new NotSupportedException();
        public WriteOutcome Insert(StorageValues values, WriteOptions? options = null) => Capture(RowWriteMode.Insert, values, null, options);
        public WriteOutcome Update(StorageValues values, WriteOptions? options = null) => Capture(RowWriteMode.Update, values, null, options);
        public WriteOutcome Upsert(StorageValues values, WriteOptions? options = null) => Capture(RowWriteMode.Upsert, values, null, options);
        public WriteOutcome Delete(StorageKey key, WriteOptions? options = null) => Capture(RowWriteMode.Delete, null, key, options);
        public WriteOutcome Append(OperationId operationId, IReadOnlyList<StorageValues> values) => throw new NotSupportedException();

        private WriteOutcome Capture(RowWriteMode mode, StorageValues? values, StorageKey? key, WriteOptions? options)
        {
            LastMode = mode;
            LastValues = values;
            LastOptions = options;
            return result;
        }
    }

    private sealed class TemporarySqliteStore : IDisposable
    {
        private readonly string directory;

        private TemporarySqliteStore(string directory)
        {
            this.directory = directory;
            ConnectionString = $"Data Source={Path.Combine(directory, "store.db")}";
        }

        public string ConnectionString { get; }

        public static TemporarySqliteStore Create()
        {
            var directory = Path.Combine(Path.GetTempPath(), "groundwork-documents-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return new TemporarySqliteStore(directory);
        }

        public void Dispose()
        {
            try { Directory.Delete(directory, recursive: true); }
            catch { }
        }
    }
}
