using Groundwork.LiveDatabases;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Groundwork.SqlServer.Tests;

public sealed class LiveSqlServerReclamationTests
{
    [Fact]
    public void A_lease_before_the_expiry_boundary_is_not_expired()
    {
        var heartbeat = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.False(LiveSqlServer.IsExpired(heartbeat, heartbeat + TimeSpan.FromHours(2) - TimeSpan.FromTicks(1), TimeSpan.FromHours(2)));
    }

    [Fact]
    public void A_lease_at_or_after_the_expiry_boundary_is_expired()
    {
        var heartbeat = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(LiveSqlServer.IsExpired(heartbeat, heartbeat + TimeSpan.FromHours(2), TimeSpan.FromHours(2)));
        Assert.True(LiveSqlServer.IsExpired(heartbeat, heartbeat + TimeSpan.FromHours(3), TimeSpan.FromHours(2)));
    }

    [Fact]
    public void An_unmarked_database_from_a_recent_acquisition_failure_is_not_reclaimable()
    {
        var created = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.False(LiveSqlServer.IsUnmarkedStale(
            created,
            created + TimeSpan.FromHours(2) - TimeSpan.FromTicks(1),
            TimeSpan.FromHours(2)));
    }

    [Fact]
    public void An_unmarked_database_from_an_old_acquisition_failure_is_reclaimable()
    {
        var created = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(LiveSqlServer.IsUnmarkedStale(
            created,
            created + TimeSpan.FromHours(2),
            TimeSpan.FromHours(2)));
    }

