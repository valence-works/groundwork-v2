using Microsoft.Data.SqlClient;
using Xunit;

namespace Groundwork.LiveDatabases;

/// <summary>
/// The SQL Server database this test process owns.
/// <para>
/// CI provisions one SQL Server per job and names one database in
/// <c>GROUNDWORK_SQLSERVER_CONNECTION</c>. <c>dotnet test</c> runs one test process per target
/// framework and dispatches those runs in parallel, so the moment a suite multi-targets, that one
/// database has two writers. Two Groundwork processes cannot share one SQL Server database even
/// when every suite names its tables apart: schema application runs its DDL in a serializable
/// transaction and reads the server catalog inside it, so the two runs take key-range locks on
/// <c>sys.sysschobjs</c> — which belongs to the database, not to any table — and deadlock over
/// objects neither has heard of.
/// </para>
/// <para>
/// So a process claims a database of its own and hands that out in place of the configured
/// connection string. Nothing a suite creates is reachable from the other run, whatever it is
/// called. This is the idiom the other live providers already use: PostgreSqlFixture claims a
/// schema of its own, and the Mongo suites a database of their own.
/// </para>
/// </summary>
internal static class LiveSqlServer
{
    /// <summary>
    /// How long a <c>groundwork_run_*</c> database survives after its creation before a sibling
    /// process is willing to drop it on the assumption its owner is never coming back. Sized
    /// against <c>concurrency-conformance</c>, the longest-running job that claims one of these:
    /// it carries the W2 harness against a live SQL Server and is capped at 30 minutes
    /// (<c>timeout-minutes: 30</c> in <c>.github/workflows/ci.yml</c>), so two hours is four times
    /// that job's own ceiling — comfortably past any real run, including one stalled right up to
    /// its timeout, so a run still in progress is never mistaken for an abandoned one.
    /// </summary>
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromHours(2);

    private static readonly Lazy<string?> Claimed = new(Claim, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The connection string for this process's own database, or <see langword="null"/> when no
    /// SQL Server is configured — so a suite's existing skip guard reads the same as before.
    /// </summary>
    internal static string? ConnectionString => Claimed.Value;

    /// <summary>
    /// The claimed connection string, skipping the calling test when no SQL Server is configured.
    /// Suites that need one call this rather than repeating the guard.
    /// </summary>
    internal static string Required()
    {
        Skip.If(ConnectionString is null, "Set GROUNDWORK_SQLSERVER_CONNECTION to run live SQL Server proofs.");
        return ConnectionString!;
    }

    private static string? Claim()
    {
        var configured = Environment.GetEnvironmentVariable("GROUNDWORK_SQLSERVER_CONNECTION");
        if (string.IsNullOrWhiteSpace(configured))
            return null;

        // ProcessExit is not a reliable place to reclaim: it does not run on a crash, a kill, or a
        // heavily-loaded shutdown — exactly the conditions that leave one of these behind. Acquire
        // is the hook every run passes through, so a run reclaims whatever a prior one abandoned
        // before it claims its own.
        ReclaimStale(configured, StaleAfter);

        var name = "groundwork_run_" + Guid.NewGuid().ToString("N");
        // A fresh database inherits the model database's recovery model. Simple recovery keeps the
        // log from growing across a concurrency probe that submits tens of thousands of writes.
        Execute(configured, $"CREATE DATABASE [{name}]; ALTER DATABASE [{name}] SET RECOVERY SIMPLE;");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Release(configured, name);
        return new SqlConnectionStringBuilder(configured) { InitialCatalog = name }.ConnectionString;
    }

    /// <summary>
    /// Drops every <c>groundwork_run_*</c> database on <paramref name="configured"/> whose
    /// <c>sys.databases.create_date</c> is older than <paramref name="olderThan"/>, skipping any
    /// still within that window because that is indistinguishable from a run still in progress.
    /// <c>create_date</c> and <c>GETDATE()</c> are read from the same server round trip, so the
    /// age comparison in <see cref="SelectStale"/> sidesteps whatever time zone the server's clock
    /// happens to be in, and never depends on this process's own clock.
    /// </summary>
    internal static void ReclaimStale(string configured, TimeSpan olderThan)
    {
        var candidates = new List<(string Name, DateTime CreateDate)>();
        var now = DateTime.MinValue;
        try
        {
            using var connection = new SqlConnection(configured);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT name, create_date, GETDATE() AS server_now FROM sys.databases
                WHERE name LIKE 'groundwork\_run\_%' ESCAPE '\';
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                candidates.Add((reader.GetString(0), reader.GetDateTime(1)));
                now = reader.GetDateTime(2);
            }
        }
        catch (SqlException)
        {
            // Reclamation is best-effort housekeeping; a server that cannot be reached for it is
            // the run's own connection failure to report, not this step's.
            return;
        }

        foreach (var name in SelectStale(candidates, now, olderThan))
        {
            try
            {
                Execute(configured, $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];");
            }
            catch (SqlException)
            {
                // Another sibling reclaiming the same abandoned database, or one whose owner is
                // still attached, is not this run's failure.
            }
        }
    }

    /// <summary>
    /// The names among <paramref name="candidates"/> whose <c>create_date</c> is older than
    /// <paramref name="olderThan"/> relative to <paramref name="now"/>. Pure and I/O-free by
    /// design: it is the whole of the reclaim decision, so a test can pin down that decision's
    /// boundary — including a database created a heartbeat before the threshold, and one a
    /// heartbeat after — without opening a connection or dropping anything.
    /// </summary>
    internal static IReadOnlyList<string> SelectStale(
        IEnumerable<(string Name, DateTime CreateDate)> candidates, DateTime now, TimeSpan olderThan) =>
        candidates.Where(candidate => now - candidate.CreateDate > olderThan)
            .Select(candidate => candidate.Name)
            .ToList();

    private static void Release(string configured, string name)
    {
        // Pooled connections to the claimed database would keep it in use; a test run that ends
        // without releasing them leaves the drop to fail rather than the run.
        SqlConnection.ClearAllPools();
        try
        {
            Execute(configured, $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];");
        }
        catch (SqlException)
        {
            // The server outliving the run is the CI container's business, not a test result.
        }
    }

    private static void Execute(string connectionString, string sql)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
