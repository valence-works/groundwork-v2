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
            () => Query(new GwLinqExecutor(fixture.Session, new ProbeConnection(fixture.Connection, new EmptyCatalog()))));
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
        var executor = new GwLinqExecutor(
            fixture.Session,
            new ProbeConnection(fixture.Connection, admission: new QueryAdmissionProfile { MaximumParameters = budget }));
        var thrown = await Record.ExceptionAsync(() => executor.ToListAsync(OverBudgetRequest(), Fixture.Model));

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

    [Fact]
    public async Task Unrelated_non_queryable_index_column_does_not_fail_every_query_on_the_unit()
    {
        using var fixture = Fixture.Open(withJsonIndex: true);

        // A JSON column cannot be a portable query index key, but Schema.Apply accepts a unit that
        // declares one: the guard that refuses it (GW-DECL-INDEX-003) runs only in the fluent
        // builder, and a hand-built StorageUnit never passes through it. So the executor must not
        // eagerly convert every declared index — doing so refuses this query with GW-QUERY-018 over
        // an index it never needed.
        var rows = await fixture.Table.Query
            .Where(ticket => ticket.Status == "open")
            .ToListAsync(fixture.Executor);

        Assert.Equal("a", Assert.Single(rows).Id);
    }

    [Fact]
    public async Task Session_decorator_cannot_drop_the_providers_budget()
    {
        using var fixture = Fixture.Open();
        // A consumer decorator that forwards IStorageSession and nothing else is a supported pattern.
        // The budget survives it because it is advertised by the connection, which the decorator does
        // not wrap — the reason it does not live on the session.
        var executor = new GwLinqExecutor(
            new PassThroughSession(fixture.Session),
            new ProbeConnection(fixture.Connection, admission: new QueryAdmissionProfile { MaximumParameters = 999 }));

        var thrown = await Record.ExceptionAsync(() => executor.ToListAsync(OverBudgetRequest(), Fixture.Model));

        var fence = Assert.IsType<RuntimeValueFenceException>(thrown);
        Assert.Equal("GW-RUNTIME-011", fence.Code);
    }

    /// <summary>Forwards the session contract and deliberately advertises no optional capability.</summary>
    private sealed class PassThroughSession(IStorageSession inner) : IStorageSession
    {
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

        public ValueTask<StoredEntry?> ReadAsync(StorageKey key, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(key, cancellationToken);

        public ValueTask<QueryMaterializedResult> QueryAsync(
            QueryRequest request,
            QueryRenderOptions? options = null,
            CancellationToken cancellationToken = default) =>
            inner.QueryAsync(request, options, cancellationToken);

        public ValueTask<AggregationResult> AggregateAsync(
            AggregationQuery query,
            CancellationToken cancellationToken = default) =>
            inner.AggregateAsync(query, cancellationToken);

        public ValueTask<WriteOutcome> InsertAsync(
            StorageValues values,
            WriteOptions? options = null,
            CancellationToken cancellationToken = default) =>
            inner.InsertAsync(values, options, cancellationToken);

        public ValueTask<WriteOutcome> UpdateAsync(
            StorageValues values,
            WriteOptions? options = null,
            CancellationToken cancellationToken = default) =>
            inner.UpdateAsync(values, options, cancellationToken);

        public ValueTask<WriteOutcome> UpsertAsync(
            StorageValues values,
            WriteOptions? options = null,
            CancellationToken cancellationToken = default) =>
            inner.UpsertAsync(values, options, cancellationToken);

        public ValueTask<WriteOutcome> DeleteAsync(
            StorageKey key,
            WriteOptions? options = null,
            CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(key, options, cancellationToken);

        public ValueTask<WriteOutcome> AppendAsync(
            OperationId operationId,
            IReadOnlyList<StorageValues> values,
            CancellationToken cancellationToken = default) =>
            inner.AppendAsync(operationId, values, cancellationToken);
    }

    /// <summary>
    /// Three memberships, each inside the per-predicate cap, that together bind more parameters than
    /// SQL Server can carry in one command and far fewer than PostgreSQL can.
    /// </summary>
    private static QueryRequest OverBudgetRequest()
    {
        var table = new TableId(Fixture.TableName);
        var id = new ColumnRef(table, "id", QueryType.String, isNullable: false, maxLength: 32);
        var status = new ColumnRef(table, "status", QueryType.String, isNullable: true, maxLength: 32);
        var weight = new ColumnRef(table, "weight", QueryType.Int32, isNullable: false);
        return new QueryRequest(
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
    }

    private sealed class EmptyCatalog : IProviderCatalog
    {
        public IReadOnlyList<ProviderIndex> ReadIndexes(StorageUnitId storageUnitId) => [];
    }

    /// <summary>
    /// A connection that reports a different catalog or a different advertised budget, and is
    /// otherwise the real one. Both inputs the executor admits against come from the connection, so
    /// substituting them is how those two decisions are exercised in isolation.
    /// </summary>
    private sealed class ProbeConnection(
        IStorageProviderConnection inner,
        IProviderCatalog? catalog = null,
        QueryAdmissionProfile? admission = null)
        : IStorageProviderConnection, IQueryAdmissionProviderConnection
    {
        public QueryAdmissionProfile QueryAdmission => admission ?? inner.GetQueryAdmission();
        public IProviderCatalog Catalog => catalog ?? inner.Catalog;
        public ISchemaCoordinator Schema => inner.Schema;
        public IReadOnlyList<CapabilityDescriptor> Capabilities => inner.Capabilities;
        public IStorageSession OpenSession(StorageUnit unit, StorageAccess access, IProviderCommandObserver? observer = null) =>
            inner.OpenSession(unit, access, observer);
        public IUnitOfWork BeginUnitOfWork(StorageAccess access, params StorageUnit[] units) =>
            inner.BeginUnitOfWork(access, units);
        public IUnitOfWork BeginUnitOfWork(StorageAccess access, BatchWriteOptions options, params StorageUnit[] units) =>
            inner.BeginUnitOfWork(access, options, units);
        public IUnitOfWork BeginUnitOfWork(StorageAccess access, BatchWriteOptions options, IProviderCommandObserver? observer, params StorageUnit[] units) =>
            inner.BeginUnitOfWork(access, options, observer, units);
        public void Dispose()
        {
            // The fixture owns the real connection's lifetime.
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal const string TableName = "linq_executor";

        private readonly IStorageProviderConnection connection;

        private Fixture(IStorageProviderConnection connection, IStorageSession session)
        {
            this.connection = connection;
            Session = session;
            Executor = new GwLinqExecutor(session, connection);
            Table = new GwQueryDatabase(Executor).Table(Model);
        }

        internal IStorageProviderConnection Connection => connection;
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

        internal static Fixture Open(bool withJsonIndex = false)
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
                    new() { Name = "optional", Type = PortableType.Int64 },
                    .. withJsonIndex
                        ? new ColumnDefinition[] { new() { Name = "payload", Type = PortableType.Json, MaxLength = 64 } }
                        : []
                ],
                Key = new KeyDefinition { Columns = ["id"] },
                Indexes =
                [
                    new() { Name = "ix_status", Columns = [new IndexColumn("status")] },
                    .. withJsonIndex
                        ? new IndexDefinition[] { new() { Name = "ix_payload", Columns = [new IndexColumn("payload")] } }
                        : []
                ]
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
