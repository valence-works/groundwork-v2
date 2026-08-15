using Groundwork.Kernel;
using Groundwork.MongoDb.TestingAdapter;
using Groundwork.PostgreSql;
using Groundwork.SqlServer;
using Groundwork.Sqlite;
using Groundwork.Testing;

if (args.Length == 0 || !string.Equals(args[0], "roundtrips", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Usage: roundtrips --workload upsert --n <count> [--provider sqlite|postgresql|sqlserver|mongodb]");
    return 2;
}

var workload = Option(args, "--workload") ?? "upsert";
var count = int.TryParse(Option(args, "--n"), out var parsed) && parsed > 0 ? parsed : 1;
var providerName = Option(args, "--provider") ?? "sqlite";
if (!string.Equals(workload, "upsert", StringComparison.OrdinalIgnoreCase))
    throw new ArgumentException($"Unsupported workload '{workload}'.");

var (provider, connectionString, temporaryDirectory) = CreateProvider(providerName);
try
{
    using (provider)
    {
        var unit = Unit(providerName);
        provider.Schema.Apply(unit);
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
    Id = new StorageUnitId("w1-benchmark-" + provider),
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
