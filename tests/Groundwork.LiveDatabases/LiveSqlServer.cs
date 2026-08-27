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

        var name = "groundwork_run_" + Guid.NewGuid().ToString("N");
        // A fresh database inherits the model database's recovery model. Simple recovery keeps the
        // log from growing across a concurrency probe that submits tens of thousands of writes.
        Execute(configured, $"CREATE DATABASE [{name}]; ALTER DATABASE [{name}] SET RECOVERY SIMPLE;");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Release(configured, name);
        return new SqlConnectionStringBuilder(configured) { InitialCatalog = name }.ConnectionString;
    }

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
