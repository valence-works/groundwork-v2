using System.Data;
using System.Data.Common;
using Groundwork.Kernel;
using Groundwork.Query.Model;
using Groundwork.Sqlite;
using Groundwork.Store;
using Groundwork.Substrate.Relational;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Groundwork.Sqlite.Tests;

public sealed class SqliteExecutionEvidenceLifecycleTests
{
    [Fact]
    public void Plan_collector_failure_preserves_original_exception_and_reports_a_successful_read_with_failed_plan()
    {
        using var fixture = new LifecycleFixture();
        var collectorException = new InvalidOperationException("plan collector failure");
        var observerException = new InvalidOperationException("terminal observer failure");
        fixture.Observer.TerminalException = observerException;
        var requested = false;
        var queries = fixture.CreateQueries((_, _, _, collectEvidence) =>
        {
            requested = collectEvidence;
            throw collectorException;
        });

        var actual = Assert.Throws<InvalidOperationException>(() =>
            queries.Query(fixture.Request, fixture.Options, transaction: null, RelationalExecution.Synchronous)
                .GetAwaiter().GetResult());

        Assert.Same(collectorException, actual);
        Assert.True(requested);
        var evidence = Assert.Single(fixture.Observer.Executions);
        Assert.Equal(ProviderExecutionOutcome.Succeeded, evidence.Outcome);
        Assert.Equal(ProviderEvidenceAvailability.Collected, evidence.ShapeAvailability);
        Assert.Equal(ProviderEvidenceAvailability.Failed, evidence.Plan.Availability);
        Assert.Equal(ProviderExecutionFailureCategory.PlanCollection, evidence.Plan.FailureCategory);
        Assert.Equal(["issued", "terminal"], fixture.Observer.CallbackOrder);
        Assert.Single(fixture.Observer.Commands);
        Assert.True(fixture.Observer.ReaderWasDisposed);
    }

    [Fact]
    public void Deferred_plan_assertion_failure_preserves_collected_plan_and_reports_a_successful_read()
    {
        using var fixture = new LifecycleFixture();
        var assertionException = new InvalidOperationException("deferred plan assertion failure");
        var observerException = new InvalidOperationException("terminal observer failure");
        fixture.Observer.TerminalException = observerException;
        var requested = false;
        var plan = new ProviderPlanEvidence(
            ProviderEvidenceAvailability.Collected,
            ProviderPlanProvenance.EstimatedExplain,
            choseExpectedIndex: false,
            expectedLogicalIndex: "collector-index",
            collectionCommandCount: 1);
        var queries = fixture.CreateQueries((_, _, _, collectEvidence) =>
        {
            requested = collectEvidence;
            return ValueTask.FromResult(new RelationalQueryPlanInspection(
                plan,
                () => throw assertionException));
        });

        var actual = Assert.Throws<InvalidOperationException>(() =>
            queries.Query(fixture.Request, fixture.Options, transaction: null, RelationalExecution.Synchronous)
                .GetAwaiter().GetResult());

        Assert.Same(assertionException, actual);
        Assert.True(requested);
        var evidence = Assert.Single(fixture.Observer.Executions);
        Assert.Equal(ProviderExecutionOutcome.Succeeded, evidence.Outcome);
        Assert.Equal(ProviderEvidenceAvailability.Collected, evidence.ShapeAvailability);
        Assert.Same(plan, evidence.Plan);
        Assert.Equal(ProviderPlanProvenance.EstimatedExplain, evidence.Plan.Provenance);
        Assert.Equal(1, evidence.Plan.CollectionCommandCount);
        Assert.Equal(["issued", "terminal"], fixture.Observer.CallbackOrder);
        Assert.Single(fixture.Observer.Commands);
        Assert.True(fixture.Observer.ReaderWasDisposed);
    }

    private sealed class LifecycleFixture : IDisposable
    {
        private const string TableName = "execution_evidence_lifecycle";
        private readonly SqliteConnection sqlite;

        internal LifecycleFixture()
        {
            Unit = StorageUnit.Declare("execution-evidence-lifecycle", TableName)
                .Int32("id", column => column.Required())
                .Int32("value", column => column.Required())
                .Key("id")
                .Build();

            sqlite = new SqliteConnection("Data Source=:memory:");
            sqlite.Open();
            using var create = sqlite.CreateCommand();
            create.CommandText =
                "CREATE TABLE \"execution_evidence_lifecycle\" (\"id\" INTEGER NOT NULL PRIMARY KEY, \"value\" INTEGER NOT NULL);";
            create.ExecuteNonQuery();
            create.CommandText =
                "INSERT INTO \"execution_evidence_lifecycle\" (\"id\", \"value\") VALUES (1, 7);";
            create.ExecuteNonQuery();

            Connection = new TrackingConnection(sqlite);
            Observer = new LifecycleObserver(() => Connection.LastReader?.IsClosed == true);
            var table = new TableId(Unit.Name);
            var id = new ColumnRef(table, "id", QueryType.Int32, isNullable: false);
            var value = new ColumnRef(table, "value", QueryType.Int32, isNullable: false);
            Request = new QueryRequest(
                table,
                new Predicate.Equal(value, QueryConstant.Of(value, 7)),
                [new OrderTerm(id, OrderDirection.Ascending, NullOrder.First)],
                Projection.ColumnsOnly(id, value),
                Paging.Keyset(2));
            Options = Unit.CreateQueryRenderOptions();
        }

