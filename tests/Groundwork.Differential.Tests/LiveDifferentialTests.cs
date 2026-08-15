using System.Data.Common;
using Groundwork.MongoDb;
using Groundwork.PostgreSql;
using Groundwork.Query.Model;
using Groundwork.SqlServer;
using Groundwork.Sqlite;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MongoDB.Bson;
using MongoDB.Driver;
using Npgsql;
using Xunit;

namespace Groundwork.Differential.Tests;

public sealed class LiveDifferentialTests
{
    [Fact]
    public void SQLite_results_match_the_provider_neutral_oracle()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var setup = connection.CreateCommand())
        {
            setup.CommandText = "CREATE TABLE customers (id INTEGER NOT NULL PRIMARY KEY, name TEXT NULL, amount INTEGER NULL);" +
                                "INSERT INTO customers (id,name,amount) VALUES (1,'Alice',2),(2,NULL,1),(3,'Bob',NULL),(4,'Alice',NULL);";
            setup.ExecuteNonQuery();
        }

        var request = Request(new TableId("customers"));
        var command = new SqliteQueryRenderer().Render(request, Options(request.Table));
        var actual = ExecuteRelational(connection, command);
        Assert.Equal(ExpectedIds(), actual);
    }

    [Fact]
    public void SQLite_nullable_keyset_pages_are_contiguous_and_deterministic()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection, "CREATE TABLE customers (id INTEGER NOT NULL PRIMARY KEY, name TEXT NULL, amount INTEGER NULL);");
        Execute(connection, "INSERT INTO customers (id,name,amount) VALUES (1,'Alice',2),(2,NULL,1),(3,'Bob',NULL),(4,'Alice',NULL);");

        var table = new TableId("customers");
        var firstRequest = Request(table, Paging.Keyset(2));
        var first = ExecuteRelational(connection, new SqliteQueryRenderer().Render(firstRequest, Options(table)));
        Assert.Equal(new[] { 4L, 2L }, first);

        var amount = new ColumnRef(table, "amount", QueryType.Int32);
        var id = new ColumnRef(table, "id", QueryType.Int64, isNullable: false);
        var continuation = QueryContinuationToken.Encode([QueryConstant.Of(amount, null), QueryConstant.Of(id, 4L)]);
        var secondRequest = Request(table, Paging.Continuation(continuation, 2));
        var second = ExecuteRelational(connection, new SqliteQueryRenderer().Render(secondRequest, Options(table)));
        Assert.Equal(new[] { 2L, 1L }, second);
    }

    [SkippableFact]
    public void PostgreSQL_results_match_the_provider_neutral_oracle_when_configured()
    {
        var connectionString = Required("GROUNDWORK_POSTGRES_CONNECTION");
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        var table = NewTable();
        try
        {
            Execute(connection, $"CREATE TABLE \"{table}\" (\"id\" bigint NOT NULL PRIMARY KEY, \"name\" text NULL, \"amount\" integer NULL);");
            Execute(connection, $"INSERT INTO \"{table}\" (\"id\",\"name\",\"amount\") VALUES (1,'Alice',2),(2,NULL,1),(3,'Bob',NULL),(4,'Alice',NULL);");
            var command = new PostgreSqlQueryRenderer().Render(Request(new TableId(table)), Options(new TableId(table)));
            Assert.Equal(ExpectedIds(), ExecuteRelational(connection, command));
        }
        finally
        {
            Execute(connection, $"DROP TABLE IF EXISTS \"{table}\";");
        }
    }

    [SkippableFact]
    public void SQL_Server_results_match_the_provider_neutral_oracle_when_configured()
    {
        var connectionString = Required("GROUNDWORK_SQLSERVER_CONNECTION");
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        var table = NewTable();
        try
        {
            Execute(connection, $"CREATE TABLE [{table}] ([id] bigint NOT NULL PRIMARY KEY, [name] nvarchar(100) NULL, [amount] int NULL);");
            Execute(connection, $"INSERT INTO [{table}] ([id],[name],[amount]) VALUES (1,N'Alice',2),(2,NULL,1),(3,N'Bob',NULL),(4,N'Alice',NULL);");
            var command = new SqlServerQueryRenderer().Render(Request(new TableId(table)), Options(new TableId(table)));
            Assert.Equal(ExpectedIds(), ExecuteRelational(connection, command));
        }
        finally
        {
            Execute(connection, $"DROP TABLE IF EXISTS [{table}];");
        }
    }

    [SkippableFact]
    public void Mongo_results_match_the_provider_neutral_oracle_when_configured()
    {
        var connectionString = Required("GROUNDWORK_MONGO_CONNECTION");
        var url = new MongoUrl(connectionString);
        using var client = new MongoClient(url);
        var database = client.GetDatabase(url.DatabaseName);
        var collectionName = NewTable();
        var collection = database.GetCollection<BsonDocument>(collectionName);
        try
        {
            collection.InsertMany([
                new BsonDocument { ["_id"] = 1, ["name"] = "Alice", ["amount"] = 2 },
                new BsonDocument { ["_id"] = 2, ["name"] = BsonNull.Value, ["amount"] = 1 },
                new BsonDocument { ["_id"] = 3, ["name"] = "Bob", ["amount"] = BsonNull.Value },
                new BsonDocument { ["_id"] = 4, ["name"] = "Alice", ["amount"] = BsonNull.Value }
            ]);
            var command = new MongoQueryRenderer().Render(Request(new TableId(collectionName)), Options(new TableId(collectionName)));
            if (command.Pipeline.Length != 0)
            {
                var aggregate = collection.Aggregate<BsonDocument>(PipelineDefinition<BsonDocument, BsonDocument>.Create(command.Pipeline));
                Assert.Equal(ExpectedIds(), aggregate.ToList().Select(document => document["_id"].ToInt64()).ToArray());
            }
            else
            {
                var find = collection.Find(command.Filter).Sort(command.Sort);
                if (command.Skip is int skip)
                    find = find.Skip(skip);
                if (command.Limit is int limit)
                    find = find.Limit(limit);
                Assert.Equal(ExpectedIds(), find.ToList().Select(document => document["_id"].ToInt64()).ToArray());
            }
        }
        finally
        {
            collection.Database.DropCollection(collectionName);
        }
    }

    private static QueryRequest Request(TableId table, Paging? paging = null) => new(
        table,
        new Predicate.In(
            new ColumnRef(table, "name", QueryType.String, maxLength: 100),
            [QueryConstant.Of("Alice"), QueryConstant.Of((string?)null)]),
        [new OrderTerm(new ColumnRef(table, "amount", QueryType.Int32), OrderDirection.Ascending, NullOrder.First)],
        Projection.All,
        paging ?? Paging.OffsetLimit(0, 10));

    private static QueryRenderOptions Options(TableId table) => new(tieBreakColumns: [new ColumnRef(table, "id", QueryType.Int64, isNullable: false)]);

    private static long[] ExpectedIds() => [4, 2, 1];

    private static long[] ExecuteRelational(DbConnection connection, Groundwork.Substrate.Relational.RelationalQueryCommand query)
    {
        using var command = connection.CreateCommand();
        command.CommandText = query.CommandText;
        foreach (var value in query.Parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@" + value.Name;
            parameter.Value = value.Value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
        using var reader = command.ExecuteReader();
        var result = new List<long>();
        while (reader.Read())
            result.Add(Convert.ToInt64(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture));
        return result.ToArray();
    }

    private static void Execute(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Required(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value))
            Skip.If(true, $"Set {variable} to run the live Q4 differential fact.");
        return value;
    }

    private static string NewTable() => "q4_" + Guid.NewGuid().ToString("N");
}
