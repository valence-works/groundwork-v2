using Groundwork.Kernel;
using Groundwork.LiveDatabases;
using Groundwork.MongoDb;
using Groundwork.PostgreSql;
using Groundwork.Query.Model;
using Groundwork.Sqlite;
using Groundwork.SqlServer;
using Groundwork.Store;
using Xunit;

namespace Groundwork.Differential.Tests;

/// <summary>
/// Four-way differential and conformance evidence for the keyed batch-read primitive
/// (valence-works/groundwork-v2#87, #145, #146, #147). Every provider receives byte-identical rows
/// and the same batch-read request, well beyond the 1,000-value `In` cap and SQLite's 999-parameter
/// budget, so a passing run proves the cap became an internal chunking concern rather than a
/// per-provider claim.
/// <para>
/// Serialized with the other live-provider differentials for the reason
/// <see cref="NativeProviderDifferentialCollection"/> documents: xUnit runs collections in
/// parallel, and two suites against one live SQL Server instance can deadlock each other.
/// </para>
/// </summary>
[Collection(NativeProviderDifferentialCollection.Name)]
public sealed class KeyedBatchReadDifferentialTests
{
    private const int RowCount = KeyedBatchReadMatrix.RowCount;

    [SkippableFact]
    public async Task Batch_read_beyond_the_thousand_value_cap_matches_identically_on_every_provider()
    {
        using var matrix = KeyedBatchReadMatrix.OpenAll();

        // Every third key plus a run of keys past the end of the table, so the assertion covers
        // matched rows, gaps inside the key range, and missing keys beyond it — on one request.
        var requested = Enumerable.Range(1, RowCount)
            .Where(id => id % 3 == 0)
            .Concat(Enumerable.Range(RowCount + 1, 50))
            .Select(id => (object?)(long)id)
            .ToArray();

        var expectedMatched = requested
            .Select(id => (long)id!)
            .Where(id => id <= RowCount)
            .ToArray();
        var expectedMissing = requested
            .Select(id => (long)id!)
            .Where(id => id > RowCount)
            .ToArray();

        KeyedBatchReadResult? reference = null;
        foreach (var provider in matrix.Providers)
        {
            var request = new KeyedBatchReadRequest(new TableId(matrix.TableName), matrix.IdColumn, requested);
            var result = await provider.Session.BatchReadAsync(request, provider.Connection);

            Assert.Equal(expectedMatched, result.Rows.Select(row => (long)row.Values["id"]!));
            Assert.Equal(expectedMissing, result.MissingKeys.Select(key => (long)key.Value!));
            Assert.All(result.Rows, row => Assert.Equal(
                "owner-" + row.Values["id"], row.Values["owner"]));

            if (reference is null)
            {
                reference = result;
                continue;
            }

            Assert.Equal(
                reference.Rows.Select(row => (long)row.Key.Value!),
                result.Rows.Select(row => (long)row.Key.Value!));
            Assert.Equal(
                reference.MissingKeys.Select(key => (long)key.Value!),
                result.MissingKeys.Select(key => (long)key.Value!));
        }
    }

    [SkippableFact]
    public async Task Duplicate_and_out_of_order_keys_dedupe_and_reorder_identically_on_every_provider()
    {
        using var matrix = KeyedBatchReadMatrix.OpenAll();
        var requested = new object?[] { 40L, 10L, 40L, 25L, 10L, 999_999L };

        foreach (var provider in matrix.Providers)
        {
            var request = new KeyedBatchReadRequest(new TableId(matrix.TableName), matrix.IdColumn, requested);
            var result = await provider.Session.BatchReadAsync(request, provider.Connection);

            Assert.Equal(new long[] { 40, 10, 25 }, result.Rows.Select(row => (long)row.Values["id"]!));
            Assert.Equal(new long[] { 999_999 }, result.MissingKeys.Select(key => (long)key.Value!));
        }
    }
}

/// <summary>
/// One declared storage unit, one large row set, and one open session per provider — SQLite always,
/// and PostgreSQL/SQL Server/MongoDB when their live connections are configured. All four run or
/// none does, matching every other live-provider matrix in this assembly.
/// </summary>
internal sealed class KeyedBatchReadMatrix : IDisposable
{
    private readonly List<IDisposable> connections = [];

    private KeyedBatchReadMatrix(string tableName)
    {
        TableName = tableName;
        IdColumn = new ColumnRef("id", QueryType.Int64, isNullable: false);
    }

    internal string TableName { get; }

    internal ColumnRef IdColumn { get; }

    internal List<KeyedBatchReadProvider> Providers { get; } = [];

    internal static KeyedBatchReadMatrix OpenAll()
    {
        var postgres = Required("GROUNDWORK_POSTGRES_CONNECTION");
        var sqlServer = LiveSqlServer.Required();
        var mongo = LiveMongo.Required();
        var matrix = new KeyedBatchReadMatrix("g2_batch_read_" + Guid.NewGuid().ToString("N"));
        try
        {
            matrix.Add("SQLite", new SqliteProviderFactory().Create(
                "Data Source=file:g2batch_" + Guid.NewGuid().ToString("N") + "?mode=memory&cache=shared"));
            matrix.Add("PostgreSQL", new PostgreSqlProviderFactory().Create(postgres));
            matrix.Add("SQL Server", new SqlServerProviderFactory().Create(sqlServer));
            matrix.Add("MongoDB", new MongoProviderFactory().Create(mongo));
            return matrix;
        }
        catch
        {
            matrix.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var connection in connections)
            connection.Dispose();
    }

    private void Add(string name, IStorageProviderConnection connection)
    {
        connections.Add(connection);
        var unit = Declare();
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        for (var id = 1; id <= RowCount; id++)
        {
            session.Insert(new StorageValues(new Dictionary<string, object?>
            {
                ["id"] = (long)id,
                ["owner"] = "owner-" + id
            }));
        }

        Providers.Add(new KeyedBatchReadProvider(name, session, connection));
    }

    /// <summary>Row count shared with <see cref="KeyedBatchReadDifferentialTests"/>'s requested keys.</summary>
    internal const int RowCount = 2_500;

    private StorageUnit Declare() => new()
    {
        Id = new StorageUnitId(TableName),
        Name = TableName,
        Columns =
        [
            new() { Name = "id", Type = PortableType.Int64, IsNullable = false },
            new() { Name = "owner", Type = PortableType.String, IsNullable = false, MaxLength = 32 }
        ],
        Key = new KeyDefinition { Columns = ["id"] }
    };

    private static string Required(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            Skip.If(true, $"Set {name} to run the four-way keyed batch-read matrix.");
        return value!;
    }
}

/// <summary>One provider's open session for the keyed batch-read matrix.</summary>
internal sealed record KeyedBatchReadProvider(
    string Name,
    IStorageSession Session,
    IStorageProviderConnection Connection);
