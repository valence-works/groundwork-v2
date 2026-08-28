using Npgsql;
using Xunit;

namespace Groundwork.PostgreSql.Tests;

internal sealed class PostgreSqlFixture : IDisposable
{
    private readonly string adminConnectionString;
    private readonly string schema;

    private PostgreSqlFixture(string adminConnectionString, string schema, string connectionString)
    {
        this.adminConnectionString = adminConnectionString;
        this.schema = schema;
        ConnectionString = connectionString;
    }

    public string ConnectionString { get; }

    public static PostgreSqlFixture OpenOrSkip()
    {
        var baseConnection = Environment.GetEnvironmentVariable("GROUNDWORK_POSTGRES_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(baseConnection),
            "Set GROUNDWORK_POSTGRES_CONNECTION to run PostgreSQL integration tests.");
        var schema = "w2_" + Guid.NewGuid().ToString("N");
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
        return new PostgreSqlFixture(baseConnection, schema, builder.ConnectionString);
    }

    public void Dispose()
    {
        // Same per-connection-string pool leak as the concurrency harness (#62): this fixture's
        // SearchPath makes its pool unreachable to every later fixture, and disposing a connection
        // only returns it to that pool. Both suites draw on one server's max_connections, so a
        // fixture that keeps its idle connections spends another suite's budget as well as its own.
        using (var pooled = new NpgsqlConnection(ConnectionString))
            NpgsqlConnection.ClearPool(pooled);

        using var admin = new NpgsqlConnection(adminConnectionString);
        admin.Open();
        using var command = admin.CreateCommand();
        command.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;";
        command.ExecuteNonQuery();
    }
}
