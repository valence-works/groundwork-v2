using Microsoft.Data.SqlClient;
using Groundwork.LiveDatabases;
using Xunit;

namespace Groundwork.SqlServer.Tests;

/// <summary>
/// Proves the reclamation path <see cref="LiveSqlServer"/> runs on acquire directly, rather than
/// relying on the best-effort <c>ProcessExit</c> drop having fired.
/// </summary>
[Collection(SqlServerLiveDatabase.Name)]
public sealed class LiveSqlServerReclamationTests(SqlServerFixture fixture)
{
    [Fact]
    public void Reclamation_drops_a_run_database_older_than_the_threshold()
    {
        var name = CreateRunDatabase();

        LiveSqlServer.ReclaimStale(fixture.ConnectionString, TimeSpan.Zero);

        Assert.False(DatabaseExists(name));
    }

    [Fact]
    public void Reclamation_leaves_a_run_database_within_the_threshold_alone()
    {
        var name = CreateRunDatabase();

        // A run still in progress is not distinguishable from one just claimed, so an age
        // threshold well past this test's own duration must leave it standing.
        LiveSqlServer.ReclaimStale(fixture.ConnectionString, TimeSpan.FromHours(2));

        Assert.True(DatabaseExists(name));

        Execute($"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];");
    }

    private string CreateRunDatabase()
    {
        var name = "groundwork_run_" + Guid.NewGuid().ToString("N");
        Execute($"CREATE DATABASE [{name}];");
        return name;
    }

    private bool DatabaseExists(string name)
    {
        using var connection = new SqlConnection(fixture.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.databases WHERE name = @name;";
        command.Parameters.AddWithValue("@name", name);
        return (int)command.ExecuteScalar() > 0;
    }

    private void Execute(string sql)
    {
        using var connection = new SqlConnection(fixture.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
