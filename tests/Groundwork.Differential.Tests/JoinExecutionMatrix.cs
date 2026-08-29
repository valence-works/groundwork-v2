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
/// Opens one source/target declaration pair with the same rows on every shipped provider. Provider
/// setup stays behind this test module's small interface so the differential assertions cannot
/// accidentally compare different declarations, data, access scopes, or query shapes.
/// </summary>
internal sealed class JoinExecutionMatrix : IDisposable
{
    private readonly List<IStorageProviderConnection> connections = [];

    private JoinExecutionMatrix(ScopePolicy scope, bool includeTargetPredicateIndex)
    {
        var suffix = Guid.NewGuid().ToString("N");
        Target = DeclareTarget("g2_join_target_" + suffix, scope, includeTargetPredicateIndex);
        Source = DeclareSource("g2_join_source_" + suffix, scope, Target);
        SourceTable = new TableId(Source.Name);
        TargetTable = new TableId(Target.Name);
        SourceId = new ColumnRef(SourceTable, "id", QueryType.Int64, isNullable: false);
        SourceStatus = new ColumnRef(SourceTable, "status", QueryType.String, isNullable: false, maxLength: 16);
        TargetName = new ColumnRef(TargetTable, "name", QueryType.String, isNullable: false, maxLength: 32);
        TargetNickname = new ColumnRef(TargetTable, "nickname", QueryType.String, isNullable: true, maxLength: 32);
        Join = new ReferenceJoin(
            "customer",
            TargetTable,
            [new JoinColumnPair(
                new ColumnRef(SourceTable, "customer_id", QueryType.String, isNullable: false, maxLength: 16),
                new ColumnRef(TargetTable, "id", QueryType.String, isNullable: false, maxLength: 16))]);
    }

    internal StorageUnit Source { get; }

    internal StorageUnit Target { get; }

    internal TableId SourceTable { get; }

    internal TableId TargetTable { get; }

    internal ColumnRef SourceId { get; }

    internal ColumnRef SourceStatus { get; }

    internal ColumnRef TargetName { get; }

    internal ColumnRef TargetNickname { get; }

    internal ReferenceJoin Join { get; }

    internal List<JoinProvider> Providers { get; } = [];

    internal static JoinExecutionMatrix OpenSqlite(
        ScopePolicy scope = ScopePolicy.Global,
        bool includeTargetPredicateIndex = true)
    {
        var matrix = new JoinExecutionMatrix(scope, includeTargetPredicateIndex);
        try
        {
            matrix.Add(
                "SQLite",
                new SqliteProviderFactory().Create(
                    "Data Source=file:g2join_" + Guid.NewGuid().ToString("N") + "?mode=memory&cache=shared"),
                scope);
            return matrix;
        }
        catch
        {
            matrix.Dispose();
            throw;
        }
    }

    internal static JoinExecutionMatrix OpenAll(
        ScopePolicy scope = ScopePolicy.Global,
        bool includeTargetPredicateIndex = true)
    {
        var postgres = Environment.GetEnvironmentVariable("GROUNDWORK_POSTGRES_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(postgres),
            "Set GROUNDWORK_POSTGRES_CONNECTION to run the four-way join execution matrix.");
        var sqlServer = LiveSqlServer.Required();
        var mongo = LiveMongo.Required();
        var matrix = new JoinExecutionMatrix(scope, includeTargetPredicateIndex);
        try
        {
            matrix.Add(
                "SQLite",
                new SqliteProviderFactory().Create(
                    "Data Source=file:g2join_" + Guid.NewGuid().ToString("N") + "?mode=memory&cache=shared"),
                scope);
            matrix.Add("PostgreSQL", new PostgreSqlProviderFactory().Create(postgres!), scope);
            matrix.Add("SQL Server", new SqlServerProviderFactory().Create(sqlServer), scope);
            matrix.Add("MongoDB", new MongoProviderFactory().Create(mongo), scope);
            return matrix;
        }
        catch
        {
            matrix.Dispose();
            throw;
        }
    }

    internal QueryRequest OrderedPage(Paging paging) => new(
        SourceTable,
        Join,
        OpenSourcePredicate(),
        [
            new OrderTerm(SourceStatus, OrderDirection.Ascending, NullOrder.First),
            new OrderTerm(TargetName, OrderDirection.Descending, NullOrder.Last)
        ],
        Projection.ColumnsOnly(SourceId, TargetName, TargetNickname),
        paging);