        internal StorageUnit Unit { get; }
        internal TrackingConnection Connection { get; }
        internal LifecycleObserver Observer { get; }
        internal QueryRequest Request { get; }
        internal QueryRenderOptions Options { get; }

        internal RelationalSessionQueries CreateQueries(RelationalQueryPlanCollector collector) =>
            new(
                Unit,
                StorageAccess.Global,
                Connection,
                new SqliteQueryRenderer(),
                () => new Dictionary<string, string>(),
                (value, column) => new SqliteDialect().ReadValue(value, column),
                (_, _, _) => default,
                Observer,
                "sqlite.lifecycle",
                new RelationalEvidenceCapture(),
                collector);

        public void Dispose()
        {
            Connection.Dispose();
        }
    }

    private sealed class LifecycleObserver(Func<bool> readerWasDisposed) : IProviderExecutionObserver
    {
        public ProviderExecutionEvidenceOptions EvidenceOptions { get; } = ProviderExecutionEvidenceOptions.ShapeAndPlans;
        internal InvalidOperationException? TerminalException { get; set; }
        internal bool ReaderWasDisposed { get; private set; }
        internal List<ProviderCommandEvent> Commands { get; } = [];
        internal List<ProviderExecutionEvidence> Executions { get; } = [];
        internal List<string> CallbackOrder { get; } = [];

        public void Observe(ProviderCommandEvent command)
        {
            Commands.Add(command);
            CallbackOrder.Add("issued");
        }

        public void ObserveExecution(ProviderExecutionEvidence evidence)
        {
            Executions.Add(evidence);
            CallbackOrder.Add("terminal");
            ReaderWasDisposed = readerWasDisposed();
            if (TerminalException is not null)
                throw TerminalException;
        }
    }

    private sealed class TrackingConnection(SqliteConnection inner) : DbConnection
    {
        internal DbDataReader? LastReader { get; private set; }
        internal SqliteConnection NativeConnection => inner;

#pragma warning disable CS8765
        public override string ConnectionString
        {
            get => inner.ConnectionString;
            set => inner.ConnectionString = value;
        }
#pragma warning restore CS8765
        public override string Database => inner.Database;
        public override string DataSource => inner.DataSource;
        public override string ServerVersion => inner.ServerVersion;
        public override ConnectionState State => inner.State;
        public override void ChangeDatabase(string databaseName) => inner.ChangeDatabase(databaseName);
        public override void Open() => inner.Open();
        public override void Close() => inner.Close();
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            inner.BeginTransaction(isolationLevel);
        protected override DbCommand CreateDbCommand() => new TrackingCommand(this, inner.CreateCommand());

        internal DbDataReader Track(DbDataReader reader) => LastReader = reader;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class TrackingCommand(TrackingConnection owner, DbCommand inner) : DbCommand
    {
#pragma warning disable CS8765
        public override string CommandText
        {
            get => inner.CommandText;
            set => inner.CommandText = value;
        }
#pragma warning restore CS8765
        public override int CommandTimeout
        {
            get => inner.CommandTimeout;
            set => inner.CommandTimeout = value;
        }
        public override CommandType CommandType
        {
            get => inner.CommandType;
            set => inner.CommandType = value;
        }
        public override bool DesignTimeVisible
        {
            get => inner.DesignTimeVisible;
            set => inner.DesignTimeVisible = value;
        }
        public override UpdateRowSource UpdatedRowSource
        {
            get => inner.UpdatedRowSource;
            set => inner.UpdatedRowSource = value;
        }
        protected override DbConnection? DbConnection
        {
            get => owner;
            set => inner.Connection = value is TrackingConnection tracked ? tracked.NativeConnection : value;
        }
        protected override DbParameterCollection DbParameterCollection => inner.Parameters;
        protected override DbTransaction? DbTransaction
        {
            get => inner.Transaction;
            set => inner.Transaction = value;
        }
        public override void Cancel() => inner.Cancel();
        public override int ExecuteNonQuery() => inner.ExecuteNonQuery();
        public override object? ExecuteScalar() => inner.ExecuteScalar();
        public override void Prepare() => inner.Prepare();
        protected override DbParameter CreateDbParameter() => inner.CreateParameter();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
            owner.Track(inner.ExecuteReader(behavior));

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }

}
