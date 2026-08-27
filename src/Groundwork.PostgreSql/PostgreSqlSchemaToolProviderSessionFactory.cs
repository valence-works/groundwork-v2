using Groundwork.Kernel.Schema;
using Groundwork.Substrate.Relational;
using Npgsql;

namespace Groundwork.PostgreSql;

/// <summary>Schema-tool plug-in that opens PostgreSQL databases for exact schema deployment.</summary>
public sealed class PostgreSqlSchemaToolProviderSessionFactory : ISchemaToolProviderSessionFactory
{
    public string Alias => "postgresql";

    public ISchemaToolProviderSession Open(SchemaToolProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Connection))
            throw new SchemaToolProviderInvocationException("The PostgreSQL schema tool requires --connection.");
        var builder = new NpgsqlConnectionStringBuilder(options.Connection);
        if (options.Database is not null)
            builder.Database = options.Database;
        var connectionString = builder.ConnectionString;
        return new RelationalSchemaToolSession(
            PostgreSqlSchemaCoordinator.Identity,
            new RelationalSchemaExecutor(() => new NpgsqlConnection(connectionString), new PostgreSqlDialect()),
            () =>
            {
                using var pooled = new NpgsqlConnection(connectionString);
                NpgsqlConnection.ClearPool(pooled);
            });
    }
}
