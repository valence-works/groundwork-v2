using System.Data;
using System.Data.Common;
using Groundwork.Kernel.Schema;
using Groundwork.LiveDatabases;
using Groundwork.PostgreSql;
using Groundwork.MySql;
using Groundwork.Sqlite;
using Groundwork.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;
using MySqlConnector;
using Xunit;

namespace Groundwork.Differential.Tests;

/// <summary>
/// One live relational provider opened through the same schema-tool session the deployment tool
/// uses, with a second connection of the test's own for asserting on the catalog and the rows.
/// Shared by the schema differential classes so a case added to one of them runs on every provider
/// without a second copy of the fixture drifting away from the first.
/// </summary>
internal sealed record RelationalSchemaProvider(string Name, Func<RelationalSchemaStore> Open)
{
    /// <summary>
    /// A file-backed SQLite store. The assertion connection is not the provider's, so it registers
    /// the ordinal collation the provider declares its string columns with.
    /// </summary>
    internal static RelationalSchemaProvider Sqlite(string prefix) => new("sqlite", () =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"{prefix}_" + Guid.NewGuid().ToString("N") + ".db");
        var connectionString = "Data Source=" + path;
        var session = new SqliteSchemaToolProviderSessionFactory().Open(
            new SchemaToolProviderOptions("sqlite", connectionString, null, AllowCreate: true, CancellationToken.None));
        return new RelationalSchemaStore(
            session,
            $"{prefix}_" + Guid.NewGuid().ToString("N")[..12],
            () =>
            {
                var connection = new SqliteConnection(connectionString);
                connection.Open();
                connection.CreateCollation(
                    "GROUNDWORK_UTF16_ORDINAL",
                    static (left, right) => string.CompareOrdinal(left, right));
                return connection;
            },
            identifier => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"",
            (table, column, required) =>
                $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" TEXT{(required ? " NOT NULL" : string.Empty)};",
            () =>
            {
                session.Dispose();
                File.Delete(path);
            });
    });

    /// <summary>A PostgreSQL schema of this store's own, dropped with everything in it.</summary>
    internal static RelationalSchemaProvider PostgreSql(string prefix) => new("postgresql", () =>
    {
        var baseConnection = Environment.GetEnvironmentVariable("GROUNDWORK_POSTGRES_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(baseConnection),
            "Set GROUNDWORK_POSTGRES_CONNECTION to run live PostgreSQL schema proofs.");
        var schema = $"{prefix}_" + Guid.NewGuid().ToString("N");
        using (var admin = new NpgsqlConnection(baseConnection))
        {
            admin.Open();
            using var create = admin.CreateCommand();
            create.CommandText = $"CREATE SCHEMA \"{schema}\";";
            create.ExecuteNonQuery();
        }
        var connectionString = new NpgsqlConnectionStringBuilder(baseConnection) { SearchPath = schema }.ConnectionString;
        var session = new PostgreSqlSchemaToolProviderSessionFactory().Open(
            new SchemaToolProviderOptions("postgresql", connectionString, null, AllowCreate: true, CancellationToken.None));
        return new RelationalSchemaStore(
            session,
            $"{prefix}_" + Guid.NewGuid().ToString("N")[..12],
            () => new NpgsqlConnection(connectionString),
            identifier => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"",
            (table, column, required) =>
                $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" text{(required ? " NOT NULL" : string.Empty)};",
            () =>
            {
                session.Dispose();
                using (var pooled = new NpgsqlConnection(connectionString))
                    NpgsqlConnection.ClearPool(pooled);
                using var admin = new NpgsqlConnection(baseConnection);
                admin.Open();
                using var drop = admin.CreateCommand();
                drop.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;";
                drop.ExecuteNonQuery();
            });
    });

    /// <summary>
    /// The SQL Server database this test process owns, claimed through
    /// <see cref="LiveSqlServer"/>. Each target framework runs as its own process, so a shared
    /// database would give schema application two writers taking key-range locks on the same server
    /// catalog. Per-store naming separates identities within a process; this separates the catalog
    /// between them.
    /// </summary>
    internal static RelationalSchemaProvider SqlServer(string prefix) => new("sqlserver", () =>
    {
        var connectionString = LiveSqlServer.Required();
        var table = $"{prefix}_" + Guid.NewGuid().ToString("N")[..12];
        var session = new SqlServerSchemaToolProviderSessionFactory().Open(
            new SchemaToolProviderOptions("sqlserver", connectionString, null, AllowCreate: true, CancellationToken.None));
        return new RelationalSchemaStore(
            session,
            table,
            () => new SqlConnection(connectionString),
            identifier => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]",
            (table, column, required) =>
                $"ALTER TABLE [{table}] ADD [{column}] nvarchar(64){(required ? " NOT NULL" : string.Empty)};",
            () =>
            {
                session.Dispose();
                using var connection = new SqlConnection(connectionString);
                connection.Open();
                foreach (var name in new[] { table, table + "_v2" })
                {
                    using var drop = connection.CreateCommand();
                    drop.CommandText = $"DROP TABLE IF EXISTS [{name}];";
                    drop.ExecuteNonQuery();
                }
            });
    });

    /// <summary>A MySQL/MariaDB database owned by this store and dropped with it.</summary>
    internal static RelationalSchemaProvider MySql(string prefix) => new("mysql", () =>
    {
        var database = LiveMySqlDatabase.OpenOrSkip();
        var session = new MySqlSchemaToolProviderSessionFactory().Open(
            new SchemaToolProviderOptions("mysql", database.ConnectionString, null, AllowCreate: true, CancellationToken.None));
        return new RelationalSchemaStore(
            session,
            $"{prefix}_" + Guid.NewGuid().ToString("N")[..12],
            () => new MySqlConnection(database.ConnectionString),
            identifier => "`" + identifier.Replace("`", "``", StringComparison.Ordinal) + "`",
            (table, column, required) =>
                $"ALTER TABLE `{table}` ADD COLUMN `{column}` varchar(64){(required ? " NOT NULL" : string.Empty)};",
            () =>
            {
                session.Dispose();
                database.Dispose();
            });
    });
}

internal sealed class RelationalSchemaStore(
    ISchemaToolProviderSession session,
    string table,
    Func<DbConnection> connect,
    Func<string, string> quote,
    Func<string, string, bool, string> addColumn,
    Action release) : IDisposable
{
    public ISchemaToolProviderSession Session { get; } = session;

    public string Table { get; } = table;

    public string Quote(string identifier) => quote(identifier);

    /// <summary>
    /// Adds a column to this store's table with the provider's own spelling, standing in for
    /// another tool extending the catalog. The three dialects disagree about <c>ADD</c> versus
    /// <c>ADD COLUMN</c> and about what a text column is called, and nothing in Groundwork emits
    /// this statement, so the test has to.
    /// </summary>
    public void AddForeignColumn(string column, bool required) =>
        Execute(addColumn(Table, column, required));

    public void Execute(string sql)
    {
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public object? Scalar(string sql)
    {
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value == DBNull.Value ? null : value;
    }

    public bool TableExists(string name)
    {
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT 1 FROM {quote(name)} WHERE 1=0;";
        try
        {
            command.ExecuteNonQuery();
            return true;
        }
        catch (DbException)
        {
            return false;
        }
    }

    public void Dispose() => release();

    private DbConnection Connect()
    {
        var connection = connect();
        if (connection.State != ConnectionState.Open)
            connection.Open();
        return connection;
    }
}
