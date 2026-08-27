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
    /// <summary>
    /// How long a claimed database survives after its marker's timestamp before a sibling process
    /// is willing to drop it on the assumption its owner is never coming back. Well past any
    /// plausible test run, so a run still in progress is never mistaken for an abandoned one.
    /// </summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(2);

    /// <summary>
    /// MongoDB only ever materializes a database once something writes to it, so this collection
    /// doubles as the write that brings a claimed database into existence and as the record of
    /// when that happened — there is no server-side creation timestamp to read back the way SQL
    /// Server's <c>sys.databases.create_date</c> offers.
    /// </summary>
    private const string MarkerCollection = "__groundwork_run_marker";

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

        // ProcessExit is not a reliable place to reclaim: it does not run on a crash, a kill, or a
        // heavily-loaded shutdown — exactly the conditions that leave one of these behind. Acquire
        // is the hook every run passes through, so a run reclaims whatever a prior one abandoned
        // before it claims its own.
        ReclaimStale(configured, prefix, StaleAfter);

        url.DatabaseName = prefix + Guid.NewGuid().ToString("N");
        var claimed = url.ToMongoUrl().ToString();
        Mark(claimed, url.DatabaseName);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Release(claimed, url.DatabaseName);
        return claimed;
    }

    /// <summary>
    /// Writes the marker document that records when this process's database was claimed, so a
    /// later reclamation pass has something to read an age from.
    /// </summary>
    private static void Mark(string connectionString, string name)
    {
        var collection = new MongoClient(connectionString).GetDatabase(name).GetCollection<BsonDocument>(MarkerCollection);
        collection.InsertOne(new BsonDocument { { "_id", "marker" }, { "claimedAtUtc", DateTime.UtcNow } });
    }

    /// <summary>
    /// Drops every database whose name starts with <paramref name="prefix"/> and whose marker
    /// records a claim older than <paramref name="olderThan"/>, skipping any database without a
    /// readable marker because that is indistinguishable from a claim still in flight.
    /// </summary>
    internal static void ReclaimStale(string configured, string prefix, TimeSpan olderThan)
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

        var now = DateTime.UtcNow;
        foreach (var name in names)
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            try
            {
                var marker = client.GetDatabase(name)
                    .GetCollection<BsonDocument>(MarkerCollection)
                    .Find(new BsonDocument("_id", "marker"))
                    .FirstOrDefault();
                if (marker is null)
                    continue;

                var claimedAtUtc = marker["claimedAtUtc"].ToUniversalTime();
                if (now - claimedAtUtc > olderThan)
                    client.DropDatabase(name);
            }
            catch (MongoException)
            {
                // Another sibling reclaiming the same abandoned database is not this run's failure.
            }
        }
    }

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
}
