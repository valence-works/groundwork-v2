using System.Linq.Expressions;
using Groundwork.Kernel;
using Groundwork.MongoDb.TestingAdapter;
using Groundwork.PostgreSql;
using Groundwork.Query.Linq;
using Groundwork.Query.Model;
using Groundwork.SqlServer;
using Groundwork.Sqlite;
using Groundwork.Testing;

if (args.Length == 0 || (args[0] is not "roundtrips" and not "linq"))
{
    Console.Error.WriteLine("Usage: roundtrips --workload upsert|commit --n <count> [--provider sqlite|postgresql|sqlserver|mongodb] | linq --n <count>");
    return 2;
}

if (string.Equals(args[0], "linq", StringComparison.OrdinalIgnoreCase))
    return RunLinqBenchmark(args);

var workload = Option(args, "--workload") ?? "upsert";
var count = int.TryParse(Option(args, "--n"), out var parsed) && parsed > 0 ? parsed : 1;
var providerName = Option(args, "--provider") ?? "sqlite";
if (!string.Equals(workload, "upsert", StringComparison.OrdinalIgnoreCase) &&
    !string.Equals(workload, "commit", StringComparison.OrdinalIgnoreCase))
    throw new ArgumentException($"Unsupported workload '{workload}'.");

var (provider, connectionString, temporaryDirectory) = CreateProvider(providerName);
try
{
    using (provider)
    {
        var unit = Unit(providerName);
        provider.Schema.Apply(unit);
        if (string.Equals(workload, "commit", StringComparison.OrdinalIgnoreCase))
        {
            RunCommitWorkload(provider, unit, count, providerName);
            return 0;
        }
        var rawSession = provider.OpenSession(unit, StorageAccess.Global);
        if (rawSession is not IConcurrencyStorageSession session)
            throw new InvalidOperationException($"Provider '{providerName}' does not expose conditional upsert.");

        var totalRoundTrips = 0;
        var totalProbes = 0;
        long? version = null;
        for (var index = 0; index < count; index++)
        {
            var observer = new WritePathObserver();
            var outcome = session.ConditionalUpsert(
                new StorageValues(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["id"] = "benchmark",
                    ["value"] = "value-" + index,
                    ["createdAt"] = DateTimeOffset.UnixEpoch
                }),
                new WriteOptions { ExpectedVersion = version, Observer = observer });
            if (!outcome.Succeeded)
                throw new InvalidOperationException($"Write {index} returned {outcome.Status}.");
            version = outcome.Version;
            totalRoundTrips += observer.RoundTrips;
            totalProbes += observer.Commands.Count(command => command.IsProbe);
        }

        Console.WriteLine($"provider={providerName} workload={workload} writes={count} round_trips={totalRoundTrips} probes={totalProbes} final_version={version?.ToString() ?? "none"}");
    }
}
finally
{
    if (temporaryDirectory is not null)
    {
        try { Directory.Delete(temporaryDirectory, recursive: true); }
        catch { }
    }
}

return 0;

