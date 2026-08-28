using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Groundwork.LiveDatabases;

/// <summary>
/// The MongoDB database this test process owns, for the same reason as
/// <see cref="LiveSqlServer"/>: <c>dotnet test</c> runs one test process per target framework and
/// dispatches those runs in parallel, so the single database named in
/// <c>GROUNDWORK_MONGO_CONNECTION</c> has two writers. Collections a suite names outright, and the
/// provider's own <c>__groundwork_metadata</c> catalog, are then shared between two runs that each
/// expect to have created them.
/// <para>
/// Several Mongo suites already build a one-off database for a single test. Claiming one for the
/// whole process makes that the rule rather than something each test has to remember.
/// </para>
/// </summary>
internal static class LiveMongo
{
    private const string MarkerCollection = "__groundwork_run_lease";
    private const string MarkerId = "lease";
    private const int TokenLength = 32;

    /// <summary>
    /// The lease duration for a run. Heartbeats refresh this lease for the process lifetime; a
    /// later acquire can reclaim it only after MongoDB's server clock says the lease expired.
    /// </summary>
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromHours(2);

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FencedRetryAfter = TimeSpan.FromMinutes(5);
    private static readonly Lazy<string?> Claimed = new(Claim, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The connection string for this process's own database, or <see langword="null"/> when no
    /// MongoDB is configured — so a suite's existing skip guard reads the same as before.
    /// </summary>
    internal static string? ConnectionString => Claimed.Value;

    /// <summary>
    /// The claimed connection string, skipping the calling test when no MongoDB is configured.
    /// Suites that need one call this rather than repeating the guard.
    /// </summary>
    internal static string Required()
    {
        Skip.If(ConnectionString is null, "Set GROUNDWORK_MONGO_CONNECTION to run live MongoDB proofs.");
        return ConnectionString!;
    }

    private static string? Claim()
    {
        var configured = Environment.GetEnvironmentVariable("GROUNDWORK_MONGO_CONNECTION");
        if (string.IsNullOrWhiteSpace(configured))
            return null;

        var url = new MongoUrlBuilder(configured);
        var configuredName = string.IsNullOrWhiteSpace(url.DatabaseName) ? "groundwork" : url.DatabaseName;
        var prefix = configuredName + "_run_";
        var name = prefix + Guid.NewGuid().ToString("N");
        var ownerToken = Guid.NewGuid().ToString("N");
        try
        {
            ReclaimStale(configured, prefix);
            url.DatabaseName = name;
            var claimed = url.ToMongoUrl().ToString();
            InitializeLease(claimed, name, ownerToken);
            var lease = new Lease(configured, name, ownerToken);
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                lease.Dispose();
                Release(claimed, name);
            };
            return claimed;
        }
        catch
        {
            // A failure between materializing the database and registering the lease must not
            // create another permanent leak. The name is generated locally and is safe to drop.
            var cleanup = new MongoUrlBuilder(configured) { DatabaseName = name }.ToMongoUrl().ToString();
            Release(cleanup, name);
            throw;
        }
    }

    /// <summary>
    /// Drops run databases only after atomically fencing an expired server-side lease. A missing
    /// or malformed marker cannot be fenced and is therefore never treated as abandoned.
    /// <paramref name="prefix"/> is the configured database name plus <c>_run_</c> in production;
    /// tests use a unique prefix to avoid sweeping another process's server.
    /// </summary>
    internal static void ReclaimStale(string configured, string prefix, string? onlyName = null)
    {
        var client = new MongoClient(configured);
        List<string> names;
        try
        {
            names = client.ListDatabaseNames().ToList();
        }
        catch (MongoException)
        {
            // Reclamation is best-effort housekeeping; a server that cannot be reached for it is
            // the run's own connection failure to report, not this step's.
            return;
        }

        foreach (var name in names)
        {
            if (!IsValidRunName(prefix, name) ||
                (onlyName is not null && !string.Equals(name, onlyName, StringComparison.Ordinal)))
                continue;

            try
            {
                if (TryFence(client, name) || TryRetryFence(client, name))
                    client.DropDatabase(name);
            }
            catch (MongoException)
            {
                // Another sibling won the fence, or the server rejected the drop. Either way,
                // this acquire must not fail because housekeeping is best effort.
            }
        }
    }

