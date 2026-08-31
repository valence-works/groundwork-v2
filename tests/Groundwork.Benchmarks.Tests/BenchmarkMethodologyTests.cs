using System.Reflection;
using BenchmarkDotNet.Attributes;
using Groundwork.Benchmarks;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Sqlite;
using Groundwork.Substrate.Relational;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
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
        var declaration = BenchmarkMethodology.CreateStorageUnit();
        Assert.Equal(
            new[] { "category", "id", "sequence", "payload" },
            Assert.Single(declaration.Indexes).Columns.Select(column => column.Column));

        var entity = BenchmarkDbContextModel.Instance.FindEntityType(typeof(BenchmarkItem));
        Assert.NotNull(entity);
        Assert.Equal("benchmark_items", entity!.GetTableName());
        var table = StoreObjectIdentifier.Table("benchmark_items", schema: null);

        Assert.Equal(new[] { "Category", "Id", "Payload", "Sequence" },
            entity.GetProperties().Select(property => property.Name).OrderBy(name => name));
        AssertProperty(entity, table, "Id", "id", "TEXT");
        AssertProperty(entity, table, "Category", "category", "TEXT");
        AssertProperty(entity, table, "Sequence", "sequence", "INTEGER");
        AssertProperty(entity, table, "Payload", "payload", "TEXT");
        Assert.Equal(new[] { "Id" }, entity.FindPrimaryKey()!.Properties.Select(property => property.Name));
        var index = Assert.Single(entity.GetIndexes());
        Assert.Equal(
            new[] { "Category", "Id", "Sequence", "Payload" },
            index.Properties.Select(property => property.Name));
        Assert.Equal("ix_benchmark_items_category_id", index.GetDatabaseName());
        Assert.False(index.IsUnique);
    }

    [Fact]
    public void Applied_sqlite_catalog_matches_the_canonical_benchmark_schema()
    {
        using var fixture = new StorageBenchmarkFixture();
        var benchmarks = fixture.Benchmarks;

        using var connection = new SqliteConnection(benchmarks.DatabaseConnectionString);
        connection.Open();

        using var tableInfo = connection.CreateCommand();
        tableInfo.CommandText = "PRAGMA table_info('benchmark_items');";
        using var tableReader = tableInfo.ExecuteReader();
        var columns = new List<(string Name, string Type, long NotNull, long PrimaryKey)>();
        while (tableReader.Read())
        {
            columns.Add((
                tableReader.GetString(1),
                tableReader.GetString(2),
                tableReader.GetInt64(3),
                tableReader.GetInt64(5)));
        }
        Assert.Equal(
            new[]
            {
                ("id", "TEXT", 1L, 1L),
                ("category", "TEXT", 1L, 0L),
                ("sequence", "INTEGER", 1L, 0L),
                ("payload", "TEXT", 1L, 0L),
                ("__groundwork_action", "TEXT", 1L, 0L)
            },
            columns);

        using var tableSql = connection.CreateCommand();
        tableSql.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='benchmark_items';";
        var sql = Assert.IsType<string>(tableSql.ExecuteScalar());
        Assert.Equal(
            "CREATE TABLE \"benchmark_items\" (" +
            "\"id\" TEXT COLLATE GROUNDWORK_UTF16_ORDINAL NOT NULL, " +
            "\"category\" TEXT COLLATE GROUNDWORK_UTF16_ORDINAL NOT NULL, " +
            "\"sequence\" INTEGER COLLATE BINARY NOT NULL, " +
            "\"payload\" TEXT COLLATE GROUNDWORK_UTF16_ORDINAL NOT NULL, " +
            "\"__groundwork_action\" TEXT COLLATE GROUNDWORK_UTF16_ORDINAL NOT NULL DEFAULT 'I', " +
            "PRIMARY KEY (\"id\"))",
            sql);

        using var indexList = connection.CreateCommand();
        indexList.CommandText = "PRAGMA index_list('benchmark_items');";
        using var listReader = indexList.ExecuteReader();
        var declaredIndexes = new List<(string Name, long Unique)>();
        while (listReader.Read())
        {
            if (string.Equals(listReader.GetString(3), "c", StringComparison.Ordinal))
                declaredIndexes.Add((listReader.GetString(1), listReader.GetInt64(2)));
        }
        var declaredIndex = Assert.Single(declaredIndexes);
        Assert.Equal(
            "__groundwork_ix_15_benchmark_items_30_ix_benchmark_items_category_id",
            declaredIndex.Name);
        Assert.Equal(0L, declaredIndex.Unique);

        using var indexInfo = connection.CreateCommand();
        var escapedIndexName = declaredIndex.Name.Replace("'", "''", StringComparison.Ordinal);
        indexInfo.CommandText = $"PRAGMA index_xinfo('{escapedIndexName}');";
        using var indexReader = indexInfo.ExecuteReader();
        var indexed = new List<(string Name, long Descending, string Collation)>();
        while (indexReader.Read())
        {
            if (indexReader.GetInt64(5) == 1)
                indexed.Add((indexReader.GetString(2), indexReader.GetInt64(3), indexReader.GetString(4)));
        }
        Assert.Equal(
            new[]
            {
                ("category", 0L, "GROUNDWORK_UTF16_ORDINAL"),
                ("id", 0L, "GROUNDWORK_UTF16_ORDINAL"),
                ("sequence", 0L, "BINARY"),
                ("payload", 0L, "GROUNDWORK_UTF16_ORDINAL")
            },
            indexed);
    }

    [Fact]
    public void Groundwork_covered_query_uses_the_declared_covering_index()
    {
        using var fixture = new StorageBenchmarkFixture();
        var benchmarks = fixture.Benchmarks;

        var request = benchmarks.GroundworkCoveredQueryRequest;
        var query = new SqliteQueryRenderer().Render(request);
        Assert.False(request.Projection.AllColumns);
        Assert.Equal(
            new[] { "id", "category", "sequence", "payload" },
            request.Projection.Columns.Select(column => column.Name));
        Assert.DoesNotContain("SELECT *", query.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("__groundwork_action", query.CommandText, StringComparison.Ordinal);

        using var connection = new SqliteConnection(benchmarks.DatabaseConnectionString);
        connection.Open();
        connection.CreateCollation("GROUNDWORK_UTF16_ORDINAL", string.CompareOrdinal);
        using var explain = connection.CreateCommand();
        explain.CommandText = "EXPLAIN QUERY PLAN " + query.CommandText;
        RelationalQueryResultReader.AddParameters(explain, query);
        using var reader = explain.ExecuteReader();
        var details = new List<string>();
        while (reader.Read())
            details.Add(reader.GetString(3));

        Assert.Contains(details, detail => detail.Contains(
            "USING COVERING INDEX __groundwork_ix_15_benchmark_items_30_ix_benchmark_items_category_id",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Published_evidence_fingerprint_matches_the_canonical_schema()
    {
        Assert.Equal(
            new SchemaSubject(BenchmarkMethodology.CreateStorageUnit()).Fingerprint,
            BenchmarkMethodology.SchemaFingerprint);
        var published = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "evidence/schema-fingerprint.txt")).Trim();
        Assert.True(
            string.Equals(BenchmarkMethodology.SchemaFingerprint, published, StringComparison.Ordinal),
            $"Canonical schema fingerprint '{BenchmarkMethodology.SchemaFingerprint}' " +
            $"did not match published '{published}'.");
    }

    [Fact]
    public async Task Every_comparison_path_executes_once_against_the_shared_physical_schema()
    {
        using var fixture = new StorageBenchmarkFixture();
        var benchmarks = fixture.Benchmarks;

        var groundworkPoint = await benchmarks.PointRead_Groundwork();
        var efPoint = await benchmarks.PointRead_EFCoreCompiledModel();
        var dapperPoint = await benchmarks.PointRead_Dapper();
        Assert.Equal("item-0500", groundworkPoint.Id);
        Assert.Equal("category-0", groundworkPoint.Category);
        Assert.Equal(500, groundworkPoint.Sequence);
        Assert.Equal("payload-0500", groundworkPoint.Payload);
        AssertEquivalent(groundworkPoint, efPoint);
        AssertEquivalent(groundworkPoint, dapperPoint);

        var groundworkCovered = await benchmarks.CoveredQuery_Groundwork();
        var efCovered = await benchmarks.CoveredQuery_EFCoreCompiledModel();
        var dapperCovered = await benchmarks.CoveredQuery_Dapper();
        Assert.Equal(BenchmarkMethodology.PageSize, groundworkCovered.Count);
        AssertEquivalent(groundworkCovered, efCovered);
        AssertEquivalent(groundworkCovered, dapperCovered);
        AssertEquivalent(
            new BenchmarkItem
            {
                Id = "item-0003",
                Category = "category-3",
                Sequence = 3,
                Payload = "payload-0003"
            },
            groundworkCovered[0]);
        AssertEquivalent(
            new BenchmarkItem
            {
                Id = "item-0243",
                Category = "category-3",
                Sequence = 243,
                Payload = "payload-0243"
            },
            groundworkCovered[^1]);

        var groundworkPage = await benchmarks.PagedQuery_Groundwork();
        var efPage = await benchmarks.PagedQuery_EFCoreCompiledModel();
        var dapperPage = await benchmarks.PagedQuery_Dapper();
        Assert.Equal(BenchmarkMethodology.PageSize, groundworkPage.Count);
        AssertEquivalent(groundworkPage, efPage);
        AssertEquivalent(groundworkPage, dapperPage);
        Assert.Equal("item-0500", groundworkPage[0].Id);
        Assert.Equal("item-0524", groundworkPage[^1].Id);

        Assert.Equal(BenchmarkMethodology.BatchSize, (await benchmarks.BatchedWrite_Groundwork()).Count);
        Assert.Equal(BenchmarkMethodology.BatchSize, await benchmarks.BatchedWrite_EFCoreCompiledModel());
        Assert.Equal(BenchmarkMethodology.BatchSize, await benchmarks.BatchedWrite_Dapper());
        Assert.Equal(1, await benchmarks.UnitOfWorkCommit_Groundwork());
        Assert.Equal(1, await benchmarks.UnitOfWorkCommit_EFCoreCompiledModel());
        Assert.Equal(1, await benchmarks.UnitOfWorkCommit_Dapper());
    }

    [Fact]
    public async Task Batched_write_paths_leave_the_same_ordered_target_state()
    {
        var ids = Enumerable.Range(0, BenchmarkMethodology.BatchSize)
            .Select(index => $"write-batch-{index:D2}")
            .ToArray();
        var groundwork = await RunAndRead(
            async benchmarks => Assert.Equal(
                BenchmarkMethodology.BatchSize,
                (await benchmarks.BatchedWrite_Groundwork()).Count),
            ids);
        var ef = await RunAndRead(
            async benchmarks => Assert.Equal(
                BenchmarkMethodology.BatchSize,
                await benchmarks.BatchedWrite_EFCoreCompiledModel()),
            ids);
        var dapper = await RunAndRead(
            async benchmarks => Assert.Equal(
                BenchmarkMethodology.BatchSize,
                await benchmarks.BatchedWrite_Dapper()),
            ids);

        AssertEquivalent(groundwork, ef);
        AssertEquivalent(groundwork, dapper);
        for (var index = 0; index < groundwork.Count; index++)
        {
            Assert.Equal("write", groundwork[index].Category);
            Assert.Equal(index, groundwork[index].Sequence);
            Assert.Equal("payload-1", groundwork[index].Payload);
        }
    }

    [Fact]
    public async Task Unit_of_work_paths_leave_the_same_target_state()
    {
        var groundwork = Assert.Single(await RunAndRead(
            async benchmarks => Assert.Equal(1, await benchmarks.UnitOfWorkCommit_Groundwork()),
            "write-commit"));
        var ef = Assert.Single(await RunAndRead(
            async benchmarks => Assert.Equal(1, await benchmarks.UnitOfWorkCommit_EFCoreCompiledModel()),
            "write-commit"));
        var dapper = Assert.Single(await RunAndRead(
            async benchmarks => Assert.Equal(1, await benchmarks.UnitOfWorkCommit_Dapper()),
            "write-commit"));

        AssertEquivalent(groundwork, ef);
        AssertEquivalent(groundwork, dapper);
        Assert.Equal("payload-1", groundwork.Payload);
    }

    private static async Task<IReadOnlyList<BenchmarkItem>> RunAndRead(
        Func<StorageBenchmarks, Task> operation,
        params string[] ids)
    {
        using var fixture = new StorageBenchmarkFixture();
        await operation(fixture.Benchmarks);
        return await fixture.Benchmarks.ReadItemsAsync(ids);
    }

    private static void AssertEquivalent(BenchmarkItem expected, BenchmarkItem actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Category, actual.Category);
        Assert.Equal(expected.Sequence, actual.Sequence);
        Assert.Equal(expected.Payload, actual.Payload);
    }

    private static void AssertProperty(
        IEntityType entity,
        StoreObjectIdentifier table,
        string propertyName,
        string columnName,
        string columnType)
    {
        var property = entity.FindProperty(propertyName);
        Assert.NotNull(property);
        Assert.Equal(columnName, property!.GetColumnName(table));
        Assert.Equal(columnType, property.GetColumnType(table));
        Assert.False(property.IsNullable);
    }

    private static void AssertEquivalent(
        IReadOnlyList<BenchmarkItem> expected,
        IReadOnlyList<BenchmarkItem> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
            AssertEquivalent(expected[index], actual[index]);
    }

    private sealed class StorageBenchmarkFixture : IDisposable
    {
        public StorageBenchmarkFixture()
        {
            Benchmarks.Setup();
        }

        public StorageBenchmarks Benchmarks { get; } = new();

        public void Dispose() => Benchmarks.Cleanup();
    }
}
