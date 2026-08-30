using System.Security.Cryptography;
using System.Text;

namespace Groundwork.Benchmarks;

public sealed record BenchmarkCase(string Workload, string Stack, string SchemaFingerprint);

public static class BenchmarkMethodology
{
    public const int SeedRowCount = 1_000;
    public const int PageSize = 25;
    public const int BatchSize = 32;

    public const string CanonicalSchema =
        "benchmark_items(id:text:not-null:pk,category:text:not-null,sequence:integer:not-null,payload:text:not-null);" +
        "index:ix_benchmark_items_category_id(category,id)";

    public static string SchemaFingerprint { get; } = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalSchema))).ToLowerInvariant();

    public static IReadOnlyList<BenchmarkCase> Cases { get; } =
        new[] { "PointRead", "CoveredQuery", "PagedQuery", "BatchedWrite", "UnitOfWorkCommit" }
            .SelectMany(workload => new[] { "Groundwork", "EFCoreCompiledModel", "Dapper" }
                .Select(stack => new BenchmarkCase(workload, stack, SchemaFingerprint)))
            .ToArray();
}
