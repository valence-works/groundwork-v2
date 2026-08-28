using Npgsql;
using Xunit;

namespace Groundwork.LiveDatabases;

/// <summary>
/// The PostgreSQL schema this test process owns. Each matrix gets a unique search path so its
/// physical tables and Groundwork catalog cannot collide with another test process on one server.
/// </summary>
internal sealed class LivePostgreSqlStore : IDisposable
{
    private readonly string adminConnectionString;
    private readonly string schema;

    private LivePostgreSqlStore(string adminConnectionString, string schema, string connectionString)
    {
        this.adminConnectionString = adminConnectionString;
        this.schema = schema;
        ConnectionString = connectionString;
    }

    internal string ConnectionString { get; }

    internal static LivePostgreSqlStore OpenOrSkip()
    {
        var baseConnection = Environment.GetEnvironmentVariable("GROUNDWORK_POSTGRES_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(baseConnection),
            "Set GROUNDWORK_POSTGRES_CONNECTION to run live PostgreSQL proofs.");

        var schema = "groundwork_run_" + Guid.NewGuid().ToString("N");
        using var admin = new NpgsqlConnection(baseConnection);
        try
        {
            admin.Open();
        }
        catch (Exception exception)
        {
            Skip.If(true, $"PostgreSQL is unavailable: {exception.Message}");
            throw;
        }

        using (var command = admin.CreateCommand())
        {
            command.CommandText = $"CREATE SCHEMA \"{schema}\";";
            command.ExecuteNonQuery();
        }

        var builder = new NpgsqlConnectionStringBuilder(baseConnection) { SearchPath = schema };
        return new LivePostgreSqlStore(baseConnection, schema, builder.ConnectionString);
    }

    public void Dispose()
    {
        // Disposing a provider connection returns Npgsql connections to a pool. Clear this unique
        // SearchPath pool before dropping the schema so no idle connection retains a schema lock.
        using (var pooled = new NpgsqlConnection(ConnectionString))
            NpgsqlConnection.ClearPool(pooled);

        using var admin = new NpgsqlConnection(adminConnectionString);
        admin.Open();
        using var command = admin.CreateCommand();
        command.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;";
        command.ExecuteNonQuery();
    }
}
