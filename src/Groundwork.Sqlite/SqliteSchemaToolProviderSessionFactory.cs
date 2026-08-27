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
        var builder = Builder(options);
        var connectionString = builder.ConnectionString;
        var path = SqliteDataSource.FullPath(builder.DataSource);
        var store = new Lazy<SqliteProviderConnection>(() => OpenStore(connectionString));
        if (!options.AllowCreate)
            _ = store.Value;
        var executor = new RelationalSchemaExecutor(
            () => store.Value.CreateIndependentConnection(),
            new SqliteDialect());
        return new RelationalSchemaToolSession(
            SqliteSchemaCoordinator.Identity,
            executor,
            release: () =>
            {
                if (store.IsValueCreated)
                    store.Value.Dispose();
                using var pooled = new SqliteConnection(connectionString);
                SqliteConnection.ClearPool(pooled);
            },
            inspect: options.AllowCreate
                ? target => File.Exists(path)
                    ? executor.InspectDeployedHistory(target)
                    : new PhysicalSchemaInspectionResult(PhysicalSchemaHistoryState.Empty, IsAppliedSchemaValid: true)
                : null);
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
        if (SqliteDataSource.IsMemory(builder))
            throw new SchemaToolProviderInvocationException(
                "The SQLite schema tool requires a file-backed database; an in-memory data source has no durable schema.");
        if (!options.AllowCreate)
        {
            if (!File.Exists(SqliteDataSource.FullPath(builder.DataSource)))
                throw new SchemaToolProviderException(
                    $"SQLite database '{SqliteDataSource.FullPath(builder.DataSource)}' does not exist. Only 'apply' creates the database.");
            if (builder.Mode == SqliteOpenMode.ReadWriteCreate)
                builder.Mode = SqliteOpenMode.ReadWrite;
        }
        return builder;
    }

    private static SqliteProviderConnection OpenStore(string connectionString)
    {
        try
        {
            return new SqliteProviderConnection(connectionString);
        }
        catch (InvalidOperationException exception)
        {
            throw new SchemaToolProviderException(exception.Message);
        }
    }
}