    /// <summary>
    /// Atomically fences one expired lease using MongoDB's <c>$$NOW</c> server variable. A
    /// concurrent heartbeat either wins the document update and makes this return false, or this
    /// transition wins and the owner token can no longer renew.
    /// </summary>
    internal static bool TryFence(string configured, string name)
    {
        try
        {
            return TryFence(new MongoClient(configured), name);
        }
        catch (MongoException)
        {
            return false;
        }
    }

    /// <summary>
    /// Renews a lease with MongoDB's server clock. It is internal so isolated provider tests can
    /// exercise the same heartbeat operation that the process timer uses.
    /// </summary>
    internal static bool RenewLease(string configured, string name, string ownerToken)
    {
        if (!IsValidToken(ownerToken))
            return false;

        try
        {
            var collection = new MongoClient(configured).GetDatabase(name).GetCollection<BsonDocument>(MarkerCollection);
            var result = collection.UpdateOne(
                new BsonDocument { ["_id"] = MarkerId, ["state"] = "active", ["ownerToken"] = ownerToken },
                PipelineSet(new BsonDocument { ["heartbeatUtc"] = "$$NOW" }));
            return result.MatchedCount == 1;
        }
        catch (MongoException)
        {
            return false;
        }
    }

    /// <summary>
    /// Creates the lease marker in a freshly materialized database. MongoDB assigns the initial
    /// heartbeat through <c>$$NOW</c>, never through the client clock.
    /// </summary>
    internal static void InitializeLease(string connectionString, string name, string ownerToken)
    {
        if (!IsValidToken(ownerToken))
            throw new ArgumentException("The owner token is not valid.", nameof(ownerToken));

        var collection = new MongoClient(connectionString).GetDatabase(name).GetCollection<BsonDocument>(MarkerCollection);
        collection.UpdateOne(
            new BsonDocument("_id", MarkerId),
            PipelineSet(new BsonDocument
            {
                ["ownerToken"] = ownerToken,
                ["heartbeatUtc"] = "$$NOW",
                ["state"] = "active"
            }),
            new UpdateOptions { IsUpsert = true });
    }

    /// <summary>
    /// The boundary used by the server-side <c>&lt;=</c> expiry predicate, kept pure for tests.
    /// </summary>
    internal static bool IsExpired(DateTime heartbeatUtc, DateTime serverNowUtc, TimeSpan leaseDuration) =>
        serverNowUtc - heartbeatUtc >= leaseDuration;

