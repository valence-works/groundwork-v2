using Groundwork.LiveDatabases;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Groundwork.MongoDb.Tests;

/// <summary>
/// Proves the reclamation path <see cref="LiveMongo"/> runs on acquire directly, rather than
/// relying on the best-effort <c>ProcessExit</c> drop having fired.
/// <para>
/// The age decision itself is <see cref="LiveMongo.IsStale"/>, a pure function with no connection
/// and nothing to drop. This suite shares its live server with every other suite in the
/// repository, and with whatever sibling process is running concurrently, so pinning down that
/// decision's boundary here, rather than by lowering the age threshold against a real live
/// database, is what keeps these tests from being able to touch anyone else's database.
/// </para>
/// </summary>
public sealed class LiveMongoReclamationTests
{
    private const string MarkerCollection = "__groundwork_run_marker";

    [Fact]
    public void A_claim_older_than_the_threshold_is_stale()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(LiveMongo.IsStale(now - TimeSpan.FromHours(3), now, TimeSpan.FromHours(2)));
    }

    [Fact]
    public void A_claim_within_the_threshold_is_not_stale()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.False(LiveMongo.IsStale(now - TimeSpan.FromMinutes(5), now, TimeSpan.FromHours(2)));
    }

    [Fact]
    public void A_claim_exactly_at_the_threshold_is_not_stale()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.False(LiveMongo.IsStale(now - TimeSpan.FromHours(2), now, TimeSpan.FromHours(2)));
    }

    /// <summary>
    /// Exercises the listing, marker-reading and drop plumbing end to end, against whatever server
    /// <c>GROUNDWORK_MONGO_CONNECTION</c> names, using the real production threshold rather than a
    /// lowered one. That threshold cannot mistake a database this test just claimed for one
    /// abandoned two hours ago, so this is safe to run against a server other processes share.
    /// </summary>
    [SkippableFact]
    public void Reclamation_with_the_real_threshold_leaves_a_freshly_claimed_database_alone()
    {
        var connectionString = LiveMongo.Required();
        var client = new MongoClient(connectionString);
        var name = "groundwork_run_" + Guid.NewGuid().ToString("N");
        client.GetDatabase(name).GetCollection<BsonDocument>(MarkerCollection)
            .InsertOne(new BsonDocument { { "_id", "marker" }, { "claimedAtUtc", DateTime.UtcNow } });

        try
        {
            LiveMongo.ReclaimStale(connectionString, "groundwork_run_", LiveMongo.StaleAfter);

            Assert.Contains(name, client.ListDatabaseNames().ToList());
        }
        finally
        {
            client.DropDatabase(name);
        }
    }

    /// <summary>
    /// Exercises the listing, marker-reading and drop plumbing end to end with a genuinely stale
    /// marker and the real production threshold. Unlike SQL Server's server-set <c>create_date</c>,
    /// Mongo's marker timestamp is written by the client, so it can be backdated for real rather
    /// than requiring a lowered threshold. Scoping the call to a prefix unique to this test run
    /// makes it safe regardless: no sibling process's database — claimed under the real
    /// <c>&lt;configuredName&gt;_run_</c> prefix — can ever match this one's, whatever its age.
    /// </summary>
    [SkippableFact]
    public void Reclamation_discovers_and_drops_a_database_with_a_stale_marker()
    {
        var connectionString = LiveMongo.Required();
        var client = new MongoClient(connectionString);
        var prefix = "groundwork_run_rt_" + Guid.NewGuid().ToString("N") + "_";
        var name = prefix + "t";
        client.GetDatabase(name).GetCollection<BsonDocument>(MarkerCollection)
            .InsertOne(new BsonDocument { { "_id", "marker" }, { "claimedAtUtc", DateTime.UtcNow - TimeSpan.FromHours(3) } });

        LiveMongo.ReclaimStale(connectionString, prefix, LiveMongo.StaleAfter);

        Assert.DoesNotContain(name, client.ListDatabaseNames().ToList());
    }
}
