using Groundwork.Kernel;
using Groundwork.LiveDatabases;
using Groundwork.MongoDb;
using Groundwork.PostgreSql;
using Groundwork.Query.Linq;
using Groundwork.Query.Linq.Execution;
using Groundwork.Query.Model;
using Groundwork.Sqlite;
using Groundwork.SqlServer;
using Groundwork.Store;
using Xunit;

namespace Groundwork.Differential.Tests;

/// <summary>
/// One declaration, one row set, and one closed LINQ query surface opened on all four providers,
/// each behind its own executor and its own provider catalog. Every provider in the matrix receives
/// byte-identical declarations and rows, so any difference an assertion finds is a real provider
/// difference and not a difference in how the test set the provider up.
/// </summary>
internal sealed class LinqExecutionMatrix : IDisposable
{
    private readonly List<IDisposable> connections = [];

    private LinqExecutionMatrix(string tableName)
    {
        TableName = tableName;
        Unit = Declare(tableName);
        Model = DeclareModel(tableName);
    }

    internal string TableName { get; }

    internal StorageUnit Unit { get; }

    internal GwTableModel<Ticket> Model { get; }

    internal List<LinqProvider> Providers { get; } = [];

    /// <summary>The row set every provider is loaded with, in declaration order.</summary>
    internal static IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows { get; } =
    [
        new Dictionary<string, object?> { ["id"] = 1L, ["status"] = "open", ["region"] = "eu", ["sort_key"] = 30 },
        new Dictionary<string, object?> { ["id"] = 2L, ["status"] = "open", ["region"] = null, ["sort_key"] = 10 },
        new Dictionary<string, object?> { ["id"] = 3L, ["status"] = "closed", ["region"] = "eu", ["sort_key"] = 20 },
        new Dictionary<string, object?> { ["id"] = 4L, ["status"] = "open", ["region"] = "us", ["sort_key"] = 40 },
        new Dictionary<string, object?> { ["id"] = 5L, ["status"] = "open", ["region"] = "eu", ["sort_key"] = 20 },
        new Dictionary<string, object?> { ["id"] = 6L, ["status"] = "closed", ["region"] = null, ["sort_key"] = 50 }
    ];

    /// <summary>
    /// Opens the matrix, skipping the whole test unless every live provider is configured. All four
    /// run or none does: a two-way pass would quietly stop being evidence for the other two.
    /// </summary>
    internal static LinqExecutionMatrix OpenAll()
    {
        var postgres = Required("GROUNDWORK_POSTGRES_CONNECTION");
        var sqlServer = LiveSqlServer.Required();
        var mongo = LiveMongo.Required();
        var matrix = new LinqExecutionMatrix("g2_linq_exec_" + Guid.NewGuid().ToString("N"));
        try
        {
            matrix.Add("SQLite", new SqliteProviderFactory().Create(
                "Data Source=file:g2linq_" + Guid.NewGuid().ToString("N") + "?mode=memory&cache=shared"));
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
        connection.Schema.Apply(Unit);
        var session = connection.OpenSession(Unit, StorageAccess.Global);
        foreach (var row in Rows)
        {
            session.Delete(new StorageKey(new Dictionary<string, object?> { ["id"] = row["id"] }));
            session.Insert(new StorageValues(row));
        }
        var executor = new GwLinqExecutor(session, connection);
        Providers.Add(new LinqProvider(
            name,
            executor,
            new GwQueryDatabase(executor).Table(Model),
            session,
            connection));
    }

    private static StorageUnit Declare(string tableName) => new()
    {
        Id = new StorageUnitId(tableName),
        Name = tableName,
        Columns =
        [
            new() { Name = "id", Type = PortableType.Int64, IsNullable = false },
            new() { Name = "status", Type = PortableType.String, IsNullable = false, MaxLength = 32 },
            new() { Name = "region", Type = PortableType.String, IsNullable = true, MaxLength = 32 },
            new() { Name = "sort_key", Type = PortableType.Int32, IsNullable = false }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        Indexes =
        [
            new() { Name = "ix_status_sort", Columns = [new IndexColumn("status"), new IndexColumn("sort_key")] },
            new() { Name = "ix_region_sort", Columns = [new IndexColumn("region"), new IndexColumn("sort_key")] },
            new()
            {
                Name = "ix_region_desc_sort",
                Columns = [new IndexColumn("region", SortDirection.Descending), new IndexColumn("sort_key")]
            }
        ]
    };

    private static GwTableModel<Ticket> DeclareModel(string tableName) => new(tableName,
    [
        new GwColumn<Ticket>(nameof(Ticket.Id), "id", QueryType.Int64, IsNullable: false),
        new GwColumn<Ticket>(nameof(Ticket.Status), "status", QueryType.String, IsNullable: false, MaxLength: 32),
        new GwColumn<Ticket>(nameof(Ticket.Region), "region", QueryType.String, IsNullable: true, MaxLength: 32),
        new GwColumn<Ticket>(nameof(Ticket.SortKey), "sort_key", QueryType.Int32, IsNullable: false)
    ]);

    private static string Required(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            Skip.If(true, $"Set {name} to run the four-way LINQ execution matrix.");
        return value!;
    }
}

/// <summary>One provider's LINQ surface: the executor under test and the session that backs it.</summary>
internal sealed record LinqProvider(
    string Name,
    GwLinqExecutor Executor,
    GwQueryTable<Ticket> Table,
    IStorageSession Session,
    IStorageProviderConnection Connection);

/// <summary>The mapped row the matrix materializes on every provider.</summary>
internal sealed class Ticket
{
    public long Id { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Region { get; set; }
    public int SortKey { get; set; }

    public override string ToString() => $"{Id}/{Status}/{Region ?? "<null>"}/{SortKey}";
}
