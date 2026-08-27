using Groundwork.Kernel;
using Groundwork.LiveDatabases;
using Groundwork.PostgreSql;
using Groundwork.SqlServer;
using Groundwork.Sqlite;
using Groundwork.Testing;
using Groundwork.Store;
using Groundwork.Query.Model;
using Xunit;

namespace Groundwork.StreamCapabilities.Tests;

/// <summary>Public query, reduction, and optimistic-write behavior inside a multi-unit UOW.</summary>
public sealed class UnitOfWorkQueryConformanceTests
{
    [Fact]
    public void InMemory_unit_of_work_query_and_versioned_write_contract_is_preserved()
    {
        using var connection = new InMemoryProviderFactory().Create("uow-query-inmemory-" + Guid.NewGuid().ToString("N"));
        AssertProvider(connection, "inmemory");
    }

    [Fact]
    public void SQLite_unit_of_work_query_and_versioned_write_contract_is_preserved()
    {
        var path = Path.Combine(Path.GetTempPath(), "groundwork-uow-query-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var connection = new SqliteProviderFactory().Create($"Data Source={path}");
            AssertProvider(connection, "sqlite");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [SkippableFact]
    public void PostgreSQL_unit_of_work_query_and_versioned_write_contract_is_preserved()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_POSTGRES_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_POSTGRES_CONNECTION to run PostgreSQL UOW query conformance.");
        using var connection = new PostgreSqlProviderFactory().Create(connectionString!);
        AssertProvider(connection, "postgresql");
    }

    [SkippableFact]
    public void SQLServer_unit_of_work_query_and_versioned_write_contract_is_preserved()
    {
        var connectionString = LiveSqlServer.ConnectionString;
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_SQLSERVER_CONNECTION to run SQL Server UOW query conformance.");
        using var connection = new SqlServerProviderFactory().Create(connectionString!);
        AssertProvider(connection, "sqlserver");
    }

    private static void AssertProvider(IStorageProviderConnection connection, string provider)
    {
        var first = Unit("uow_query_" + provider + "_first_" + Guid.NewGuid().ToString("N"));
        var second = Unit("uow_query_" + provider + "_second_" + Guid.NewGuid().ToString("N"));
        Assert.True(connection.Schema.Apply(first).Applied);
        Assert.True(connection.Schema.Apply(second).Applied);

        IStorageSession firstSession;
        IStorageSession secondSession;
        using (var work = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, first, second))
        {
            firstSession = work.OpenSession(first);
            secondSession = work.OpenSession(second);

            Assert.Equal(WriteOutcomeStatus.Inserted, firstSession.Insert(Values("a", "first", 1L)).Status);
            Assert.Equal(WriteOutcomeStatus.Inserted, firstSession.Insert(Values("b", "first", 2L)).Status);
            var firstUpdate = firstSession.Update(Values("a", "first", 3L), WriteOptions.IfVersion(1));
            Assert.Equal(WriteOutcomeStatus.Updated, firstUpdate.Status);
            Assert.Equal(2L, firstUpdate.Version);

            Assert.Equal(WriteOutcomeStatus.Inserted, secondSession.Insert(Values("x", "second", 5L)).Status);
            var secondUpdate = secondSession.Update(Values("x", "second-updated", 8L), WriteOptions.IfVersion(1));
            Assert.Equal(WriteOutcomeStatus.Updated, secondUpdate.Status);
            Assert.Equal(2L, secondUpdate.Version);

            var firstRead = firstSession.Read(Key("a"));
            Assert.NotNull(firstRead);
            Assert.Equal(2L, firstRead.Version);
            Assert.Equal("first", firstRead.Values.Values["bucket"]);
            Assert.Equal(3L, firstRead.Values.Values["amount"]);

            var firstPage = firstSession.Query(Page(first));
            Assert.Equal(2L, firstPage.TotalCount);
            var firstPageRow = Assert.Single(firstPage.Rows);
            Assert.Equal("a", firstPageRow["id"]);
            Assert.Equal(3L, firstPageRow["amount"]);
            Assert.NotNull(firstPage.NextContinuationToken);

            var secondPage = firstSession.Query(Page(first, firstPage.NextContinuationToken));
            Assert.Equal(2L, secondPage.TotalCount);
            Assert.Equal("b", Assert.Single(secondPage.Rows)["id"]);

            var aggregate = firstSession.Aggregate(new AggregationQuery("count"));
            var aggregateRow = Assert.Single(aggregate.Rows);
            Assert.Equal("first", aggregateRow["bucket"]);
            Assert.Equal(2L, aggregateRow["rowCount"]);

            var secondAggregate = secondSession.Aggregate(new AggregationQuery("count"));
            Assert.Equal(1L, Assert.Single(secondAggregate.Rows)["rowCount"]);

            work.CommitWithOutcomes();
        }

        Assert.Equal(2L, connection.OpenSession(first, StorageAccess.Global).Read(Key("a"))!.Version);
        Assert.Equal(2L, connection.OpenSession(second, StorageAccess.Global).Read(Key("x"))!.Version);
        AssertTerminalRefusal(() => firstSession.Read(Key("a")));
        AssertTerminalRefusal(() => firstSession.Query(Page(first)));
        AssertTerminalRefusal(() => firstSession.Aggregate(new AggregationQuery("count")));

        using (var rollback = connection.BeginUnitOfWork(StorageAccess.Global, first, second))
        {
            Assert.Equal(WriteOutcomeStatus.Inserted, rollback.OpenSession(first).Insert(Values("rollback", "rollback", 1L)).Status);
            Assert.Equal(WriteOutcomeStatus.Inserted, rollback.OpenSession(second).Insert(Values("rollback", "rollback", 1L)).Status);
            rollback.Rollback();
        }

        Assert.Null(connection.OpenSession(first, StorageAccess.Global).Read(Key("rollback")));
        Assert.Null(connection.OpenSession(second, StorageAccess.Global).Read(Key("rollback")));
    }

