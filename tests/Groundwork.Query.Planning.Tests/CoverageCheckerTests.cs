using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Versioning;
using Groundwork.Query.Model;
using Groundwork.Query.Planning;
using Xunit;

namespace Groundwork.Query.Planning.Tests;

public sealed class CoverageCheckerTests
{
    private static readonly TableId Table = new("tickets");
    private static readonly ColumnRef Tenant = new(Table, "tenant", QueryType.String);
    private static readonly ColumnRef Status = new(Table, "status", QueryType.String);
    private static readonly ColumnRef Assignee = new(Table, "assignee", QueryType.String);
    private static readonly ColumnRef Created = new(Table, "created_at", QueryType.DateTimeOffset);
    private static readonly ColumnRef Id = new(Table, "id", QueryType.String, isNullable: false);
    private static readonly ColumnRef Amount = new(Table, "amount", QueryType.Decimal);
    private static readonly ColumnRef Enabled = new(Table, "enabled", QueryType.Boolean, isNullable: false);
    private static readonly ColumnRef Weight = new(Table, "weight", QueryType.Double);
    private static readonly ColumnRef Payload = new(Table, "payload", QueryType.Binary);
    private static readonly TableId Customers = new("customers");
    private static readonly ColumnRef CustomerId = new(Table, "customer_id", QueryType.String, isNullable: false);
    private static readonly ColumnRef CustomerRegion = new(Table, "customer_region", QueryType.String, isNullable: false);
    private static readonly ColumnRef TargetId = new(Customers, "id", QueryType.String, isNullable: false);
    private static readonly ColumnRef TargetRegion = new(Customers, "region", QueryType.String, isNullable: false);
    private static readonly ColumnRef TargetStatus = new(Customers, "status", QueryType.String);
    private static readonly ColumnRef TargetName = new(Customers, "name", QueryType.String);

    [Fact]
    public void Equality_prefix_and_ordered_suffix_are_covered()
    {
        var result = Check(
            new Predicate.Equal(Status, QueryConstant.Of(Status, "open")),
            [new OrderTerm(Created, OrderDirection.Descending, NullOrder.Last)],
            Paging.None,
            Index("ix_status_created", new CoverageIndexColumn("status"), new CoverageIndexColumn("created_at", OrderDirection.Descending)));

        Assert.True(result.IsCovered, result.Refusal?.Message);
        Assert.Equal("ix_status_created", result.Index!.Name);
        Assert.Empty(result.Refusals);
    }

    [Fact]
    public void Joined_query_requires_covered_driving_and_target_sides()
    {
        var request = JoinedRequest(
            new Predicate.Equal(Status, QueryConstant.Of(Status, "open")));

        var result = QueryCoverageChecker.Check(
            request,
            Candidates(
                [Index("ix_tickets_status", "status")],
                [Index("ix_customers_id", "id")]));

        Assert.True(result.IsCovered, result.Refusal?.Message);
        Assert.Equal("ix_tickets_status", result.Index!.Name);
    }

