using System.Text.Json;
using Groundwork.Kernel;
using Groundwork.Query.Model;
using Groundwork.Sqlite;
using Groundwork.Store;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Groundwork.Sqlite.Tests;

public sealed class SqliteExecutionEvidenceTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Point_read_reports_the_issued_shape_without_inventing_a_native_limit(bool found, bool useAsync)
    {
        using var fixture = new Fixture();
        var key = Key(found ? Fixture.SecretKey : "missing-secret-key");
        var result = useAsync ? await fixture.Session.ReadAsync(key) : fixture.Session.Read(key);

        Assert.Equal(found, result is not null);
        var evidence = Assert.Single(fixture.Observer.Executions);
        Assert.Equal(["issued", "terminal"], fixture.Observer.CallbackOrder);
        Assert.Single(fixture.Observer.Commands);
        Assert.Equal(ProviderExecutionOperation.PointRead, evidence.Operation);
        Assert.Equal(ProviderExecutionOutcome.Succeeded, evidence.Outcome);
        Assert.Equal(ProviderEvidenceAvailability.Collected, evidence.ShapeAvailability);
        Assert.Equal(ProviderScopeBindingMode.Predicate, evidence.Target.ScopeBinding);
        Assert.Equal(fixture.Unit.Id, evidence.Target.LogicalUnitId);
        var pointRead = Assert.IsType<ProviderPointReadEvidence>(evidence.PointRead);
        Assert.True(pointRead.IncludesScopeBinding);
        Assert.Equal("id", Assert.Single(pointRead.KeyBounds,
            bound => bound.BindingRole == ProviderPointReadBindingRole.Key).LogicalColumn);
        Assert.Equal(ProviderNativeBoundKind.Absent, pointRead.NativeLimit.Kind);
        Assert.True(pointRead.MaterializerReadsAtMostOne);
        Assert.Equal(ProviderEvidenceAvailability.NotRequested, evidence.Plan.Availability);

        var serialized = JsonSerializer.Serialize(evidence);
        Assert.DoesNotContain(Fixture.SecretScope, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(Fixture.SecretKey, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(Fixture.SecretPayload, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.ConnectionString, serialized, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Failed_native_read_emits_failed_terminal_evidence_without_raw_error_payload(bool observerAlsoFails, bool query)
    {
        using var fixture = new Fixture();
        if (observerAlsoFails)
            fixture.Observer.TerminalCallback = () => throw new InvalidOperationException("observer failure");
        using (var raw = new SqliteConnection(fixture.ConnectionString))
        {
            raw.Open();
            using var command = raw.CreateCommand();
            command.CommandText = "DROP TABLE evidence_rows;";
            command.ExecuteNonQuery();
        }

        Assert.Throws<SqliteException>(() =>
        {
            if (query)
                fixture.Session.Query(PayloadQuery(fixture, payload => new Predicate.Equal(
                    payload, QueryConstant.Of(payload, Fixture.SecretPayload))),
                    fixture.Unit.CreateQueryRenderOptions("by_payload_id"));
            else
                fixture.Session.Read(Key(Fixture.SecretKey));
        });

        var evidence = Assert.Single(fixture.Observer.Executions);
        Assert.Equal(["issued", "terminal"], fixture.Observer.CallbackOrder);
        Assert.Equal(ProviderExecutionOutcome.Failed, evidence.Outcome);
        Assert.NotNull(evidence.FailureCategory);
        Assert.DoesNotContain("no such table", JsonSerializer.Serialize(evidence), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>#423: a point read collects its native plan through the query explain seam and the catalog's uniqueness witness.</summary>
    [Fact]
    public void Point_read_plan_collection_maps_the_key_search_and_observes_the_enforced_key()
    {
        using var fixture = new Fixture();
        fixture.Observer.EvidenceOptions = ProviderExecutionEvidenceOptions.ShapeAndPlans;

        fixture.Session.Read(Key(Fixture.SecretKey));

        var evidence = Assert.Single(fixture.Observer.Executions);
        Assert.Equal(ProviderEvidenceAvailability.Collected, evidence.Plan.Availability);
        Assert.Equal(ProviderPlanProvenance.EstimatedExplain, evidence.Plan.Provenance);
        var nodes = evidence.Plan.WinningPlan!.Nodes;
        var access = Assert.Single(nodes, node => node.TargetId is not null);
        Assert.Contains(access.Operation, new[] { ProviderPlanOperator.PrimaryKeySearch, ProviderPlanOperator.IndexSearch });
        var uniqueness = evidence.PointRead!.Uniqueness;
        Assert.Equal(ProviderPointReadUniquenessStatus.Observed, uniqueness.Status);
        Assert.Equal(new[] { "id" }, uniqueness.EnforcedKeyColumns.ToArray());
        Assert.True(uniqueness.IncludesScopeBinding);
        Assert.Single(fixture.Observer.Commands);
        Assert.DoesNotContain(Fixture.SecretKey, System.Text.Json.JsonSerializer.Serialize(evidence), StringComparison.Ordinal);
    }

    [Fact]
    public void Point_read_identities_correlate_within_one_session_but_not_across_sessions()
    {
        using var first = new Fixture();
        using var second = new Fixture();
        first.Session.Read(Key(Fixture.SecretKey));
        first.Session.Read(Key("missing"));
        second.Session.Read(Key(Fixture.SecretKey));

        var a = first.Observer.Executions[0];
        var b = first.Observer.Executions[1];
        var c = Assert.Single(second.Observer.Executions);
        Assert.Equal(a.Identity.CaptureId, b.Identity.CaptureId);
        Assert.Equal(a.Target.PhysicalTargetId, b.Target.PhysicalTargetId);
        Assert.NotEqual(a.Identity.InvocationId, b.Identity.InvocationId);
        Assert.NotEqual(a.Identity.CommandId, b.Identity.CommandId);
        Assert.NotEqual(a.Identity.StatementId, b.Identity.StatementId);
        Assert.NotEqual(a.PointRead!.KeyBounds[0].BindingId, b.PointRead!.KeyBounds[0].BindingId);
        Assert.NotEqual(a.Identity.CaptureId, c.Identity.CaptureId);
        Assert.NotEqual(a.Target.PhysicalTargetId, c.Target.PhysicalTargetId);
        Assert.Equal(0, a.Identity.CommandOrdinal);
        Assert.Equal(0, b.Identity.CommandOrdinal);
        Assert.Equal(0, a.Identity.StatementOrdinal);
    }

    [Fact]
    public async Task Cancellation_before_issue_cannot_produce_success_evidence()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await fixture.Session.ReadAsync(Key(Fixture.SecretKey), cancellation.Token));

        Assert.Empty(fixture.Observer.Commands);
        Assert.Empty(fixture.Observer.Executions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Structured_callbacks_preserve_the_unit_of_work_reentry_guard(bool optionsCallback)
    {
        using var fixture = new Fixture();
        Exception? refusal = null;
        Action callback = () => refusal = Record.Exception(() => fixture.Connection.Catalog.ReadIndexes(fixture.Unit.Id));
        if (optionsCallback)
            fixture.Observer.OptionsCallback = callback;
        else
            fixture.Observer.TerminalCallback = callback;
        using var work = fixture.Connection.BeginUnitOfWork(
            StorageAccess.Scoped(new StorageScope(Fixture.SecretScope)),
            BatchWriteOptions.Exact, fixture.Observer, fixture.Unit);

        Assert.NotNull(work.OpenSession(fixture.Unit).Read(Key(Fixture.SecretKey)));

        Assert.Contains("observer cannot re-enter", Assert.IsType<InvalidOperationException>(refusal).Message, StringComparison.Ordinal);
        Assert.Equal(ProviderExecutionOutcome.Succeeded, Assert.Single(fixture.Observer.Executions).Outcome);
        work.Rollback();
    }

    [Fact]
    public void Bounded_query_reports_the_effective_scoped_shape_and_native_lookahead()
    {
        using var fixture = new Fixture();
        var otherScope = fixture.Connection.OpenSession(fixture.Unit,
            StorageAccess.Scoped(new StorageScope(Fixture.SecretScope + "-other")));
        Assert.Equal(WriteOutcomeStatus.Inserted, otherScope.Insert(new StorageValues(
            new Dictionary<string, object?> { ["id"] = "foreign-row", ["payload"] = Fixture.SecretPayload })).Status);
        var result = fixture.Session.Query(
            PayloadQuery(fixture, payload => new Predicate.Equal(
                payload,
                QueryConstant.Of(payload, Fixture.SecretPayload))),
            fixture.Unit.CreateQueryRenderOptions("by_payload_id"));

        var row = Assert.Single(result.Rows);
        Assert.Equal(Fixture.SecretKey, row["id"]);
        Assert.Equal(Fixture.SecretPayload, row["payload"]);

        var evidence = Assert.Single(fixture.Observer.Executions);
        Assert.Equal(["issued", "terminal"], fixture.Observer.CallbackOrder);
        Assert.Single(fixture.Observer.Commands);
        Assert.Equal(ProviderExecutionOperation.BoundedQuery, evidence.Operation);
        Assert.Equal(ProviderExecutionOutcome.Succeeded, evidence.Outcome);
        Assert.Equal(ProviderEvidenceAvailability.Collected, evidence.ShapeAvailability);
        Assert.Equal(fixture.Unit.Id, evidence.Target.LogicalUnitId);
        Assert.Equal(ProviderScopeBindingMode.Predicate, evidence.Target.ScopeBinding);

        var bounded = Assert.IsType<ProviderBoundedQueryEvidence>(evidence.BoundedQuery);
        Assert.Equal(2, bounded.Predicate.Facts.Length);
        var caller = Assert.Single(bounded.Predicate.Facts, fact => fact.BindingRole == ProviderPredicateBindingRole.Caller);
        Assert.Equal("payload", caller.LogicalColumn);
        Assert.Equal(ProviderPredicateOperator.Equal, caller.Operator);
        Assert.Equal(QueryType.String, caller.ValueType);
        Assert.Equal(ProviderPredicateComparison.Ordinal, caller.Comparison);
        Assert.Equal(ProviderPredicateBoundInclusivity.NotApplicable, caller.BoundInclusivity);
        var scope = Assert.Single(bounded.Predicate.Facts, fact => fact.BindingRole == ProviderPredicateBindingRole.Scope);
        Assert.Equal(ProviderPredicateOperator.Equal, scope.Operator);
        Assert.Equal(QueryType.String, scope.ValueType);
        Assert.Equal(ProviderPredicateComparison.Ordinal, scope.Comparison);
        Assert.Equal(ProviderPredicateBoundInclusivity.NotApplicable, scope.BoundInclusivity);
        Assert.All(bounded.Predicate.Facts, fact => Assert.NotEqual(Guid.Empty, fact.BindingId.Value));

        var order = Assert.Single(bounded.Ordering);
        Assert.Equal("id", order.LogicalColumn);
        Assert.Equal(OrderDirection.Ascending, order.Direction);
        Assert.Null(order.NullPlacement);
        Assert.Empty(order.Transforms);
        Assert.Equal(ProviderPredicateComparison.Ordinal, order.Comparison);
        Assert.False(bounded.Projection.AllColumns);
        Assert.Collection(bounded.Projection.LogicalColumns,
            column => Assert.Equal("id", column), column => Assert.Equal("payload", column));
        Assert.Equal(ProviderNativeBoundKind.Absent, bounded.NativeOffset.Kind);
        Assert.Equal(ProviderNativeBoundKind.Explicit, bounded.NativeLimit.Kind);
        Assert.Equal(3, bounded.NativeLimit.Value);
        Assert.False(bounded.HasContinuation);
        Assert.True(bounded.HasLookahead);
        Assert.False(bounded.IncludesTotalCount);
        Assert.Equal(ProviderEvidenceAvailability.NotRequested, evidence.Plan.Availability);

        var serialized = JsonSerializer.Serialize(evidence);
        Assert.DoesNotContain(Fixture.SecretScope, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(Fixture.SecretKey, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(Fixture.SecretPayload, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.ConnectionString, serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void Shape_and_plan_opt_in_reports_estimated_explain_and_actual_selected_index()
    {
        using var fixture = new Fixture();
        fixture.Observer.EvidenceOptions = ProviderExecutionEvidenceOptions.ShapeAndPlans;

        fixture.Session.Query(
            PayloadQuery(fixture, payload => new Predicate.Equal(
                payload,
                QueryConstant.Of(payload, Fixture.SecretPayload))),
            fixture.Unit.CreateQueryRenderOptions("by_payload_id"));

        var evidence = Assert.Single(fixture.Observer.Executions);
        Assert.Equal(ProviderExecutionOutcome.Succeeded, evidence.Outcome);
        Assert.Equal(ProviderEvidenceAvailability.Collected, evidence.ShapeAvailability);
        Assert.Equal(ProviderEvidenceAvailability.Collected, evidence.Plan.Availability);
        Assert.Equal(ProviderPlanProvenance.EstimatedExplain, evidence.Plan.Provenance);
        Assert.True(evidence.Plan.ChoseExpectedIndex == true);
        Assert.Equal("by_payload_id", evidence.Plan.ExpectedLogicalIndex);
        Assert.NotNull(evidence.Plan.ChosenPhysicalIndexId);
        var forest = Assert.IsType<ProviderPlanForest>(evidence.Plan.WinningPlan);
        var access = Assert.Single(forest.Nodes.Where(node => node.TargetId is not null));
        Assert.Equal(ProviderPlanOperator.IndexSearch, access.Operation);
        Assert.Equal(evidence.Target.PhysicalTargetId, access.TargetId);
        Assert.Equal(evidence.Plan.ChosenPhysicalIndexId, access.IndexId);
        Assert.Equal("by_payload_id", access.LogicalIndexName);
        Assert.Equal(1, evidence.Plan.CollectionCommandCount);
        Assert.Single(fixture.Observer.Commands);
    }

    [Fact]
    public void Native_index_choice_is_mapped_even_without_a_requested_index()
    {
        using var fixture = new Fixture();
        fixture.Observer.EvidenceOptions = ProviderExecutionEvidenceOptions.ShapeAndPlans;

        fixture.Session.Query(PayloadQuery(fixture,
            payload => new Predicate.Equal(payload, QueryConstant.Of(payload, Fixture.SecretPayload))),
            fixture.Unit.CreateQueryRenderOptions());

        var evidence = Assert.Single(fixture.Observer.Executions);
        Assert.Equal(ProviderEvidenceAvailability.Collected, evidence.Plan.Availability);
        Assert.Null(evidence.Plan.ExpectedLogicalIndex);
        Assert.Null(evidence.Plan.ChoseExpectedIndex);
        var access = Assert.Single(Assert.IsType<ProviderPlanForest>(evidence.Plan.WinningPlan).Nodes
            .Where(node => node.TargetId is not null));
        Assert.Equal(ProviderPlanOperator.IndexSearch, access.Operation);
        Assert.Equal("by_payload_id", access.LogicalIndexName);
        Assert.Equal(evidence.Target.PhysicalTargetId, access.TargetId);
        Assert.Single(fixture.Observer.Commands);
    }

    [Fact]
    public void Actual_unindexed_query_reports_table_scan_and_sort_without_an_index_success_claim()
    {
        using var fixture = new Fixture(scoped: false, indexed: false);
        fixture.Observer.EvidenceOptions = ProviderExecutionEvidenceOptions.ShapeAndPlans;
        var table = new TableId(fixture.Unit.Name);
        var payload = new ColumnRef(table, "payload", QueryType.String, isNullable: false);
        var query = new QueryRequest(table, new Predicate.AlwaysTrue(),
            [new OrderTerm(payload, OrderDirection.Ascending, NullOrder.First)],
            Projection.ColumnsOnly(payload), Paging.Keyset(2));

        var result = fixture.Session.Query(query, fixture.Unit.CreateQueryRenderOptions());

        Assert.Single(result.Rows);
        var evidence = Assert.Single(fixture.Observer.Executions);
        Assert.Equal(ProviderEvidenceAvailability.Collected, evidence.Plan.Availability);
        Assert.Null(evidence.Plan.ChoseExpectedIndex);
        var forest = Assert.IsType<ProviderPlanForest>(evidence.Plan.WinningPlan);
        Assert.Equal(2, forest.Nodes.Length);
        var scan = Assert.Single(forest.Nodes.Where(node => node.Operation == ProviderPlanOperator.TableScan));
        Assert.Equal(evidence.Target.PhysicalTargetId, scan.TargetId);
        var sort = Assert.Single(forest.Nodes.Where(node => node.Operation == ProviderPlanOperator.Sort));
        Assert.Equal(ProviderPlanSortPurpose.OrderBy, sort.SortPurpose);
        Assert.All(forest.Nodes, node => Assert.Null(node.ParentId));
        Assert.Equal(1, evidence.Plan.CollectionCommandCount);
        Assert.Single(fixture.Observer.Commands);
    }

    [Fact]
    public void Plan_evidence_reports_when_the_expected_index_is_not_actually_available()
    {
        using var fixture = new Fixture();
        fixture.Observer.EvidenceOptions = ProviderExecutionEvidenceOptions.ShapeAndPlans;
        var physicalIndex = SqliteDialect.PhysicalIndexName(fixture.Unit.Name, "by_payload_id");
        using (var raw = new SqliteConnection(fixture.ConnectionString))
        {
            raw.Open();
            using var command = raw.CreateCommand();
            command.CommandText = $"DROP INDEX \"{physicalIndex.Replace("\"", "\"\"", StringComparison.Ordinal)}\";";
            command.ExecuteNonQuery();
        }

        var result = fixture.Session.Query(
            PayloadQuery(fixture, payload => new Predicate.Equal(
                payload, QueryConstant.Of(payload, Fixture.SecretPayload))),
            fixture.Unit.CreateQueryRenderOptions("by_payload_id"));

        Assert.Single(result.Rows);
        var evidence = Assert.Single(fixture.Observer.Executions);
        Assert.Equal(ProviderExecutionOutcome.Succeeded, evidence.Outcome);
        Assert.Equal(ProviderEvidenceAvailability.Collected, evidence.Plan.Availability);
        Assert.Equal(ProviderPlanProvenance.EstimatedExplain, evidence.Plan.Provenance);
        Assert.False(evidence.Plan.ChoseExpectedIndex);
        Assert.Null(evidence.Plan.ChosenPhysicalIndexId);
        Assert.Equal(1, evidence.Plan.CollectionCommandCount);
        Assert.Single(fixture.Observer.Commands);
    }

    [Fact]
    public void Point_and_query_evidence_share_session_identity_but_have_distinct_invocations()
    {
        using var fixture = new Fixture();
        fixture.Session.Read(Key(Fixture.SecretKey));
        fixture.Session.Query(
            PayloadQuery(fixture, payload => new Predicate.Equal(
                payload, QueryConstant.Of(payload, Fixture.SecretPayload))),
            fixture.Unit.CreateQueryRenderOptions("by_payload_id"));

        Assert.Equal(2, fixture.Observer.Executions.Count);
        var point = fixture.Observer.Executions[0];
        var query = fixture.Observer.Executions[1];
        Assert.Equal(point.Identity.CaptureId, query.Identity.CaptureId);
        Assert.Equal(point.Target.PhysicalTargetId, query.Target.PhysicalTargetId);
        Assert.NotEqual(point.Identity.InvocationId, query.Identity.InvocationId);
        Assert.NotEqual(point.Identity.CommandId, query.Identity.CommandId);
        Assert.NotEqual(point.Identity.StatementId, query.Identity.StatementId);
    }

    [Fact]
    public void Unsupported_boolean_shape_keeps_command_success_but_withholds_shape_facts()
    {
        using var fixture = new Fixture();
        fixture.Session.Query(
            PayloadQuery(fixture, payload => new Predicate.Or([
                new Predicate.Equal(payload, QueryConstant.Of(payload, Fixture.SecretPayload)),
                new Predicate.Equal(payload, QueryConstant.Of(payload, "another-payload"))])),
            fixture.Unit.CreateQueryRenderOptions("by_payload_id"));

        var evidence = Assert.Single(fixture.Observer.Executions);
        Assert.Equal(ProviderExecutionOperation.BoundedQuery, evidence.Operation);
        Assert.Equal(ProviderExecutionOutcome.Succeeded, evidence.Outcome);
        Assert.Equal(ProviderEvidenceAvailability.Unsupported, evidence.ShapeAvailability);
        Assert.Null(evidence.BoundedQuery);
        Assert.Equal(ProviderEvidenceAvailability.NotRequested, evidence.Plan.Availability);
        Assert.Single(fixture.Observer.Commands);
    }

    [Theory]
    [InlineData(NullOrder.First)]
    [InlineData(NullOrder.Last)]
    public void Nullable_order_reports_the_emitted_null_rank_and_ordinal_comparison(NullOrder nullOrder)
    {
        using var fixture = new Fixture(nullablePayload: true);
        var table = new TableId(fixture.Unit.Name);
        var id = new ColumnRef(table, "id", QueryType.String, isNullable: false);
        var payload = new ColumnRef(table, "payload", QueryType.String, isNullable: true);
        var query = new QueryRequest(table,
            new Predicate.Equal(payload, QueryConstant.Of(payload, Fixture.SecretPayload)),
            [new OrderTerm(payload, OrderDirection.Descending, nullOrder),
                new OrderTerm(id, OrderDirection.Ascending, NullOrder.First)],
            Projection.ColumnsOnly(id, payload), Paging.Keyset(2));

        Assert.Single(fixture.Session.Query(query, fixture.Unit.CreateQueryRenderOptions("by_payload_id")).Rows);

        var evidence = Assert.Single(fixture.Observer.Executions);
        Assert.Equal(ProviderEvidenceAvailability.Collected, evidence.ShapeAvailability);
        var shape = Assert.IsType<ProviderBoundedQueryEvidence>(evidence.BoundedQuery);
        var order = Assert.Single(shape.Ordering, term => term.LogicalColumn == "payload");
        Assert.Equal(OrderDirection.Descending, order.Direction);
        Assert.Equal(nullOrder, order.NullPlacement);
        Assert.Equal(ProviderOrderingTransform.NullRank, Assert.Single(order.Transforms));
        Assert.Equal(ProviderPredicateComparison.Ordinal, order.Comparison);
    }

    [Fact]
    public void Joined_materializer_preflight_refusal_does_not_claim_a_failed_provider_command()
    {
        using var fixture = new Fixture();
        var source = new TableId(fixture.Unit.Name);
        var target = new TableId("not_executed_target");
        var sourceId = new ColumnRef(source, "id", QueryType.String, isNullable: false);
        var targetId = new ColumnRef(target, "id", QueryType.String, isNullable: false);
        var request = new QueryRequest(source,
            new ReferenceJoin("target", target, [new JoinColumnPair(sourceId, targetId)]),
            Predicate.AlwaysTrue.Instance, [], Projection.All, Paging.Keyset(2));

        var refusal = Assert.Throws<QueryRenderException>(() => fixture.Session.Query(request));

        Assert.Equal("GW-QUERY-032", refusal.Code);
        Assert.Single(fixture.Observer.Commands);
        Assert.Empty(fixture.Observer.Executions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Read_cancelled_by_the_legacy_callback_before_native_execution_has_no_terminal_evidence(bool query)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Observer.IssuedCallback = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (query)
                await fixture.Session.QueryAsync(
                    PayloadQuery(fixture, payload => new Predicate.Equal(payload, QueryConstant.Of(payload, Fixture.SecretPayload))),
                    fixture.Unit.CreateQueryRenderOptions("by_payload_id"), cancellation.Token);
            else
                await fixture.Session.ReadAsync(Key(Fixture.SecretKey), cancellation.Token);
        });

        Assert.Single(fixture.Observer.Commands);
        Assert.Empty(fixture.Observer.Executions);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Bounded_range_reports_native_endpoint_inclusivity_and_bindings(bool testLower, bool inclusive)
    {
        using var fixture = new Fixture();
        var table = new TableId(fixture.Unit.Name);
        var id = new ColumnRef(table, "id", QueryType.String, isNullable: false);
        var payload = new ColumnRef(table, "payload", QueryType.String, isNullable: false);
        var lowerValue = QueryConstant.Of(payload, testLower ? Fixture.SecretPayload : "a");
        var upperValue = QueryConstant.Of(payload, testLower ? "z" : Fixture.SecretPayload);
        var lowerInclusive = !testLower || inclusive;
        var upperInclusive = testLower || inclusive;
        var query = new QueryRequest(table, new Predicate.Range(payload,
                lowerInclusive ? Bound.Inclusive(lowerValue) : Bound.Exclusive(lowerValue),
                upperInclusive ? Bound.Inclusive(upperValue) : Bound.Exclusive(upperValue)),
            [new OrderTerm(payload, OrderDirection.Ascending, NullOrder.First),
                new OrderTerm(id, OrderDirection.Ascending, NullOrder.First)],
            Projection.ColumnsOnly(id, payload), Paging.Keyset(2));

        var result = fixture.Session.Query(query, fixture.Unit.CreateQueryRenderOptions("by_payload_id"));

        Assert.Equal(inclusive ? 1 : 0, result.Rows.Count);
        var evidence = Assert.Single(fixture.Observer.Executions);
        Assert.Equal(ProviderExecutionOutcome.Succeeded, evidence.Outcome);
        Assert.Equal(ProviderEvidenceAvailability.Collected, evidence.ShapeAvailability);
        var shape = Assert.IsType<ProviderBoundedQueryEvidence>(evidence.BoundedQuery);
        Assert.Equal(3, shape.NativeLimit.Value);
        Assert.Equal(3, shape.Predicate.Facts.Length);
        var lower = Assert.Single(shape.Predicate.Facts, fact => fact.Operator == ProviderPredicateOperator.LowerBound);
        var upper = Assert.Single(shape.Predicate.Facts, fact => fact.Operator == ProviderPredicateOperator.UpperBound);
        Assert.Equal(lowerInclusive ? ProviderPredicateBoundInclusivity.Inclusive : ProviderPredicateBoundInclusivity.Exclusive,
            lower.BoundInclusivity);
        Assert.Equal(upperInclusive ? ProviderPredicateBoundInclusivity.Inclusive : ProviderPredicateBoundInclusivity.Exclusive,
            upper.BoundInclusivity);
        Assert.All(new[] { lower, upper }, fact =>
        {
            Assert.Equal("payload", fact.LogicalColumn);
            Assert.Equal(QueryType.String, fact.ValueType);
            Assert.Equal(ProviderPredicateComparison.Ordinal, fact.Comparison);
            Assert.Equal(ProviderPredicateBindingRole.Caller, fact.BindingRole);
            Assert.NotEqual(Guid.Empty, fact.BindingId.Value);
        });
        Assert.NotEqual(lower.BindingId, upper.BindingId);
        Assert.DoesNotContain(Fixture.SecretPayload, JsonSerializer.Serialize(evidence), StringComparison.Ordinal);
    }

    private static StorageKey Key(string value) => new(new Dictionary<string, object?> { ["id"] = value });

    private static QueryRequest PayloadQuery(Fixture fixture, Func<ColumnRef, Predicate> predicate)
    {
        var table = new TableId(fixture.Unit.Name);
        var id = new ColumnRef(table, "id", QueryType.String, isNullable: false);
        var payload = new ColumnRef(table, "payload", QueryType.String, isNullable: false);
        return new QueryRequest(
            table,
            predicate(payload),
            [new OrderTerm(id, OrderDirection.Ascending, NullOrder.First)],
            Projection.ColumnsOnly(id, payload),
            Paging.Keyset(2));
    }

    private sealed class Fixture : IDisposable
    {
        internal const string SecretScope = "scope-value-must-never-enter-evidence";
        internal const string SecretKey = "key-value-must-never-enter-evidence";
        internal const string SecretPayload = "payload-must-never-enter-evidence";
        private readonly string directory = Path.Combine(Path.GetTempPath(), "groundwork-evidence-" + Guid.NewGuid().ToString("N"));
        private readonly IStorageProviderConnection connection;

        internal Fixture(bool nullablePayload = false, bool scoped = true, bool indexed = true)
        {
            Directory.CreateDirectory(directory);
            ConnectionString = $"Data Source={Path.Combine(directory, "store.db")};Pooling=False";
            connection = new SqliteProviderFactory().Create(ConnectionString);
            var declaration = StorageUnit.Declare("evidence-rows", "evidence_rows")
                .String("id", 128, column => column.Required())
                .String("payload", 128, column =>
                {
                    if (!nullablePayload)
                        column.Required();
                })
                .Key("id");
            if (indexed)
                declaration.Index("by_payload_id", "payload", "id");
            if (scoped)
                declaration.Scoped();
            Unit = declaration.Build();
            try
            {
                Assert.True(connection.Schema.Apply(Unit).Applied);
                Session = connection.OpenSession(Unit,
                    scoped ? StorageAccess.Scoped(new StorageScope(SecretScope)) : StorageAccess.Global, Observer);
                Assert.Equal(WriteOutcomeStatus.Inserted, Session.Insert(new StorageValues(
                    new Dictionary<string, object?> { ["id"] = SecretKey, ["payload"] = SecretPayload })).Status);
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
        internal IStorageProviderConnection Connection => connection;
        internal EvidenceObserver Observer { get; } = new();

        public void Dispose()
        {
            connection.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class EvidenceObserver : IProviderExecutionObserver
    {
        private ProviderExecutionEvidenceOptions evidenceOptions = ProviderExecutionEvidenceOptions.ShapeOnly;
        public ProviderExecutionEvidenceOptions EvidenceOptions
        {
            get
            {
                OptionsCallback?.Invoke();
                return evidenceOptions;
            }
            set => evidenceOptions = value;
        }
        internal Action? OptionsCallback { get; set; }
        internal Action? IssuedCallback { get; set; }
        internal Action? TerminalCallback { get; set; }
        internal List<ProviderCommandEvent> Commands { get; } = [];
        internal List<ProviderExecutionEvidence> Executions { get; } = [];
        internal List<string> CallbackOrder { get; } = [];

        public void Observe(ProviderCommandEvent command)
        {
            Commands.Add(command);
            CallbackOrder.Add("issued");
            IssuedCallback?.Invoke();
        }

        public void ObserveExecution(ProviderExecutionEvidence evidence)
        {
            Executions.Add(evidence);
            CallbackOrder.Add("terminal");
            TerminalCallback?.Invoke();
        }

        internal void Clear()
        {
            Commands.Clear();
            Executions.Clear();
            CallbackOrder.Clear();
        }
    }

    /// <summary>#422: the shared lexicographic emitter records SQLite's continuation page value-free.</summary>
    [Fact]
    public void Continuation_page_records_its_lexicographic_branches_without_values()
    {
        var table = new TableId("records");
        var updated = new ColumnRef(table, "updated", QueryType.Int64, isNullable: true);
        var id = new ColumnRef(table, "id", QueryType.String, isNullable: false);
        System.Collections.Immutable.ImmutableArray<OrderTerm> order =
            [new OrderTerm(updated, OrderDirection.Descending, NullOrder.Last), new OrderTerm(id, nullOrder: NullOrder.First)];
        var first = new QueryRequest(table, Predicate.AlwaysTrue.Instance, order, Projection.ColumnsOnly(updated, id), Paging.Keyset(2));
        var token = QueryContinuationToken.Encode(first, QueryRenderOptions.Default, [QueryConstant.Of(updated, 4242L), QueryConstant.Of(id, "cursor-id")]);
        var page = new SqliteQueryRenderer().RenderForExecution(
            new QueryRequest(table, Predicate.AlwaysTrue.Instance, order, Projection.ColumnsOnly(updated, id), Paging.Continuation(token, 2)),
            QueryRenderOptions.Default, hasLookahead: true);

        var shape = Assert.IsType<ProviderBoundedQueryEvidence>(page.Shape);
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
                Assert.Equal(ProviderPredicateOperator.Equal, Assert.Single(branch.Equalities).Operator);
                Assert.Equal("id", branch.Boundary.LogicalColumn);
                Assert.Equal(ProviderPredicateOperator.LowerBound, branch.Boundary.Operator);
                Assert.Equal(ProviderPredicateComparison.Ordinal, branch.Boundary.Comparison);
                Assert.False(branch.BoundaryAdmitsNull);
            });
        var serialized = System.Text.Json.JsonSerializer.Serialize(shape);
        Assert.DoesNotContain("4242", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("cursor-id", serialized, StringComparison.Ordinal);
    }
}
