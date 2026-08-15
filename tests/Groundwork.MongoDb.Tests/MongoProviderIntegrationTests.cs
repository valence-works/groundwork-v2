using Groundwork.Kernel;
using Groundwork.MongoDb;
using Groundwork.MongoDb.TestingAdapter;
using Groundwork.Testing;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Groundwork.MongoDb.Tests;

public sealed class MongoProviderIntegrationTests
{
    [SkippableFact]
    public void Provider_passes_the_shipped_conformance_suite()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_MONGO_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString),
            "Set GROUNDWORK_MONGO_CONNECTION to run MongoDB integration tests.");
        var url = new MongoUrlBuilder(connectionString) { DatabaseName = "p1conformance_" + Guid.NewGuid().ToString("N") };

        var report = ConformanceSuite.Run(new MongoDbTestingFactory(), url.ToMongoUrl().ToString());

        Assert.True(report.Passed, string.Join(Environment.NewLine,
            report.Failures.Select(failure => $"{failure.Name}: {failure.Failure}")));
    }

    [SkippableFact]
    public void Customer_schema_is_native_and_catalog_is_read_from_mongodb()
    {
        using var connection = OpenConnection();
        var unit = TestUnits.Customer with
        {
            Id = new StorageUnitId("p1-customer-" + Guid.NewGuid().ToString("N")),
            Name = "P1Customer_" + Guid.NewGuid().ToString("N")
        };

        var first = connection.Schema.Apply(unit);
        var second = connection.Schema.Apply(unit);
        var indexes = connection.Catalog.ReadIndexes(unit.Id);
        using var reopened = new MongoDbProviderFactory().Create(
            Environment.GetEnvironmentVariable("GROUNDWORK_MONGO_CONNECTION")!);
        var reopenedIndexes = reopened.Catalog.ReadIndexes(unit.Id);
        var native = Assert.IsType<MongoDbProviderConnection>(connection).Database
            .GetCollection<BsonDocument>(unit.Name);
        var indexNames = native.Indexes.List().ToList().Select(index => index["name"].AsString).ToArray();

        Assert.False(first.IsNoOp);
        Assert.True(second.IsNoOp);
        Assert.Contains(indexes, index => index.Name == "unique-email" &&
            index.IsUnique && index.MissingValues == MissingValueBehavior.Excluded);
        Assert.Contains(reopenedIndexes, index => index.Name == "unique-email");
        Assert.Contains("unique-email", indexNames);

        var session = connection.OpenSession(unit, MongoStorageAccess.Global);
        var values = CustomerValues("one", "one@example.test");
        Assert.Equal(MongoWriteOutcomeStatus.Inserted, session.Insert(values).Status);
        var loaded = session.Read(new MongoStorageKey(new Dictionary<string, object?> { ["id"] = "one" }));
        Assert.Equal("Ada", loaded!.Values.Values["name"]);
        Assert.Equal(1.2346m, loaded.Values.Values["balance"]);
        Assert.Equal(MongoWriteOutcomeStatus.UniqueViolation,
            session.Insert(CustomerValues("two", "one@example.test")).Status);
        Assert.Equal(MongoWriteOutcomeStatus.Inserted,
            session.Insert(CustomerValues("null-one", null)).Status);
        Assert.Equal(MongoWriteOutcomeStatus.Inserted,
            session.Insert(CustomerValues("null-two", null)).Status);

        var document = native.Find(new BsonDocument("_id", "one")).First();
        Assert.Equal("Ada", document["name"].AsString);
        Assert.Equal(BsonType.Decimal128, document["balance"].BsonType);
        Assert.True(document.Contains("email"));
        Assert.True(native.Find(new BsonDocument("_id", "null-one")).First()["email"].IsBsonNull);
        Assert.DoesNotContain("body", document.Names, StringComparer.Ordinal);
        Assert.DoesNotContain("envelope", document.Names, StringComparer.Ordinal);
    }

    [SkippableFact]
    public void Composite_keys_use_an_ordered_native_id_subdocument()
    {
        using var connection = OpenConnection();
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("p1-composite-" + Guid.NewGuid().ToString("N")),
            Name = "P1Composite_" + Guid.NewGuid().ToString("N"),
            Columns =
            [
                new() { Name = "region", Type = PortableType.String, IsNullable = false },
                new() { Name = "customerNo", Type = PortableType.Int64, IsNullable = false },
                new() { Name = "name", Type = PortableType.String }
            ],
            Key = new KeyDefinition { Columns = ["region", "customerNo"] }
        };
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, MongoStorageAccess.Global);
        session.Insert(new MongoStorageValues(new Dictionary<string, object?>
        {
            ["region"] = "west",
            ["customerNo"] = 7L,
            ["name"] = "Ada"
        }));

        var document = Assert.IsType<MongoDbProviderConnection>(connection).Database
            .GetCollection<BsonDocument>(unit.Name).Find(FilterDefinition<BsonDocument>.Empty).First();
        var id = document["_id"].AsBsonDocument;
        Assert.Equal(["region", "customerNo"], id.Names);
        Assert.Equal("west", id["region"].AsString);
        Assert.Equal(7L, id["customerNo"].AsInt64);
    }

    [SkippableFact]
    public void Composite_key_reordering_is_refused_after_reopening_the_provider()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_MONGO_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString),
            "Set GROUNDWORK_MONGO_CONNECTION to run MongoDB integration tests.");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("p1-reorder-" + Guid.NewGuid().ToString("N")),
            Name = "P1Reorder_" + Guid.NewGuid().ToString("N"),
            Columns =
            [
                new() { Name = "region", Type = PortableType.String, IsNullable = false },
                new() { Name = "customerNo", Type = PortableType.Int64, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["region", "customerNo"] }
        };
        using (var first = new MongoDbProviderFactory().Create(connectionString!))
            first.Schema.Apply(unit);

        var reordered = unit with { Key = new KeyDefinition { Columns = ["customerNo", "region"] } };
        using var reopened = new MongoDbProviderFactory().Create(connectionString!);
        var refusal = Assert.Throws<InvalidOperationException>(() => reopened.Schema.Apply(reordered));

        Assert.Contains("GW-PORT-008", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("native _id field order", refusal.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Admission_refuses_documents_missing_declared_columns_with_a_backfill_command()
    {
        using var connection = OpenConnection();
        var unit = TestUnits.Customer with
        {
            Id = new StorageUnitId("p1-admission-" + Guid.NewGuid().ToString("N")),
            Name = "P1Admission_" + Guid.NewGuid().ToString("N")
        };
        connection.Schema.Apply(unit);
        Assert.IsType<MongoDbProviderConnection>(connection).Database
            .GetCollection<BsonDocument>(unit.Name)
            .InsertOne(new BsonDocument("_id", "legacy"));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            connection.OpenSession(unit, MongoStorageAccess.Global));

        Assert.Contains("backfill", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(unit.Name, exception.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Scope_and_optimistic_concurrency_are_provider_behaviors()
    {
        using var connection = OpenConnection();
        var unit = TestUnits.Customer with
        {
            Id = new StorageUnitId("p1-scope-" + Guid.NewGuid().ToString("N")),
            Name = "P1Scope_" + Guid.NewGuid().ToString("N"),
            Scope = ScopePolicy.Scoped,
            Concurrency = ConcurrencyDeclaration.Optimistic
        };
        connection.Schema.Apply(unit);
        var first = connection.OpenSession(unit, MongoStorageAccess.Scoped(new StorageScope("a")));
        var second = connection.OpenSession(unit, MongoStorageAccess.Scoped(new StorageScope("b")));

        var inserted = first.Insert(CustomerValues("same", null));
        Assert.Equal(1, inserted.Version);
        Assert.Equal("Ada", first.Read(Key("same"))!.Values.Values["name"]);
        Assert.Null(second.Read(Key("same")));
        Assert.Equal(MongoWriteOutcomeStatus.ConcurrencyConflict,
            first.Update(CustomerValues("same", null), MongoWriteOptions.ForVersion(9)).Status);
        Assert.Equal(MongoWriteOutcomeStatus.Updated,
            first.Update(CustomerValues("same", null), MongoWriteOptions.ForVersion(1)).Status);
    }

    [SkippableFact]
    public void Provider_sequence_is_capability_gated_by_mongodb_transactions()
    {
        using var connection = OpenConnection();
        var unit = TestUnits.Customer with
        {
            Id = new StorageUnitId("p1-sequence-" + Guid.NewGuid().ToString("N")),
            Name = "P1Sequence_" + Guid.NewGuid().ToString("N"),
            Columns = [.. TestUnits.Customer.Columns, new ColumnDefinition
            {
                Name = "sequence", Type = PortableType.Int64, IsNullable = false,
                Generation = ColumnGeneration.ProviderSequence
            }]
        };

        var hello = Assert.IsType<MongoDbProviderConnection>(connection).Database
            .RunCommand<BsonDocument>(new BsonDocument("hello", 1));
        if (!hello.Contains("setName") && !string.Equals(hello.GetValue("msg", "").AsString, "isdbgrid", StringComparison.Ordinal))
        {
            var refusal = Assert.Throws<InvalidOperationException>(() => connection.Schema.Apply(unit));
            Assert.Contains("transaction-capable", refusal.Message, StringComparison.OrdinalIgnoreCase);
            return;
        }

        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, MongoStorageAccess.Global);
        var result = session.Insert(CustomerValues("sequence", null));
        Assert.True(result.Succeeded);
        Assert.Equal(1L, session.Read(Key("sequence"))!.Values.Values["sequence"]);
    }

    private static MongoStorageValues CustomerValues(string id, string? email) => new(new Dictionary<string, object?>
    {
        ["id"] = id,
        ["name"] = "Ada",
        ["email"] = email,
        ["createdAt"] = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero),
        ["isActive"] = true,
        ["balance"] = 1.23456m
    });

    private static MongoStorageKey Key(string id) => new(new Dictionary<string, object?> { ["id"] = id });

    private static IMongoProviderConnection OpenConnection()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_MONGO_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString),
            "Set GROUNDWORK_MONGO_CONNECTION to run MongoDB integration tests.");
        return new MongoDbProviderFactory().Create(connectionString!);
    }

}
