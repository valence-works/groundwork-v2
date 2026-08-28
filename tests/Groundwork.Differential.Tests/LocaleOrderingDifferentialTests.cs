using Groundwork.Kernel;
using Groundwork.LiveDatabases;
using Groundwork.MongoDb;
using Groundwork.PostgreSql;
using Groundwork.Query.Model;
using Groundwork.SqlServer;
using Groundwork.Sqlite;
using Groundwork.Store;
using Xunit;

namespace Groundwork.Differential.Tests;

[Collection(NativeProviderDifferentialCollection.Name)]
public sealed class LocaleOrderingDifferentialTests
{
    private static readonly string TableName = "locale_order_" + Guid.NewGuid().ToString("N");
    private static readonly string[] Input = ["Ake", "Åke", "Äke", "Öke", "Zebra"];

    [SkippableFact]
    public void Swedish_and_German_phonebook_order_and_page_identically_on_all_four_providers()
    {
        var postgres = Required("GROUNDWORK_POSTGRES_CONNECTION");
        var sqlServer = LiveSqlServer.Required();
        var mongo = LiveMongo.Required();
        using var sqlite = OpenRelational(
            "SQLite",
            new SqliteProviderFactory().Create("Data Source=file:locale_" + Guid.NewGuid().ToString("N") + "?mode=memory&cache=shared"));
        using var pg = OpenRelational("PostgreSQL", new PostgreSqlProviderFactory().Create(postgres));
        using var sql = OpenRelational("SQL Server", new SqlServerProviderFactory().Create(sqlServer));
        using var mongoSession = OpenMongo(mongo);

        AssertLocale(["Ake", "Zebra", "Åke", "Äke", "Öke"], "swedish", sqlite, pg, sql, mongoSession);
        AssertLocale(["Äke", "Ake", "Åke", "Öke", "Zebra"], "german", sqlite, pg, sql, mongoSession);
    }

    private static void AssertLocale(
        string[] expected,
        string columnName,
        params LocaleSession[] providers)
    {
        var table = new TableId(TableName);
        var column = new ColumnRef(table, columnName, QueryType.String, false, 32);

        foreach (var provider in providers)
        {
            var actual = new List<string>();
            var paging = Paging.Keyset(2);
            do
            {
                var request = new QueryRequest(
                    table,
                    Predicate.AlwaysTrue.Instance,
                    [new OrderTerm(column, OrderDirection.Ascending, NullOrder.First)],
                    Projection.ColumnsOnly(column),
                    paging);
                var page = provider.Query(request, QueryRenderOptions.Default);
                actual.AddRange(page.Rows.Select(row => (string)row[columnName]!));
                Assert.All(page.Rows, row => Assert.DoesNotContain(row.Keys, key => key.StartsWith("__groundwork_", StringComparison.Ordinal)));
                paging = page.NextContinuationToken is null
                    ? Paging.None
                    : Paging.Continuation(page.NextContinuationToken, 2);
            }
            while (paging != Paging.None);

            Assert.True(expected.SequenceEqual(actual),
                $"{provider.Name}/{columnName}: expected [{string.Join(",", expected)}], actual [{string.Join(",", actual)}]. " +
                $"Ordinal input order is [{string.Join(",", Input.OrderBy(value => value, StringComparer.Ordinal))}].");
        }
    }

    private static LocaleSession OpenRelational(string name, IStorageProviderConnection connection)
    {
        connection.Schema.Apply(Unit);
        var session = connection.OpenSession(Unit, StorageAccess.Global);
        Seed(row => session.Insert(new StorageValues(row)));
        return new LocaleSession(name, session.Query, connection.Dispose);
    }

    private static LocaleSession OpenMongo(string connectionString)
    {
        var connection = new MongoDbProviderFactory().Create(connectionString);
        connection.Schema.Apply(Unit);
        var session = connection.OpenSession(Unit, MongoStorageAccess.Global);
        Seed(row => session.Insert(new MongoStorageValues(row)));
        return new LocaleSession("MongoDB", session.Query, connection.Dispose);
    }

    private static void Seed(Action<IReadOnlyDictionary<string, object?>> insert)
    {
        for (var index = 0; index < Input.Length; index++)
        {
            insert(new Dictionary<string, object?>
            {
                ["id"] = (long)(index + 1),
                ["swedish"] = Input[index],
                ["german"] = Input[index]
            });
        }
    }

    private static StorageUnit Unit => new()
    {
        Id = new StorageUnitId(TableName),
        Name = TableName,
        Columns =
        [
            new ColumnDefinition { Name = "id", Type = PortableType.Int64, IsNullable = false },
            LocaleColumn("swedish", "sv-SE"),
            LocaleColumn("german", "de-DE-u-co-phonebk")
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        Indexes =
        [
            new IndexDefinition { Name = "ix_swedish", Columns = [new IndexColumn("swedish")] },
            new IndexDefinition { Name = "ix_german", Columns = [new IndexColumn("german")] }
        ]
    };

    private static ColumnDefinition LocaleColumn(string name, string cultureName) => new()
    {
        Name = name,
        Type = PortableType.String,
        IsNullable = false,
        MaxLength = 32,
        LocaleSortKey = new LocaleSortKeyDefinition
        {
            CultureName = cultureName,
            MaximumExpansionFactor = 12
        }
    };

    private static string Required(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            Skip.If(true, $"Set {name} to run locale ordering conformance.");
        return value!;
    }

    private sealed class LocaleSession(
        string name,
        Func<QueryRequest, QueryRenderOptions?, QueryMaterializedResult> query,
        Action dispose) : IDisposable
    {
        public string Name { get; } = name;
        public QueryMaterializedResult Query(QueryRequest request, QueryRenderOptions options) => query(request, options);
        public void Dispose() => dispose();
    }
}
