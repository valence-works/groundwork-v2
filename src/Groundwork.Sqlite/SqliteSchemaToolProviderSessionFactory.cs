using Groundwork.Kernel.Schema;
using Groundwork.Substrate.Relational;
using Microsoft.Data.Sqlite;

namespace Groundwork.Sqlite;

/// <summary>Schema-tool plug-in that opens SQLite stores through the durable store-scoped schema lock.</summary>
public sealed class SqliteSchemaToolProviderSessionFactory : ISchemaToolProviderSessionFactory
{
    public string Alias => "sqlite";

    public ISchemaToolProviderSession Open(SchemaToolProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var store = new SqliteProviderConnection(ConnectionString(options));
        return new RelationalSchemaToolSession(
            new("SQLite", "1.0"),
            store.CreateIndependentConnection,
            new SqliteDialect(),
            store.Dispose);
    }

    private static string ConnectionString(SchemaToolProviderOptions options)
    {
        if (options.Connection is null && options.Database is null)
            throw new ArgumentException(
                "The SQLite schema tool requires a connection string or a database file path.",
                nameof(options));
        var builder = options.Connection is null
            ? new SqliteConnectionStringBuilder()
            : new SqliteConnectionStringBuilder(options.Connection);
        if (options.Database is not null)
            builder.DataSource = options.Database;
        return builder.ConnectionString;
    }
}
