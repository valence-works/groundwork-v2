using Groundwork.MySql;
using MySqlConnector;
using Xunit;

namespace Groundwork.LiveDatabases;

/// <summary>One isolated MySQL/MariaDB database owned by a single live test.</summary>
internal sealed class LiveMySqlDatabase : IDisposable
{
    private readonly string adminConnectionString;
    private readonly string database;

    private LiveMySqlDatabase(string adminConnectionString, string database, string connectionString)
    {
        this.adminConnectionString = adminConnectionString;
        this.database = database;
        ConnectionString = connectionString;
    }

    internal string ConnectionString { get; }

    internal static LiveMySqlDatabase OpenOrSkip()
    {
        var configured = Environment.GetEnvironmentVariable("GROUNDWORK_MYSQL_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(configured),
            "Set GROUNDWORK_MYSQL_CONNECTION to run MySQL/MariaDB integration tests.");
        var builder = new MySqlConnectionStringBuilder(configured)
        {
            Database = string.Empty,
            UseAffectedRows = false
        };
        var adminConnectionString = builder.ConnectionString;
        var database = "groundwork_" + Guid.NewGuid().ToString("N");
        using var admin = new MySqlConnection(adminConnectionString);
        try
        {
            admin.Open();
        }
        catch (Exception exception)
        {
            Skip.If(true, $"MySQL/MariaDB is unavailable: {exception.Message}");
            throw;
        }
        using (var command = admin.CreateCommand())
        {
            command.CommandText =
                $"CREATE DATABASE `{database}` CHARACTER SET utf8mb4 COLLATE {MySqlDialect.OrdinalCollation};";
            command.ExecuteNonQuery();
        }
        builder.Database = database;
        return new LiveMySqlDatabase(adminConnectionString, database, builder.ConnectionString);
    }

    public void Dispose()
    {
        using (var pooled = new MySqlConnection(ConnectionString))
            MySqlConnection.ClearPool(pooled);
        using var admin = new MySqlConnection(adminConnectionString);
        admin.Open();
        using var command = admin.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS `{database}`;";
        command.ExecuteNonQuery();
    }
}
