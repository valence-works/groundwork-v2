using System.Reflection;
using BenchmarkDotNet.Attributes;
using Groundwork.Benchmarks;
using Xunit;

namespace Groundwork.Benchmarks.Tests;

public sealed class BenchmarkMethodologyTests
{
    [Fact]
    public void Every_required_workload_compares_the_same_three_stacks()
    {
        var expectedWorkloads = new[]
        {
            "PointRead",
            "CoveredQuery",
            "PagedQuery",
            "BatchedWrite",
            "UnitOfWorkCommit"
        };
        var expectedStacks = new[] { "Groundwork", "EFCoreCompiledModel", "Dapper" };

        Assert.Equal(expectedWorkloads, BenchmarkMethodology.Cases.Select(item => item.Workload).Distinct());
        foreach (var workload in expectedWorkloads)
        {
            Assert.Equal(expectedStacks, BenchmarkMethodology.Cases
                .Where(item => item.Workload == workload)
                .Select(item => item.Stack));
        }
        Assert.All(BenchmarkMethodology.Cases, item => Assert.Equal(BenchmarkMethodology.SchemaFingerprint, item.SchemaFingerprint));
    }

    [Fact]
    public void Methodology_catalog_matches_the_discoverable_benchmark_methods()
    {
        var discovered = typeof(StorageBenchmarks).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.GetCustomAttribute<BenchmarkAttribute>() is not null)
            .Select(method =>
            {
                var category = Assert.Single(method.GetCustomAttribute<BenchmarkCategoryAttribute>()!.Categories);
                var separator = method.Name.LastIndexOf('_');
                return (Workload: category, Stack: method.Name[(separator + 1)..]);
            })
            .OrderBy(item => item.Workload, StringComparer.Ordinal)
            .ThenBy(item => item.Stack, StringComparer.Ordinal)
            .ToArray();
        var declared = BenchmarkMethodology.Cases
            .Select(item => (item.Workload, item.Stack))
            .OrderBy(item => item.Workload, StringComparer.Ordinal)
            .ThenBy(item => item.Stack, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(declared, discovered);
    }

    [Fact]
    public void Checked_in_ef_compiled_model_contains_the_canonical_entity_shape()
    {
        var entity = BenchmarkDbContextModel.Instance.FindEntityType(typeof(BenchmarkItem));
        Assert.NotNull(entity);

        Assert.Equal(new[] { "Category", "Id", "Payload", "Sequence" },
            entity!.GetProperties().Select(property => property.Name).OrderBy(name => name));
        Assert.Equal(new[] { "Category", "Id" }, Assert.Single(entity.GetIndexes()).Properties.Select(property => property.Name));
    }

    [Fact]
    public void Published_evidence_fingerprint_matches_the_canonical_schema()
    {
        Assert.Equal(
            BenchmarkMethodology.SchemaFingerprint,
            File.ReadAllText(Path.Combine(
                AppContext.BaseDirectory,
                "evidence/schema-fingerprint.txt")).Trim());
    }

    [Fact]
    public async Task Every_comparison_path_executes_once_against_the_shared_physical_schema()
    {
        var benchmarks = new StorageBenchmarks();
        benchmarks.Setup();
        try
        {
            var groundworkPoint = await benchmarks.PointRead_Groundwork();
            var efPoint = await benchmarks.PointRead_EFCoreCompiledModel();
            var dapperPoint = await benchmarks.PointRead_Dapper();
            Assert.Equal("item-0500", groundworkPoint.Id);
            AssertEquivalent(groundworkPoint, efPoint);
            AssertEquivalent(groundworkPoint, dapperPoint);

            Assert.Equal(BenchmarkMethodology.PageSize, (await benchmarks.CoveredQuery_Groundwork()).Count);
            Assert.Equal(BenchmarkMethodology.PageSize, (await benchmarks.CoveredQuery_EFCoreCompiledModel()).Count);
            Assert.Equal(BenchmarkMethodology.PageSize, (await benchmarks.CoveredQuery_Dapper()).Count);
            Assert.Equal(BenchmarkMethodology.PageSize, (await benchmarks.PagedQuery_Groundwork()).Count);
            Assert.Equal(BenchmarkMethodology.PageSize, (await benchmarks.PagedQuery_EFCoreCompiledModel()).Count);
            Assert.Equal(BenchmarkMethodology.PageSize, (await benchmarks.PagedQuery_Dapper()).Count);

            Assert.Equal(BenchmarkMethodology.BatchSize, (await benchmarks.BatchedWrite_Groundwork()).Count);
            Assert.Equal(BenchmarkMethodology.BatchSize, await benchmarks.BatchedWrite_EFCoreCompiledModel());
            Assert.Equal(BenchmarkMethodology.BatchSize, await benchmarks.BatchedWrite_Dapper());
            Assert.Equal(1, await benchmarks.UnitOfWorkCommit_Groundwork());
            Assert.Equal(1, await benchmarks.UnitOfWorkCommit_EFCoreCompiledModel());
            Assert.Equal(1, await benchmarks.UnitOfWorkCommit_Dapper());
        }
        finally
        {
            benchmarks.Cleanup();
        }
    }

    private static void AssertEquivalent(BenchmarkItem expected, BenchmarkItem actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Category, actual.Category);
        Assert.Equal(expected.Sequence, actual.Sequence);
        Assert.Equal(expected.Payload, actual.Payload);
    }
}
