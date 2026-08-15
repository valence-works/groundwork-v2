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

    [Fact]
    public void Equality_prefix_and_ordered_suffix_are_covered()
    {
        var result = Check(
            new Predicate.Equal(Status, QueryConstant.Of(Status, "open")),
            [new OrderTerm(Created, OrderDirection.Descending, NullOrder.Last)],
            Paging.None,
            Index("ix_status_created", new CoverageIndexColumn("status"), new CoverageIndexColumn("created_at", OrderDirection.Descending)));

        Assert.True(result.IsCovered, result.Diagnostic?.Message);
        Assert.Equal("ix_status_created", result.Index!.Name);
        Assert.Empty(result.Diagnostics);
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
        Assert.Equal("GW-COVER-006", result.Diagnostic!.Code);
        Assert.Contains("compound index prefix", result.Diagnostic.Message, StringComparison.OrdinalIgnoreCase);
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

        Assert.True(result.IsCovered, result.Diagnostic?.Message);
    }

    [Fact]
    public void Starts_with_is_a_range_on_the_indexed_string_key()
    {
        var result = Check(
            new Predicate.StartsWith(Status, "op"),
            [],
            Paging.None,
            Index("ix_status", "status"));

        Assert.True(result.IsCovered, result.Diagnostic?.Message);
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
        Assert.Equal("GW-COVER-006", result.Diagnostic!.Code);
    }

    [Fact]
    public void A_range_before_the_equality_prefix_is_refused()
    {
        var predicate = new Predicate.And([
            new Predicate.Range(Tenant, Bound.Inclusive(QueryConstant.Of(Tenant, "a")), null),
            new Predicate.Equal(Status, QueryConstant.Of(Status, "open"))]);
        var result = Check(predicate, [], Paging.None, Index("ix_tenant_status", "tenant", "status"));

        Assert.False(result.IsCovered);
        Assert.Equal("GW-COVER-006", result.Diagnostic!.Code);
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
        Assert.Contains("single-value equality", result.Diagnostic!.Message, StringComparison.OrdinalIgnoreCase);
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
        Assert.Equal("GW-COVER-005", unbounded.Diagnostic!.Code);
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
        Assert.Contains("unbounded Count", unbounded.Diagnostic!.Message, StringComparison.OrdinalIgnoreCase);
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
        Assert.Equal("GW-COVER-016", crossColumn.Diagnostic!.Code);
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
        Assert.Equal("GW-COVER-009", nullValue.Diagnostic!.Code);
        Assert.True(nonNullValue.IsCovered);
        Assert.False(ordered.IsCovered);
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
        Assert.Equal("ix_tickets_status_created", result.Diagnostic!.NearestIndex!.Name);
        Assert.Contains("ix_tickets_status_created", result.Diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("assignee", result.Diagnostic.SuggestedDeclaration, StringComparison.Ordinal);
        Assert.Contains("GwIndex", result.Diagnostic.SuggestedDeclaration, StringComparison.Ordinal);
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
        Assert.Throws<NotSupportedException>(() => ((IList<CoverageDiagnostic>)result.Diagnostics).Clear());
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
        ResultShape? result = null) =>
        QueryCoverageChecker.Check(
            new QueryRequest(Table, predicate, order, Projection.All, paging, result ?? ResultShape.Rows.Instance),
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
}
