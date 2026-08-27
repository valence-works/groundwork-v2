using Groundwork.LiveDatabases;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Groundwork.MongoDb.Tests;

/// <summary>
/// Proves the reclamation path <see cref="LiveMongo"/> runs on acquire directly, rather than
/// relying on the best-effort <c>ProcessExit</c> drop having fired.
/// </summary>
public sealed class LiveMongoReclamationTests
{
    private const string MarkerCollection = "__groundwork_run_marker";

    [SkippableFact]
    public void Reclamation_drops_a_run_database_whose_marker_is_older_than_the_threshold()
    {
        var connectionString = LiveMongo.Required();
        var name = "groundwork_run_" + Guid.NewGuid().ToString("N");
        Mark(connectionString, name, DateTime.UtcNow - TimeSpan.FromDays(1));

        LiveMongo.ReclaimStale(connectionString, "groundwork_run_", TimeSpan.FromHours(2));

        Assert.DoesNotContain(name, new MongoClient(connectionString).ListDatabaseNames().ToList());
    }

    [SkippableFact]
    public void Reclamation_leaves_a_run_database_within_the_threshold_alone()
    {
        var connectionString = LiveMongo.Required();
        var name = "groundwork_run_" + Guid.NewGuid().ToString("N");
        Mark(connectionString, name, DateTime.UtcNow);

        // A run still in progress is not distinguishable from one just claimed, so an age
        // threshold well past this test's own duration must leave it standing.
        LiveMongo.ReclaimStale(connectionString, "groundwork_run_", TimeSpan.FromHours(2));

        var client = new MongoClient(connectionString);
        Assert.Contains(name, client.ListDatabaseNames().ToList());

        client.DropDatabase(name);
    }

    private static void Mark(string connectionString, string name, DateTime claimedAtUtc)
    {
        var collection = new MongoClient(connectionString).GetDatabase(name).GetCollection<BsonDocument>(MarkerCollection);
        collection.InsertOne(new BsonDocument { { "_id", "marker" }, { "claimedAtUtc", claimedAtUtc } });
    }
}
