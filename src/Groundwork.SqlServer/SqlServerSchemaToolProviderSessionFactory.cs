using Groundwork.Kernel.Schema;
using Groundwork.Substrate.Relational;
using Microsoft.Data.SqlClient;

namespace Groundwork.SqlServer;

/// <summary>Schema-tool plug-in that opens SQL Server databases for exact schema deployment.</summary>
public sealed class SqlServerSchemaToolProviderSessionFactory : ISchemaToolProviderSessionFactory
{
    public string Alias => "sqlserver";

    public ISchemaToolProviderSession Open(SchemaToolProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Connection))
            throw new SchemaToolProviderInvocationException("The SQL Server schema tool requires --connection.");
        var builder = new SqlConnectionStringBuilder(options.Connection);
        if (options.Database is not null)
            builder.InitialCatalog = options.Database;
        var connectionString = builder.ConnectionString;
        return new RelationalSchemaToolSession(
            SqlServerSchemaCoordinator.Identity,
            new RelationalSchemaExecutor(() => new SqlConnection(connectionString), new SqlServerDialect()),
            declaration => SqlServerSchemaCoordinator.Target(SqlServerSchemaCoordinator.Prepare(declaration)),
            () =>
            {
                using var pooled = new SqlConnection(connectionString);
                SqlConnection.ClearPool(pooled);
            });
    }
}
