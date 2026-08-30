using Groundwork.Kernel;
using Groundwork.LiveDatabases;
using Groundwork.MongoDb;
using Groundwork.Query.Linq.Execution;
using Groundwork.Query.Model;
using Groundwork.Store;
using MongoDB.Driver;
using Xunit;

namespace Groundwork.MongoDb.Tests;

/// <summary>
/// MongoDB-specific guard proofs for set-based mutation. The differential suite proves the
/// portable behavior across providers; these tests pin the Mongo deployment boundary where a
/// standalone server can issue updateMany/deleteMany but cannot provide a transaction-backed unit
/// of work, and where a privileged capability call must be refused before a native command.
/// </summary>
public sealed class MongoSetMutationGuardTests
{
    [SkippableFact]
    public void MongoDB_standalone_set_mutation_works_without_advertising_atomic_commit()
    {
        var configured = Environment.GetEnvironmentVariable("GROUNDWORK_MONGO_STANDALONE_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(configured),
            "Set GROUNDWORK_MONGO_STANDALONE_CONNECTION to prove standalone set-based mutation.");

        var url = new MongoUrlBuilder(configured)
        {
            DatabaseName = "gw_set_mutation_standalone_" + Guid.NewGuid().ToString("N")
        };
        var connectionString = url.ToMongoUrl().ToString();
        try
        {
            using var connection = new MongoProviderFactory().Create(connectionString);
            var unit = Unit("mongo_set_mutation_standalone");
            Assert.True(connection.Schema.Apply(unit).Applied);

            Assert.Contains(connection.Capabilities,
                capability => capability.Id == BatchWriteCapabilities.SetMutation);
            Assert.DoesNotContain(connection.Capabilities,
                capability => capability.Id == WellKnownCapabilities.AtomicCommit);

            var seed = connection.OpenSession(unit, StorageAccess.Global);
            seed.Insert(Row("one", "open", "before"));
            seed.Insert(Row("two", "open", "before"));
            seed.Insert(Row("three", "closed", "before"));

            var observer = new ProviderCommandObserver();
            var session = connection.OpenSession(unit, StorageAccess.Global, observer);
            Assert.IsAssignableFrom<ISetMutationStorageSession>(session);

            Assert.Equal(2L, session.UpdateWhere(
                Status(unit, "open"),
                new Dictionary<string, object?> { ["label"] = "after" }).MatchedRows);
            Assert.Equal(1L, session.DeleteWhere(Status(unit, "closed")).MatchedRows);

            Assert.Equal(
                ["mongodb.update-where", "mongodb.delete-where"],
                observer.Commands.Select(command => command.Operation));
            Assert.Equal(1, observer.Commands.Count(command => command.Operation == "mongodb.update-where"));
            Assert.Equal(1, observer.Commands.Count(command => command.Operation == "mongodb.delete-where"));
        }
        finally
        {
            try { new MongoClient(connectionString).DropDatabase(url.DatabaseName); }
            catch (MongoException) { }
        }
    }

    [SkippableFact]
    public void MongoDB_direct_set_mutation_capability_refuses_before_a_native_command()
    {
        using var connection = OpenReplicaSetConnection();
        var unit = Unit("mongo_set_mutation_privileged") with { Scope = ScopePolicy.Scoped };
        Assert.True(connection.Schema.Apply(unit).Applied);

        var observer = new ProviderCommandObserver();
        var session = connection.OpenSession(
            unit,
            StorageAccess.PrivilegedAcrossScopes(new StorageAccessAudit(
                "set-mutation-guard", "prove privileged mutation refusal")),
            observer);
        var capability = Assert.IsAssignableFrom<ISetMutationStorageSession>(session);

        var refusal = Assert.Throws<InvalidOperationException>(() => capability.DeleteWhere(Status(unit, "open")));
        Assert.Contains("GW-COVER-001", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(observer.Commands);
    }

    private static IStorageProviderConnection OpenReplicaSetConnection()
    {
        var configured = LiveMongo.ConnectionString;
        Skip.If(string.IsNullOrWhiteSpace(configured),
            "Set GROUNDWORK_MONGO_CONNECTION to prove MongoDB set-mutation guards.");
        return new MongoProviderFactory().Create(configured!);
    }

    private static Predicate Status(StorageUnit unit, string value)
    {
        var column = new ColumnRef(new TableId(unit.Name), "status", QueryType.String,
            isNullable: false, maxLength: 32);
        return new Predicate.Equal(column, QueryConstant.Of(column, value));
    }

    private static StorageValues Row(string id, string status, string label) =>
        new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = id,
            ["status"] = status,
            ["label"] = label
        });

    private static StorageUnit Unit(string suffix) => new()
    {
        Id = new StorageUnitId(suffix + "_" + Guid.NewGuid().ToString("N")),
        Name = suffix + "_" + Guid.NewGuid().ToString("N"),
        Columns =
        [
            new() { Name = "id", Type = PortableType.String, IsNullable = false, MaxLength = 64 },
            new() { Name = "status", Type = PortableType.String, IsNullable = false, MaxLength = 32 },
            new() { Name = "label", Type = PortableType.String, IsNullable = false, MaxLength = 32 }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        Indexes = [new IndexDefinition { Name = "by_status", Columns = [new IndexColumn("status")] }]
    };
}
