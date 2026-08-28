using System.Text.Json;
using Groundwork.Kernel;
using Groundwork.MongoDb;
using Groundwork.Substrate.Mongo;
using MongoDB.Bson;
using Xunit;

namespace Groundwork.MongoDb.Tests;

public sealed class MongoValueCodecTests
{
    [Fact]
    public void Schema_diff_and_apply_refuse_an_invalid_raw_json_string_default_before_provider_work()
    {
        using var connection = new MongoDbProviderFactory().Create("mongodb://localhost:27017/groundwork-default-validation");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("mongo-invalid-raw-json-default"),
            Name = "mongo_invalid_raw_json_default",
            Columns =
            [
                new() { Name = "id", Type = PortableType.Guid, IsNullable = false },
                new() { Name = "payload", Type = PortableType.Json, Default = new PortableDefault("pending") }
            ],
            Key = new KeyDefinition { Columns = ["id"] }
        };

        var diffFailure = Assert.Throws<InvalidOperationException>(() => connection.Schema.Diff(unit));
        Assert.Contains("GW-PORT-013", diffFailure.Message, StringComparison.Ordinal);
        Assert.Contains("payload", diffFailure.Message, StringComparison.Ordinal);
        Assert.Contains("Json", diffFailure.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(String), diffFailure.Message, StringComparison.Ordinal);

        var applyFailure = Assert.Throws<InvalidOperationException>(() => connection.Schema.Apply(unit));
        Assert.Contains("GW-PORT-013", applyFailure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Partial_index_specification_excludes_explicit_nulls_for_every_key_term()
    {
        var definition = new IndexDefinition
        {
            Name = "unique_email_name",
            Columns = [new IndexColumn("email"), new IndexColumn("name", SortDirection.Descending)],
            IsUnique = true,
            MissingValues = MissingValueBehavior.Excluded
        };

        var specification = new MongoIndexSpecification(definition, TestUnits.Customer.Columns);

        Assert.Equal("$and", specification.PartialFilter!.GetElement(0).Name);
        Assert.Contains("email", specification.PartialFilter.ToJson(), StringComparison.Ordinal);
        Assert.Contains("name", specification.PartialFilter.ToJson(), StringComparison.Ordinal);
        Assert.Contains("$type", specification.PartialFilter.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void Codec_maps_declared_values_to_native_bson_without_an_envelope()
    {
        var columns = TestUnits.Customer.Columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
        var values = new Dictionary<string, object?>
        {
            ["id"] = "customer-1",
            ["name"] = "Ada",
            ["email"] = null,
            ["createdAt"] = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.FromHours(2)),
            ["isActive"] = true,
            ["balance"] = 1.23456m
        };
        var document = new BsonDocument();
        foreach (var column in TestUnits.Customer.Columns)
            document.Add(column.Name, MongoValueCodec.Encode(values[column.Name], columns[column.Name]));

        Assert.Equal(BsonType.String, document["id"].BsonType);
        Assert.Equal(BsonType.Int64, document["createdAt"].BsonType);
        Assert.Equal(BsonType.Boolean, document["isActive"].BsonType);
        Assert.Equal(BsonType.Decimal128, document["balance"].BsonType);
        Assert.True(document["email"].IsBsonNull);
        Assert.DoesNotContain("body", document.Names, StringComparer.Ordinal);
        Assert.DoesNotContain("envelope", document.Names, StringComparer.Ordinal);
        Assert.Equal(1.2346m, MongoValueCodec.Decode(document["balance"], columns["balance"]));
    }

    [Fact]
    public void Codec_enforces_decimal128_precision_and_scale()
    {
        var over_precision = new ColumnDefinition
        {
            Name = "amount",
            Type = PortableType.Decimal,
            Precision = 35,
            Scale = 4
        };
        var over_digits = new ColumnDefinition
        {
            Name = "amount",
            Type = PortableType.Decimal,
            Precision = 6,
            Scale = 2
        };

        var precision = Assert.Throws<InvalidOperationException>(() => MongoValueCodec.Encode(1m, over_precision));
        var digits = Assert.Throws<OverflowException>(() => MongoValueCodec.Encode(12345.67m, over_digits));

        Assert.Contains("Decimal128", precision.Message, StringComparison.Ordinal);
        Assert.Contains("Decimal(6,2)", digits.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_codec_returns_a_detached_json_value()
    {
        var column = new ColumnDefinition { Name = "payload", Type = PortableType.Json };
        using var json = JsonDocument.Parse("{\"answer\":42}");

        var encoded = MongoValueCodec.Encode(json, column);
        var decoded = Assert.IsType<JsonElement>(MongoValueCodec.Decode(encoded, column));

        Assert.Equal(42, decoded.GetProperty("answer").GetInt32());
    }

    [Fact]
    public void Provider_sequence_is_declared_as_an_evidence_gated_capability()
    {
        var (registry, evidence) = new GroundworkModuleCatalog()
            .Add(new MongoCapabilityModule())
            .Build();

        Assert.Equal(MongoCapabilities.ProviderSequence, MongoCapabilities.ProviderSequenceDescriptor.Id);
        Assert.Equal(MongoCapabilities.ProviderSequenceDescriptor,
            registry.Get(MongoCapabilities.ProviderSequence));
        Assert.Contains(MongoCapabilities.ProviderSequence, evidence.EvidenceGatedCapabilities);
    }
}

internal static class TestUnits
{
    internal static readonly StorageUnit Customer = new()
    {
        Id = new StorageUnitId("p1-customer"),
        Name = "P1Customer",
        Columns =
        [
            new() { Name = "id", Type = PortableType.String, IsNullable = false },
            new() { Name = "name", Type = PortableType.String, IsNullable = false },
            new() { Name = "email", Type = PortableType.String, MaxLength = 256 },
            new() { Name = "createdAt", Type = PortableType.DateTimeOffset, IsNullable = false },
            new() { Name = "isActive", Type = PortableType.Boolean, IsNullable = false },
            new() { Name = "balance", Type = PortableType.Decimal, Precision = 19, Scale = 4 }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        Indexes =
        [
            new IndexDefinition
            {
                Name = "unique_email",
                Columns = [new IndexColumn("email")],
                IsUnique = true,
                MissingValues = MissingValueBehavior.Excluded
            }
        ]
    };
}
