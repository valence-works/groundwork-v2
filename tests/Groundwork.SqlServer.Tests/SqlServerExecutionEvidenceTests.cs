using System.Text.Json;
using Groundwork.Kernel;
using Groundwork.Query.Model;
using Groundwork.SqlServer;
using Groundwork.Store;
using Groundwork.Substrate.Relational;
using Xunit;

namespace Groundwork.SqlServer.Tests;

[Collection(SqlServerLiveDatabase.Name)]
public sealed class SqlServerExecutionEvidenceTests(SqlServerFixture fixture)
{
    [Fact]
    public void Structured_observer_cannot_reenter_the_same_provider_gate()
    {
        using var provider = new SqlServerProviderConnection("Server=unused;Database=unused");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            provider.InvokeStructuredObserver(() =>
            {
                using var lease = provider.EnterGate();
            }));

        Assert.Contains("structured execution observer", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Structured_observer_async_reentry_cannot_wait_on_the_same_provider_gate()
    {
        using var provider = new SqlServerProviderConnection("Server=unused;Database=unused");
        using var held = provider.EnterGate();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            provider.InvokeStructuredObserver(() =>
            {
                using var lease = provider.EnterGate(RelationalExecution.Asynchronous(cancellation.Token))
                    .GetAwaiter().GetResult();
            }));

        Assert.Contains("structured execution observer", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Structured_observer_callback_depth_is_restored_after_callback_failure()
    {
        using var provider = new SqlServerProviderConnection("Server=unused;Database=unused");

        Assert.Throws<InvalidOperationException>(() =>
            provider.InvokeStructuredObserver(() => throw new InvalidOperationException("callback failure")));

        using var lease = provider.EnterGate();
    }

    [Fact]
    public void Structured_observer_cannot_dispose_its_provider()
    {
        using var provider = new SqlServerProviderConnection("Server=unused;Database=unused");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            provider.InvokeStructuredObserver(provider.Dispose));

        Assert.Contains("structured execution observer", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Structured_observer_entrypoint_guard_precedes_disposed_check()
    {
        using var provider = new SqlServerProviderConnection("Server=unused;Database=unused");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            provider.InvokeStructuredObserver(provider.ThrowIfDisposed));

        Assert.Contains("structured execution observer", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Structured_observer_may_enter_a_different_provider()
    {
        using var provider = new SqlServerProviderConnection("Server=unused;Database=unused");
        using var other = new SqlServerProviderConnection("Server=unused;Database=unused");

        provider.InvokeStructuredObserver(() =>
        {
            using var lease = other.EnterGate();
        });
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Public_point_read_reports_evidence_for_found_and_missing_rows(bool found)
    {
        using var state = new Fixture(fixture);
        var result = state.Session.Read(Key(found ? Fixture.SecretKey : "missing-key"));

        Assert.Equal(found, result is not null);
        var evidence = Assert.Single(state.Observer.Executions);
        Assert.Equal(ProviderExecutionOperation.PointRead, evidence.Operation);
        Assert.Equal(ProviderExecutionOutcome.Succeeded, evidence.Outcome);
        Assert.Equal(ProviderEvidenceAvailability.Collected, evidence.ShapeAvailability);
        Assert.Equal(ProviderScopeBindingMode.Predicate, evidence.Target.ScopeBinding);
        Assert.Equal(state.Unit.Id, evidence.Target.LogicalUnitId);
        Assert.Equal("SQL Server", evidence.Provider.Name);

        var pointRead = Assert.IsType<ProviderPointReadEvidence>(evidence.PointRead);
        Assert.True(pointRead.IncludesScopeBinding);
        Assert.Equal("id", Assert.Single(pointRead.KeyBounds,
            bound => bound.BindingRole == ProviderPointReadBindingRole.Key).LogicalColumn);
        Assert.Equal(ProviderPointReadBindingRole.Scope,
            Assert.Single(pointRead.KeyBounds, bound => bound.BindingRole == ProviderPointReadBindingRole.Scope).BindingRole);
        Assert.Equal(ProviderNativeBoundKind.Absent, pointRead.NativeLimit.Kind);
        // #423: the primary key on (scope, id) is the catalog's uniqueness witness for the scoped point read.
        Assert.Equal(ProviderPointReadUniquenessStatus.Observed, pointRead.Uniqueness.Status);
        Assert.Equal(new[] { "id" }, pointRead.Uniqueness.EnforcedKeyColumns.ToArray());
        Assert.True(pointRead.Uniqueness.IncludesScopeBinding);
        Assert.True(pointRead.MaterializerReadsAtMostOne);
        Assert.Equal(ProviderPointReadLockMode.None, pointRead.LockMode);
        Assert.Equal(ProviderEvidenceAvailability.NotRequested, evidence.Plan.Availability);

        var serialized = JsonSerializer.Serialize(evidence);
        Assert.DoesNotContain(Fixture.SecretScope, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(Fixture.SecretKey, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(Fixture.SecretPayload, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(state.ConnectionString, serialized, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Public_point_read_preserves_trailing_space_payload_and_plan_opt_in_maps_the_key_search()
    {
        using var state = new Fixture(fixture);
        state.Observer.EvidenceOptions = ProviderExecutionEvidenceOptions.ShapeAndPlans;

        var result = state.Session.Read(Key(Fixture.SecretKey));

        Assert.Equal(Fixture.SecretPayload, result!.Values.Values["payload"]);
        var evidence = Assert.Single(state.Observer.Executions);
        // #423: the point read's plan comes through the same explain seam as bounded queries.
        Assert.Equal(ProviderEvidenceAvailability.Collected, evidence.Plan.Availability);
        var access = Assert.Single(evidence.Plan.WinningPlan!.Nodes, node => node.TargetId is not null);
        Assert.Contains(access.Operation, new[] { ProviderPlanOperator.PrimaryKeySearch, ProviderPlanOperator.IndexSearch });
        Assert.Equal(ProviderPointReadUniquenessStatus.Observed, evidence.PointRead!.Uniqueness.Status);
        Assert.Single(state.Observer.Commands);
    }

    [SkippableFact]
    public void Point_read_and_query_evidence_share_the_session_capture()
    {
        using var state = new Fixture(fixture);
        state.Session.Read(Key(Fixture.SecretKey));
        state.Session.Query(Query(state.Unit));

        var point = state.Observer.Executions[0];
        var query = state.Observer.Executions[1];
        Assert.Equal(ProviderExecutionOperation.PointRead, point.Operation);
        Assert.Equal(ProviderExecutionOperation.BoundedQuery, query.Operation);
        Assert.Equal(point.Identity.CaptureId, query.Identity.CaptureId);
        Assert.Equal(point.Target.PhysicalTargetId, query.Target.PhysicalTargetId);
        Assert.NotEqual(point.Identity.InvocationId, query.Identity.InvocationId);
        Assert.NotEqual(point.Identity.CommandId, query.Identity.CommandId);
        Assert.NotEqual(point.Identity.StatementId, query.Identity.StatementId);
    }

    private static StorageKey Key(string value) => new(new Dictionary<string, object?>
    {
        ["id"] = value
    });

    private static QueryRequest Query(StorageUnit unit)
    {
        var table = new TableId(unit.Name);
        var id = new ColumnRef(table, "id", QueryType.String, isNullable: false);
        var payload = new ColumnRef(table, "payload", QueryType.String, isNullable: false);
        return new QueryRequest(
            table,
            Predicate.AlwaysTrue.Instance,
            [new OrderTerm(id, OrderDirection.Ascending, NullOrder.First)],
            Projection.ColumnsOnly(id, payload),
            Paging.Keyset(2));
    }

    private sealed class Fixture : IDisposable
    {
        internal const string SecretScope = "scope-value-must-never-enter-evidence";
        internal const string SecretKey = "key-value-must-never-enter-evidence";
        internal const string SecretPayload = "payload-value-must-survive-trailing-space ";

        private readonly IStorageProviderConnection connection;

        internal Fixture(SqlServerFixture database)
        {
            ConnectionString = database.Reset();
            connection = new SqlServerProviderFactory().Create(ConnectionString);
            var name = "w2_sqlserver_point_evidence_" + Guid.NewGuid().ToString("N");
            Unit = StorageUnit.Declare(name, name)
                .String("id", 128, column => column.Required())
                .String("payload", 128, column => column.Required())
                .Key("id")
                .Scoped()
                .Build();

            try
            {
                Assert.True(connection.Schema.Apply(Unit).Applied);
                Session = connection.OpenSession(
                    Unit,
                    StorageAccess.Scoped(new StorageScope(SecretScope)),
                    Observer);
                Assert.Equal(WriteOutcomeStatus.Inserted, Session.Insert(new StorageValues(
                    new Dictionary<string, object?>
                    {
                        ["id"] = SecretKey,
                        ["payload"] = SecretPayload
                    })).Status);
                Observer.Clear();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal string ConnectionString { get; }
        internal StorageUnit Unit { get; }
        internal IStorageSession Session { get; }
        internal EvidenceObserver Observer { get; } = new();

        public void Dispose() => connection.Dispose();
    }

    private sealed class EvidenceObserver : IProviderExecutionObserver
    {
        public ProviderExecutionEvidenceOptions EvidenceOptions { get; set; } = ProviderExecutionEvidenceOptions.ShapeOnly;
        internal List<ProviderCommandEvent> Commands { get; } = [];
        internal List<ProviderExecutionEvidence> Executions { get; } = [];

        public void Observe(ProviderCommandEvent command) => Commands.Add(command);

        public void ObserveExecution(ProviderExecutionEvidence evidence) => Executions.Add(evidence);

        internal void Clear()
        {
            Commands.Clear();
            Executions.Clear();
        }
    }
}
