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
    /// <c>sys.databases.create_date</c> is older than <paramref name="olderThan"/> and which has no
    /// session currently connected to it, skipping any still within that age window — or with a
    /// live session — because either is indistinguishable from a run still in progress.
    /// <c>create_date</c> and <c>GETDATE()</c> are read from the same server round trip, so the
    /// age comparison in <see cref="SelectStale"/> sidesteps whatever time zone the server's clock
    /// happens to be in, and never depends on this process's own clock.
    /// <para>
    /// Age alone is not sufficient: a run that takes longer than <paramref name="olderThan"/> — an
    /// unusually large concurrency probe, a debugger attached mid-test — is still connected to its
    /// database the entire time, so <see cref="HasActiveSession"/> checks <c>sys.dm_exec_sessions</c>
    /// immediately before a drop and skips any candidate with a session still attached, however old.
    /// A candidate that cannot be checked (for example, a permissions error) is left alone rather
    /// than assumed idle, since the whole point of this check is to err toward not touching a
    /// database whose state cannot be confirmed.
    /// </para>
    /// <para>
    /// <paramref name="onlyName"/>, when given, narrows discovery to that one database name in
    /// addition to the usual pattern. Production never sets it. It exists so a test can prove the
    /// query-and-drop path end to end with a lowered <paramref name="olderThan"/> — <c>create_date</c>
    /// cannot be backdated, so that is the only way to manufacture staleness — while the exact-name
    /// filter makes it structurally impossible for that lowered threshold to also match a sibling
    /// process's database, whatever its age.
    /// </para>
    /// </summary>
    internal static void ReclaimStale(string configured, TimeSpan olderThan, string? onlyName = null)
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
                WHERE name LIKE 'groundwork\_run\_%' ESCAPE '\' AND (@onlyName IS NULL OR name = @onlyName);
                """;
            command.Parameters.AddWithValue("@onlyName", (object?)onlyName ?? DBNull.Value);
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
                if (HasActiveSession(configured, name))
                    continue;

                Execute(configured, $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];");
            }
            catch (SqlException)
            {
                // Another sibling reclaiming the same abandoned database, one whose owner is still
                // attached, or a failure to even check for one — none of those are this run's
                // failure, and none of them justify dropping a database this process could not
                // confirm was actually abandoned.
            }
        }
    }

    /// <summary>
    /// Whether any session is currently connected to the database named <paramref name="name"/>.
    /// A database past the age threshold with a session still attached belongs to a run still in
    /// progress — long enough to have outlasted <see cref="StaleAfter"/> — not an abandoned one, so
    /// reclamation defers to this check rather than age alone.
    /// </summary>
    private static bool HasActiveSession(string configured, string name)
    {
        using var connection = new SqlConnection(configured);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE database_id = DB_ID(@name);";
        command.Parameters.AddWithValue("@name", name);
        return (int)command.ExecuteScalar()! > 0;
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
