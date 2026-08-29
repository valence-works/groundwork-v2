using System.Globalization;
using Groundwork.Kernel;
using Groundwork.Query.Linq.Execution;
using Groundwork.Query.Model;
using Groundwork.Query.Planning;
using Groundwork.Schema;
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
    [GwTable("linq_executor")]
    public sealed class Ticket
    {
        [GwKey, GwColumn(Name = "id", Length = 32, Required = true)]
        public string Id { get; set; } = string.Empty;
        [GwColumn(Name = "status", Length = 32)]
        public string? Status { get; set; }
        [GwColumn(Name = "weight", Required = true)]
        public int Weight { get; set; }
        [GwColumn(Name = "optional")]
        public long? Optional { get; set; }
        public string Unmapped = "untouched";
    }

    public sealed class StatusDto
    {
        public string? Status { get; init; }
    }

    public sealed class ConstructorStatusDto
    {
        public ConstructorStatusDto(string? status) => Status = status;
        public string? Status { get; }
    }

    public sealed record StatusRecord(string? Status);

    public sealed record AmbiguousProjectionRegistration(string? Status);

    public sealed class SettableConstructorStatusDto
    {
        public SettableConstructorStatusDto(string? status) => Status = status;
        public string? Status { get; set; }
        public string? Identifier { get; set; }
    }

    [Fact]
    public async Task Async_cardinality_materializes_anonymous_and_constructor_projections()
    {
        using var fixture = Fixture.Open();
        var dynamicCodeBefore = LinqRowMaterializer.DynamicCodeGenerationCount;

        var anonymous = await fixture.Table.Query
            .OrderBy(ticket => ticket.Status)
            .Select(ticket => new { ticket.Status })
            .FirstOrDefaultAsync(fixture.Executor);
        var initialized = await fixture.Table.Query
            .OrderBy(ticket => ticket.Status)
            .Select(ticket => new StatusDto { Status = ticket.Status })
            .FirstAsync(fixture.Executor);
        var constructed = await fixture.Table.Query
            .OrderBy(ticket => ticket.Status)
            .Select(ticket => new ConstructorStatusDto(ticket.Status))
            .SingleAsync(fixture.Executor);

        Assert.Equal("open", anonymous.Status);
        Assert.Equal("open", initialized.Status);
        Assert.Equal("open", constructed.Status);
        Assert.Equal(dynamicCodeBefore, LinqRowMaterializer.DynamicCodeGenerationCount);
    }

    [Fact]
    public async Task Async_cardinality_materializes_scalar_records_and_settable_constructor_projections()
    {
        using var fixture = Fixture.Open();
        var dynamicCodeBefore = LinqRowMaterializer.DynamicCodeGenerationCount;

        var scalar = await fixture.Table.Query
            .Where(ticket => ticket.Status == "open")
            .OrderBy(ticket => ticket.Status)
            .Select(ticket => ticket.Status)
            .Distinct()
            .FirstAsync(fixture.Executor);
        var record = await fixture.Table.Query
            .Where(ticket => ticket.Status == "open")
            .OrderBy(ticket => ticket.Status)
            .Select(ticket => new StatusRecord(ticket.Status))
            .Distinct()
            .FirstOrDefaultAsync(fixture.Executor);
        var settable = await fixture.Table.Query
            .OrderBy(ticket => ticket.Status)
            .Select(ticket => new SettableConstructorStatusDto(ticket.Status) { Identifier = ticket.Id })
            .SingleAsync(fixture.Executor);

        Assert.Equal("open", scalar);
        Assert.Equal("open", record.Status);
        Assert.Equal("open", settable.Status);
        Assert.Equal("a", settable.Identifier);
        Assert.Equal(dynamicCodeBefore, LinqRowMaterializer.DynamicCodeGenerationCount);
    }

    [Fact]
    public void Duplicate_projection_registrations_fail_closed()
    {
        GwGeneratedRows.RegisterProjection(
            typeof(AmbiguousProjectionRegistration),
            1,
            static (values, columns) => new AmbiguousProjectionRegistration(
                GwGeneratedRowValue.ReadProjection<string?>(values, columns, 0)));
        Assert.True(GwGeneratedRows.TryGetProjection<AmbiguousProjectionRegistration>(1, out _));

        GwGeneratedRows.RegisterProjection(
            typeof(AmbiguousProjectionRegistration),
            1,
            static (_, _) => new AmbiguousProjectionRegistration("different"));

        Assert.False(GwGeneratedRows.TryGetProjection<AmbiguousProjectionRegistration>(1, out _));
    }

    [Fact]
    public async Task Distinct_executor_applies_windows_after_deduplicating_the_provider_source()
    {
        using var fixture = Fixture.Open(withDuplicateStatus: true);
        var query = fixture.Table.Query
            .OrderBy(ticket => ticket.Status)
            .Select(ticket => ticket.Status)
            .Distinct()
            .AcceptScan("GW-SCAN-DISTINCT", "distinct paging test", "query-tests", new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var page = await fixture.Executor.ToListAsync<string>(query.Take(2).ToQueryRequest());
        Assert.Equal(["closed", "open"], page);

        var afterDuplicate = await query.Skip(1).FirstAsync(fixture.Executor);
        Assert.Equal("open", afterDuplicate);
        await Assert.ThrowsAsync<InvalidOperationException>(() => query.SingleAsync(fixture.Executor));
    }

    [Fact]
    public async Task Unbounded_distinct_executor_requires_an_accepted_scan()
    {
        using var fixture = Fixture.Open();
        var request = fixture.Table.Query
            .OrderBy(ticket => ticket.Status)
            .Select(ticket => ticket.Status)
            .Distinct()
            .ToQueryRequest();

        var refusal = await Assert.ThrowsAsync<QueryCoverageException>(() =>
            fixture.Executor.ToListAsync<string>(request));

        Assert.Equal("GW-COVER-005", refusal.Code);
    }

    [Fact]
    public async Task Executor_materializes_mapped_columns_and_leaves_unmapped_members_alone()
    {
        using var fixture = Fixture.Open();
        var dynamicCodeBefore = LinqRowMaterializer.DynamicCodeGenerationCount;
        Assert.True(GwGeneratedRows.TryGet<Ticket>(out _));

        var rows = await fixture.Table.Query
            .Where(ticket => ticket.Status == "open")
            .ToListAsync(fixture.Executor);

        var row = Assert.Single(rows);
        Assert.Equal("a", row.Id);
        Assert.Equal("open", row.Status);
        Assert.Equal(7, row.Weight);
        Assert.Null(row.Optional);
        Assert.Equal("untouched", row.Unmapped);
        Assert.Equal(dynamicCodeBefore, LinqRowMaterializer.DynamicCodeGenerationCount);
    }

    [Fact]
    public async Task Generated_materializer_defaults_model_columns_omitted_by_a_partial_request()
    {
        using var fixture = Fixture.Open();
        var request = fixture.Table.Query
            .Where(ticket => ticket.Id == "a")
            .ToQueryRequest();
        var partial = new QueryRequest(
            request.Table,
            request.Where,
            request.Order,
            Projection.ColumnsOnly(Fixture.Model.Columns[nameof(Ticket.Id)]),
            request.Paging,
            request.Result,
            request.LatestPerKey,
            request.AcceptedScan,
            request.Distinct);

        var rows = await fixture.Executor.ToListAsync(partial, Fixture.Model);

        var row = Assert.Single(rows);
        Assert.Equal("a", row.Id);
        Assert.Null(row.Status);
        Assert.Equal(0, row.Weight);
        Assert.Null(row.Optional);
        Assert.Equal("untouched", row.Unmapped);
    }

    [Fact]
    public async Task Generated_materializer_uses_declared_column_names_for_projection_all_without_a_model()
    {
        using var fixture = Fixture.Open();
        var request = fixture.Table.Query
            .Where(ticket => ticket.Id == "a")
            .ToQueryRequest();

        var rows = await fixture.Executor.ToListAsync<Ticket>(request);

        var row = Assert.Single(rows);
        Assert.Equal("a", row.Id);
        Assert.Equal("open", row.Status);
        Assert.Equal(7, row.Weight);
        Assert.Null(row.Optional);
        Assert.Equal("untouched", row.Unmapped);
    }

    [Fact]
    public async Task Locale_sort_key_index_covers_logical_ordering_and_returns_locale_order()
    {
        using var fixture = Fixture.Open(localeOrder: true);

        var rows = await fixture.Table.Query
            .OrderBy(ticket => ticket.Status)
            .Take(5)
            .ToListAsync(fixture.Executor);

        Assert.Equal(["Ake", "Zebra", "Åke", "Äke", "Öke"], rows.Select(row => row.Status));
    }

    [Fact]
    public void In_memory_locale_ordering_continuation_uses_the_hidden_sort_key()
    {
        using var fixture = Fixture.Open(localeOrder: true);
        var table = new TableId(Fixture.TableName);
        var status = new ColumnRef(table, "status", QueryType.String, true, 32);
        QueryRequest Request(Paging paging) => new(
            table,
            Predicate.AlwaysTrue.Instance,
            [new OrderTerm(status, OrderDirection.Ascending, NullOrder.First)],
            Projection.ColumnsOnly(status),
            paging);

        var first = fixture.Session.Query(Request(Paging.Keyset(2)));
        var second = fixture.Session.Query(Request(Paging.Continuation(first.NextContinuationToken!, 2)));

        Assert.Equal(["Ake", "Zebra"], first.Rows.Select(row => row["status"]));
        Assert.Equal(["Åke", "Äke"], second.Rows.Select(row => row["status"]));
    }

    [Fact]
    public async Task Executor_distinct_continuation_preserves_provider_locale_ordering()
    {
        using var fixture = Fixture.Open(localeOrder: true);
        var table = new TableId(Fixture.TableName);
        var status = new ColumnRef(table, "status", QueryType.String, true, 32);
        QueryRequest Request(Paging paging) => new(
            table,
            Predicate.AlwaysTrue.Instance,
            [new OrderTerm(status, OrderDirection.Ascending, NullOrder.First)],
            Projection.ColumnsOnly(status),
            paging,
            ResultShape.Rows.Instance,
            acceptedScan: ScanAcceptance.Allow(
                "GW-SCAN-DISTINCT-LOCALE",
                "distinct continuation test",
                "query-tests",
                new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            distinct: true);

        var first = fixture.Session.Query(Request(Paging.Keyset(2)));
        var second = await fixture.Executor.ToListAsync<string>(Request(Paging.Continuation(first.NextContinuationToken!, 2)));

        Assert.Equal(["Åke", "Äke"], second);
    }

    [Fact]
    public void Locale_only_hidden_indexes_cannot_admit_set_mutation_prefixes()
    {
        var logical = new StorageUnit
        {
            Id = new StorageUnitId("locale_set_mutation"),
            Name = "locale_set_mutation",
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, IsNullable = false, MaxLength = 32 },
                new()
                {
                    Name = "status",
                    Type = PortableType.String,
                    MaxLength = 32,
                    LocaleSortKey = new LocaleSortKeyDefinition
                    {
                        CultureName = "sv-SE",
                        MaximumExpansionFactor = 12
                    }
                }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Indexes = [new IndexDefinition { Name = "ix_status", Columns = [new IndexColumn("status")] }]
        };
        var unit = SearchKeyProjection.Expand(logical);
        var status = new ColumnRef(
            new TableId(unit.Name), "status", QueryType.String, true, 32);

        var refusal = Assert.Throws<QueryCoverageException>(() =>
            SetMutationAdmission.Admit(unit, new Predicate.StartsWith(status, "A")));

        Assert.Equal("GW-COVER-006", refusal.Code);
        Assert.DoesNotContain(
            StorageUnitCoverage.PortableIndexes(unit).SelectMany(index => index.Columns),
            column => column.Column == "status");

        var accepted = SetMutationAdmission.Admit(
            unit,
            new Predicate.StartsWith(status, "A"),
            new SetMutationOptions
            {
                AcceptedScan = ScanAcceptance.Allow(
                    "GW-SCAN-LOCALE",
                    "locale sort keys are not executable mutation predicates",
                    "query-team",
                    new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero))
            });
        Assert.IsType<Predicate.Range>(accepted);
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

    /// <summary>
    /// The unit declares an index on <c>status</c> only, so nothing but the declared key can admit
    /// a read filtered on <c>id</c>.
    /// </summary>
    [Fact]
    public async Task Declared_key_equality_is_admitted_without_a_declared_index()
    {
        using var fixture = Fixture.Open();

        var rows = await fixture.Table.Query
            .Where(ticket => ticket.Id == "a")
            .ToListAsync(fixture.Executor);

        Assert.Equal("a", Assert.Single(rows).Id);
    }

    /// <summary>
    /// The declared-versus-deployed intersection guards declared indexes, which a rolling deploy can
    /// be missing. The key is not one of them: the coordinator emits it as the PRIMARY KEY of the
    /// CREATE TABLE, so it exists exactly when the table does.
    /// </summary>
    [Fact]
    public async Task Declared_key_admits_a_query_even_when_the_catalog_reports_no_indexes()
    {
        using var fixture = Fixture.Open();
        var executor = new GwLinqExecutor(
            fixture.Session,
            new ProbeConnection(fixture.Connection, new EmptyCatalog()));

        var rows = await new GwQueryDatabase(executor).Table(Fixture.Model).Query
            .Where(ticket => ticket.Id == "a")
            .ToListAsync(executor);

        Assert.Equal("a", Assert.Single(rows).Id);
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

    [Fact]
    public void Set_mutation_refuses_a_declared_index_the_catalog_does_not_carry()
    {
        using var fixture = Fixture.Open();
        IStorageSession session = new PassThroughSession(
            fixture.Session,
            new ProbeConnection(fixture.Connection, new EmptyCatalog()));

        var refusal = Assert.Throws<QueryCoverageException>(() => session.DeleteWhere(
            StatusPredicate(Fixture.TableName, "open")));

        Assert.Equal("GW-COVER-006", refusal.Code);
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
    public async Task Supplying_a_known_budget_does_not_cost_callers_the_explicit_null_connection()
    {
        using var fixture = Fixture.Open();

        // That both calls below compile is half the assertion. Expressing "budgets known at compile
        // time" as a second two-argument constructor would have made the first one ambiguous with
        // the connection constructor: a null literal converts to both reference types and neither is
        // more specific, so a caller who already wrote it would stop compiling with CS0121.
        var explicitNull = new GwLinqExecutor(fixture.Session, null);
        var known = GwLinqExecutor.WithAdmission(
            fixture.Session,
            new QueryAdmissionProfile { MaximumParameters = 999 });

        // Neither has a connection to advertise a budget. The first falls back to the portable
        // default and the second uses the one it was handed, so the two refuse at different numbers.
        var fellBack = Assert.IsType<RuntimeValueFenceException>(
            await Record.ExceptionAsync(() => explicitNull.ToListAsync(OverBudgetRequest(), Fixture.Model)));
        var supplied = Assert.IsType<RuntimeValueFenceException>(
            await Record.ExceptionAsync(() => known.ToListAsync(OverBudgetRequest(), Fixture.Model)));

        Assert.Equal("GW-RUNTIME-011", fellBack.Code);
        Assert.Equal("GW-RUNTIME-011", supplied.Code);
        Assert.Contains(
            QueryAdmissionProfile.Default.MaximumParameters.ToString(CultureInfo.InvariantCulture),
            fellBack.Message,
            StringComparison.Ordinal);
        Assert.Contains("999", supplied.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_supplied_budget_is_required_rather_than_quietly_replaced_by_the_default()
    {
        using var fixture = Fixture.Open();
        Assert.Throws<ArgumentNullException>(() => GwLinqExecutor.WithAdmission(fixture.Session, null!));
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
    private sealed class PassThroughSession(
        IStorageSession inner,
        IStorageProviderConnection? providerConnection = null)
        : IStorageSession, ISetMutationStorageSession, IProviderBoundStorageSession
    {
        public StorageUnit Unit => inner.Unit;
        public StorageAccess Access => inner.Access;
        IStorageProviderConnection? IProviderBoundStorageSession.ProviderConnection => providerConnection;
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

        public SetMutationResult UpdateWhere(Predicate where, IReadOnlyDictionary<string, object?> assignments) =>
            new(0);

        public ValueTask<SetMutationResult> UpdateWhereAsync(
            Predicate where,
            IReadOnlyDictionary<string, object?> assignments,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new SetMutationResult(0));

        public SetMutationResult DeleteWhere(Predicate where) => new(0);

        public ValueTask<SetMutationResult> DeleteWhereAsync(
            Predicate where,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new SetMutationResult(0));
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

    private static Predicate StatusPredicate(string table, string value)
    {
        var status = new ColumnRef(new TableId(table), "status", QueryType.String, isNullable: true, maxLength: 32);
        return new Predicate.Equal(status, QueryConstant.Of(status, value));
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
        public IOwnedStorageSession OpenOwnedSession(StorageUnit unit, StorageAccess access, IProviderCommandObserver? observer = null) =>
            inner.OpenOwnedSession(unit, access, observer);
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

        internal static Fixture Open(bool localeOrder = false, bool withDuplicateStatus = false)
        {
            var connection = new InMemoryProviderFactory().Create("memory://linq-executor-" + Guid.NewGuid().ToString("N"));
            var unit = new StorageUnit
            {
                Id = new StorageUnitId(TableName),
                Name = TableName,
                Columns =
                [
                    new() { Name = "id", Type = PortableType.String, IsNullable = false, MaxLength = 32 },
                    new()
                    {
                        Name = "status",
                        Type = PortableType.String,
                        MaxLength = 32,
                        LocaleSortKey = localeOrder
                            ? new LocaleSortKeyDefinition
                            {
                                CultureName = "sv-SE",
                                MaximumExpansionFactor = 12
                            }
                            : null
                    },
                    new() { Name = "weight", Type = PortableType.Int32, IsNullable = false },
                    new() { Name = "optional", Type = PortableType.Int64 }
                ],
                Key = new KeyDefinition { Columns = ["id"] },
                Indexes =
                [
                    new() { Name = "ix_status", Columns = [new IndexColumn("status")] }
                ]
            };
            connection.Schema.Apply(unit);
            var session = connection.OpenSession(unit, StorageAccess.Global);
            var statuses = localeOrder
                ? new[] { "Ake", "Åke", "Äke", "Öke", "Zebra" }
                : withDuplicateStatus ? ["closed", "closed", "open"] : ["open"];
            for (var index = 0; index < statuses.Length; index++)
            {
                session.Insert(new StorageValues(new Dictionary<string, object?>
                {
                    ["id"] = ((char)('a' + index)).ToString(),
                    ["status"] = statuses[index],
                    ["weight"] = 7,
                    ["optional"] = null
                }));
            }
            return new Fixture(connection, session);
        }

        public void Dispose() => connection.Dispose();
    }
}
