using Groundwork.Kernel;
using Groundwork.Query.Linq.Execution;
using Groundwork.Query.Model;
using Groundwork.Query.Planning;
using Groundwork.Store;
using Groundwork.Testing;
using Xunit;

namespace Groundwork.Query.Linq.Tests;

/// <summary>
/// Provider-independent behavior of the one LINQ executor, proven against the reference provider so
/// it runs everywhere. The four-way proof that real providers agree lives in
/// <c>Groundwork.Differential.Tests</c>; this pins the parts that must not depend on a provider at
/// all — what the gate admits, and what the materializer produces.
/// </summary>
public sealed class LinqExecutorTests
{
    private sealed class Ticket
    {
        public string Id { get; set; } = string.Empty;
        public string? Status { get; set; }
        public int Weight { get; set; }
        public long? Optional { get; set; }
        public string Unmapped = "untouched";
    }

    [Fact]
    public async Task Executor_materializes_mapped_columns_and_leaves_unmapped_members_alone()
    {
        using var fixture = Fixture.Open();

        var rows = await fixture.Table.Query
            .Where(ticket => ticket.Status == "open")
            .ToListAsync(fixture.Executor);

        var row = Assert.Single(rows);
        Assert.Equal("a", row.Id);
        Assert.Equal("open", row.Status);
        Assert.Equal(7, row.Weight);
        Assert.Null(row.Optional);
        Assert.Equal("untouched", row.Unmapped);
    }

