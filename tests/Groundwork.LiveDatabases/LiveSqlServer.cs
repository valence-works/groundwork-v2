using System.Data;
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
    private const string DatabasePrefix = "groundwork_run_";
    private const string LeaseTable = "[dbo].[__groundwork_run_lease]";
    private const int TokenLength = 32;

    /// <summary>
    /// The lease duration for a run. Heartbeats refresh this lease for the process lifetime; a
    /// later acquire can reclaim it only after the server says the lease expired. A database left
    /// unmarked by an interrupted acquisition gets the same server-side age grace period before it
    /// is eligible for cleanup.
    /// </summary>
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromHours(2);

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FencedRetryAfter = TimeSpan.FromMinutes(5);
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

        // Acquire is the only lifecycle hook every run reaches after a prior process crashes.
        // Reclaim before creating this process's database so abandoned leases do not accumulate.
        ReclaimStale(configured);

        var name = DatabasePrefix + Guid.NewGuid().ToString("N");
        var ownerToken = Guid.NewGuid().ToString("N");
        var databaseCreated = false;
        try
        {
            // A session-owned application lock closes the gap between database creation and the
            // first lease row. Reclaimers use this same name-scoped lock for markerless cleanup.
            using var databaseLock = AcquireDatabaseLock(configured, name);
            CreateDatabase(databaseLock.Connection, name);
            databaseCreated = true;
            SetRecoverySimple(databaseLock.Connection, name);
            InitializeLeaseUnderLock(configured, name, ownerToken);
            var lease = new Lease(configured, name, ownerToken);
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                lease.Dispose();
                Release(configured, name);
            };
            return new SqlConnectionStringBuilder(configured) { InitialCatalog = name }.ConnectionString;
        }
        catch
        {
            // A failure between CREATE DATABASE and lease registration must not create another
            // permanent leak. The name is generated and validated locally, so cleanup is safe.
            if (databaseCreated)
                Release(configured, name);
            throw;
        }
    }

    /// <summary>
    /// Drops run databases whose own server-side lease has expired. Discovery is deliberately
    /// separate from deletion: each candidate must atomically fence its owner immediately before
    /// the destructive operation, so a concurrent heartbeat wins if it reaches the row first.
    /// A database with no marker is considered only when its server-recorded creation age has
    /// crossed the same grace period; that path takes the name-scoped creation lock and rechecks
    /// both age and marker immediately before dropping. A present but malformed marker is
    /// preserved.
    /// <paramref name="onlyName"/> and <paramref name="unmarkedGracePeriod"/> are test-only
    /// narrowing: a lowered grace is accepted only together with one exact, validated generated
    /// name, so it cannot widen a destructive sweep. Production leaves both <see langword="null"/>.
    /// </summary>
    internal static void ReclaimStale(
        string configured,
        string? onlyName = null,
        TimeSpan? unmarkedGracePeriod = null)
    {
        if (onlyName is not null && !IsValidRunName(onlyName))
            return;
        if (unmarkedGracePeriod is { } gracePeriod && gracePeriod < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(unmarkedGracePeriod));
        if (unmarkedGracePeriod is not null && onlyName is null)
            throw new ArgumentException(
                "A reclamation grace override requires an exact database name.",
                nameof(onlyName));

        var candidates = new List<string>();
        try
        {
            using var connection = new SqlConnection(configured);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT name FROM sys.databases
                WHERE name LIKE 'groundwork\_run\_%' ESCAPE '\'
                  AND (@onlyName IS NULL OR name = @onlyName);
                """;
            AddNullableString(command, "@onlyName", onlyName);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(0);
                if (IsValidRunName(name))
                    candidates.Add(name);
            }
        }
        catch (SqlException)
        {
            // Reclamation is best-effort housekeeping; a server that cannot be reached for it is
            // the run's own connection failure to report, not this step's.
            return;
        }

        foreach (var name in candidates)
        {
            try
            {
                if (TryFence(configured, name) || TryRetryFence(configured, name))
                {
                    DropDatabase(configured, name);
                }
                else
                {
                    TryReclaimUnmarkedStale(
                        configured,
                        name,
                        unmarkedGracePeriod ?? StaleAfter);
                }
            }
            catch (SqlException)
            {
                // Another sibling won the fence, or the server rejected the drop. Either way,
                // this acquire must not fail because housekeeping is best effort.
            }
        }
    }

    /// <summary>
    /// Atomically fences one expired lease. The expiry check and owner transition happen in one
    /// server-side update, so a heartbeat either commits first and makes this return false, or
    /// this commits first and prevents the owner token from renewing afterward.
    /// </summary>
    internal static bool TryFence(string configured, string name)
    {
        if (!IsValidRunName(name))
            return false;

        try
        {
            using var connection = OpenRunDatabase(configured, name);
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                UPDATE {LeaseTable}
                SET [state] = 'fenced', [fence_token] = @fenceToken, [fenced_utc] = SYSUTCDATETIME()
                WHERE [lease_id] = 1
                  AND [state] = 'active'
                  AND LEN([owner_token]) = {TokenLength}
                  AND DATALENGTH([owner_token]) = {TokenLength}
                  AND [owner_token] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9a-fA-F]%'
                  AND [heartbeat_utc] <= DATEADD(SECOND, -@leaseSeconds, SYSUTCDATETIME());
                SELECT @@ROWCOUNT;
                """;
            AddToken(command, "@fenceToken", Guid.NewGuid().ToString("N"));
            command.Parameters.Add("@leaseSeconds", SqlDbType.Int).Value = LeaseSeconds;
            return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1;
        }
        catch (SqlException)
        {
            // Missing or malformed lease metadata is not evidence that this process owns the
            // database. Never fall back to age-only destruction.
            return false;
        }
    }

    private static bool TryRetryFence(string configured, string name)
    {
        if (!IsValidRunName(name))
            return false;

        try
        {
            using var connection = OpenRunDatabase(configured, name);
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                UPDATE {LeaseTable}
                SET [fence_token] = @fenceToken, [fenced_utc] = SYSUTCDATETIME()
                WHERE [lease_id] = 1
                  AND [state] = 'fenced'
                  AND LEN([owner_token]) = {TokenLength}
                  AND DATALENGTH([owner_token]) = {TokenLength}
                  AND [owner_token] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9a-fA-F]%'
                  AND DATALENGTH([fence_token]) = {TokenLength}
                  AND LEN([fence_token]) = {TokenLength}
                  AND [fence_token] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9a-fA-F]%'
                  AND [fenced_utc] <= DATEADD(SECOND, -@retrySeconds, SYSUTCDATETIME());
                SELECT @@ROWCOUNT;
                """;
            AddToken(command, "@fenceToken", Guid.NewGuid().ToString("N"));
            command.Parameters.Add("@retrySeconds", SqlDbType.Int).Value = FencedRetrySeconds;
            return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1;
        }
        catch (SqlException)
        {
            return false;
        }
    }

    /// <summary>
    /// Renews a lease using SQL Server's clock. It is internal so isolated provider tests can
    /// exercise the same heartbeat operation that the process timer uses.
    /// </summary>
    internal static bool RenewLease(string configured, string name, string ownerToken)
    {
        if (!IsValidRunName(name) || !IsValidToken(ownerToken))
            return false;

        try
        {
            using var connection = OpenRunDatabase(configured, name);
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                UPDATE {LeaseTable}
                SET [heartbeat_utc] = SYSUTCDATETIME()
                WHERE [lease_id] = 1 AND [state] = 'active' AND [owner_token] = @ownerToken;
                SELECT @@ROWCOUNT;
                """;
            AddToken(command, "@ownerToken", ownerToken);
            return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1;
        }
        catch (SqlException)
        {
            return false;
        }
    }

    /// <summary>
    /// Creates the lease metadata inside a freshly created run database. The initial heartbeat is
    /// assigned by SQL Server, never by the client clock. Direct callers also take the
    /// name-scoped application lock so marker creation cannot race markerless reclamation.
    /// </summary>
    internal static void InitializeLease(string configured, string name, string ownerToken)
    {
        if (!IsValidRunName(name))
            throw new ArgumentException("The run database name is not a valid Groundwork run name.", nameof(name));
        if (!IsValidToken(ownerToken))
            throw new ArgumentException("The owner token is not valid.", nameof(ownerToken));

        using var databaseLock = AcquireDatabaseLock(configured, name);
        InitializeLeaseUnderLock(configured, name, ownerToken);
    }

    private static void InitializeLeaseUnderLock(string configured, string name, string ownerToken)
    {
        using var connection = OpenRunDatabase(configured, name);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            CREATE TABLE {LeaseTable}
            (
                [lease_id] tinyint NOT NULL CONSTRAINT [PK___groundwork_run_lease] PRIMARY KEY,
                [owner_token] varchar({TokenLength}) NOT NULL,
                [heartbeat_utc] datetime2(7) NOT NULL,
                [state] varchar(16) NOT NULL,
                [fence_token] varchar({TokenLength}) NULL,
                [fenced_utc] datetime2(7) NULL,
                CONSTRAINT [CK___groundwork_run_lease_id] CHECK ([lease_id] = 1),
                CONSTRAINT [CK___groundwork_run_lease_state] CHECK ([state] IN ('active', 'fenced'))
            );
            INSERT INTO {LeaseTable} ([lease_id], [owner_token], [heartbeat_utc], [state])
            VALUES (1, @ownerToken, SYSUTCDATETIME(), 'active');
            COMMIT TRANSACTION;
            """;
        AddToken(command, "@ownerToken", ownerToken);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// The boundary used by the server-side <c>&lt;=</c> expiry predicate, kept pure for tests.
    /// </summary>
    internal static bool IsExpired(DateTime heartbeatUtc, DateTime serverNowUtc, TimeSpan leaseDuration) =>
        serverNowUtc - heartbeatUtc >= leaseDuration;

    /// <summary>
    /// An unmarked database is eligible for cleanup only after its server-recorded creation time
    /// has crossed the same conservative lease window used for marked databases.
    /// </summary>
    internal static bool IsUnmarkedStale(DateTime createdAt, DateTime serverNowUtc, TimeSpan gracePeriod) =>
        serverNowUtc - createdAt >= gracePeriod;

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

    private static int LeaseSeconds => checked((int)StaleAfter.TotalSeconds);
    private static int FencedRetrySeconds => checked((int)FencedRetryAfter.TotalSeconds);

    private static void CreateDatabase(SqlConnection connection, string name) =>
        Execute(connection, $"CREATE DATABASE {QuoteRunName(name)};");

    private static void SetRecoverySimple(SqlConnection connection, string name) =>
        Execute(connection, $"ALTER DATABASE {QuoteRunName(name)} SET RECOVERY SIMPLE;");

    private static void DropDatabase(string configured, string name)
    {
        using var connection = OpenMasterConnection(configured);
        DropDatabase(connection, name);
    }

    private static void DropDatabase(SqlConnection connection, string name)
    {
        SqlConnection.ClearAllPools();
        Execute(connection, $"ALTER DATABASE {QuoteRunName(name)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {QuoteRunName(name)};");
    }

    private static void Release(string configured, string name)
    {
        try
        {
            DropDatabase(configured, name);
        }
        catch (SqlException)
        {
            // The server outliving the run is the CI container's business, not a test result.
        }
    }

    private static SqlConnection OpenRunDatabase(string configured, string name)
    {
        var connection = new SqlConnection(new SqlConnectionStringBuilder(configured) { InitialCatalog = name }.ConnectionString);
        connection.Open();
        return connection;
    }

    private static SqlConnection OpenMasterConnection(string configured)
    {
        var connection = new SqlConnection(new SqlConnectionStringBuilder(configured) { InitialCatalog = "master" }.ConnectionString);
        connection.Open();
        return connection;
    }

    private static bool HasLeaseTable(string configured, string name)
    {
        try
        {
            using var connection = OpenRunDatabase(configured, name);
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT TOP (1) 1 FROM {LeaseTable};";
            command.ExecuteScalar();
            return true;
        }
        catch (SqlException exception) when (exception.Number == 208)
        {
            return false;
        }
        catch (SqlException)
        {
            // If metadata cannot be inspected, preserve the database rather than treating the
            // failure as evidence that this is an unmarked abandoned run.
            return true;
        }
    }

    private static void TryReclaimUnmarkedStale(
        string configured,
        string name,
        TimeSpan gracePeriod)
    {
        using var databaseLock = TryAcquireDatabaseLock(configured, name);
        if (databaseLock is null)
            return;

        if (!TryReadDatabaseAge(databaseLock.Connection, name, out var createdAt, out var serverNow) ||
            !IsUnmarkedStale(createdAt, serverNow, gracePeriod) ||
            HasLeaseTable(configured, name))
            return;

        // The lock is held through the final marker check and drop. Claim acquires this same
        // session-owned lock before CREATE DATABASE and keeps it through lease registration.
        DropDatabase(databaseLock.Connection, name);
    }

    private static bool TryReadDatabaseAge(SqlConnection connection, string name, out DateTime createdAt, out DateTime serverNow)
    {
        createdAt = default;
        serverNow = default;
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT [create_date], GETDATE()
            FROM sys.databases
            WHERE [name] = @name;
            """;
        command.Parameters.Add("@name", SqlDbType.NVarChar, 128).Value = name;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return false;

        createdAt = reader.GetDateTime(0);
        serverNow = reader.GetDateTime(1);
        return true;
    }

    private static DatabaseLock AcquireDatabaseLock(string configured, string name)
    {
        var connection = OpenMasterConnection(configured);
        try
        {
            var resource = DatabaseLockResource(name);
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
            command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = resource;
            var result = Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            if (result < 0)
                throw new InvalidOperationException($"Could not acquire SQL Server application lock '{resource}' (result {result}).");

            return new DatabaseLock(connection, resource);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static DatabaseLock? TryAcquireDatabaseLock(string configured, string name)
    {
        try
        {
            return AcquireDatabaseLock(configured, name);
        }
        catch (SqlException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static void Execute(string connectionString, string sql)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        Execute(connection, sql);
    }

    private static void Execute(SqlConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void AddNullableString(SqlCommand command, string name, string? value)
    {
        command.Parameters.Add(name, SqlDbType.NVarChar, 128).Value = (object?)value ?? DBNull.Value;
    }

    private static void AddToken(SqlCommand command, string name, string token)
    {
        command.Parameters.Add(name, SqlDbType.VarChar, TokenLength).Value = token;
    }

    private static string QuoteRunName(string name)
    {
        if (!IsValidRunName(name))
            throw new ArgumentException("The run database name is not a valid Groundwork run name.", nameof(name));
        return $"[{name}]";
    }

    internal static string DatabaseLockResource(string name)
    {
        if (!IsValidRunName(name))
            throw new ArgumentException("The run database name is not a valid Groundwork run name.", nameof(name));
        return "groundwork-run-database:" + name;
    }

    private static bool IsValidRunName(string name)
    {
        if (!name.StartsWith(DatabasePrefix, StringComparison.Ordinal) ||
            name.Length != DatabasePrefix.Length + TokenLength)
            return false;

        for (var index = DatabasePrefix.Length; index < name.Length; index++)
        {
            if (!IsHex(name[index]))
                return false;
        }

        return true;
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

    private sealed class DatabaseLock : IDisposable
    {
        private readonly SqlConnection connection;
        private readonly string resource;
        private int disposed;

        public DatabaseLock(SqlConnection connection, string resource)
        {
            this.connection = connection;
            this.resource = resource;
        }

        public SqlConnection Connection => connection;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;

            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    DECLARE @result int;
                    EXEC @result = sys.sp_releaseapplock
                        @Resource = @resource,
                        @LockOwner = 'Session';
                    SELECT @result;
                    """;
                command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = resource;
                command.ExecuteScalar();
            }
            catch (SqlException)
            {
                // Closing the session in the finally block releases a session-owned lock even
                // when explicit release is unavailable during an error or process shutdown.
            }
            finally
            {
                SqlConnection.ClearPool(connection);
                connection.Dispose();
            }
        }
    }
}
