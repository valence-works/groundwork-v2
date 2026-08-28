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
        Assert.Contains("unbounded Count", unbounded.Refusal!.Message, StringComparison.OrdinalIgnoreCase);
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
    public void Unsupported_element_predicates_name_the_element_set_in_the_suggestion()
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
        Assert.Contains("ticket_id", result.Refusal.SuggestedDeclaration, StringComparison.Ordinal);
    }

    [Fact]
    public void Refusal_diagnostic_names_nearest_index_and_emits_covering_declaration()
    {
        var result = Check(
            new Predicate.And([
                new Predicate.Equal(Status, QueryConstant.Of(Status, "open")),
                new Predicate.Not(new Predicate.Equal(Assignee, QueryConstant.Of(Assignee, "sam")))]),
            [new OrderTerm(Created, OrderDirection.Descending, NullOrder.Last)],
            Paging.None,
            Index("ix_tickets_status_created", "status", new CoverageIndexColumn("created_at", OrderDirection.Descending)));

        Assert.False(result.IsCovered);
        Assert.Equal("ix_tickets_status_created", result.Refusal!.NearestIndex!.Name);
        Assert.Contains("ix_tickets_status_created", result.Refusal.Message, StringComparison.Ordinal);
        Assert.Contains("assignee", result.Refusal.SuggestedDeclaration, StringComparison.Ordinal);
        Assert.Contains("GwIndex", result.Refusal.SuggestedDeclaration, StringComparison.Ordinal);
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
