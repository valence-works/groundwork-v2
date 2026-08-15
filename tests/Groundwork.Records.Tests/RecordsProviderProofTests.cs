using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Groundwork.MongoDb;
using Groundwork.PostgreSql;
using Groundwork.Records;
using Groundwork.Records.TestingAdapter;
using Groundwork.SqlServer;
using Groundwork.Sqlite;
using Groundwork.Testing;
using MongoDB.Bson;
using MongoDB.Driver;
using Npgsql;
using Xunit;

namespace Groundwork.Records.Tests;

public sealed class RecordsProviderProofTests
{
    [Fact]
    public void Providers_do_not_reference_the_typed_records_contract()
    {
        var providers = new[]
        {
            typeof(PostgreSqlProviderFactory).Assembly,
            typeof(SqlServerProviderFactory).Assembly,
            typeof(SqliteProviderFactory).Assembly,
            typeof(MongoDbProviderFactory).Assembly
        };

        Assert.All(providers, provider => Assert.DoesNotContain(
            provider.GetReferencedAssemblies(),
            reference => string.Equals(reference.Name, "Groundwork.Records", StringComparison.Ordinal)));
    }

    [Fact]
    public void SQLite_executes_typed_records_and_catalog_has_exact_declared_columns()
    {
        var path = Path.Combine(Path.GetTempPath(), "groundwork-records-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var connection = new SqliteProviderFactory().Create("Data Source=" + path);
            var table = RecordTestFixture.CustomerTable("_records_sqlite_" + Guid.NewGuid().ToString("N"));
            AssertTypedCrud(connection, table, "sqlite@example.test");
            var catalogTable = RecordTestFixture.CustomerTable(table.Definition.Name + "_plain", optimistic: false);
            Assert.True(connection.Schema.Apply(catalogTable.Definition).Applied);
            using var catalog = new SqliteConnection("Data Source=" + path);
            catalog.Open();
            using var command = catalog.CreateCommand();
            command.CommandText = $"PRAGMA table_info([{catalogTable.Definition.Name}]);";
            using var reader = command.ExecuteReader();
            var columns = new List<string>();
            while (reader.Read()) columns.Add(reader.GetString(1));
            Assert.Equal(["id", "name", "email"], columns.Where(name => name != "__groundwork_action"));
            Assert.DoesNotContain(columns, name => name.Contains("json", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(columns, name => name.Contains("envelope", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain("__groundwork_version", columns);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [SkippableFact]
    public void PostgreSQL_executes_typed_records()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_POSTGRES_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_POSTGRES_CONNECTION to run PostgreSQL records proof.");
        using var connection = new PostgreSqlProviderFactory().Create(connectionString!);
        AssertTypedCrud(connection, RecordTestFixture.CustomerTable("records_pg_" + Guid.NewGuid().ToString("N")), "pg@example.test");

        var catalogTable = RecordTestFixture.CustomerTable("records_pg_plain_" + Guid.NewGuid().ToString("N"), optimistic: false);
        Assert.True(connection.Schema.Apply(catalogTable.Definition).Applied);
        Assert.Equal(
            ["id", "name", "email"],
            ReadPostgreSqlColumns(connectionString!, catalogTable.Definition.Name));
    }

    [SkippableFact]
    public void SQLServer_executes_typed_records()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_SQLSERVER_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_SQLSERVER_CONNECTION to run SQL Server records proof.");
        using var connection = new SqlServerProviderFactory().Create(connectionString!);
        AssertTypedCrud(connection, RecordTestFixture.CustomerTable("records_sqlserver_" + Guid.NewGuid().ToString("N")), "sqlserver@example.test");

        var catalogTable = RecordTestFixture.CustomerTable("records_sqlserver_plain_" + Guid.NewGuid().ToString("N"), optimistic: false);
        Assert.True(connection.Schema.Apply(catalogTable.Definition).Applied);
        Assert.Equal(
            ["id", "name", "email"],
            ReadSqlServerColumns(connectionString!, catalogTable.Definition.Name));
    }

    [SkippableFact]
    public void MongoDB_executes_typed_records()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_MONGO_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_MONGO_CONNECTION to run MongoDB records proof.");
        using var connection = new MongoDbProviderFactory().Create(connectionString!);
        var nativeConnection = Assert.IsType<MongoDbProviderConnection>(connection);
        var table = RecordTestFixture.CustomerTable("records_mongo_" + Guid.NewGuid().ToString("N"));
        Assert.True(connection.Schema.Apply(table.Definition).Applied);
        var records = table.Open(connection);
        var customer = Customer.Create("Ada", "mongo@example.test");
        var inserted = records.Insert(customer);
        Assert.Equal(RecordWriteStatus.Inserted, inserted.Status);
        var updated = records.Update(customer with { Name = "Ada Lovelace" }, RecordWriteOptions.IfVersion(1));
        Assert.Equal(RecordWriteStatus.Updated, updated.Status);
        Assert.Equal(2, updated.Version);
        Assert.Equal(RecordWriteStatus.ConcurrencyConflict,
            records.Update(customer with { Name = "stale" }, RecordWriteOptions.IfVersion(1)).Status);
        Assert.Equal("Ada Lovelace", Assert.Single(records.Query(
            table.Query.Where(row => row.Email == "mongo@example.test")).ToArray()).Name);

        var catalogTable = RecordTestFixture.CustomerTable("records_mongo_plain_" + Guid.NewGuid().ToString("N"), optimistic: false);
        Assert.True(connection.Schema.Apply(catalogTable.Definition).Applied);
        var catalogRecords = catalogTable.Open(connection);
        Assert.Equal(RecordWriteStatus.Inserted,
            catalogRecords.Insert(Customer.Create("Ada", "mongo-plain@example.test")).Status);
        var document = nativeConnection.Database
            .GetCollection<BsonDocument>(catalogTable.Definition.Name)
            .Find(FilterDefinition<BsonDocument>.Empty)
            .First();
        Assert.Equal(["_id", "id", "name", "email"], document.Names);
    }

    private static IReadOnlyList<string> ReadPostgreSqlColumns(string connectionString, string tableName)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT column_name
            FROM information_schema.columns
            WHERE table_schema = current_schema() AND table_name = @table
            ORDER BY ordinal_position;
            """;
        command.Parameters.AddWithValue("table", tableName);
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
            columns.Add(reader.GetString(0));
        return columns;
    }

    private static IReadOnlyList<string> ReadSqlServerColumns(string connectionString, string tableName)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.name
            FROM sys.columns c
            JOIN sys.tables t ON t.object_id = c.object_id
            WHERE t.name = @table
            ORDER BY c.column_id;
            """;
        command.Parameters.AddWithValue("@table", tableName);
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
            columns.Add(reader.GetString(0));
        return columns;
    }

    private static void AssertTypedCrud(
        IStorageProviderConnection connection,
        RecordTable<Customer> table,
        string email)
    {
        Assert.True(connection.Schema.Apply(table.Definition).Applied);
        var records = table.Open(connection);
        var customer = Customer.Create("Ada", email);
        var inserted = records.Insert(customer);
        Assert.Equal(RecordWriteStatus.Inserted, inserted.Status);
        Assert.Equal(1, inserted.Version);

        var updated = records.Update(customer with { Name = "Ada Lovelace" }, RecordWriteOptions.IfVersion(1));
        Assert.Equal(RecordWriteStatus.Updated, updated.Status);
        Assert.Equal(2, updated.Version);

        var stale = records.Update(customer with { Name = "stale" }, RecordWriteOptions.IfVersion(1));
        Assert.Equal(RecordWriteStatus.ConcurrencyConflict, stale.Status);

        var query = table.Query.Where(row => row.Email == email).OrderBy(row => row.Name);
        var result = records.Query(query);
        var match = Assert.Single(result);
        Assert.Equal("Ada Lovelace", match.Name);
    }
}
