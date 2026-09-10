using System.Collections.Immutable;
using Groundwork.Kernel;
using Groundwork.MongoDb;
using Groundwork.Query.Model;
using Xunit;

namespace Groundwork.MongoDb.Tests;

public sealed class MongoExecutionEvidenceTests
{
    [Fact]
    public void Renderer_pairs_a_bounded_native_page_with_value_free_shape_facts()
    {
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("mongo-evidence-renderer"),
            Name = "mongo_evidence_renderer",
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, IsNullable = false },
                new() { Name = "status", Type = PortableType.String, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] }
        };
        var id = new ColumnRef(new TableId(unit.Name), "id", QueryType.String, isNullable: false);
        var status = new ColumnRef(new TableId(unit.Name), "status", QueryType.String, isNullable: false);
        var request = new QueryRequest(
            new TableId(unit.Name),
            new Predicate.And(
            [
                new Predicate.Equal(status, QueryConstant.Of(status, "secret-status")),
                new Predicate.Equal(id, QueryConstant.Of(id, "secret-id"))
            ]),
            [new OrderTerm(status, OrderDirection.Ascending, NullOrder.First)],
            Projection.ColumnsOnly(id, status),
            Paging.OffsetLimit(2, 3));
        var observer = new RecordingEvidenceObserver();
        var capture = new MongoExecutionEvidenceCapture(observer, unit, MongoStorageAccess.Global, TestServerVersion);

        var emission = new MongoQueryRenderer().RenderWithEvidence(
            request,
            QueryRenderOptions.Default,
            "physical_collection",
            capture,
            hasLookahead: true);

        var structuredShape = emission.StructuredShape;
        Assert.NotNull(structuredShape);
        var shape = structuredShape!;
        Assert.Equal(2, shape.NativeOffset.Value);
        Assert.Equal(3, shape.NativeLimit.Value);
        Assert.True(shape.HasLookahead);
        Assert.Equal(new[] { "id", "status" }, shape.Projection.LogicalColumns.ToArray());
        Assert.Equal(2, shape.Predicate.Facts.Length);
        Assert.All(shape.Predicate.Facts, fact =>
        {
            Assert.Equal(ProviderPredicateBindingRole.Caller, fact.BindingRole);
            Assert.DoesNotContain("secret", fact.BindingId.ToString(), StringComparison.OrdinalIgnoreCase);
        });
        Assert.Equal(ProviderEvidenceAvailability.NotRequested,
            new ProviderPlanEvidence(ProviderEvidenceAvailability.NotRequested).Availability);

        capture.Publish(
            capture.BeginInvocation(),
            0,
            ProviderExecutionOperation.BoundedQuery,
            ProviderExecutionRole.Statement,
            ProviderCommandKind.Read,
            ProviderExecutionOutcome.Succeeded,
            failureCategory: null,
            boundedQuery: shape,
            pointRead: null);
        var observed = Assert.Single(observer.Observed);
        Assert.Equal(ProviderEvidenceAvailability.Collected, observed.ShapeAvailability);
        Assert.DoesNotContain("secret-status", observed.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-id", observed.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Point_read_shape_uses_native_limit_without_claiming_uniqueness()
    {
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("mongo-evidence-point"),
            Name = "mongo_evidence_point",
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] }
        };
        var observer = new RecordingEvidenceObserver();
        var capture = new MongoExecutionEvidenceCapture(observer, unit, MongoStorageAccess.Scoped(new StorageScope("tenant-secret")), TestServerVersion);

        var point = MongoExecutionEvidenceBuilder.CreatePointRead(unit, capture);

        Assert.Equal(ProviderNativeBoundKind.Explicit, point.NativeLimit.Kind);
        Assert.Equal(1, point.NativeLimit.Value);
        Assert.Equal(ProviderPointReadUniquenessStatus.NotObserved, point.Uniqueness.Status);
        Assert.True(point.MaterializerReadsAtMostOne);
        Assert.Equal(ProviderScopeBindingMode.PhysicalTarget,
            capture.Target.ScopeBinding);
        Assert.DoesNotContain("tenant-secret", capture.Target.PhysicalTargetId.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Renderer_with_no_native_limit_withdraws_the_shape()
    {
        var unit = CreateSimpleUnit("mongo-evidence-unbounded");
        var id = new ColumnRef(new TableId(unit.Name), "id", QueryType.String, isNullable: false);
        var request = new QueryRequest(
            new TableId(unit.Name),
            new Predicate.Equal(id, QueryConstant.Of(id, "value-sentinel")),
            [],
            Projection.All,
            Paging.None);
        var capture = new MongoExecutionEvidenceCapture(new RecordingEvidenceObserver(), unit, MongoStorageAccess.Global, TestServerVersion);

        var emission = new MongoQueryRenderer().RenderWithEvidence(request, QueryRenderOptions.Default, "physical", capture, false);

        Assert.Null(emission.StructuredShape);
    }

    [Fact]
    public void Renderer_without_order_does_not_retain_order_facts()
    {
        var unit = CreateSimpleUnit("mongo-evidence-orderless");
        var id = new ColumnRef(new TableId(unit.Name), "id", QueryType.String, isNullable: false);
        var request = new QueryRequest(
            new TableId(unit.Name),
            new Predicate.Equal(id, QueryConstant.Of(id, "value-sentinel")),
            [],
            Projection.All,
            Paging.OffsetLimit(0, 2));
        var capture = new MongoExecutionEvidenceCapture(new RecordingEvidenceObserver(), unit, MongoStorageAccess.Global, TestServerVersion);

        var emission = new MongoQueryRenderer().RenderWithEvidence(request, QueryRenderOptions.Default, "physical", capture, false);

        Assert.NotNull(emission.StructuredShape);
        Assert.Empty(emission.StructuredShape!.Ordering);
    }

    [Fact]
    public void Renderer_does_not_claim_null_order_when_selected_index_proves_non_null()
    {
        var unit = CreateSimpleUnit("mongo-evidence-null-order");
        var id = new ColumnRef(new TableId(unit.Name), "id", QueryType.String, isNullable: false);
        var index = new QueryIndexDeclaration(
            "id_index",
            [new QueryIndexColumn("id", isNullable: false, type: QueryType.String)]);
        var request = new QueryRequest(
            new TableId(unit.Name),
            new Predicate.Equal(id, QueryConstant.Of(id, "value-sentinel")),
            [new OrderTerm(id, OrderDirection.Ascending, NullOrder.Last)],
            Projection.All,
            Paging.OffsetLimit(0, 2));
        var options = new QueryRenderOptions([index], selectedIndex: "id_index");
        var capture = new MongoExecutionEvidenceCapture(new RecordingEvidenceObserver(), unit, MongoStorageAccess.Global, TestServerVersion);

        var emission = new MongoQueryRenderer().RenderWithEvidence(request, options, "physical", capture, false);

        Assert.NotNull(emission.StructuredShape);
        var term = Assert.Single(emission.StructuredShape!.Ordering);
        Assert.Null(term.NullPlacement);
        Assert.DoesNotContain(ProviderOrderingTransform.NullRank, term.Transforms);
    }

    [Fact]
    public void Renderer_keeps_all_columns_when_native_pipeline_adds_internal_order_fields()
    {
        var unit = CreateSimpleUnit("mongo-evidence-all-columns");
        var id = new ColumnRef(new TableId(unit.Name), "id", QueryType.String, isNullable: false);
        var request = new QueryRequest(
            new TableId(unit.Name),
            new Predicate.Equal(id, QueryConstant.Of(id, "value-sentinel")),
            [new OrderTerm(id, OrderDirection.Ascending, NullOrder.First)],
            Projection.All,
            Paging.OffsetLimit(0, 2));
        var capture = new MongoExecutionEvidenceCapture(new RecordingEvidenceObserver(), unit, MongoStorageAccess.Global, TestServerVersion);

        var emission = new MongoQueryRenderer().RenderWithEvidence(request, QueryRenderOptions.Default, "physical", capture, false);

        Assert.NotNull(emission.StructuredShape);
        Assert.True(emission.StructuredShape!.Projection.AllColumns);
        Assert.Empty(emission.StructuredShape.Projection.LogicalColumns);
    }

    [Fact]
    public void Renderer_with_null_equality_withdraws_the_shape()
    {
        var unit = CreateSimpleUnit("mongo-evidence-null");
        var id = new ColumnRef(new TableId(unit.Name), "id", QueryType.String, isNullable: true);
        var request = new QueryRequest(
            new TableId(unit.Name),
            new Predicate.Equal(id, QueryConstant.Of(id, null)),
            [],
            Projection.All,
            Paging.OffsetLimit(0, 2));
        var capture = new MongoExecutionEvidenceCapture(new RecordingEvidenceObserver(), unit, MongoStorageAccess.Global, TestServerVersion);

        var emission = new MongoQueryRenderer().RenderWithEvidence(request, QueryRenderOptions.Default, "physical", capture, false);

        Assert.Null(emission.StructuredShape);
    }

    [Fact]
    public void Renderer_with_nullable_string_range_withdraws_the_shape_for_native_null_exclusion()
    {
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("mongo-evidence-null-range"),
            Name = "mongo_evidence_null_range",
            Columns = [new() { Name = "name", Type = PortableType.String, IsNullable = true }],
            Key = new KeyDefinition { Columns = ["name"] }
        };
        var name = new ColumnRef(new TableId(unit.Name), "name", QueryType.String, isNullable: true);
        var request = new QueryRequest(
            new TableId(unit.Name),
            new Predicate.Range(name, Bound.Inclusive(QueryConstant.Of(name, "a")), null),
            [],
            Projection.All,
            Paging.OffsetLimit(0, 2));
        var capture = new MongoExecutionEvidenceCapture(new RecordingEvidenceObserver(), unit, MongoStorageAccess.Global, TestServerVersion);

        var emission = new MongoQueryRenderer().RenderWithEvidence(request, QueryRenderOptions.Default, "physical", capture, false);

        Assert.Null(emission.StructuredShape);
    }

    [Fact]
    public void Renderer_with_nonnullable_declared_string_range_still_withdraws_the_shape()
    {
        var unit = CreateSimpleUnit("mongo-evidence-nonnull-range");
        var id = new ColumnRef(new TableId(unit.Name), "id", QueryType.String, isNullable: false);
        var request = new QueryRequest(
            new TableId(unit.Name),
            new Predicate.Range(id, Bound.Inclusive(QueryConstant.Of(id, "a")), null),
            [],
            Projection.All,
            Paging.OffsetLimit(0, 2));
        var capture = new MongoExecutionEvidenceCapture(new RecordingEvidenceObserver(), unit, MongoStorageAccess.Global, TestServerVersion);

        var emission = new MongoQueryRenderer().RenderWithEvidence(request, QueryRenderOptions.Default, "physical", capture, false);

        Assert.Null(emission.StructuredShape);
    }

    [Fact]
    public void Renderer_with_rewritten_search_key_withdraws_the_shape()
    {
        var unit = CreateSimpleUnit("mongo-evidence-search-key");
        var id = new ColumnRef(new TableId(unit.Name), "id", QueryType.String, isNullable: false);
        var request = new QueryRequest(
            new TableId(unit.Name),
            new Predicate.Equal(id, QueryConstant.Of(id, "value-sentinel")),
            [],
            Projection.All,
            Paging.OffsetLimit(0, 2));
        var options = QueryRenderOptions.Default with
        {
            SearchKeyColumns = new Dictionary<string, QuerySearchKeyColumn>(StringComparer.Ordinal)
            {
                ["id"] = new("id", "id_search", QuerySearchKeyPolicy.Ordinal,
                    orderByPhysicalColumn: false, preservesOrdinalIdentity: true)
            }
        };
        var capture = new MongoExecutionEvidenceCapture(new RecordingEvidenceObserver(), unit, MongoStorageAccess.Global, TestServerVersion);

        var emission = new MongoQueryRenderer().RenderWithEvidence(request, options, "physical", capture, false);

        Assert.Null(emission.StructuredShape);
    }

    [Fact]
    public void Renderer_orders_a_declared_non_null_column_without_a_null_rank_when_no_index_is_selected()
    {
        // The unit declaration is a non-null witness like the selected index (#441).
        var unit = CreateSimpleUnit("mongo-evidence-declared-non-null");
        var id = new ColumnRef(new TableId(unit.Name), "id", QueryType.String, isNullable: false);
        var request = new QueryRequest(
            new TableId(unit.Name),
            new Predicate.Equal(id, QueryConstant.Of(id, "value-sentinel")),
            [new OrderTerm(id, OrderDirection.Ascending, NullOrder.First)],
            Projection.All,
            Paging.OffsetLimit(0, 2));
        var options = QueryRenderOptions.Default with { NonNullColumns = ImmutableHashSet.Create(StringComparer.Ordinal, "id") };
        var capture = new MongoExecutionEvidenceCapture(new RecordingEvidenceObserver(), unit, MongoStorageAccess.Global, TestServerVersion);

        var emission = new MongoQueryRenderer().RenderWithEvidence(request, options, "physical", capture, false);

        var order = Assert.Single(Assert.IsType<ProviderBoundedQueryEvidence>(emission.StructuredShape).Ordering);
        Assert.DoesNotContain(ProviderOrderingTransform.NullRank, order.Transforms);
        Assert.Null(order.NullPlacement);
    }

    [Fact]
    public void Renderer_ordering_by_an_ordinal_identity_key_reports_the_source_column_with_the_physical_transform()
    {
        // A persisted ordinal identity key orders exactly as the logical column's ordinal order, so the
        // shape is supported and names the source column, as the relational providers report it (#432).
        var unit = CreateSimpleUnit("mongo-evidence-identity-order") with
        {
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, IsNullable = false },
                new() { Name = "status", Type = PortableType.String, IsNullable = false }
            ]
        };
        var status = new ColumnRef(new TableId(unit.Name), "status", QueryType.String, isNullable: false);
        var orderKey = new ColumnRef(new TableId(unit.Name), "__groundwork_ordinal_id", QueryType.String, isNullable: false);
        var request = new QueryRequest(
            new TableId(unit.Name),
            new Predicate.Equal(status, QueryConstant.Of(status, "value-sentinel")),
            [new OrderTerm(orderKey, OrderDirection.Ascending, NullOrder.First)],
            Projection.All,
            Paging.OffsetLimit(0, 2));
        var options = QueryRenderOptions.Default with
        {
            SearchKeyColumns = new Dictionary<string, QuerySearchKeyColumn>(StringComparer.Ordinal)
            {
                ["id"] = new("id", "__groundwork_ordinal_id", QuerySearchKeyPolicy.Ordinal,
                    orderByPhysicalColumn: true, supportsPrefixPredicates: false, preservesOrdinalIdentity: true)
            }
        };
        var capture = new MongoExecutionEvidenceCapture(new RecordingEvidenceObserver(), unit, MongoStorageAccess.Global, TestServerVersion);

        var emission = new MongoQueryRenderer().RenderWithEvidence(request, options, "physical", capture, false);

        var shape = Assert.IsType<ProviderBoundedQueryEvidence>(emission.StructuredShape);
        var order = Assert.Single(shape.Ordering);
        Assert.Equal("id", order.LogicalColumn);
        Assert.Contains(ProviderOrderingTransform.PhysicalSearchKey, order.Transforms);
        Assert.DoesNotContain(ProviderOrderingTransform.OrdinalStringKey, order.Transforms);
        Assert.Equal(ProviderPredicateComparison.Ordinal, order.Comparison);
        Assert.DoesNotContain("__groundwork_ordinal_id", System.Text.Json.JsonSerializer.Serialize(shape), StringComparison.Ordinal);
    }

    [Fact]
    public void Renderer_ordering_by_a_non_identity_persisted_key_withdraws_the_shape()
    {
        var unit = CreateSimpleUnit("mongo-evidence-folded-order") with
        {
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, IsNullable = false },
                new() { Name = "status", Type = PortableType.String, IsNullable = false }
            ]
        };
        var status = new ColumnRef(new TableId(unit.Name), "status", QueryType.String, isNullable: false);
        var orderKey = new ColumnRef(new TableId(unit.Name), "__groundwork_folded_id", QueryType.String, isNullable: false);
        var request = new QueryRequest(
            new TableId(unit.Name),
            new Predicate.Equal(status, QueryConstant.Of(status, "value-sentinel")),
            [new OrderTerm(orderKey, OrderDirection.Ascending, NullOrder.First)],
            Projection.All,
            Paging.OffsetLimit(0, 2));
        var options = QueryRenderOptions.Default with
        {
            SearchKeyColumns = new Dictionary<string, QuerySearchKeyColumn>(StringComparer.Ordinal)
            {
                ["id"] = new("id", "__groundwork_folded_id", QuerySearchKeyPolicy.AsciiIgnoreCase, orderByPhysicalColumn: true)
            }
        };
        var capture = new MongoExecutionEvidenceCapture(new RecordingEvidenceObserver(), unit, MongoStorageAccess.Global, TestServerVersion);

        var emission = new MongoQueryRenderer().RenderWithEvidence(request, options, "physical", capture, false);

        Assert.Null(emission.StructuredShape);
    }

    [Fact]
    public void Renderer_with_conflicting_physical_mappings_withdraws_the_shape()
    {
        var unit = CreateSimpleUnit("mongo-evidence-conflict");
        var id = new ColumnRef(new TableId(unit.Name), "id", QueryType.String, isNullable: false);
        var request = new QueryRequest(
            new TableId(unit.Name),
            new Predicate.Equal(id, QueryConstant.Of(id, "value-sentinel")),
            [],
            Projection.All,
            Paging.OffsetLimit(0, 2));
        var options = QueryRenderOptions.Default with
        {
            SearchKeyColumns = new Dictionary<string, QuerySearchKeyColumn>(StringComparer.Ordinal)
            {
                ["id"] = new("id", "shared_search", QuerySearchKeyPolicy.Ordinal),
                ["other"] = new("other", "shared_search", QuerySearchKeyPolicy.Ordinal)
            }
        };
        var capture = new MongoExecutionEvidenceCapture(new RecordingEvidenceObserver(), unit, MongoStorageAccess.Global, TestServerVersion);

        var emission = new MongoQueryRenderer().RenderWithEvidence(request, options, "physical", capture, false);

        Assert.Null(emission.StructuredShape);
    }

    [Fact]
    public void Capture_snapshots_options_once_and_restores_callback_guard_after_failure()
    {
        var unit = CreateSimpleUnit("mongo-evidence-callback");
        var observer = new RecordingEvidenceObserver { ThrowOnObserve = true };
        var capture = new MongoExecutionEvidenceCapture(observer, unit, MongoStorageAccess.Global, TestServerVersion);
        var invocation = capture.BeginInvocation();

        Assert.Throws<InvalidOperationException>(() => capture.Publish(
            invocation,
            0,
            ProviderExecutionOperation.BoundedQuery,
            ProviderExecutionRole.Statement,
            ProviderCommandKind.Read,
            ProviderExecutionOutcome.Succeeded,
            failureCategory: null,
            boundedQuery: null,
            pointRead: null));
        capture.ThrowIfCallbackReentry();
        Assert.Equal(1, observer.EvidenceOptionsReads);
    }

    [Fact]
    public void Capture_rejects_callback_reentry_while_terminal_observation_runs()
    {
        var unit = CreateSimpleUnit("mongo-evidence-reentry");
        MongoExecutionEvidenceCapture? capture = null;
        var observer = new RecordingEvidenceObserver
        {
            OnObserve = _ => Assert.Throws<InvalidOperationException>(() => capture!.ThrowIfCallbackReentry())
        };
        capture = new MongoExecutionEvidenceCapture(observer, unit, MongoStorageAccess.Global, TestServerVersion);

        capture.Publish(
            capture.BeginInvocation(),
            0,
            ProviderExecutionOperation.BoundedQuery,
            ProviderExecutionRole.Statement,
            ProviderCommandKind.Read,
            ProviderExecutionOutcome.Succeeded,
            failureCategory: null,
            boundedQuery: null,
            pointRead: null);
    }

    [Fact]
    public void Capture_preserves_cancelled_terminal_outcome_when_shape_is_unsupported()
    {
        var unit = CreateSimpleUnit("mongo-evidence-cancelled");
        var observer = new RecordingEvidenceObserver();
        var capture = new MongoExecutionEvidenceCapture(observer, unit, MongoStorageAccess.Global, TestServerVersion);

        capture.Publish(
            capture.BeginInvocation(),
            0,
            ProviderExecutionOperation.BoundedQuery,
            ProviderExecutionRole.Statement,
            ProviderCommandKind.Read,
            ProviderExecutionOutcome.Cancelled,
            ProviderExecutionFailureCategory.Cancellation,
            boundedQuery: null,
            pointRead: null);

        var evidence = Assert.Single(observer.Observed);
        Assert.Equal(ProviderExecutionOutcome.Cancelled, evidence.Outcome);
        Assert.Equal(ProviderExecutionFailureCategory.Cancellation, evidence.FailureCategory);
        Assert.Equal(ProviderEvidenceAvailability.Unsupported, evidence.ShapeAvailability);
        Assert.Null(evidence.BoundedQuery);
    }

    [Fact]
    public void Owner_registry_forwards_structured_guards_skips_ordinary_observers_and_prunes_expired_entries()
    {
        var registry = new MongoStructuredEvidenceOwnerRegistry();
        var owner = new RecordingEvidenceOwnerGuard();
        var ordinaryObserver = new CommandOnlyObserver();
        var structuredObserver = new RecordingEvidenceObserver();

        registry.RegisterIfStructured(ordinaryObserver, owner);
        Assert.Equal(0, registry.EntryCount);

        registry.RegisterIfStructured(structuredObserver, owner);
        registry.AddExpiredEntryForTest();
        Assert.Equal(2, registry.EntryCount);

        registry.EnsureNotReentered();

        Assert.Equal(1, registry.EntryCount);
        Assert.Equal(1, owner.GuardCalls);
    }

    [Fact]
    public void Owner_registry_rejects_reentry_from_a_live_structured_owner()
    {
        var registry = new MongoStructuredEvidenceOwnerRegistry();
        var owner = new RecordingEvidenceOwnerGuard { ThrowOnGuard = true };

        registry.RegisterIfStructured(new RecordingEvidenceObserver(), owner);

        Assert.Throws<InvalidOperationException>(() => registry.EnsureNotReentered());
        Assert.Equal(1, registry.EntryCount);
        Assert.Equal(1, owner.GuardCalls);
    }

    /// <summary>#422: a continuation page records its lexicographic branches; a nulls-last boundary without a non-null witness admits null rows.</summary>
    [Fact]
    public void Renderer_records_the_continuation_branches_of_a_keyset_page()
    {
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("mongo-evidence-continuation"),
            Name = "mongo_evidence_continuation",
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, IsNullable = false },
                new() { Name = "updated", Type = PortableType.Int64, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] }
        };
        var id = new ColumnRef(new TableId(unit.Name), "id", QueryType.String, isNullable: false);
        // Without a non-null witness in the options, MongoDB's nulls-last boundary still emits the
        // null alternative; the evidence records that emitted fact, not the declaration.
        var updated = new ColumnRef(new TableId(unit.Name), "updated", QueryType.Int64, isNullable: false);
        System.Collections.Immutable.ImmutableArray<OrderTerm> order =
            [new OrderTerm(updated, OrderDirection.Descending, NullOrder.Last), new OrderTerm(id, OrderDirection.Ascending, NullOrder.First)];
        var first = new QueryRequest(new TableId(unit.Name), Predicate.AlwaysTrue.Instance, order, Projection.ColumnsOnly(id, updated), Paging.Keyset(3));
        var token = QueryContinuationToken.Encode(first, QueryRenderOptions.Default, [QueryConstant.Of(updated, 4242L), QueryConstant.Of(id, "secret-cursor")]);
        var request = new QueryRequest(new TableId(unit.Name), Predicate.AlwaysTrue.Instance, order, Projection.ColumnsOnly(id, updated), Paging.Continuation(token, 3));
        var observer = new RecordingEvidenceObserver();
        var capture = new MongoExecutionEvidenceCapture(observer, unit, MongoStorageAccess.Global, TestServerVersion);

        var emission = new MongoQueryRenderer().RenderWithEvidence(request, QueryRenderOptions.Default, "physical_collection", capture, hasLookahead: true);

        var shape = Assert.IsType<ProviderBoundedQueryEvidence>(emission.StructuredShape);
        Assert.True(shape.HasContinuation);
        var emitted = Assert.IsType<ProviderContinuationPredicate>(shape.Continuation);
        Assert.Equal(ProviderContinuationForm.Lexicographic, emitted.Form);
        Assert.Collection(emitted.Branches,
            branch =>
            {
                Assert.Empty(branch.Equalities);
                Assert.Equal("updated", branch.Boundary.LogicalColumn);
                Assert.Equal(ProviderPredicateOperator.UpperBound, branch.Boundary.Operator);
                Assert.Equal(ProviderPredicateComparison.Exact, branch.Boundary.Comparison);
                Assert.True(branch.BoundaryAdmitsNull);
            },
            branch =>
            {
                var equality = Assert.Single(branch.Equalities);
                Assert.Equal("updated", equality.LogicalColumn);
                Assert.Equal(ProviderPredicateOperator.Equal, equality.Operator);
                Assert.Equal("id", branch.Boundary.LogicalColumn);
                Assert.Equal(ProviderPredicateOperator.LowerBound, branch.Boundary.Operator);
                Assert.Equal(ProviderPredicateComparison.Ordinal, branch.Boundary.Comparison);
                Assert.False(branch.BoundaryAdmitsNull);
            });
        Assert.All(emitted.Branches.SelectMany(branch => branch.Equalities.Append(branch.Boundary)),
            fact => Assert.Equal(ProviderPredicateBindingRole.Continuation, fact.BindingRole));
        var serialized = System.Text.Json.JsonSerializer.Serialize(shape);
        Assert.DoesNotContain("4242", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-cursor", serialized, StringComparison.Ordinal);
    }

    private static StorageUnit CreateSimpleUnit(string name) => new()
    {
        Id = new StorageUnitId(name),
        Name = name,
        Columns = [new() { Name = "id", Type = PortableType.String, IsNullable = false }],
        Key = new KeyDefinition { Columns = ["id"] }
    };

    private sealed class RecordingEvidenceObserver : IProviderExecutionObserver
    {
        public ProviderExecutionEvidenceOptions EvidenceOptions { get; } = ProviderExecutionEvidenceOptions.ShapeOnly;
        public List<ProviderExecutionEvidence> Observed { get; } = [];

        public int EvidenceOptionsReads { get; private set; }
        public bool ThrowOnObserve { get; init; }
        public Action<ProviderExecutionEvidence>? OnObserve { get; init; }

        ProviderExecutionEvidenceOptions IProviderExecutionObserver.EvidenceOptions
        {
            get
            {
                EvidenceOptionsReads++;
                return EvidenceOptions;
            }
        }

        public void Observe(ProviderCommandEvent command)
        {
        }

        public void ObserveExecution(ProviderExecutionEvidence evidence)
        {
            Observed.Add(evidence);
            OnObserve?.Invoke(evidence);
            if (ThrowOnObserve)
                throw new InvalidOperationException("observer failure");
        }
    }

    private sealed class CommandOnlyObserver : IProviderCommandObserver
    {
        public void Observe(ProviderCommandEvent command)
        {
        }
    }

    private sealed class RecordingEvidenceOwnerGuard : IMongoStructuredEvidenceOwner
    {
        public int GuardCalls { get; private set; }
        public bool ThrowOnGuard { get; init; }

        public void ThrowIfStructuredObserverReentry()
        {
            GuardCalls++;
            if (ThrowOnGuard)
                throw new InvalidOperationException("re-entry");
        }
    }
    [Fact]
    public void Capture_stamps_the_provider_name_and_the_connected_server_version()
    {
        var reads = 0;
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("mongo-evidence-provider"),
            Name = "mongo_evidence_provider",
            Columns = [new() { Name = "id", Type = PortableType.String, IsNullable = false }],
            Key = new KeyDefinition { Columns = ["id"] }
        };
        var capture = new MongoExecutionEvidenceCapture(new RecordingEvidenceObserver(), unit, MongoStorageAccess.Global, () =>
        {
            reads++;
            return "8.0.4";
        });

        Assert.Equal(0, reads);
        Assert.Equal(new ProviderIdentity(MongoSchemaTargets.Provider.Name, "8.0.4"), capture.Provider);
        Assert.Equal(capture.Provider, capture.Provider);
        Assert.Equal(1, reads);
    }

    private static string TestServerVersion() => "7.0.0-test";
}
