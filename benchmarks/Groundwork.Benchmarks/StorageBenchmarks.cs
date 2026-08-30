using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Order;
using Dapper;
using Groundwork.Kernel;
using Groundwork.Query.Linq;
using Groundwork.Query.Linq.Sqlite;
using Groundwork.Query.Model;
using Groundwork.Sqlite;
using Groundwork.Store;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Groundwork.Benchmarks;

[MemoryDiagnoser]
[OperationsPerSecond]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class StorageBenchmarks
{
    private const string PointId = "item-0500";
    private const string CoveredCategory = "category-3";
    private const string CommitId = "commit";

    private readonly StorageUnit unit = CreateUnit();
    private readonly GwTableModel<BenchmarkItem> tableModel = new("benchmark_items",
    [
        new(nameof(BenchmarkItem.Id), "id", QueryType.String, false),
        new(nameof(BenchmarkItem.Category), "category", QueryType.String, false),
        new(nameof(BenchmarkItem.Sequence), "sequence", QueryType.Int32, false),
        new(nameof(BenchmarkItem.Payload), "payload", QueryType.String, false)
    ]);

    private string? temporaryDirectory;
    private IStorageProviderConnection? provider;
    private IStorageSession? session;
    private IBatchedStorageSession? batchedSession;
    private SqliteConnection? dapperConnection;
    private SqliteConnection? efConnection;
    private BenchmarkDbContext? efQueryContext;
    private BenchmarkDbContext? efBatchContext;
    private BenchmarkDbContext? efCommitContext;
    private BenchmarkItem[] efBatchItems = [];
    private BenchmarkItem? efCommitItem;
    private GwQueryTable<BenchmarkItem>? groundworkTable;
    private IGwQueryable<BenchmarkItem>? groundworkCoveredQuery;
    private IGwQueryable<BenchmarkItem>? groundworkPagedQuery;
    private string dapperBatchSql = string.Empty;
    private int payloadGeneration;

    [GlobalSetup]
    public void Setup()
    {
        temporaryDirectory = Path.Combine(Path.GetTempPath(), "groundwork-bdn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var connectionString = $"Data Source={Path.Combine(temporaryDirectory, "benchmark.db")};Pooling=False";

        provider = new SqliteProviderFactory().Create(connectionString);
        var apply = provider.Schema.Apply(unit);
        if (!apply.Applied)
            throw new InvalidOperationException("The benchmark schema was not applied.");
        session = provider.OpenSession(unit, StorageAccess.Global);
        batchedSession = session as IBatchedStorageSession
            ?? throw new InvalidOperationException("SQLite did not expose its public batched-write contract.");

        dapperConnection = new SqliteConnection(connectionString);
        dapperConnection.Open();
        ConfigureSqliteConnection(dapperConnection);
        Seed(dapperConnection);

        efConnection = new SqliteConnection(connectionString);
        efConnection.Open();
        ConfigureSqliteConnection(efConnection);
        efQueryContext = BenchmarkDbContext.Create(efConnection);
        efBatchContext = BenchmarkDbContext.Create(efConnection);
        efCommitContext = BenchmarkDbContext.Create(efConnection);
        efBatchItems = efBatchContext.Items.Where(item => item.Id.StartsWith("batch-")).OrderBy(item => item.Id).ToArray();
        efCommitItem = efCommitContext.Items.Single(item => item.Id == CommitId);

        var executor = new SqliteLinqExecutor(session, provider);
        groundworkTable = new GwQueryDatabase(executor).Table(tableModel);
        groundworkCoveredQuery = groundworkTable
            .Where(item => item.Category == CoveredCategory)
            .OrderBy(item => item.Id)
            .Take(BenchmarkMethodology.PageSize);
        groundworkPagedQuery = groundworkTable
            .OrderBy(item => item.Id)
            .Skip(BenchmarkMethodology.SeedRowCount / 2)
            .Take(BenchmarkMethodology.PageSize);
        dapperBatchSql = string.Join(';', Enumerable.Range(0, BenchmarkMethodology.BatchSize)
            .Select(index => $"UPDATE benchmark_items SET payload = @payload{index} WHERE id = @id{index}"));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        efCommitContext?.Dispose();
        efBatchContext?.Dispose();
        efQueryContext?.Dispose();
        efConnection?.Dispose();
        dapperConnection?.Dispose();
        provider?.Dispose();
        if (temporaryDirectory is not null && Directory.Exists(temporaryDirectory))
            Directory.Delete(temporaryDirectory, recursive: true);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("PointRead")]
    public async Task<BenchmarkItem> PointRead_Groundwork()
    {
        var stored = await Session.ReadAsync(new StorageKey(new Dictionary<string, object?> { ["id"] = PointId }))
            ?? throw new InvalidOperationException($"Seed row '{PointId}' was not found.");
        return Materialize(stored);
    }

    [Benchmark]
    [BenchmarkCategory("PointRead")]
    public Task<BenchmarkItem> PointRead_EFCoreCompiledModel() =>
        EfQueryContext.Items.AsNoTracking().SingleAsync(item => item.Id == PointId);

    [Benchmark]
    [BenchmarkCategory("PointRead")]
    public Task<BenchmarkItem> PointRead_Dapper() =>
        DapperConnection.QuerySingleAsync<BenchmarkItem>(
            "SELECT id, category, sequence, payload FROM benchmark_items WHERE id = @id",
            new { id = PointId });

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("CoveredQuery")]
    public Task<IReadOnlyList<BenchmarkItem>> CoveredQuery_Groundwork() =>
        GroundworkCoveredQuery.ToListAsync();

    [Benchmark]
    [BenchmarkCategory("CoveredQuery")]
    public Task<List<BenchmarkItem>> CoveredQuery_EFCoreCompiledModel() =>
        EfQueryContext.Items.AsNoTracking()
            .Where(item => item.Category == CoveredCategory)
            .OrderBy(item => item.Id)
            .Take(BenchmarkMethodology.PageSize)
            .ToListAsync();

    [Benchmark]
    [BenchmarkCategory("CoveredQuery")]
    public async Task<IReadOnlyList<BenchmarkItem>> CoveredQuery_Dapper() =>
        (await DapperConnection.QueryAsync<BenchmarkItem>(
            "SELECT id, category, sequence, payload FROM benchmark_items WHERE category = @category ORDER BY id LIMIT @take",
            new { category = CoveredCategory, take = BenchmarkMethodology.PageSize })).AsList();

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("PagedQuery")]
    public Task<IReadOnlyList<BenchmarkItem>> PagedQuery_Groundwork() =>
        GroundworkPagedQuery.ToListAsync();

    [Benchmark]
    [BenchmarkCategory("PagedQuery")]
    public Task<List<BenchmarkItem>> PagedQuery_EFCoreCompiledModel() =>
        EfQueryContext.Items.AsNoTracking()
            .OrderBy(item => item.Id)
            .Skip(BenchmarkMethodology.SeedRowCount / 2)
            .Take(BenchmarkMethodology.PageSize)
            .ToListAsync();

    [Benchmark]
    [BenchmarkCategory("PagedQuery")]
    public async Task<IReadOnlyList<BenchmarkItem>> PagedQuery_Dapper() =>
        (await DapperConnection.QueryAsync<BenchmarkItem>(
            "SELECT id, category, sequence, payload FROM benchmark_items ORDER BY id LIMIT @take OFFSET @skip",
            new { take = BenchmarkMethodology.PageSize, skip = BenchmarkMethodology.SeedRowCount / 2 })).AsList();

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("BatchedWrite")]
    public ValueTask<IReadOnlyList<RowWriteOutcome>> BatchedWrite_Groundwork()
    {
        var payload = NextPayload();
        var writes = Enumerable.Range(0, BenchmarkMethodology.BatchSize)
            .Select(index => RowWrite.Update(unit, Values($"batch-{index:D2}", "write", index, payload)))
            .ToArray();
        return BatchedSession.ApplyBatchAsync(writes, exactOutcomes: false);
    }

    [Benchmark]
    [BenchmarkCategory("BatchedWrite")]
    public Task<int> BatchedWrite_EFCoreCompiledModel()
    {
        var payload = NextPayload();
        foreach (var item in efBatchItems)
            item.Payload = payload;
        return EfBatchContext.SaveChangesAsync();
    }

    [Benchmark]
    [BenchmarkCategory("BatchedWrite")]
    public async Task<int> BatchedWrite_Dapper()
    {
        var payload = NextPayload();
        var parameters = new DynamicParameters();
        for (var index = 0; index < BenchmarkMethodology.BatchSize; index++)
        {
            parameters.Add($"id{index}", $"batch-{index:D2}");
            parameters.Add($"payload{index}", payload);
        }
        await using var transaction = await DapperConnection.BeginTransactionAsync();
        var affected = await DapperConnection.ExecuteAsync(dapperBatchSql, parameters, transaction);
        await transaction.CommitAsync();
        return affected;
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("UnitOfWorkCommit")]
    public async Task<int> UnitOfWorkCommit_Groundwork()
    {
        using var work = Provider.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Default, unit);
        work.Stage(RowWrite.Update(unit, Values(CommitId, "commit", -1, NextPayload())));
        return (await work.CommitAsync()).Succeeded;
    }

    [Benchmark]
    [BenchmarkCategory("UnitOfWorkCommit")]
    public async Task<int> UnitOfWorkCommit_EFCoreCompiledModel()
    {
        await using var transaction = await EfCommitContext.Database.BeginTransactionAsync();
        EfCommitItem.Payload = NextPayload();
        var affected = await EfCommitContext.SaveChangesAsync();
        await transaction.CommitAsync();
        return affected;
    }

    [Benchmark]
    [BenchmarkCategory("UnitOfWorkCommit")]
    public async Task<int> UnitOfWorkCommit_Dapper()
    {
        await using var transaction = await DapperConnection.BeginTransactionAsync();
        var affected = await DapperConnection.ExecuteAsync(
            "UPDATE benchmark_items SET payload = @payload WHERE id = @id",
            new { payload = NextPayload(), id = CommitId },
            transaction);
        await transaction.CommitAsync();
        return affected;
    }

    private IStorageProviderConnection Provider => provider ?? throw new InvalidOperationException("Setup has not run.");
    private IStorageSession Session => session ?? throw new InvalidOperationException("Setup has not run.");
    private IBatchedStorageSession BatchedSession => batchedSession ?? throw new InvalidOperationException("Setup has not run.");
    private SqliteConnection DapperConnection => dapperConnection ?? throw new InvalidOperationException("Setup has not run.");
    private BenchmarkDbContext EfQueryContext => efQueryContext ?? throw new InvalidOperationException("Setup has not run.");
    private BenchmarkDbContext EfBatchContext => efBatchContext ?? throw new InvalidOperationException("Setup has not run.");
    private BenchmarkDbContext EfCommitContext => efCommitContext ?? throw new InvalidOperationException("Setup has not run.");
    private BenchmarkItem EfCommitItem => efCommitItem ?? throw new InvalidOperationException("Setup has not run.");
    private IGwQueryable<BenchmarkItem> GroundworkCoveredQuery => groundworkCoveredQuery ?? throw new InvalidOperationException("Setup has not run.");
    private IGwQueryable<BenchmarkItem> GroundworkPagedQuery => groundworkPagedQuery ?? throw new InvalidOperationException("Setup has not run.");

    private string NextPayload() => "payload-" + Interlocked.Increment(ref payloadGeneration);

    private static void Seed(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        const string insert = "INSERT INTO benchmark_items (id, category, sequence, payload) VALUES (@Id, @Category, @Sequence, @Payload)";
        connection.Execute(insert, Enumerable.Range(0, BenchmarkMethodology.SeedRowCount)
            .Select(index => new BenchmarkItem
            {
                Id = $"item-{index:D4}",
                Category = $"category-{index % 10}",
                Sequence = index,
                Payload = $"payload-{index:D4}"
            }), transaction);
        connection.Execute(insert, Enumerable.Range(0, BenchmarkMethodology.BatchSize)
            .Select(index => new BenchmarkItem
            {
                Id = $"batch-{index:D2}",
                Category = "write",
                Sequence = index,
                Payload = "batch-seed"
            }), transaction);
        connection.Execute(insert, new BenchmarkItem
        {
            Id = CommitId,
            Category = "commit",
            Sequence = -1,
            Payload = "commit-seed"
        }, transaction);
        transaction.Commit();
    }

    private static void ConfigureSqliteConnection(SqliteConnection connection)
    {
        connection.CreateCollation("GROUNDWORK_UTF16_ORDINAL", string.CompareOrdinal);
        connection.Execute("PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;");
    }

    private static BenchmarkItem Materialize(StoredEntry stored) => new()
    {
        Id = Convert.ToString(stored.Values.Values["id"], System.Globalization.CultureInfo.InvariantCulture)!,
        Category = Convert.ToString(stored.Values.Values["category"], System.Globalization.CultureInfo.InvariantCulture)!,
        Sequence = Convert.ToInt32(stored.Values.Values["sequence"], System.Globalization.CultureInfo.InvariantCulture),
        Payload = Convert.ToString(stored.Values.Values["payload"], System.Globalization.CultureInfo.InvariantCulture)!
    };

    private static StorageValues Values(string id, string category, int sequence, string payload) => new(
        new Dictionary<string, object?>
        {
            ["id"] = id,
            ["category"] = category,
            ["sequence"] = sequence,
            ["payload"] = payload
        });

    private static StorageUnit CreateUnit() => new()
    {
        Id = new StorageUnitId("benchmark-items"),
        Name = "benchmark_items",
        Columns =
        [
            new() { Name = "id", Type = PortableType.String, IsNullable = false },
            new() { Name = "category", Type = PortableType.String, IsNullable = false },
            new() { Name = "sequence", Type = PortableType.Int32, IsNullable = false },
            new() { Name = "payload", Type = PortableType.String, IsNullable = false }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        Indexes =
        [
            new IndexDefinition
            {
                Name = "ix_benchmark_items_category_id",
                Columns = [new IndexColumn("category"), new IndexColumn("id")]
            }
        ]
    };
}