    [Theory]
    [MemberData(nameof(UncoveredTargetJoinPrefixes))]
    public void Joined_query_refuses_incomplete_reordered_or_nonleading_target_join_prefix(
        CoverageIndex targetIndex)
    {
        var request = JoinedRequest(
            new Predicate.Equal(Status, QueryConstant.Of(Status, "open")),
            composite: true);

        var result = QueryCoverageChecker.Check(
            request,
            Candidates(
                [Index("ix_tickets_status", "status")],
                [targetIndex]));

        Assert.False(result.IsCovered);
        Assert.Equal("GW-COVER-006", result.Refusal!.Code);
        Assert.Contains("target", result.Refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("prefix", result.Refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Joined_target_predicate_and_order_are_covered_by_the_join_index_suffix()
    {
        var request = JoinedRequest(
            new Predicate.And([
                new Predicate.Equal(Status, QueryConstant.Of(Status, "open")),
                new Predicate.Equal(TargetStatus, QueryConstant.Of(TargetStatus, "active"))]),
            [new OrderTerm(TargetName, OrderDirection.Descending, NullOrder.Last)]);

        var covered = QueryCoverageChecker.Check(
            request,
            Candidates(
                [Index("ix_tickets_status", "status")],
                [Index(
                    "ix_customers_id_status_name",
                    "id",
                    "status",
                    new CoverageIndexColumn("name", OrderDirection.Descending))]));
        var missingPredicate = QueryCoverageChecker.Check(
            request,
            Candidates(
                [Index("ix_tickets_status", "status")],
                [Index("ix_customers_id_name", "id", new CoverageIndexColumn("name", OrderDirection.Descending))]));

        Assert.True(covered.IsCovered, covered.Refusal?.Message);
        Assert.False(missingPredicate.IsCovered);
        Assert.Equal("GW-COVER-006", missingPredicate.Refusal!.Code);
    }

    [Fact]
    public void Joined_target_range_must_follow_the_complete_join_prefix()
    {
        var request = JoinedRequest(
            new Predicate.And([
                new Predicate.Equal(Status, QueryConstant.Of(Status, "open")),
                new Predicate.Range(TargetName, Bound.Inclusive(QueryConstant.Of(TargetName, "m")), null)]),
            [new OrderTerm(TargetName, OrderDirection.Ascending, NullOrder.First)]);

        var covered = QueryCoverageChecker.Check(
            request,
            Candidates(
                [Index("ix_tickets_status", "status")],
                [Index("ix_customers_id_name", "id", "name")]));
        var skipped = QueryCoverageChecker.Check(
            request,
            Candidates(
                [Index("ix_tickets_status", "status")],
                [Index("ix_customers_id_status_name", "id", "status", "name")]));

        Assert.True(covered.IsCovered, covered.Refusal?.Message);
        Assert.False(skipped.IsCovered);
        Assert.Equal("GW-COVER-006", skipped.Refusal!.Code);
    }

    [Fact]
    public void Target_only_bound_does_not_hide_an_unbounded_driving_scan()
    {
        var request = JoinedRequest(
            new Predicate.Equal(TargetStatus, QueryConstant.Of(TargetStatus, "active")));

        var result = QueryCoverageChecker.Check(
            request,
            Candidates(
                [Index("ix_tickets_status", "status")],
                [Index("ix_customers_id_status", "id", "status")]));

        Assert.False(result.IsCovered);
        Assert.Equal("GW-COVER-005", result.Refusal!.Code);
    }

    [Fact]
    public void Source_indexes_cannot_rescue_a_missing_target_index()
    {
        var request = JoinedRequest(
            new Predicate.Equal(Status, QueryConstant.Of(Status, "open")));

        var result = QueryCoverageChecker.Check(
            request,
            Candidates(
                [Index("ix_source_that_looks_like_target", "status", "id")],
                []));

        Assert.False(result.IsCovered);
        Assert.Equal("GW-COVER-006", result.Refusal!.Code);
        Assert.Contains("target", result.Refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Joined_predicate_disjunction_cannot_cross_table_sides()
    {
        var request = JoinedRequest(
            new Predicate.Or([
                new Predicate.Equal(Status, QueryConstant.Of(Status, "open")),
                new Predicate.Equal(TargetStatus, QueryConstant.Of(TargetStatus, "active"))]));

        var result = QueryCoverageChecker.Check(
            request,
            Candidates(
                [Index("ix_tickets_status", "status")],
                [Index("ix_customers_id_status", "id", "status")]));

        Assert.False(result.IsCovered);
        Assert.Equal("GW-COVER-016", result.Refusal!.Code);
        Assert.Contains("both sides", result.Refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Correlated_target_key_proves_a_sparse_join_prefix_is_present()
    {
        var request = JoinedRequest(
            new Predicate.Equal(Status, QueryConstant.Of(Status, "open")));
        var sparseTarget = new CoverageIndex(
            "ix_customers_id",
            [new CoverageIndexColumn("id")],
            IndexMissingValueBehavior.Excluded);

        var result = QueryCoverageChecker.Check(
            request,
            Candidates(
                [Index("ix_tickets_status", "status")],
                [sparseTarget]));

        Assert.True(result.IsCovered, result.Refusal?.Message);
    }

    [Fact]
    public void Runtime_enforcer_uses_the_same_side_aware_join_verdict()
    {
        var request = JoinedRequest(
            new Predicate.Equal(Status, QueryConstant.Of(Status, "open")));
        var covered = Candidates(
            [Index("ix_tickets_status", "status")],
            [Index("ix_customers_id", "id")]);

        QueryCoverageEnforcer.EnsureCovered(request, covered, DateTimeOffset.UtcNow);
        var exception = Assert.Throws<QueryCoverageException>(() =>
            QueryCoverageEnforcer.EnsureCovered(
                request,
                Candidates(covered.Driving, []),
                DateTimeOffset.UtcNow));

        Assert.Equal("GW-COVER-006", exception.Code);
        Assert.Contains("target", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_single_table_overload_fails_closed_for_a_join()
    {
        var request = JoinedRequest(
            new Predicate.Equal(Status, QueryConstant.Of(Status, "open")));

        var result = QueryCoverageChecker.Check(
            request,
            [Index("ix_tickets_status_id", "status", "id")]);

        Assert.False(result.IsCovered);
        Assert.Equal("GW-COVER-006", result.Refusal!.Code);
        Assert.Contains("target", result.Refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(OrderDirection.Ascending, OrderDirection.Ascending)]
    [InlineData(OrderDirection.Descending, OrderDirection.Descending)]
    [InlineData(OrderDirection.Ascending, OrderDirection.Descending)]
    [InlineData(OrderDirection.Descending, OrderDirection.Ascending)]
    public void Compound_order_accepts_uniform_same_or_opposite_directions(
        OrderDirection indexDirection,
        OrderDirection requestedDirection)
    {
        var result = Check(
            new Predicate.Equal(Status, QueryConstant.Of(Status, "open")),
            [new OrderTerm(Created, requestedDirection, NullOrderFor(requestedDirection))],
            Paging.Continuation("cursor"),
            Index(
                "ix_status_created",
                new CoverageIndexColumn("status", indexDirection),
                new CoverageIndexColumn("created_at", indexDirection)));

        Assert.True(result.IsCovered, result.Refusal?.Message);
    }

    [Fact]
    public void Compound_order_rejects_mixed_direction_suffixes()
    {
        var result = Check(
            new Predicate.Equal(Status, QueryConstant.Of(Status, "open")),
            [
                new OrderTerm(Created, OrderDirection.Ascending, NullOrder.First),
                new OrderTerm(Id, OrderDirection.Descending, NullOrder.Last)
            ],
            Paging.Continuation("cursor"),
            Index(
                "ix_status_created_id",
                new CoverageIndexColumn("status"),
                new CoverageIndexColumn("created_at"),
                new CoverageIndexColumn("id")));

        Assert.False(result.IsCovered);
        Assert.Equal("GW-COVER-006", result.Refusal!.Code);
    }

    [Fact]
    public void A_nonleading_predicate_is_refused_instead_of_using_a_skip_scan()
    {
        var result = Check(
            new Predicate.Equal(Status, QueryConstant.Of(Status, "open")),
            [],
            Paging.None,
            Index("ix_created_status", "created_at", "status"));

        Assert.False(result.IsCovered);
        Assert.Equal("GW-COVER-006", result.Refusal!.Code);
        Assert.Contains("compound index prefix", result.Refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Range_may_follow_equalities_and_lead_the_requested_order()
    {
        var predicate = new Predicate.And([
            new Predicate.Equal(Tenant, QueryConstant.Of(Tenant, "acme")),
            new Predicate.Equal(Status, QueryConstant.Of(Status, "open")),
            new Predicate.Range(Created, Bound.Inclusive(QueryConstant.Of(Created, DateTimeOffset.UnixEpoch)), null)]);
        var result = Check(
            predicate,
            [
                new OrderTerm(Created, OrderDirection.Ascending, NullOrder.First),
                new OrderTerm(Id, OrderDirection.Ascending, NullOrder.First)
            ],
            Paging.Continuation("cursor"),
            Index("ix_tenant_status_created_id", "tenant", "status", "created_at", "id"));

        Assert.True(result.IsCovered, result.Refusal?.Message);
    }

    [Fact]
    public void Starts_with_is_a_range_on_the_indexed_string_key()
    {
        var result = Check(
            new Predicate.StartsWith(Status, "op"),
            [],
            Paging.None,
            Index("ix_status", "status"));

        Assert.True(result.IsCovered, result.Refusal?.Message);
    }

    [Fact]
    public void Nonportable_ordering_and_range_types_are_refused_by_the_checker()
    {
        var boolean = new ColumnRef(Table, "is_open", QueryType.Boolean);
        var binary = new ColumnRef(Table, "payload", QueryType.Binary);
        var order = Check(
            Predicate.AlwaysTrue.Instance,
            [new OrderTerm(boolean, OrderDirection.Ascending, NullOrder.First)],
            Paging.OffsetLimit(0, 10),
            Index("ix_is_open", "is_open"));
        var range = Check(
            new Predicate.Range(binary, Bound.Inclusive(QueryConstant.Of(binary, new byte[] { 1 })), null),
            [],
            Paging.None,
            Index("ix_payload", "payload"));

        Assert.False(order.IsCovered);
        Assert.Equal("GW-COVER-016", order.Refusal!.Code);
        Assert.False(range.IsCovered);
        Assert.Equal("GW-COVER-016", range.Refusal!.Code);
    }

    [Fact]
    public void Always_false_queries_still_validate_nonportable_ordering()
    {
        var boolean = new ColumnRef(Table, "is_open", QueryType.Boolean);
        var request = new QueryRequest(
            Table,
            Predicate.AlwaysFalse.Instance,
            [new OrderTerm(boolean, OrderDirection.Ascending, NullOrder.First)],
            Projection.All,
            Paging.None,
            ResultShape.First.Instance);

        var result = QueryCoverageChecker.Check(request, [Index("ix_is_open", "is_open")]);

        Assert.False(result.IsCovered);
        Assert.Equal("GW-COVER-016", result.Refusal!.Code);
    }

    [Fact]
    public void Provider_default_null_ordering_is_refused()
    {
        var result = Check(
            Predicate.AlwaysTrue.Instance,
            [new OrderTerm(Created, OrderDirection.Ascending)],
            Paging.OffsetLimit(0, 10),
            Index("ix_created", "created_at"));

        Assert.False(result.IsCovered);
        Assert.Equal("GW-COVER-016", result.Refusal!.Code);
    }

    [Fact]
    public void Provider_default_null_ordering_does_not_suggest_an_index()
    {
        var result = Check(
            Predicate.AlwaysTrue.Instance,
            [new OrderTerm(Created, OrderDirection.Ascending)],
            Paging.OffsetLimit(0, 10),
            Index("ix_created", "created_at"));

        Assert.False(result.IsCovered);
        Assert.Equal(
            "Query on 'tickets' is not index-covered. Provider-default null ordering is not portable; choose explicit nulls-first or nulls-last ordering. Nearest index 'ix_created' (created_at ASC). Or mark the read: .AcceptScan(\"GW-SCAN-nnnn\", reason: \"reason\", owner: \"team\", expiresOn: \"yyyy-MM-dd\").",
            result.Refusal!.Message);
        Assert.Null(result.Refusal.SuggestedIndex);
    }

    [Fact]
    public void Boolean_double_and_binary_ordering_do_not_suggest_an_index()
    {
        var columns = new[]
        {
            (Column: Enabled, IndexName: "ix_enabled", ExpectedMessage: "Query on 'tickets' is not index-covered. Ordering this type is not portable; order a declared portable projection or key instead. Nearest index 'ix_enabled' (enabled ASC). Or mark the read: .AcceptScan(\"GW-SCAN-nnnn\", reason: \"reason\", owner: \"team\", expiresOn: \"yyyy-MM-dd\")."),
            (Column: Weight, IndexName: "ix_weight", ExpectedMessage: "Query on 'tickets' is not index-covered. Ordering this type is not portable; order a declared portable projection or key instead. Nearest index 'ix_weight' (weight ASC). Or mark the read: .AcceptScan(\"GW-SCAN-nnnn\", reason: \"reason\", owner: \"team\", expiresOn: \"yyyy-MM-dd\")."),
            (Column: Payload, IndexName: "ix_payload", ExpectedMessage: "Query on 'tickets' is not index-covered. Ordering this type is not portable; order a declared portable projection or key instead. Nearest index 'ix_payload' (payload ASC). Or mark the read: .AcceptScan(\"GW-SCAN-nnnn\", reason: \"reason\", owner: \"team\", expiresOn: \"yyyy-MM-dd\").")
        };

        foreach (var (column, indexName, expectedMessage) in columns)
        {
            var result = Check(
                Predicate.AlwaysTrue.Instance,
                [new OrderTerm(column, OrderDirection.Ascending, NullOrder.First)],
                Paging.OffsetLimit(0, 10),
                Index(indexName, column.Name));

            Assert.False(result.IsCovered);
            Assert.Equal("GW-COVER-016", result.Refusal!.Code);
            Assert.Equal(expectedMessage, result.Refusal.Message);
            Assert.Null(result.Refusal.SuggestedIndex);
        }
    }

    [Fact]
    public void Nonportable_range_ordering_does_not_suggest_an_index()
    {
        var result = Check(
            new Predicate.Range(Payload, Bound.Inclusive(QueryConstant.Of(Payload, new byte[] { 1 })), null),
            [],
            Paging.None,
            Index("ix_payload", "payload"));

        Assert.False(result.IsCovered);
        Assert.Equal(
            "Query on 'tickets' is not index-covered. Range ordering for column 'payload' is not portable; use equality/membership or a declared orderable projection instead. Nearest index 'ix_payload' (payload ASC). Or mark the read: .AcceptScan(\"GW-SCAN-nnnn\", reason: \"reason\", owner: \"team\", expiresOn: \"yyyy-MM-dd\").",
            result.Refusal!.Message);
        Assert.Null(result.Refusal.SuggestedIndex);
    }

    [Fact]
    public void Cross_column_or_does_not_suggest_an_index()
    {
        var result = Check(
            new Predicate.Or([
                new Predicate.Equal(Status, QueryConstant.Of(Status, "open")),
                new Predicate.Equal(Assignee, QueryConstant.Of(Assignee, "sam"))]),
            [],
            Paging.None,
            Index("ix_status_assignee", "status", "assignee"));

        Assert.False(result.IsCovered);
        Assert.Equal(
            "Query on 'tickets' is not index-covered. A cross-column Or is not index-covered; only a single-column Or folded to In is portable. Nearest index 'ix_status_assignee' (status ASC, assignee ASC). Or mark the read: .AcceptScan(\"GW-SCAN-nnnn\", reason: \"reason\", owner: \"team\", expiresOn: \"yyyy-MM-dd\").",
            result.Refusal!.Message);
        Assert.Null(result.Refusal.SuggestedIndex);
    }

    [Fact]
    public void Non_index_representable_predicate_does_not_suggest_an_index()
    {
        var result = Check(
            new Predicate.Substring(Status, "open", Anchor.Contains),
            [],
            Paging.None,
            Index("ix_status", "status"));

        Assert.False(result.IsCovered);
        Assert.Equal(
            "Query on 'tickets' is not index-covered. The query contains a predicate that cannot be represented by an ordered index. Nearest index 'ix_status' (status ASC). Or mark the read: .AcceptScan(\"GW-SCAN-nnnn\", reason: \"reason\", owner: \"team\", expiresOn: \"yyyy-MM-dd\").",
            result.Refusal!.Message);
        Assert.Null(result.Refusal.SuggestedIndex);
    }

    [Fact]
    public void Multiple_range_columns_are_not_portable()
    {
        var result = Check(
            new Predicate.And([
                new Predicate.Range(Tenant, Bound.Inclusive(QueryConstant.Of(Tenant, "a")), null),
                new Predicate.Range(Created, Bound.Inclusive(QueryConstant.Of(Created, DateTimeOffset.UnixEpoch)), null)]),
            [],
            Paging.None,
            Index("ix_tenant_created", "tenant", "created_at"));

        Assert.False(result.IsCovered);
        Assert.Equal("GW-COVER-006", result.Refusal!.Code);
    }

    [Fact]
    public void A_range_before_the_equality_prefix_is_refused()
    {
        var predicate = new Predicate.And([
            new Predicate.Range(Tenant, Bound.Inclusive(QueryConstant.Of(Tenant, "a")), null),
            new Predicate.Equal(Status, QueryConstant.Of(Status, "open"))]);
        var result = Check(predicate, [], Paging.None, Index("ix_tenant_status", "tenant", "status"));

        Assert.False(result.IsCovered);
        Assert.Equal("GW-COVER-006", result.Refusal!.Code);
    }

    [Fact]
    public void Membership_before_an_ordered_suffix_requires_a_single_value()
    {
        var predicate = new Predicate.In(Status, [
            QueryConstant.Of(Status, "open"),
            QueryConstant.Of(Status, "closed")]);
        var result = Check(
            predicate,
            [new OrderTerm(Created, OrderDirection.Ascending, NullOrder.First)],
            Paging.Continuation("cursor"),
            Index("ix_status_created", "status", "created_at"));

        Assert.False(result.IsCovered);
        Assert.Contains("single-value equality", result.Refusal!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_bounded_ordered_take_is_the_unfiltered_carve_out()
    {
        var covered = Check(
            Predicate.AlwaysTrue.Instance,
            [new OrderTerm(Created, OrderDirection.Descending, NullOrder.Last)],
            Paging.OffsetLimit(0, 20),
            Index("ix_created", new CoverageIndexColumn("created_at", OrderDirection.Descending)));
        var unbounded = Check(
            Predicate.AlwaysTrue.Instance,
            [new OrderTerm(Created, OrderDirection.Descending, NullOrder.Last)],
            Paging.None,
            Index("ix_created", new CoverageIndexColumn("created_at", OrderDirection.Descending)));

        Assert.True(covered.IsCovered);
        Assert.False(unbounded.IsCovered);
        Assert.Equal("GW-COVER-005", unbounded.Refusal!.Code);
    }

    [Fact]
    public void Bounded_distinct_projection_is_covered_when_all_projected_columns_are_indexed()
    {
        var result = Check(
            new Predicate.Equal(Status, QueryConstant.Of(Status, "open")),
            [new OrderTerm(Status, OrderDirection.Ascending, NullOrder.Last)],
            Paging.OffsetLimit(0, 1),
            Index("ix_status", "status"),
            projection: Projection.ColumnsOnly(Status),
            distinct: true);

        Assert.True(result.IsCovered, result.Refusal?.Message);
        Assert.Equal("ix_status", result.Index!.Name);
    }

    [Fact]
    public void Unbounded_distinct_projection_is_not_covered_by_its_projection_index()
    {
        var result = Check(
            Predicate.AlwaysTrue.Instance,
            [],
            Paging.None,
            Index("ix_status", "status"),
            projection: Projection.ColumnsOnly(Status),
            distinct: true);

        Assert.False(result.IsCovered);
        Assert.Equal("GW-COVER-005", result.Refusal!.Code);
    }

    [Fact]
    public void Bounded_order_does_not_admit_an_unfiltered_distinct_scan()
    {
        var result = Check(
            Predicate.AlwaysTrue.Instance,
            [new OrderTerm(Status, OrderDirection.Ascending, NullOrder.Last)],
            Paging.OffsetLimit(0, 1),
            Index("ix_status", "status"),
            projection: Projection.ColumnsOnly(Status),
            distinct: true);

        Assert.False(result.IsCovered);
        Assert.Equal("GW-COVER-005", result.Refusal!.Code);
    }

    [Fact]
    public void Unbounded_distinct_projection_accepts_a_live_scan_without_stale_marker()
    {
        var request = new QueryRequest(
            Table,
            Predicate.AlwaysTrue.Instance,
            [],
            Projection.ColumnsOnly(Status),
            Paging.None,
            acceptedScan: ScanAcceptance.Allow(
                "GW-SCAN-DISTINCT",
                "distinct report",
                "query-team",
                new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            distinct: true);

        var result = QueryCoverageChecker.Check(request, [Index("ix_status", "status")]);

        Assert.False(result.IsCovered);
        Assert.Equal("GW-COVER-005", result.Refusal!.Code);
        Assert.NotEqual("GW-COVER-901", result.Refusal.Code);
        QueryCoverageEnforcer.EnsureCovered(request, [Index("ix_status", "status")],
            new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Distinct_projection_requires_an_index_or_an_accepted_scan()
    {
        var result = Check(
            Predicate.AlwaysTrue.Instance,
            [],
            Paging.None,
            Index("ix_other", "other"),
            projection: Projection.ColumnsOnly(Status),
            distinct: true);

        Assert.False(result.IsCovered);
        Assert.Equal("GW-COVER-005", result.Refusal!.Code);
        Assert.Contains("unbounded Distinct", result.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Cardinality_results_require_an_explicit_order()
    {
        var request = new QueryRequest(
            Table,
            Predicate.AlwaysTrue.Instance,
            [],
            Projection.All,
            Paging.OffsetLimit(0, 1),
            ResultShape.First.Instance);

        var result = QueryCoverageChecker.Check(request, [Index("ix_created", "created_at")]);

        Assert.False(result.IsCovered);
        Assert.Equal("GW-COVER-016", result.Refusal!.Code);
        Assert.Contains("deterministic order", result.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reduction_column_must_be_present_in_the_covering_index()
    {
        var request = new QueryRequest(
            Table,
            new Predicate.Equal(Status, QueryConstant.Of(Status, "open")),
            [],
            Projection.ColumnsOnly(Amount),
            Paging.None,
            new ResultShape.Sum(Amount));

        var covered = QueryCoverageChecker.Check(request, [Index("ix_status_amount", "status", "amount")]);
        var uncovered = QueryCoverageChecker.Check(request, [Index("ix_status", "status")]);

        Assert.True(covered.IsCovered);
        Assert.False(uncovered.IsCovered);
        Assert.Equal("GW-COVER-006", uncovered.Refusal!.Code);
        Assert.Contains("reduction column", uncovered.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reduction_type_is_refused_before_index_coverage()
    {
        var request = new QueryRequest(
            Table,
            new Predicate.Equal(Status, QueryConstant.Of(Status, "open")),
            [],
            Projection.ColumnsOnly(Enabled),
            Paging.None,
            new ResultShape.Min(Enabled));

        var result = QueryCoverageChecker.Check(request, [Index("ix_status_enabled", "status", "enabled")]);

        Assert.False(result.IsCovered);
        Assert.Equal("GW-COVER-016", result.Refusal!.Code);
        Assert.Contains("orderable", result.Refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_always_false_predicate_needs_no_index_or_provider_read()
    {
        var result = QueryCoverageChecker.Check(
            new QueryRequest(Table, Predicate.AlwaysFalse.Instance, [], Projection.All, Paging.None, ResultShape.Rows.Instance),
            []);

        Assert.True(result.IsCovered);
        Assert.Null(result.Index);
        Assert.Empty(result.Refusals);
    }

    [Fact]
    public void Covered_query_with_accept_scan_is_a_stale_marker_error()
    {
        var request = new QueryRequest(
            Table,
            new Predicate.Equal(Status, QueryConstant.Of(Status, "open")),
            [],
            Projection.All,
            Paging.None,
            acceptedScan: ScanAcceptance.Allow(
                "GW-SCAN-0007",
                "admin report",
                "billing",
                new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        var result = QueryCoverageChecker.Check(request, [Index("ix_status", Status.Name)]);

        Assert.False(result.IsCovered);
        Assert.Equal("GW-COVER-901", result.Refusal!.Code);
    }

    [Fact]
    public void Runtime_enforcement_allows_only_an_active_acceptance_for_an_uncovered_query()
    {
        var request = new QueryRequest(
            Table,
            new Predicate.Substring(Status, "open", Anchor.Contains),
            [],
            Projection.All,
            Paging.None);

        var exception = Assert.Throws<QueryCoverageException>(() =>
            QueryCoverageEnforcer.EnsureCovered(request, [Index("ix_status", Status.Name)], DateTimeOffset.UtcNow));
        Assert.Equal("GW-COVER-016", exception.Code);

        var accepted = new QueryRequest(
            Table,
            request.Where,
            [],
            Projection.All,
            Paging.None,
            acceptedScan: ScanAcceptance.Allow(
                "GW-SCAN-0008",
                "admin report",
                "billing",
                new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        QueryCoverageEnforcer.EnsureCovered(
            accepted,
            [Index("ix_status", Status.Name)],
            new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero));

        var expired = Assert.Throws<QueryCoverageException>(() =>
            QueryCoverageEnforcer.EnsureCovered(
                accepted,
                [Index("ix_status", Status.Name)],
                new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal("GW-COVER-903", expired.Code);
    }

    [Fact]
    public void An_unbounded_count_is_refused_but_a_bounded_count_with_a_key_is_covered()
    {
        var unbounded = Check(
            Predicate.AlwaysTrue.Instance,
            [],
            Paging.None,
            Index("ix_status", "status"),
            ResultShape.TotalCount.Instance);
        var bounded = Check(
            new Predicate.Equal(Status, QueryConstant.Of(Status, "open")),
            [],
            Paging.None,
            Index("ix_status", "status"),
            ResultShape.TotalCount.Instance);

        Assert.False(unbounded.IsCovered);
        Assert.Equal(
            "Query on 'tickets' is not index-covered. An unbounded Count is not index-covered; full counts are scans. Nearest index 'ix_status' (status ASC). Add: [GwIndex(\"ix_tickets\", \"<query-bound> ASC\")] Or mark the read: .AcceptScan(\"GW-SCAN-nnnn\", reason: \"reason\", owner: \"team\", expiresOn: \"yyyy-MM-dd\").",
            unbounded.Refusal!.Message);
        Assert.Equal("[GwIndex(\"ix_tickets\", \"<query-bound> ASC\")]", unbounded.Refusal.SuggestedDeclaration);
        Assert.True(bounded.IsCovered);
    }

    [Fact]
    public void Same_column_or_is_normalized_to_membership_but_cross_column_or_is_refused()
    {
        var sameColumn = Check(
            new Predicate.Or([
                new Predicate.Equal(Status, QueryConstant.Of(Status, "open")),
                new Predicate.Equal(Status, QueryConstant.Of(Status, "closed"))]),
            [],
            Paging.None,
            Index("ix_status", "status"));
        var crossColumn = Check(
            new Predicate.Or([
                new Predicate.Equal(Status, QueryConstant.Of(Status, "open")),
                new Predicate.Equal(Assignee, QueryConstant.Of(Assignee, "sam"))]),
            [],
            Paging.None,
            Index("ix_status_assignee", "status", "assignee"));

        Assert.True(sameColumn.IsCovered);
        Assert.False(crossColumn.IsCovered);
        Assert.Equal("GW-COVER-016", crossColumn.Refusal!.Code);
    }

    [Fact]
    public void Sparse_indexes_must_prove_nonnull_values()
    {
        var nullValue = Check(
            new Predicate.Equal(Status, QueryConstant.Of(Status, null)),
            [],
            Paging.None,
            new CoverageIndex("ix_status_sparse", new[] { new CoverageIndexColumn("status") }, IndexMissingValueBehavior.Excluded));
        var nonNullValue = Check(
            new Predicate.Equal(Status, QueryConstant.Of(Status, "open")),
            [],
            Paging.None,
            new CoverageIndex("ix_status_sparse", new[] { new CoverageIndexColumn("status") }, IndexMissingValueBehavior.Excluded));
        var ordered = Check(
            Predicate.AlwaysTrue.Instance,
            [new OrderTerm(Status, OrderDirection.Ascending, NullOrder.First)],
            Paging.OffsetLimit(0, 10),
            new CoverageIndex("ix_status_sparse", new[] { new CoverageIndexColumn("status") }, IndexMissingValueBehavior.Excluded));

        Assert.False(nullValue.IsCovered);
        Assert.Equal("GW-COVER-009", nullValue.Refusal!.Code);
        Assert.True(nonNullValue.IsCovered);
        Assert.False(ordered.IsCovered);
    }

    [Fact]
    public void Sparse_indexes_do_not_reject_nonnullable_keys_without_a_predicate()
    {
        var result = Check(
            Predicate.AlwaysTrue.Instance,
            [new OrderTerm(Id, OrderDirection.Ascending, NullOrder.First)],
            Paging.OffsetLimit(0, 20),
            new CoverageIndex(
                "ix_id_sparse",
                [new CoverageIndexColumn("id", OrderDirection.Ascending, isNullable: false)],
                IndexMissingValueBehavior.Excluded));

        Assert.True(result.IsCovered, result.Refusal?.Message);
    }

    [Fact]
    public void Unsupported_element_predicates_do_not_suggest_an_index()
    {
        var result = Check(
            new Predicate.ElementOf(
                new ElementSetRef("ticket_id", QueryType.String),
                [QueryConstant.Of(Status, "open")],
                SetQuantifier.Any),
            [],
            Paging.None,
            Index("ix_status", "status"));

        Assert.False(result.IsCovered);
        Assert.Equal("GW-COVER-016", result.Refusal!.Code);
        Assert.Null(result.Refusal.SuggestedIndex);
        Assert.DoesNotContain("Add: [GwIndex(", result.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Refusal_diagnostic_names_nearest_index_without_an_index_suggestion()
    {
        var result = Check(
            new Predicate.And([
                new Predicate.Equal(Status, QueryConstant.Of(Status, "open")),
                new Predicate.Not(new Predicate.Equal(Assignee, QueryConstant.Of(Assignee, "sam")))]),
            [new OrderTerm(Created, OrderDirection.Descending, NullOrder.Last)],
            Paging.None,
            Index("ix_tickets_status_created", "status", new CoverageIndexColumn("created_at", OrderDirection.Descending)));

        Assert.False(result.IsCovered);
        Assert.Equal("GW-COVER-016", result.Refusal!.Code);
        Assert.Equal("ix_tickets_status_created", result.Refusal!.NearestIndex!.Name);
        Assert.Contains("ix_tickets_status_created", result.Refusal.Message, StringComparison.Ordinal);
        Assert.Null(result.Refusal.SuggestedIndex);
        Assert.DoesNotContain("Add: [GwIndex(", result.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Suggested_declaration_uses_one_parseable_spec_and_preserves_order_direction()
    {
        var result = Check(
            new Predicate.Equal(Status, QueryConstant.Of(Status, "open")),
            [new OrderTerm(Created, OrderDirection.Descending, NullOrder.Last)],
            Paging.None,
            Index("ix_wrong", "assignee"));

        Assert.Equal(
            "[GwIndex(\"ix_tickets\", \"status ASC, created_at DESC\")]",
            result.Refusal!.SuggestedDeclaration);
    }

    [Fact]
    public void Index_contract_rejects_duplicate_columns_and_unknown_enum_values()
    {
        Assert.Throws<ArgumentException>(() => Index("duplicate", "status", "status"));
        Assert.Throws<ArgumentException>(() => new CoverageIndex("null-column", new CoverageIndexColumn[] { null! }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoverageIndexColumn("status", (OrderDirection)999));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CoverageIndex("invalid", [new CoverageIndexColumn("status")], (IndexMissingValueBehavior)999));
    }

    [Fact]
    public void Coverage_result_and_index_collections_are_snapshots()
    {
        var columns = new List<CoverageIndexColumn> { new("status") };
        var index = new CoverageIndex("ix_status", columns);
        columns.Add(new CoverageIndexColumn("created_at"));
        var result = Check(new Predicate.Equal(Status, QueryConstant.Of(Status, "open")), [], Paging.None, index);

        Assert.Single(index.Columns);
        Assert.True(result.IsCovered);
        Assert.Throws<NotSupportedException>(() => ((IList<CoverageRefusal>)result.Refusals).Clear());
    }

    [Fact]
    public void Joined_candidate_collections_are_validated_immutable_snapshots()
    {
        var driving = new List<CoverageIndex> { Index("ix_status", "status") };
        var target = new List<CoverageIndex> { Index("ix_customer_id", "id") };

        var candidates = new QueryCoverageCandidates(driving, target);
        driving.Clear();
        target.Clear();

        Assert.Single(candidates.Driving);
        Assert.Single(candidates.Target);
        Assert.Throws<ArgumentNullException>(() => new QueryCoverageCandidates(null!, []));
        Assert.Throws<ArgumentNullException>(() => new QueryCoverageCandidates([], null!));
        Assert.Throws<ArgumentException>(() => new QueryCoverageCandidates([null!], []));
        Assert.Throws<ArgumentException>(() => new QueryCoverageCandidates([], [null!]));
    }

    [Fact]
    public void Planning_assembly_is_netstandard_and_has_one_checker_without_provider_or_ado_references()
    {
        var assembly = typeof(QueryCoverageChecker).Assembly;
        var framework = assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;
        Assert.Equal(".NETStandard,Version=v2.0", framework);
        var references = assembly.GetReferencedAssemblies().Select(reference => reference.Name!).ToArray();
        Assert.DoesNotContain(references, name => name.Contains("Data", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(references, name => name.Contains("Provider", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(references, name => name.StartsWith("Groundwork.Kernel", StringComparison.Ordinal));
        var implementations = assembly.GetExportedTypes()
            .Where(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Any(method => method.Name == "Check" && method.GetParameters().Length == 2))
            .ToArray();
        Assert.Single(implementations);
        Assert.Equal(typeof(QueryCoverageChecker), implementations[0]);
    }

    public static TheoryData<CoverageIndex> UncoveredTargetJoinPrefixes => new()
    {
        Index("ix_customers_partial", "id"),
        Index("ix_customers_reordered", "region", "id"),
        Index("ix_customers_nonleading", "status", "id", "region")
    };

    private static QueryRequest JoinedRequest(
        Predicate predicate,
        ImmutableArray<OrderTerm> order = default,
        bool composite = false)
    {
        var pairs = composite
            ? new[]
            {
                new JoinColumnPair(CustomerId, TargetId),
                new JoinColumnPair(CustomerRegion, TargetRegion)
            }
            : [new JoinColumnPair(CustomerId, TargetId)];
        return new QueryRequest(
            Table,
            new ReferenceJoin("customer", Customers, pairs),
            predicate,
            order.IsDefault ? [] : order,
            Projection.All,
            Paging.OffsetLimit(0, 25));
    }

    private static QueryCoverageResult Check(
        Predicate predicate,
        ImmutableArray<OrderTerm> order,
        Paging paging,
        CoverageIndex index,
        ResultShape? result = null,
        Projection? projection = null,
        bool distinct = false) =>
        QueryCoverageChecker.Check(
            new QueryRequest(Table, predicate, order, projection ?? Projection.All, paging, result ?? ResultShape.Rows.Instance, distinct: distinct),
            [index]);

    private static QueryCoverageCandidates Candidates(
        IEnumerable<CoverageIndex> driving,
        IEnumerable<CoverageIndex> target) =>
        new(driving, target);

    private static CoverageIndex Index(string name, params object[] columns) =>
        new(
            name,
            columns.Select(column => column switch
            {
                string value => new CoverageIndexColumn(value),
                CoverageIndexColumn typed => typed,
                _ => throw new ArgumentException("Unsupported test index column.", nameof(columns))
            }));

    private static NullOrder NullOrderFor(OrderDirection direction) =>
        direction == OrderDirection.Ascending ? NullOrder.First : NullOrder.Last;
}
