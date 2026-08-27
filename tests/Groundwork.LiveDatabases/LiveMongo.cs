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
        url.DatabaseName = configuredName + "_run_" + Guid.NewGuid().ToString("N");
        var claimed = url.ToMongoUrl().ToString();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Release(claimed, url.DatabaseName);
        return claimed;
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
