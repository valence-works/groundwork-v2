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

}