static string? Option(string[] args, string name)
{
    var index = Array.FindIndex(args, arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static int RunLinqBenchmark(string[] args)
{
    var count = int.TryParse(Option(args, "--n"), out var parsed) && parsed > 0 ? parsed : 1000;
    var model = new GwTableModel<BenchmarkTicket>("benchmark_tickets", new[]
    {
        new GwColumn<BenchmarkTicket>(nameof(BenchmarkTicket.Id), nameof(BenchmarkTicket.Id), QueryType.Int32, false)
    });
    var before = ExpressionLowerer.ClosedAccessorCompilationCount;
    for (var value = 0; value < count; value++)
    {
        var predicate = ExpressionLowerer.Lower(ClosedPredicate(value), model);
        var equal = predicate as Predicate.Equal ?? throw new InvalidOperationException("LINQ benchmark did not lower to equality.");
        if (!Equals(equal.Value.Value, value))
            throw new InvalidOperationException($"LINQ closure value was stale: expected {value}, got {equal.Value.Value}.");
    }

    var compilationDelta = ExpressionLowerer.ClosedAccessorCompilationCount - before;
    if (compilationDelta != 1)
        throw new InvalidOperationException($"LINQ closure accessor compiled {compilationDelta} times; expected exactly once.");
    Console.WriteLine($"provider=none workload=linq closures={count} accessor_compilations={compilationDelta} values=fresh");
    return 0;
}

static Expression<Func<BenchmarkTicket, bool>> ClosedPredicate(int value) => ticket => ticket.Id == value;

static (IStorageProviderConnection Provider, string ConnectionString, string? TemporaryDirectory) CreateProvider(string name) =>
    name.ToLowerInvariant() switch
    {
        "sqlite" => CreateSqlite(),
        "postgresql" or "postgres" => (new PostgreSqlProviderFactory().Create(Environment.GetEnvironmentVariable("GROUNDWORK_POSTGRES_CONNECTION") ?? throw new InvalidOperationException("GROUNDWORK_POSTGRES_CONNECTION is required.")), "", null),
        "sqlserver" => (new SqlServerProviderFactory().Create(Environment.GetEnvironmentVariable("GROUNDWORK_SQLSERVER_CONNECTION") ?? throw new InvalidOperationException("GROUNDWORK_SQLSERVER_CONNECTION is required.")), "", null),
        "mongodb" or "mongo" => (new MongoDbTestingFactory().Create(Environment.GetEnvironmentVariable("GROUNDWORK_MONGO_CONNECTION") ?? throw new InvalidOperationException("GROUNDWORK_MONGO_CONNECTION is required.")), "", null),
        _ => throw new ArgumentException($"Unsupported provider '{name}'.")
    };

static (IStorageProviderConnection Provider, string ConnectionString, string? TemporaryDirectory) CreateSqlite()
{
    var directory = Path.Combine(Path.GetTempPath(), "groundwork-w1-benchmark-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var connectionString = $"Data Source={Path.Combine(directory, "store.db")}";
    return (new SqliteProviderFactory().Create(connectionString), connectionString, directory);
}

static StorageUnit Unit(string provider) => new()
{
    // Keep each process independent: the table name is randomized and the schema
    // subject must be randomized with it so a second run cannot collide with the
    // first run's persisted declaration metadata.
    Id = new StorageUnitId("w1-benchmark-" + provider + "-" + Guid.NewGuid().ToString("N")),
    Name = "w1_benchmark_" + provider + "_" + Guid.NewGuid().ToString("N"),
    Columns =
    [
        new() { Name = "id", Type = PortableType.String, IsNullable = false, MaxLength = 200 },
        new() { Name = "value", Type = PortableType.String, IsNullable = false, MaxLength = 200 },
        new() { Name = "createdAt", Type = PortableType.DateTimeOffset, IsNullable = false }
    ],
    Key = new KeyDefinition { Columns = ["id"] },
    Concurrency = ConcurrencyDeclaration.Optimistic
};

static void RunCommitWorkload(
    IStorageProviderConnection provider,
    StorageUnit unit,
    int count,
    string providerName)
{
    var observer = new WritePathObserver();
    using var work = provider.BeginUnitOfWork(
        StorageAccess.Global,
        new BatchWriteOptions { MaxRowsPerFlush = 1_000, OutcomeMode = BatchOutcomeMode.Aggregate },
        unit);
    for (var index = 0; index < count; index++)
    {
        work.Stage(RowWrite.Upsert(unit, new StorageValues(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = "row-" + index,
            ["value"] = "value-" + index,
            ["createdAt"] = DateTimeOffset.UnixEpoch
        }), new WriteOptions { Observer = observer }));
    }

    // Measure the aggregate-cost path. CommitWithOutcomes is intentionally more
    // expensive on providers such as MongoDB because it requests exact row evidence.
    var summary = work.Commit();
    // The observer counts provider write commands. An explicit unit of work also
    // has one transaction-open and one commit exchange, so include those in the
    // proof's round-trip estimate.
    var roundTrips = observer.RoundTrips + 2;
    Console.WriteLine($"provider={providerName} workload=commit writes={count} round_trips={roundTrips} batch_round_trips={observer.RoundTrips} probes={observer.Commands.Count(command => command.IsProbe)} succeeded={summary.Succeeded} failed={summary.Failed}");
}

sealed class BenchmarkTicket
{
    public int Id { get; set; }
}
