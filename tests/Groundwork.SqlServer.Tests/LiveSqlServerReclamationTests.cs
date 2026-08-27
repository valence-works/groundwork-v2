using Microsoft.Data.SqlClient;
using Groundwork.LiveDatabases;
using Xunit;

namespace Groundwork.SqlServer.Tests;

/// <summary>
/// Proves the reclamation path <see cref="LiveSqlServer"/> runs on acquire directly, rather than
/// relying on the best-effort <c>ProcessExit</c> drop having fired.
/// <para>
/// The decision itself — which names among a set of candidates are old enough to reclaim — is
/// <see cref="LiveSqlServer.SelectStale"/>, a pure function with no connection and nothing to drop.
/// This suite shares its live server with every other suite in the repository, and with whatever
/// sibling process is running concurrently, so pinning down that decision's boundary here, rather
/// than by lowering the age threshold against a real live database, is what keeps these tests from
/// being able to touch anyone else's database.
/// </para>
/// </summary>
public sealed class LiveSqlServerReclamationTests
{
    [Fact]
    public void A_database_older_than_the_threshold_is_selected()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var candidates = new[] { ("groundwork_run_stale", now - TimeSpan.FromHours(3)) };

        var stale = LiveSqlServer.SelectStale(candidates, now, TimeSpan.FromHours(2));

        Assert.Equal(["groundwork_run_stale"], stale);
    }

    [Fact]
    public void A_database_within_the_threshold_is_left_alone()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var candidates = new[] { ("groundwork_run_fresh", now - TimeSpan.FromMinutes(5)) };

        var stale = LiveSqlServer.SelectStale(candidates, now, TimeSpan.FromHours(2));

        Assert.Empty(stale);
    }

    [Fact]
    public void A_database_exactly_at_the_threshold_is_left_alone()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var candidates = new[] { ("groundwork_run_boundary", now - TimeSpan.FromHours(2)) };

        var stale = LiveSqlServer.SelectStale(candidates, now, TimeSpan.FromHours(2));

        Assert.Empty(stale);
    }

    [Fact]
    public void Only_the_stale_names_among_several_candidates_are_selected()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var candidates = new[]
        {
            ("groundwork_run_stale", now - TimeSpan.FromHours(3)),
            ("groundwork_run_fresh", now - TimeSpan.FromMinutes(5)),
            ("groundwork_run_also_stale", now - TimeSpan.FromDays(1))
        };

        var stale = LiveSqlServer.SelectStale(candidates, now, TimeSpan.FromHours(2));

        Assert.Equal(["groundwork_run_stale", "groundwork_run_also_stale"], stale);
    }

    /// <summary>
    /// Exercises the query and drop plumbing end to end, against whatever server
    /// <c>GROUNDWORK_SQLSERVER_CONNECTION</c> names, using the real production threshold rather
    /// than a lowered one. That threshold cannot mistake a database this test just created for one
    /// abandoned two hours ago, so this is safe to run against a server other processes share.
    /// </summary>
    [SkippableFact]
    public void Reclamation_with_the_real_threshold_leaves_a_freshly_created_database_alone()
    {
        var connectionString = LiveSqlServer.Required();
        var name = "groundwork_run_" + Guid.NewGuid().ToString("N");
        Execute(connectionString, $"CREATE DATABASE [{name}];");

        try
        {
            LiveSqlServer.ReclaimStale(connectionString, LiveSqlServer.StaleAfter);

            Assert.True(DatabaseExists(connectionString, name));
        }
        finally
        {
            Execute(connectionString, $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];");
        }
    }

    private static bool DatabaseExists(string connectionString, string name)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.databases WHERE name = @name;";
        command.Parameters.AddWithValue("@name", name);
        return (int)command.ExecuteScalar() > 0;
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
