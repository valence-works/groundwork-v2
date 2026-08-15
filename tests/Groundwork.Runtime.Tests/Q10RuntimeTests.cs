using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Query.Model;
using Groundwork.Query.Planning;
using Xunit;

namespace Groundwork.Runtime.Tests;

public sealed class Q10RuntimeTests
{
    private static readonly TableId Table = new("tickets");
    private static readonly ColumnRef Status = new(Table, "status", QueryType.String);
    private static readonly ColumnRef Assignee = new(Table, "assignee", QueryType.String);

    [Fact]
    public void Column_drift_is_startup_fatal_and_names_the_column()
    {
        var target = Target([SchemaIndex("ix_status", "status")]);
        var history = ApplyTarget(target);
        var inspection = PhysicalSchemaInspection.Compare(
            history,
            target,
            new PhysicalSchemaSnapshot(
                new StorageUnitId("tickets"),
                "tickets",
                [new PhysicalSchemaColumn("assignee", "String", true)],
                [new PhysicalSchemaIndex("ix_status", [new IndexColumn("status")], false)]));
        var result = new GroundworkRuntimeSchemaAdmissionResult(
            inspection,
            PhysicalSchemaDiffPlanner.Plan(target, history, DateTimeOffset.UnixEpoch));

        Assert.False(result.IsReady);
        Assert.Contains("status", result.Refusals.Single().Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Index_drift_does_not_fail_the_process()
    {
        var target = Target([SchemaIndex("ix_status", "status")]);
        var history = ApplyTarget(target);
        var inspection = PhysicalSchemaInspection.Compare(
            history,
            target,
            new PhysicalSchemaSnapshot(
                new StorageUnitId("tickets"),
                "tickets",
                [
                    new PhysicalSchemaColumn("status", "String", true),
                    new PhysicalSchemaColumn("assignee", "String", true)
                ],
                []));
        var result = new GroundworkRuntimeSchemaAdmissionResult(
            inspection,
            PhysicalSchemaDiffPlanner.Plan(target, history, DateTimeOffset.UnixEpoch));

        Assert.True(inspection.IsAppliedSchemaValid);
        Assert.True(inspection.HasIndexDrift);
        Assert.True(result.IsReady);
        Assert.Contains("ix_status", result.Refusals.Single().Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Legacy_two_argument_inspection_remains_safe_to_enumerate()
    {
        var target = Target([]);
        var inspection = new PhysicalSchemaInspectionResult(
            PhysicalSchemaHistoryState.Empty,
            IsAppliedSchemaValid: true);
        var result = new GroundworkRuntimeSchemaAdmissionResult(
            inspection,
            PhysicalSchemaDiffPlanner.Plan(target, PhysicalSchemaHistoryState.Empty, DateTimeOffset.UnixEpoch));

        Assert.Empty(result.Refusals);
    }

    [Fact]
    public void Physical_schema_compare_classifies_collation_search_key_and_index_shape_drift()
    {
        var target = new PhysicalSchemaTarget(
            new SchemaSubject(new StorageUnit
            {
                Id = new StorageUnitId("search"),
                Name = "search",
                Columns = [new ColumnDefinition
                {
                    Name = "status",
                    Type = PortableType.String,
                    Collation = PortableCollation.OrdinalIgnoreCase
                }, new ColumnDefinition { Name = "status_folded", Type = PortableType.String, IsNullable = false }],
                DerivedColumns = [new DerivedColumnDefinition
                {
                    Name = "status_folded",
                    SourceColumn = "status",
                    Projection = PortableProjection.UnicodeFold
                }],
                Key = new KeyDefinition { Columns = ["status"] },
                Indexes = [new IndexDefinition
                {
                    Name = "ix_status",
                    Columns = [new IndexColumn("status", SortDirection.Ascending)],
                    IsUnique = true,
                    MissingValues = MissingValueBehavior.Excluded
                }]
            }),
            new ProviderIdentity("fake", "1"));
        var inspection = PhysicalSchemaInspection.Compare(
            PhysicalSchemaHistoryState.Empty,
            target,
            new PhysicalSchemaSnapshot(
                new StorageUnitId("search"),
                "search",
                [
                    new PhysicalSchemaColumn("status", "String", true, "Ordinal"),
                    new PhysicalSchemaColumn("status_folded", "String", false, SearchKeyAlgorithmId: "old-fold-v1")
                ],
                [new PhysicalSchemaIndex("ix_status", [new IndexColumn("status", SortDirection.Descending)], false)]));

        Assert.Contains(inspection.ColumnDrift, refusal => refusal.Message.Contains("collation", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(inspection.ColumnDrift, refusal => refusal.Message.Contains("search-key", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(inspection.ColumnDrift, refusal =>
            refusal.Message.Contains(PortableStringComparison.UnicodeOrdinalIgnoreCaseAlgorithmId, StringComparison.Ordinal));
        Assert.Contains(inspection.IndexDrift, refusal => refusal.Message.Contains("index", StringComparison.OrdinalIgnoreCase));
        Assert.False(inspection.IsAppliedSchemaValid);
    }

    [Fact]
    public void Declared_indexes_are_intersected_with_deployed_indexes()
    {
        var request = Request(new Predicate.Equal(Status, QueryConstant.Of(Status, "open")));
        var gate = new RuntimeCoverageGate(
            [Index("ix_status", "status")],
            [Index("ix_extra", "status")]);

        var verdict = gate.Check(request);

        Assert.False(verdict.Coverage.IsCovered);
        Assert.Equal("GW-COVER-006", verdict.Coverage.Refusal!.Code);
    }

    [Fact]
    public void An_extra_deployed_index_cannot_rescue_an_uncovered_shape()
    {
        var request = Request(new Predicate.Equal(Status, QueryConstant.Of(Status, "open")));
        var gate = new RuntimeCoverageGate([], [Index("ix_status", "status")]);

        Assert.False(gate.Check(request).Coverage.IsCovered);
    }

    [Fact]
    public void Recognized_and_unrecognized_shapes_use_the_same_checker_verdict()
    {
        var request = Request(new Predicate.Equal(Status, QueryConstant.Of(Status, "open")));
        var index = Index("ix_status", "status");
        var recognized = new RuntimeCoverageGate(
            [index],
            [index],
            [RuntimeVerifiedShape.Covered(request, index)]);
        var unrecognized = new RuntimeCoverageGate([index], [index]);

        var recognizedVerdict = recognized.Check(request);
        var unrecognizedVerdict = unrecognized.Check(request);

        Assert.True(recognizedVerdict.IsRecognized);
        Assert.False(unrecognizedVerdict.IsRecognized);
        Assert.True(recognizedVerdict.Coverage.IsCovered);
        Assert.Equal(recognizedVerdict.Coverage.Decision, unrecognizedVerdict.Coverage.Decision);
    }

    [Fact]
    public void Rolling_index_drift_refuses_only_the_endpoint_that_needs_the_missing_index()
    {
        var statusIndex = Index("ix_status", "status");
        var assigneeIndex = Index("ix_assignee", "assignee");
        var statusRequest = Request(new Predicate.Equal(Status, QueryConstant.Of(Status, "open")));
        var assigneeRequest = Request(new Predicate.Equal(Assignee, QueryConstant.Of(Assignee, "alice")));
        var gate = new RuntimeCoverageGate(
            [statusIndex, assigneeIndex],
            [statusIndex],
            [
                RuntimeVerifiedShape.Covered(statusRequest, statusIndex),
                RuntimeVerifiedShape.Covered(assigneeRequest, assigneeIndex)
            ]);

        gate.EnsureCovered(statusRequest, DateTimeOffset.UtcNow);
        var exception = Assert.Throws<QueryCoverageException>(() =>
            gate.EnsureCovered(assigneeRequest, DateTimeOffset.UtcNow));

        Assert.Equal("GW-COVER-006", exception.Code);
        Assert.Contains("assignee", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Shape_cache_is_bounded_and_emits_an_eviction_metric()
    {
        var metrics = new List<RuntimeCoverageMetric>();
        var gate = new RuntimeCoverageGate(
            [],
            [],
            options: new RuntimeCoverageGateOptions { MaximumCachedShapes = 1 },
            metric: metrics.Add);

        gate.Check(Request(new Predicate.Equal(Status, QueryConstant.Of(Status, "open"))));
        gate.Check(Request(new Predicate.Equal(Assignee, QueryConstant.Of(Assignee, "alice"))));

        Assert.Equal(1, gate.CachedShapeCount);
        Assert.Contains(metrics, metric => metric.Name == "groundwork.runtime.coverage.cache.eviction");
    }

    [Fact]
    public void Runtime_value_fence_rejects_excess_membership_and_parameters()
    {
        var values = Enumerable.Range(0, 3)
            .Select(value => QueryConstant.Of(Status, "value-" + value))
            .ToArray();
        var request = Request(new Predicate.In(Status, values));

        var exception = Assert.Throws<RuntimeValueFenceException>(() =>
            RuntimeValueFence.Validate(request, new RuntimeValueFenceOptions
            {
                MaximumInValues = 2,
                MaximumParameters = 2
            }));

        Assert.Equal("GW-RUNTIME-010", exception.Code);
        Assert.Contains("In", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Runtime_value_construction_keeps_length_precision_scale_and_unicode_fences()
    {
        var shortText = new ColumnRef(Table, "status", QueryType.String, maxLength: 3);
        var decimalColumn = new ColumnRef(Table, "amount", QueryType.Decimal, decimalPrecision: 5, decimalScale: 2);

        Assert.Throws<ArgumentException>(() => QueryConstant.Of(shortText, "toolong"));
        Assert.Throws<ArgumentException>(() => QueryConstant.Of(decimalColumn, 123.456m));
        Assert.Throws<ArgumentException>(() => new Predicate.StartsWith(Status, "\ud800"));
    }

    [Fact]
    public void Runtime_value_fence_rejects_parameter_budget_and_continuation_plan_drift()
    {
        var parameters = Request(new Predicate.And([
            new Predicate.Equal(Status, QueryConstant.Of(Status, "open")),
            new Predicate.Equal(Assignee, QueryConstant.Of(Assignee, "alice"))]));
        var parameterException = Assert.Throws<RuntimeValueFenceException>(() =>
            RuntimeValueFence.Validate(parameters, new RuntimeValueFenceOptions { MaximumParameters = 1 }));
        Assert.Equal("GW-RUNTIME-011", parameterException.Code);

        var original = new QueryRequest(
            Table,
            Predicate.AlwaysTrue.Instance,
            [new OrderTerm(Status, OrderDirection.Ascending, NullOrder.First)],
            Projection.All,
            Paging.Continuation("cursor"));
        var binding = RuntimeContinuationBinding.Create(original);
        var changed = new QueryRequest(
            Table,
            Predicate.AlwaysTrue.Instance,
            [new OrderTerm(Assignee, OrderDirection.Ascending, NullOrder.First)],
            Projection.All,
            Paging.Continuation("cursor"));

        var continuationException = Assert.Throws<RuntimeValueFenceException>(() =>
            RuntimeValueFence.Validate(changed, new RuntimeValueFenceOptions { ContinuationBinding = binding }));
        Assert.Equal("GW-RUNTIME-012", continuationException.Code);
    }

    private static QueryRequest Request(Predicate predicate) =>
        new(Table, predicate, [], Projection.All, Paging.None);

    private static CoverageIndex Index(string name, params string[] columns) =>
        new(name, columns);

    private static IndexDefinition SchemaIndex(string name, params string[] columns) =>
        new()
        {
            Name = name,
            Columns = columns.Select(column => new IndexColumn(column)).ToArray()
        };

    private static PhysicalSchemaTarget Target(IReadOnlyList<IndexDefinition> indexes) =>
        new(
            new SchemaSubject(new StorageUnit
            {
                Id = new StorageUnitId("tickets"),
                Name = "tickets",
                Columns =
                [
                    new ColumnDefinition { Name = "status", Type = PortableType.String },
                    new ColumnDefinition { Name = "assignee", Type = PortableType.String }
                ],
                Key = new KeyDefinition { Columns = ["status"] },
                Indexes = indexes
            }),
            new ProviderIdentity("fake", "1"));

    private static PhysicalSchemaHistoryState ApplyTarget(PhysicalSchemaTarget target)
    {
        var executor = new FakeSchemaExecutor();
        var result = PhysicalSchemaApplication.Apply(target, executor, DateTimeOffset.UnixEpoch);
        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, result.Outcome);
        return executor.History;
    }

    private sealed class FakeSchemaExecutor : IPhysicalSchemaExecutor
    {
        public PhysicalSchemaHistoryState History { get; private set; } = PhysicalSchemaHistoryState.Empty;

        public IPhysicalSchemaApplicationLock AcquireApplicationLock(PhysicalSchemaTargetIdentity target) => new FakeLock(target);

        public PhysicalSchemaHistoryState ReadHistory(PhysicalSchemaTargetIdentity target, IPhysicalSchemaApplicationLock applicationLock) => History;

        public PhysicalSchemaOperationAcknowledgement ApplyOperation(
            PhysicalSchemaTargetIdentity target,
            PhysicalSchemaOperation operation,
            IPhysicalSchemaApplicationLock applicationLock) =>
            new(operation.Identity, operation.Fingerprint, DateTimeOffset.UnixEpoch);

        public void PublishAppliedState(
            PhysicalSchemaAppliedState state,
            string? expectedAppliedTargetFingerprint,
            IPhysicalSchemaApplicationLock applicationLock) =>
            History = PhysicalSchemaHistoryState.FromApplied(state);

        private sealed class FakeLock(PhysicalSchemaTargetIdentity target) : IPhysicalSchemaApplicationLock
        {
            public PhysicalSchemaTargetIdentity Target { get; } = target;

            public void Dispose()
            {
            }
        }
    }
}