    internal QueryRequest TwoValuedNullPredicate() => new(
        SourceTable,
        Join,
        new Predicate.And([
            OpenSourcePredicate(),
            new Predicate.Not(new Predicate.Equal(
                TargetNickname,
                QueryConstant.Of(TargetNickname, "blocked")))
        ]),
        [new OrderTerm(SourceStatus, OrderDirection.Ascending, NullOrder.First)],
        Projection.ColumnsOnly(SourceId, TargetNickname),
        Paging.None,
        acceptedScan: ScanAcceptance.Allow(
            "GW-SCAN-JOIN-NULL",
            "Exercise portable two-valued null semantics for an intentionally non-sargable complement.",
            "groundwork-tests",
            new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero)));

    internal QueryRequest ScopedRows() => new(
        SourceTable,
        Join,
        OpenSourcePredicate(),
        [new OrderTerm(SourceStatus, OrderDirection.Ascending, NullOrder.First)],
        Projection.ColumnsOnly(SourceId, TargetName),
        Paging.None);

    internal QueryRequest UncoveredTargetPredicate() => new(
        SourceTable,
        Join,
        new Predicate.And([
            OpenSourcePredicate(),
            new Predicate.Equal(TargetNickname, QueryConstant.Of(TargetNickname, "blocked"))
        ]),
        [new OrderTerm(SourceStatus, OrderDirection.Ascending, NullOrder.First)],
        Projection.ColumnsOnly(SourceId, TargetNickname),
        Paging.None);

    public void Dispose()
    {
        foreach (var connection in connections)
            connection.Dispose();
    }

    private Predicate OpenSourcePredicate() =>
        new Predicate.Equal(SourceStatus, QueryConstant.Of(SourceStatus, "open"));

    private void Add(string name, IStorageProviderConnection connection, ScopePolicy scope)
    {
        connections.Add(connection);
        Assert.True(connection.Schema.Apply(Target).Applied, $"{name}: target declaration did not apply.");
        Assert.True(connection.Schema.Apply(Source).Applied, $"{name}: source declaration did not apply.");

        if (scope == ScopePolicy.Global)
        {
            var target = connection.OpenSession(Target, StorageAccess.Global);
            var source = connection.OpenSession(Source, StorageAccess.Global);
            SeedGlobal(target, source);
            Providers.Add(new JoinProvider(name, source, null, target));
            return;
        }

        var first = SeedScope(connection, "scope-a", "Ada-A");
        var second = SeedScope(connection, "scope-b", "Ada-B");
        Providers.Add(new JoinProvider(name, first.Source, second.Source, first.Target));
    }

    private (IStorageSession Source, IStorageSession Target) SeedScope(
        IStorageProviderConnection connection,
        string scopeName,
        string customerName)
    {
        var access = StorageAccess.Scoped(new StorageScope(scopeName));
        var target = connection.OpenSession(Target, access);
        var source = connection.OpenSession(Source, access);
        Assert.True(target.Insert(TargetRow("customer-a", customerName, null)).Succeeded);
        Assert.True(source.Insert(SourceRow(1L, "open", "customer-a")).Succeeded);
        return (source, target);
    }

    private static void SeedGlobal(IStorageSession target, IStorageSession source)
    {
        Assert.True(target.Insert(TargetRow("customer-a", "Ada", null)).Succeeded);
        Assert.True(target.Insert(TargetRow("customer-b", "Bea", "blocked")).Succeeded);
        Assert.True(target.Insert(TargetRow("customer-c", "Cy", "ok")).Succeeded);

        Assert.True(source.Insert(SourceRow(1L, "open", "customer-b")).Succeeded);
        Assert.True(source.Insert(SourceRow(2L, "open", "customer-a")).Succeeded);
        Assert.True(source.Insert(SourceRow(3L, "closed", "customer-c")).Succeeded);
        Assert.True(source.Insert(SourceRow(4L, "open", "customer-c")).Succeeded);
        Assert.True(source.Insert(SourceRow(5L, "open", "customer-a")).Succeeded);
    }

    private static StorageValues TargetRow(string id, string name, string? nickname) =>
        new(new Dictionary<string, object?>
        {
            ["id"] = id,
            ["name"] = name,
            ["nickname"] = nickname
        });

    private static StorageValues SourceRow(long id, string status, string customerId) =>
        new(new Dictionary<string, object?>
        {
            ["id"] = id,
            ["status"] = status,
            ["customer_id"] = customerId
        });

    private static StorageUnit DeclareTarget(
        string name,
        ScopePolicy scope,
        bool includeTargetPredicateIndex) => new()
    {
        Id = new StorageUnitId(name),
        Name = name,
        Scope = scope,
        Columns =
        [
            new() { Name = "id", Type = PortableType.String, IsNullable = false, MaxLength = 16 },
            new() { Name = "name", Type = PortableType.String, IsNullable = false, MaxLength = 32 },
            new() { Name = "nickname", Type = PortableType.String, IsNullable = true, MaxLength = 32 }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        Indexes = includeTargetPredicateIndex
            ? [
                new IndexDefinition
                {
                    Name = "by_join_name",
                    Columns = [new IndexColumn("id"), new IndexColumn("name", SortDirection.Descending)]
                },
                new IndexDefinition
                {
                    Name = "by_join_nickname",
                    Columns = [new IndexColumn("id"), new IndexColumn("nickname")]
                }
            ]
            : []
    };

    private static StorageUnit DeclareSource(string name, ScopePolicy scope, StorageUnit target) => new()
    {
        Id = new StorageUnitId(name),
        Name = name,
        Scope = scope,
        Columns =
        [
            new() { Name = "id", Type = PortableType.Int64, IsNullable = false },
            new() { Name = "status", Type = PortableType.String, IsNullable = false, MaxLength = 16 },
            new() { Name = "customer_id", Type = PortableType.String, IsNullable = false, MaxLength = 16 }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        Indexes =
        [
            new IndexDefinition { Name = "by_status", Columns = [new IndexColumn("status")] },
            new IndexDefinition { Name = "by_customer", Columns = [new IndexColumn("customer_id")] }
        ],
        References =
        [
            new ReferenceDefinition
            {
                Name = "customer",
                Columns = ["customer_id"],
                TargetUnitId = target.Id,
                TargetScope = scope
            }
        ]
    };
}

internal sealed record JoinProvider(
    string Name,
    IStorageSession FirstScope,
    IStorageSession? SecondScope,
    IStorageSession Target);
