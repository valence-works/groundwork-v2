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
            throw new ArgumentException("The SQL Server schema tool requires a connection string.", nameof(options));
        var builder = new SqlConnectionStringBuilder(options.Connection);
        if (options.Database is not null)
            builder.InitialCatalog = options.Database;
        var connectionString = builder.ConnectionString;
        return new RelationalSchemaToolSession(
            new("SQLServer", "1.0"),
            () => new SqlConnection(connectionString),
            new SqlServerDialect(),
            () =>
            {
                using var pooled = new SqlConnection(connectionString);
                SqlConnection.ClearPool(pooled);
            });
    }
}
