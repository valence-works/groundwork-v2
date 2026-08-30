using Groundwork.Kernel.Schema;
using Groundwork.Substrate.Relational;
using MySqlConnector;

namespace Groundwork.MySql;

/// <summary>Schema-tool plug-in for MySQL and MariaDB databases.</summary>
public sealed class MySqlSchemaToolProviderSessionFactory : ISchemaToolProviderSessionFactory
{
    public string Alias => "mysql";

    public ISchemaToolProviderSession Open(SchemaToolProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Connection))
            throw new SchemaToolProviderInvocationException("The MySQL schema tool requires --connection.");
        var builder = new MySqlConnectionStringBuilder(options.Connection)
        {
            UseAffectedRows = false
        };
        if (options.Database is not null)
            builder.Database = options.Database;
        var connectionString = builder.ConnectionString;
        return new RelationalSchemaToolSession(
            MySqlSchemaCoordinator.Identity,
            new RelationalSchemaExecutor(() => new MySqlConnection(connectionString), new MySqlDialect()),
            declaration => MySqlSchemaCoordinator.Target(MySqlSchemaCoordinator.Physicalize(declaration)),
            () => MySqlConnection.ClearAllPools());
    }
}