    [Fact]
    public async Task Uncovered_query_is_refused_before_the_provider_is_asked_to_render_it()
    {
        using var fixture = Fixture.Open();

        var refusal = await Assert.ThrowsAsync<QueryCoverageException>(() => fixture.Table.Query
            .Where(ticket => ticket.Weight == 7)
            .ToListAsync(fixture.Executor));

        Assert.Equal("GW-COVER-006", refusal.Code);
        Assert.Contains("Add: [GwIndex(", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Declared_index_the_catalog_does_not_carry_cannot_rescue_a_query()
    {
        using var fixture = Fixture.Open();

        // The declaration is identical in both cases; only the deployed evidence differs. An index a
        // rolling deploy has not created yet must not admit the query.
        Assert.NotEmpty(await Query(new GwLinqExecutor(fixture.Session)));
        var refusal = await Assert.ThrowsAsync<QueryCoverageException>(
            () => Query(new GwLinqExecutor(fixture.Session, new EmptyCatalog())));
        Assert.Equal("GW-COVER-006", refusal.Code);

        static Task<IReadOnlyList<Ticket>> Query(GwLinqExecutor executor) =>
            new GwQueryDatabase(executor).Table(Fixture.Model).Query
                .Where(ticket => ticket.Status == "open")
                .ToListAsync(executor);
    }

    [Theory]
    [InlineData(2_100, true)]
    [InlineData(65_535, false)]
    public async Task Parameter_budget_is_read_from_the_provider_rather_than_assumed(int budget, bool refused)
    {
        using var fixture = Fixture.Open();
        var executor = new GwLinqExecutor(new BudgetedSession(fixture.Session, budget));
        var table = new TableId(Fixture.TableName);
        var id = new ColumnRef(table, "id", QueryType.String, isNullable: false, maxLength: 32);
        var status = new ColumnRef(table, "status", QueryType.String, isNullable: true, maxLength: 32);
        var weight = new ColumnRef(table, "weight", QueryType.Int32, isNullable: false);
        // Three memberships, each inside the per-predicate cap, that together bind more parameters
        // than SQL Server can carry in one command and far fewer than PostgreSQL can.
        var request = new QueryRequest(
            table,
            new Predicate.And(
            [
                new Predicate.In(id, Enumerable.Range(0, 1_000).Select(value => QueryConstant.Of(id, "id" + value))),
                new Predicate.In(status, Enumerable.Range(0, 1_000).Select(value => QueryConstant.Of(status, "s" + value))),
                new Predicate.In(weight, Enumerable.Range(0, 500).Select(value => QueryConstant.Of(weight, value)))
            ]),
            [],
            Projection.All,
            Paging.OffsetLimit(0, 1));

        var thrown = await Record.ExceptionAsync(() => executor.ToListAsync(request, Fixture.Model));

        if (refused)
        {
            var fence = Assert.IsType<RuntimeValueFenceException>(thrown);
            Assert.Equal("GW-RUNTIME-011", fence.Code);
        }
        else
        {
            // PostgreSQL's real budget binds this many parameters, so the fence must not be the thing
            // that stops it. Coverage still gets its say, and does.
            Assert.IsNotType<RuntimeValueFenceException>(thrown);
        }
    }

    private sealed class EmptyCatalog : IProviderCatalog
    {
        public IReadOnlyList<ProviderIndex> ReadIndexes(StorageUnitId storageUnitId) => [];
    }

    /// <summary>A session that advertises a different native budget without changing anything else.</summary>
    private sealed class BudgetedSession(IStorageSession inner, int maximumParameters)
        : IStorageSession, IQueryAdmissionStorageSession
    {
        public QueryAdmissionProfile QueryAdmission => new() { MaximumParameters = maximumParameters };
        public StorageUnit Unit => inner.Unit;
        public StorageAccess Access => inner.Access;
        public StoredEntry? Read(StorageKey key) => inner.Read(key);
        public QueryMaterializedResult Query(QueryRequest request, QueryRenderOptions? options = null) => inner.Query(request, options);
        public AggregationResult Aggregate(AggregationQuery query) => inner.Aggregate(query);
        public WriteOutcome Insert(StorageValues values, WriteOptions? options = null) => inner.Insert(values, options);
        public WriteOutcome Update(StorageValues values, WriteOptions? options = null) => inner.Update(values, options);
        public WriteOutcome Upsert(StorageValues values, WriteOptions? options = null) => inner.Upsert(values, options);
        public WriteOutcome Delete(StorageKey key, WriteOptions? options = null) => inner.Delete(key, options);
        public WriteOutcome Append(OperationId operationId, IReadOnlyList<StorageValues> values) => inner.Append(operationId, values);
    }

    private sealed class Fixture : IDisposable
    {
        internal const string TableName = "linq_executor";

        private readonly IStorageProviderConnection connection;

        private Fixture(IStorageProviderConnection connection, IStorageSession session)
        {
            this.connection = connection;
            Session = session;
            Executor = new GwLinqExecutor(session, connection.Catalog);
            Table = new GwQueryDatabase(Executor).Table(Model);
        }

        internal IStorageSession Session { get; }
        internal GwLinqExecutor Executor { get; }
        internal GwQueryTable<Ticket> Table { get; }

        internal static GwTableModel<Ticket> Model { get; } = new(TableName,
        [
            new GwColumn<Ticket>(nameof(Ticket.Id), "id", QueryType.String, IsNullable: false, MaxLength: 32),
            new GwColumn<Ticket>(nameof(Ticket.Status), "status", QueryType.String, IsNullable: true, MaxLength: 32),
            new GwColumn<Ticket>(nameof(Ticket.Weight), "weight", QueryType.Int32, IsNullable: false),
            new GwColumn<Ticket>(nameof(Ticket.Optional), "optional", QueryType.Int64, IsNullable: true)
        ]);

        internal static Fixture Open()
        {
            var connection = new InMemoryProviderFactory().Create("memory://linq-executor-" + Guid.NewGuid().ToString("N"));
            var unit = new StorageUnit
            {
                Id = new StorageUnitId(TableName),
                Name = TableName,
                Columns =
                [
                    new() { Name = "id", Type = PortableType.String, IsNullable = false, MaxLength = 32 },
                    new() { Name = "status", Type = PortableType.String, MaxLength = 32 },
                    new() { Name = "weight", Type = PortableType.Int32, IsNullable = false },
                    new() { Name = "optional", Type = PortableType.Int64 }
                ],
                Key = new KeyDefinition { Columns = ["id"] },
                Indexes = [new() { Name = "ix_status", Columns = [new IndexColumn("status")] }]
            };
            connection.Schema.Apply(unit);
            var session = connection.OpenSession(unit, StorageAccess.Global);
            session.Insert(new StorageValues(new Dictionary<string, object?>
            {
                ["id"] = "a", ["status"] = "open", ["weight"] = 7, ["optional"] = null
            }));
            return new Fixture(connection, session);
        }

        public void Dispose() => connection.Dispose();
    }
}
