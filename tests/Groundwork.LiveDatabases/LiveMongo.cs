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
    /// is willing to drop it on the assumption its owner is never coming back. Sized against
    /// <c>concurrency-conformance</c>, the longest-running job that claims one of these: it is
    /// capped at 30 minutes (<c>timeout-minutes: 30</c> in <c>.github/workflows/ci.yml</c>), so two
    /// hours is four times that job's own ceiling — comfortably past any real run, including one
    /// stalled right up to its timeout, so a run still in progress is never mistaken for an
    /// abandoned one.
    /// </summary>
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromHours(2);

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

        // Mongo has no notion of "a session is connected to this database" the way SQL Server does
        // — a client's connections are pooled per server, not scoped to one database — so age since
        // claim is the only signal a sibling can read back, and a run that simply outlasts the age
        // threshold would otherwise look identical to an abandoned one. A heartbeat closes that
        // gap: for as long as this process is alive, it keeps refreshing its own marker, so a
        // sibling only ever sees an old timestamp once nothing is left to refresh it.
        var heartbeat = new Timer(_ => Refresh(claimed, url.DatabaseName), null, HeartbeatInterval, HeartbeatInterval);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            heartbeat.Dispose();
            Release(claimed, url.DatabaseName);
        };
        return claimed;
    }

    /// <summary>
    /// How often the claiming process refreshes its own marker's <c>claimedAtUtc</c>. A small
    /// fraction of <see cref="StaleAfter"/> so a process that stalls between refreshes — under load,
    /// under a debugger, or simply between two slow tests — never drifts past the threshold while
    /// still alive.
    /// </summary>
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(5);

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
    /// Updates the marker's <c>claimedAtUtc</c> to now, the heartbeat this process's database
    /// relies on to keep reading as recently active for as long as this process is alive. Best
    /// effort: a transient failure here is not this run's failure, and the next tick tries again.
    /// </summary>
    private static void Refresh(string connectionString, string name)
    {
        try
        {
            var collection = new MongoClient(connectionString).GetDatabase(name).GetCollection<BsonDocument>(MarkerCollection);
            collection.UpdateOne(new BsonDocument("_id", "marker"), new BsonDocument("$set", new BsonDocument("claimedAtUtc", DateTime.UtcNow)));
        }
        catch (MongoException)
        {
            // A dropped connection or a server blip is the next heartbeat's problem, not this
            // process's to report — the process is still alive regardless of whether this tick
            // reached the server.
        }
    }

    /// <summary>
    /// Drops every database whose name starts with <paramref name="prefix"/> and whose marker
    /// records a claim older than <paramref name="olderThan"/>, skipping any database without a
    /// readable marker because that is indistinguishable from a claim still in flight. The age
    /// decision itself is <see cref="IsStale"/>, kept separate and pure so a test can pin down its
    /// boundary without writing to or dropping a real database.
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
                if (IsStale(claimedAtUtc, now, olderThan))
                    client.DropDatabase(name);
            }
            catch (MongoException)
            {
                // Another sibling reclaiming the same abandoned database is not this run's failure.
            }
        }
    }

    /// <summary>
    /// Whether a database claimed at <paramref name="claimedAtUtc"/> is old enough, relative to
    /// <paramref name="nowUtc"/>, that <paramref name="olderThan"/> calls it abandoned. Pure and
    /// I/O-free by design: it is the whole of the reclaim decision, so a test can pin down that
    /// decision's boundary — including a claim a heartbeat before the threshold, and one a
    /// heartbeat after — without touching a live database.
    /// </summary>
    internal static bool IsStale(DateTime claimedAtUtc, DateTime nowUtc, TimeSpan olderThan) =>
        nowUtc - claimedAtUtc > olderThan;

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