    /// <summary>
    /// Groundwork run databases end in one generated 32-character hexadecimal token. Restricting
    /// reclamation to that exact shape keeps a similarly prefixed foreign database out of the
    /// destructive path.
    /// </summary>
    internal static bool IsValidRunName(string prefix, string name)
    {
        if (string.IsNullOrEmpty(prefix) ||
            !name.StartsWith(prefix, StringComparison.Ordinal) ||
            name.Length != prefix.Length + TokenLength)
            return false;

        for (var index = prefix.Length; index < name.Length; index++)
        {
            if (!IsHex(name[index]))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Retry cleanup requires a complete, well-formed fence record. This pure rule mirrors the
    /// server predicate and keeps malformed owner metadata from becoming destructive evidence.
    /// </summary>
    internal static bool IsRetryableFencedLease(
        string? ownerToken,
        string? fenceToken,
        DateTime? fencedUtc,
        DateTime serverNowUtc,
        TimeSpan retryAfter) =>
        IsValidToken(ownerToken) &&
        IsValidToken(fenceToken) &&
        fencedUtc is { } fencedAt &&
        serverNowUtc - fencedAt >= retryAfter;

    private static bool TryFence(IMongoClient client, string name)
    {
        var collection = client.GetDatabase(name).GetCollection<BsonDocument>(MarkerCollection);
        var fenceToken = Guid.NewGuid().ToString("N");
        var filter = new BsonDocument
        {
            ["_id"] = MarkerId,
            ["state"] = "active",
            ["$expr"] = new BsonDocument("$cond", new BsonArray
            {
                new BsonDocument("$and", new BsonArray
                {
                    new BsonDocument("$eq", new BsonArray { new BsonDocument("$type", "$ownerToken"), "string" }),
                    new BsonDocument("$eq", new BsonArray { new BsonDocument("$type", "$heartbeatUtc"), "date" }),
                    new BsonDocument("$regexMatch", new BsonDocument
                    {
                        ["input"] = "$ownerToken",
                        ["regex"] = "^[0-9a-fA-F]{32}$"
                    })
                }),
                new BsonDocument("$lte", new BsonArray
                {
                    "$heartbeatUtc",
                    new BsonDocument("$dateSubtract", new BsonDocument
                    {
                        ["startDate"] = "$$NOW",
                        ["unit"] = "second",
                        ["amount"] = LeaseSeconds
                    })
                }),
                false
            })
        };
        var set = new BsonDocument
        {
            ["state"] = "fenced",
            ["fenceToken"] = fenceToken,
            ["fencedUtc"] = "$$NOW"
        };
        var fenced = collection.FindOneAndUpdate(
            filter,
            PipelineSet(set),
            new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After });
        return fenced is not null &&
               fenced.GetValue("state", BsonNull.Value).IsString &&
               fenced.GetValue("state").AsString == "fenced" &&
               fenced.GetValue("fenceToken", BsonNull.Value).IsString &&
               fenced.GetValue("fenceToken").AsString == fenceToken;
    }

    private static bool TryRetryFence(IMongoClient client, string name)
    {
        var collection = client.GetDatabase(name).GetCollection<BsonDocument>(MarkerCollection);
        var fenceToken = Guid.NewGuid().ToString("N");
        var filter = new BsonDocument
        {
            ["_id"] = MarkerId,
            ["state"] = "fenced",
            ["$expr"] = new BsonDocument("$cond", new BsonArray
            {
                new BsonDocument("$and", new BsonArray
                {
                    new BsonDocument("$eq", new BsonArray { new BsonDocument("$type", "$ownerToken"), "string" }),
                    new BsonDocument("$regexMatch", new BsonDocument
                    {
                        ["input"] = "$ownerToken",
                        ["regex"] = "^[0-9a-fA-F]{32}$"
                    }),
                    new BsonDocument("$eq", new BsonArray { new BsonDocument("$type", "$fenceToken"), "string" }),
                    new BsonDocument("$eq", new BsonArray { new BsonDocument("$type", "$fencedUtc"), "date" }),
                    new BsonDocument("$regexMatch", new BsonDocument
                    {
                        ["input"] = "$fenceToken",
                        ["regex"] = "^[0-9a-fA-F]{32}$"
                    })
                }),
                new BsonDocument("$lte", new BsonArray
                {
                    "$fencedUtc",
                    new BsonDocument("$dateSubtract", new BsonDocument
                    {
                        ["startDate"] = "$$NOW",
                        ["unit"] = "second",
                        ["amount"] = FencedRetrySeconds
                    })
                }),
                false
            })
        };
        var fenced = collection.FindOneAndUpdate(
            filter,
            PipelineSet(new BsonDocument
            {
                ["fenceToken"] = fenceToken,
                ["fencedUtc"] = "$$NOW"
            }),
            new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After });
        return fenced is not null &&
               fenced.GetValue("state", BsonNull.Value).IsString &&
               fenced.GetValue("state").AsString == "fenced" &&
               fenced.GetValue("fenceToken", BsonNull.Value).IsString &&
               fenced.GetValue("fenceToken").AsString == fenceToken;
    }

    private static UpdateDefinition<BsonDocument> PipelineSet(BsonDocument values) =>
        Builders<BsonDocument>.Update.Pipeline(
            new EmptyPipelineDefinition<BsonDocument>()
                .AppendStage<BsonDocument, BsonDocument, BsonDocument>(new BsonDocument("$set", values)));

    private static int LeaseSeconds => checked((int)StaleAfter.TotalSeconds);
    private static int FencedRetrySeconds => checked((int)FencedRetryAfter.TotalSeconds);

    private static void Release(string connectionString, string name)
    {
        try
        {
            new MongoClient(connectionString).DropDatabase(name);
        }
        catch (MongoException)
        {
            // The server outliving the run is the CI container's business, not a test result.
        }
    }

    private static bool IsValidToken(string? token) =>
        token is not null && token.Length == TokenLength && token.All(IsHex);

    private static bool IsHex(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private sealed class Lease : IDisposable
    {
        private readonly string configured;
        private readonly string name;
        private readonly string ownerToken;
        private readonly Timer timer;
        private int disposed;

        public Lease(string configured, string name, string ownerToken)
        {
            this.configured = configured;
            this.name = name;
            this.ownerToken = ownerToken;
            timer = new Timer(static state => ((Lease)state!).Heartbeat(), this, HeartbeatInterval, HeartbeatInterval);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                timer.Dispose();
        }

        private void Heartbeat()
        {
            if (Volatile.Read(ref disposed) == 0)
                RenewLease(configured, name, ownerToken);
        }
    }
}
