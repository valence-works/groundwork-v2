using Groundwork.LiveDatabases;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Groundwork.MongoDb.Tests;

public sealed class LiveMongoReclamationTests
{
    private const string MarkerCollection = "__groundwork_run_lease";
    private const string MarkerId = "lease";

    [Fact]
    public void A_lease_before_the_expiry_boundary_is_not_expired()
    {
        var heartbeat = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.False(LiveMongo.IsExpired(heartbeat, heartbeat + TimeSpan.FromHours(2) - TimeSpan.FromTicks(1), TimeSpan.FromHours(2)));
    }

    [Fact]
    public void A_lease_at_or_after_the_expiry_boundary_is_expired()
    {
        var heartbeat = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(LiveMongo.IsExpired(heartbeat, heartbeat + TimeSpan.FromHours(2), TimeSpan.FromHours(2)));
        Assert.True(LiveMongo.IsExpired(heartbeat, heartbeat + TimeSpan.FromHours(3), TimeSpan.FromHours(2)));
    }

    [Fact]
    public void A_fenced_lease_with_malformed_owner_metadata_is_not_retryable()
    {
        var fencedAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.False(LiveMongo.IsRetryableFencedLease(
            "not-a-token",
            Guid.NewGuid().ToString("N"),
            fencedAt,
            fencedAt + TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Mongo_reclamation_requires_the_exact_generated_name_shape()
    {
        var prefix = "groundwork_run_" + Guid.NewGuid().ToString("N")[..14] + "_";

        Assert.True(LiveMongo.IsValidRunName(prefix, prefix + Guid.NewGuid().ToString("N")));
        Assert.False(LiveMongo.IsValidRunName(prefix, prefix + "scratch"));
        Assert.False(LiveMongo.IsValidRunName(prefix, prefix + Guid.NewGuid().ToString("N") + "extra"));

        var overlongPrefix = "groundwork_run_" + Guid.NewGuid().ToString("N")[..16] + "_";
        Assert.False(LiveMongo.IsValidRunName(overlongPrefix, overlongPrefix + Guid.NewGuid().ToString("N")));
    }

    [SkippableFact]
    public void A_live_owner_heartbeat_preserves_an_old_run()
    {
        var connectionString = ConnectionOrSkip();
        var (prefix, name) = NewRunName();
        var ownerToken = Guid.NewGuid().ToString("N");
        var client = new MongoClient(connectionString);
        RunWithDatabases(client, () =>
        {
            LiveMongo.InitializeLease(connectionString, name, ownerToken);
            ExpireLease(client, name);

            Assert.True(LiveMongo.RenewLease(connectionString, name, ownerToken));
            LiveMongo.ReclaimStale(connectionString, prefix, onlyName: name);

            Assert.Contains(name, client.ListDatabaseNames().ToList());
            Assert.True(LiveMongo.RenewLease(connectionString, name, ownerToken));
        }, name);
    }

    [SkippableFact]
    public void An_abandoned_lease_is_reclaimed()
    {
        var connectionString = ConnectionOrSkip();
        var (prefix, name) = NewRunName();
        var client = new MongoClient(connectionString);
        RunWithDatabases(client, () =>
        {
            LiveMongo.InitializeLease(connectionString, name, Guid.NewGuid().ToString("N"));
            ExpireLease(client, name);
            LiveMongo.ReclaimStale(connectionString, prefix, onlyName: name);

            Assert.DoesNotContain(name, client.ListDatabaseNames().ToList());
        }, name);
    }

    [SkippableFact]
    public void Exactly_one_concurrent_reclaimer_can_fence_an_abandoned_lease()
    {
        var connectionString = ConnectionOrSkip();
        var (_, name) = NewRunName();
        var ownerToken = Guid.NewGuid().ToString("N");
        var client = new MongoClient(connectionString);
        RunWithDatabases(client, () =>
        {
            LiveMongo.InitializeLease(connectionString, name, ownerToken);
            ExpireLease(client, name);

            var fences = Task.WhenAll(
                Task.Run(() => LiveMongo.TryFence(connectionString, name)),
                Task.Run(() => LiveMongo.TryFence(connectionString, name))).GetAwaiter().GetResult();

            Assert.Equal(1, fences.Count(fenced => fenced));
            Assert.Contains(name, client.ListDatabaseNames().ToList());
            Assert.False(LiveMongo.RenewLease(connectionString, name, ownerToken));
        }, name);
    }

    [SkippableFact]
    public void Missing_or_malformed_lease_metadata_is_not_reclaimed()
    {
        var connectionString = ConnectionOrSkip();
        var (missingPrefix, missingName) = NewRunName();
        var (malformedPrefix, malformedName) = NewRunName();
        var client = new MongoClient(connectionString);
        RunWithDatabases(client, () =>
        {
            client.GetDatabase(missingName).GetCollection<BsonDocument>("payload")
                .InsertOne(new BsonDocument("_id", "keep"));
            client.GetDatabase(malformedName).GetCollection<BsonDocument>(MarkerCollection)
                .InsertOne(new BsonDocument
                {
                    ["_id"] = MarkerId,
                    ["state"] = "active",
                    ["ownerToken"] = "bad",
                    ["heartbeatUtc"] = DateTime.UtcNow - TimeSpan.FromHours(3)
                });

            LiveMongo.ReclaimStale(connectionString, missingPrefix, onlyName: missingName);
            LiveMongo.ReclaimStale(connectionString, malformedPrefix, onlyName: malformedName);

            Assert.Contains(missingName, client.ListDatabaseNames().ToList());
            Assert.Contains(malformedName, client.ListDatabaseNames().ToList());
        }, missingName, malformedName);
    }

    private static string ConnectionOrSkip()
    {
        var connection = Environment.GetEnvironmentVariable("GROUNDWORK_MONGO_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connection), "Set GROUNDWORK_MONGO_CONNECTION to run live MongoDB proofs.");
        return connection!;
    }

    private static (string Prefix, string Name) NewRunName()
    {
        var prefix = "groundwork_run_" + Guid.NewGuid().ToString("N")[..14] + "_";
        return (prefix, prefix + Guid.NewGuid().ToString("N"));
    }

    private static void ExpireLease(IMongoClient client, string name)
    {
        client.GetDatabase(name).GetCollection<BsonDocument>(MarkerCollection).UpdateOne(
            new BsonDocument("_id", MarkerId),
            new BsonDocument("$set", new BsonDocument("heartbeatUtc", DateTime.UtcNow - TimeSpan.FromHours(3))));
    }

    private static void RunWithDatabases(IMongoClient client, Action body, params string[] names)
    {
        try
        {
            body();
        }
        finally
        {
            foreach (var name in names)
            {
                try
                {
                    client.DropDatabase(name);
                }
                catch (MongoException)
                {
                    // The server outliving the run is the container's business, not a test result.
                }
            }
        }
    }
}