    [Fact]
    public void A_fenced_lease_with_malformed_owner_metadata_is_not_retryable()
    {
        var fencedAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.False(LiveSqlServer.IsRetryableFencedLease(
            "not-a-token",
            Guid.NewGuid().ToString("N"),
            fencedAt,
            fencedAt + TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Database_lock_resource_is_name_scoped_and_rejects_invalid_names()
    {
        var name = NewName();

        Assert.Equal("groundwork-run-database:" + name, LiveSqlServer.DatabaseLockResource(name));
        Assert.Throws<ArgumentException>(() => LiveSqlServer.DatabaseLockResource("groundwork_run_not_a_run"));
    }

    [Fact]
    public void A_lowered_unmarked_grace_requires_an_exact_candidate()
    {
        Assert.Throws<ArgumentException>(() =>
            LiveSqlServer.ReclaimStale("Server=unused", unmarkedGracePeriod: TimeSpan.Zero));
    }

    [SkippableFact]
    public void A_live_owner_heartbeat_preserves_an_old_run()
    {
        var connectionString = ConnectionOrSkip();
        var name = NewName();
        var ownerToken = Guid.NewGuid().ToString("N");
        RunWithDatabases(connectionString, () =>
        {
            CreateDatabase(connectionString, name);
            LiveSqlServer.InitializeLease(connectionString, name, ownerToken);
            ExpireLease(connectionString, name);

            Assert.True(LiveSqlServer.RenewLease(connectionString, name, ownerToken));
            LiveSqlServer.ReclaimStale(connectionString, onlyName: name);

            Assert.True(DatabaseExists(connectionString, name));
            Assert.True(LiveSqlServer.RenewLease(connectionString, name, ownerToken));
        }, name);
    }

    [SkippableFact]
    public void An_abandoned_lease_is_reclaimed()
    {
        var connectionString = ConnectionOrSkip();
        var name = NewName();
        RunWithDatabases(connectionString, () =>
        {
            CreateDatabase(connectionString, name);
            LiveSqlServer.InitializeLease(connectionString, name, Guid.NewGuid().ToString("N"));
            ExpireLease(connectionString, name);

            LiveSqlServer.ReclaimStale(connectionString, onlyName: name);

            Assert.False(DatabaseExists(connectionString, name));
        }, name);
    }

    [SkippableFact]
    public void Exactly_one_concurrent_reclaimer_can_fence_an_abandoned_lease()
    {
        var connectionString = ConnectionOrSkip();
        var name = NewName();
        var ownerToken = Guid.NewGuid().ToString("N");
        RunWithDatabases(connectionString, () =>
        {
            CreateDatabase(connectionString, name);
            LiveSqlServer.InitializeLease(connectionString, name, ownerToken);
            ExpireLease(connectionString, name);

            var fences = Task.WhenAll(
                Task.Run(() => LiveSqlServer.TryFence(connectionString, name)),
                Task.Run(() => LiveSqlServer.TryFence(connectionString, name))).GetAwaiter().GetResult();

            Assert.Equal(1, fences.Count(fenced => fenced));
            Assert.True(DatabaseExists(connectionString, name));
            Assert.False(LiveSqlServer.RenewLease(connectionString, name, ownerToken));
        }, name);
    }

    [SkippableFact]
    public void Missing_or_malformed_lease_metadata_is_not_reclaimed()
    {
        var connectionString = ConnectionOrSkip();
        var missingName = NewName();
        var malformedName = NewName();
        RunWithDatabases(connectionString, () =>
        {
            CreateDatabase(connectionString, missingName);
            CreateDatabase(connectionString, malformedName);
            CreateMalformedLeaseTable(connectionString, malformedName);

            LiveSqlServer.ReclaimStale(connectionString, onlyName: missingName);
            LiveSqlServer.ReclaimStale(connectionString, onlyName: malformedName);

            Assert.True(DatabaseExists(connectionString, missingName));
            Assert.True(DatabaseExists(connectionString, malformedName));
        }, missingName, malformedName);
    }

    [SkippableFact]
    public void Markerless_reclamation_waits_for_the_name_scoped_creation_lock()
    {
        var connectionString = ConnectionOrSkip();
        var name = NewName();
        RunWithDatabases(connectionString, () =>
        {
            CreateDatabase(connectionString, name);
            using var creationLock = HoldDatabaseLock(connectionString, name);

            LiveSqlServer.ReclaimStale(
                connectionString,
                onlyName: name,
                unmarkedGracePeriod: TimeSpan.Zero);

            Assert.True(DatabaseExists(connectionString, name));
            creationLock.Dispose();

            LiveSqlServer.ReclaimStale(
                connectionString,
                onlyName: name,
                unmarkedGracePeriod: TimeSpan.Zero);

            Assert.False(DatabaseExists(connectionString, name));
        }, name);
    }

    private static string ConnectionOrSkip()
    {
        var connection = Environment.GetEnvironmentVariable("GROUNDWORK_SQLSERVER_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connection), "Set GROUNDWORK_SQLSERVER_CONNECTION to run live SQL Server proofs.");
        return connection!;
    }

    private static string NewName() => "groundwork_run_" + Guid.NewGuid().ToString("N");

    private static void CreateDatabase(string connectionString, string name) =>
        Execute(connectionString, $"CREATE DATABASE [{name}];");

    private static void ExpireLease(string connectionString, string name)
    {
        using var connection = OpenRunDatabase(connectionString, name);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE [dbo].[__groundwork_run_lease] SET [heartbeat_utc] = DATEADD(HOUR, -3, SYSUTCDATETIME());";
        command.ExecuteNonQuery();
    }

    private static void CreateMalformedLeaseTable(string connectionString, string name)
    {
        using var connection = OpenRunDatabase(connectionString, name);
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE [dbo].[__groundwork_run_lease]
            (
                [lease_id] tinyint NOT NULL PRIMARY KEY,
                [owner_token] varchar(32) NOT NULL,
                [heartbeat_utc] datetime2(7) NOT NULL,
                [state] varchar(16) NOT NULL,
                [fence_token] varchar(32) NULL,
                [fenced_utc] datetime2(7) NULL
            );
            INSERT INTO [dbo].[__groundwork_run_lease]
                ([lease_id], [owner_token], [heartbeat_utc], [state])
            VALUES (1, 'bad', DATEADD(HOUR, -3, SYSUTCDATETIME()), 'active');
            """;
        command.ExecuteNonQuery();
    }

    private static bool DatabaseExists(string connectionString, string name)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.databases WHERE name = @name;";
        command.Parameters.Add("@name", System.Data.SqlDbType.NVarChar, 128).Value = name;
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    private static SqlConnection OpenRunDatabase(string connectionString, string name)
    {
        var connection = new SqlConnection(new SqlConnectionStringBuilder(connectionString) { InitialCatalog = name }.ConnectionString);
        connection.Open();
        return connection;
    }

    private static SqlConnection HoldDatabaseLock(string connectionString, string name)
    {
        var connection = new SqlConnection(
            new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" }.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = @resource,
                @LockMode = 'Exclusive',
                @LockOwner = 'Session',
                @LockTimeout = 0,
                @DbPrincipal = 'public';
            SELECT @result;
            """;
        command.Parameters.Add("@resource", System.Data.SqlDbType.NVarChar, 255).Value =
            LiveSqlServer.DatabaseLockResource(name);
        var result = Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        if (result < 0)
        {
            connection.Dispose();
            throw new InvalidOperationException($"Could not hold the database lock (result {result}).");
        }

        return connection;
    }

    private static void DropIfExists(string connectionString, string name)
    {
        try
        {
            Execute(connectionString, $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];");
        }
        catch (SqlException)
        {
        }
    }

    private static void RunWithDatabases(string connectionString, Action body, params string[] names)
    {
        try
        {
            body();
        }
        finally
        {
            foreach (var name in names)
                DropIfExists(connectionString, name);
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