    private static void AssertTerminalRefusal(Action operation)
    {
        var exception = Assert.ThrowsAny<Exception>(operation);
        Assert.True(exception is InvalidOperationException or ObjectDisposedException,
            $"A completed unit-of-work session must refuse the operation with a lifecycle exception, got {exception.GetType().FullName}.");
    }

    private static QueryRequest Page(StorageUnit unit, string? continuation = null)
    {
        var table = new TableId(unit.Name);
        var id = new ColumnRef(table, "id", QueryType.String, isNullable: false);
        var bucket = new ColumnRef(table, "bucket", QueryType.String, isNullable: false);
        var amount = new ColumnRef(table, "amount", QueryType.Int64, isNullable: false);
        return new QueryRequest(
            table,
            Predicate.AlwaysTrue.Instance,
            [new OrderTerm(id, OrderDirection.Ascending, NullOrder.Last)],
            Projection.ColumnsOnly(id, bucket, amount),
            continuation is null ? Paging.Keyset(1) : Paging.Continuation(continuation, 1),
            ResultShape.TotalCount.Instance);
    }

    private static StorageValues Values(string id, string bucket, long amount) => new(new Dictionary<string, object?>
    {
        ["id"] = id,
        ["bucket"] = bucket,
        ["amount"] = amount
    });

    private static StorageKey Key(string id) => new(new Dictionary<string, object?> { ["id"] = id });

    private static StorageUnit Unit(string name) => new()
    {
        Id = new StorageUnitId(name),
        Name = name,
        Columns =
        [
            new() { Name = "id", Type = PortableType.String, MaxLength = 128, IsNullable = false },
            new() { Name = "bucket", Type = PortableType.String, MaxLength = 128, IsNullable = false },
            new() { Name = "amount", Type = PortableType.Int64, IsNullable = false }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        Concurrency = ConcurrencyDeclaration.Optimistic(),
        AggregationProfiles =
        [
            new AggregationProfile
            {
                Name = "count",
                GroupByColumns = ["bucket"],
                Aggregates = [new Aggregate.Count("rowCount")],
                MaxGroups = 4,
                MaxInputRows = 16
            }
        ]
    };
}
