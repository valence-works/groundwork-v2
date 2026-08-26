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
        var connectionString = Builder(options).ConnectionString;
        var store = new SqliteProviderConnection(connectionString);
        return new RelationalSchemaToolSession(
            SqliteSchemaCoordinator.Identity,
            store.CreateIndependentConnection,
            new SqliteDialect(),
            () =>
            {
                store.Dispose();
                using var pooled = new SqliteConnection(connectionString);
                SqliteConnection.ClearPool(pooled);
            });
    }

    private static SqliteConnectionStringBuilder Builder(SchemaToolProviderOptions options)
    {
        if (options.Connection is null && options.Database is null)
            throw new SchemaToolProviderInvocationException(
                "The SQLite schema tool requires --connection or --database.");
        var builder = options.Connection is null
            ? new SqliteConnectionStringBuilder()
            : new SqliteConnectionStringBuilder(options.Connection);
        if (options.Database is not null)
            builder.DataSource = options.Database;
        if (builder.Mode == SqliteOpenMode.Memory ||
            string.IsNullOrWhiteSpace(builder.DataSource) ||
            builder.DataSource.Contains(":memory:", StringComparison.OrdinalIgnoreCase))
            throw new SchemaToolProviderInvocationException(
                "The SQLite schema tool requires a file-backed database; an in-memory data source has no durable schema.");
        if (!options.AllowCreate)
        {
            var path = DatabasePath(builder.DataSource);
            if (!File.Exists(path))
                throw new InvalidOperationException(
                    $"SQLite database '{path}' does not exist. Only 'apply' creates the database.");
            builder.Mode = SqliteOpenMode.ReadWrite;
        }
        return builder;
    }

    private static string DatabasePath(string dataSource)
    {
        if (dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            dataSource = dataSource[5..].Split('?', 2)[0];
        return Path.GetFullPath(dataSource);
    }
}
