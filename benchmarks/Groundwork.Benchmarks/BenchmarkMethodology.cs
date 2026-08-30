using Groundwork.Kernel;
using Groundwork.Kernel.Schema;

namespace Groundwork.Benchmarks;

public sealed record BenchmarkCase(string Workload, string Stack, string SchemaFingerprint);

public static class BenchmarkMethodology
{
    public const int SeedRowCount = 1_000;
    public const int PageSize = 25;
    public const int BatchSize = 32;

    public static string SchemaFingerprint { get; } = new SchemaSubject(CreateStorageUnit()).Fingerprint;

    public static IReadOnlyList<BenchmarkCase> Cases { get; } =
        new[] { "PointRead", "CoveredQuery", "PagedQuery", "BatchedWrite", "UnitOfWorkCommit" }
            .SelectMany(workload => new[] { "Groundwork", "EFCoreCompiledModel", "Dapper" }
                .Select(stack => new BenchmarkCase(workload, stack, SchemaFingerprint)))
            .ToArray();

    public static StorageUnit CreateStorageUnit() => new()
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
                Columns =
                [
                    new IndexColumn("category"),
                    new IndexColumn("id"),
                    new IndexColumn("sequence"),
                    new IndexColumn("payload")
                ]
            }
        ]
    };
}
