using System.Linq.Expressions;
using Groundwork.Query.Linq;
using Groundwork.Query.Model;
using Xunit;
using Groundwork.Query.Linq.Fragments;

namespace Groundwork.Query.Linq.Tests;

public sealed class LinqFrontEndTests
{
    public sealed class Ticket
    {
        public int TenantId { get; set; }
        public string? Status { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public bool IsOpen { get; set; }
        public decimal Amount { get; set; }
        public int OtherTenant { get; set; }
        public IReadOnlyList<int> TagIds { get; set; } = Array.Empty<int>();
        public long LongValue { get; set; }
        public DateTimeOffset? OptionalAt { get; set; }
    }

    internal static readonly GwTableModel<Ticket> Tickets = new("tickets", new[]
    {
        new GwColumn<Ticket>(nameof(Ticket.TenantId), nameof(Ticket.TenantId), QueryType.Int32, false),
        new GwColumn<Ticket>(nameof(Ticket.Status), nameof(Ticket.Status), QueryType.String),
        new GwColumn<Ticket>(nameof(Ticket.CreatedAt), nameof(Ticket.CreatedAt), QueryType.DateTimeOffset, false),
        new GwColumn<Ticket>(nameof(Ticket.IsOpen), nameof(Ticket.IsOpen), QueryType.Boolean, false),
        new GwColumn<Ticket>(nameof(Ticket.Amount), nameof(Ticket.Amount), QueryType.Decimal, false, DecimalPrecision: 18, DecimalScale: 4),
        new GwColumn<Ticket>(nameof(Ticket.OtherTenant), nameof(Ticket.OtherTenant), QueryType.Int32, false),
        new GwColumn<Ticket>(nameof(Ticket.LongValue), nameof(Ticket.LongValue), QueryType.Int64, false),
        new GwColumn<Ticket>(nameof(Ticket.OptionalAt), nameof(Ticket.OptionalAt), QueryType.DateTimeOffset, true)
    }, new[] { new GwElementSet<Ticket>(nameof(Ticket.TagIds), "tag_ids", QueryType.Int32) });

    private static class Fragments
    {
        [GwQueryFragment]
        public static Expression<Func<Ticket, bool>> Open => ticket => ticket.IsOpen;
    }

    [Fact]
    public void Closed_surface_lowers_to_the_existing_ast()
    {
        var tenant = 42;
        var since = DateTimeOffset.UtcNow.AddDays(-1);
        var request = new GwQueryDatabase().Table(Tickets).Query
            .Where(ticket => ticket.TenantId == tenant && ticket.CreatedAt >= since && ticket.IsOpen)
            .OrderByDescending(ticket => ticket.CreatedAt)
            .ThenBy(ticket => ticket.TenantId)
            .Take(50)
            .ToQueryRequest();

        Assert.Equal("tickets", request.Table.Value);
        Assert.IsType<Predicate.And>(request.Where);
        Assert.Equal(2, request.Order.Length);
        Assert.Equal(50, request.Paging.Limit);
        Assert.Equal(NullOrder.First, request.Order[0].NullOrder);

        var direct = new GwQueryDatabase().Table(Tickets)
            .Where(ticket => ticket.TenantId == tenant)
            .Take(1)
            .ToQueryRequest();
        Assert.Equal(1, direct.Paging.Limit);
    }

    [Fact]
    public void WhereIf_and_terminals_are_closed_operations()
    {
        var query = new GwQueryDatabase().Table(Tickets).Query
            .WhereIf(false, ticket => ticket.TenantId == 99)
            .WhereIf(true, ticket => ticket.IsOpen);

        Assert.IsType<Predicate.Equal>(query.ToQueryRequest().Where);
        Assert.IsType<ResultShape.TotalCount>(query.Count().Request.Result);
        Assert.Equal(1, query.Any().Request.Paging.Limit);
    }

    [Fact]
    public async Task Async_terminals_use_an_explicit_provider_adapter()
    {
        var query = new GwQueryDatabase().Table(Tickets).Query.Where(ticket => ticket.TenantId == 7);
        var result = await query.CountAsync(new RecordingExecutor());
        Assert.Equal(1, result);
    }

    private sealed class RecordingExecutor : IGwQueryExecutor
    {
        public Task<IReadOnlyList<T>> ToListAsync<T>(QueryRequest request, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<T>>(Array.Empty<T>());
        public Task<long> CountAsync(QueryRequest request, CancellationToken cancellationToken = default) => Task.FromResult(1L);
        public Task<bool> AnyAsync(QueryRequest request, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    [Fact]
    public void Bare_string_matching_and_column_arithmetic_are_diagnostics()
    {
        var bare = ExpressionLowerer.Diagnose<Ticket>(ticket => ticket.Status!.StartsWith("x"), Tickets);
        Assert.Contains(bare, diagnostic => diagnostic.Code == "GW-LINQ-108");

        var arithmetic = ExpressionLowerer.Diagnose<Ticket>(ticket => ticket.TenantId + 1 > 2, Tickets);
        Assert.Contains(arithmetic, diagnostic => diagnostic.Code == "GW-LINQ-102");
        Assert.All(bare.Concat(arithmetic), diagnostic => Assert.NotEqual(ExpressionType.Lambda, diagnostic.Span.NodeType));
    }

    [Fact]
    public void Explicit_string_comparison_and_date_parts_lower()
    {
        var expression = (Expression<Func<Ticket, bool>>)(ticket =>
            ticket.Status!.Contains("open", StringComparison.Ordinal) && ticket.CreatedAt.Year == 2026);
        var predicate = ExpressionLowerer.Lower(expression, Tickets);
        Assert.IsType<Predicate.And>(predicate);
    }

    [Fact]
    public void Select_accepts_only_mapped_columns_and_does_not_expose_IQueryable()
    {
        var query = new GwQueryDatabase().Table(Tickets).Query
            .Select(ticket => new { ticket.TenantId, ticket.Status });
        Assert.DoesNotContain(typeof(System.Linq.IQueryable), query.GetType().GetInterfaces());
        Assert.False(query.ToQueryRequest().Projection.AllColumns);
        Assert.Equal(2, query.ToQueryRequest().Projection.Columns.Length);
    }

    [Fact]
    public void Scan_acceptance_and_latest_per_are_represented_in_the_request()
    {
        var request = new GwQueryDatabase().Table(Tickets).Query
            .Where(ticket => ticket.TenantId == ticket.OtherTenant)
            .AcceptScan("GW-SCAN-Q8-001", "known bounded scan", "query-team", DateTimeOffset.UtcNow.AddDays(1))
            .LatestPer(ticket => ticket.TenantId, ticket => ticket.CreatedAt)
            .ToQueryRequest();

        Assert.True(request.AcceptedScan!.Allowed);
        Assert.NotNull(request.LatestPerKey);
        Assert.Equal("TenantId", request.LatestPerKey!.Key.Name);
    }

    [Fact]
    public void Attributed_expression_fragments_are_inlined_without_IQueryable()
    {
        var query = new GwQueryDatabase().Table(Tickets).Query.Where(Fragments.Open);
        Assert.IsType<Predicate.Equal>(query.ToQueryRequest().Where);
    }

    [Fact]
    public void Attributed_expression_fragments_inline_across_assembly_boundaries()
    {
        var model = new GwTableModel<ExternalTicket>("external_tickets", new[]
        {
            new GwColumn<ExternalTicket>(nameof(ExternalTicket.IsOpen), nameof(ExternalTicket.IsOpen), QueryType.Boolean, false)
        });
        var predicate = ExpressionLowerer.Lower(ExternalFragments.IsOpen, model);
        Assert.IsType<Predicate.Equal>(predicate);
    }

    [Fact]
    public void Collection_and_declared_element_set_membership_lower_to_their_AST_nodes()
    {
        var values = new[] { 1, 2, 3 };
        var collectionDiagnostics = ExpressionLowerer.Diagnose<Ticket>(ticket => values.Contains(ticket.TenantId), Tickets);
        Assert.True(collectionDiagnostics.Count == 0, string.Join(";", collectionDiagnostics.Select(diagnostic => diagnostic.Code + ":" + diagnostic.Span.NodeType + ":" + diagnostic.Span)));
        var collection = ExpressionLowerer.Lower<Ticket>(ticket => values.Contains(ticket.TenantId), Tickets);
        var declared = ExpressionLowerer.Lower<Ticket>(ticket => ticket.TagIds.Any(value => value == 2), Tickets);
        Assert.IsType<Predicate.In>(collection);
        Assert.IsType<Predicate.ElementOf>(declared);
    }

    [Fact]
    public void Closed_accessor_is_compiled_once_for_a_repeated_expression_shape()
    {
        var before = GetClosedAccessorCount();
        var first = ExpressionLowerer.Lower<Ticket>(MakeTenantPredicate(7), Tickets);
        var second = ExpressionLowerer.Lower<Ticket>(MakeTenantPredicate(8), Tickets);
        var after = GetClosedAccessorCount();
        Assert.InRange(after - before, 1, 3);
        Assert.Equal(7, Assert.IsType<Predicate.Equal>(first).Value.Value);
        Assert.Equal(8, Assert.IsType<Predicate.Equal>(second).Value.Value);
    }

    private static Expression<Func<Ticket, bool>> MakeTenantPredicate(int tenant) => ticket => ticket.TenantId == tenant;

    private static int GetClosedAccessorCount() =>
        (int)typeof(ExpressionLowerer).GetProperty("ClosedAccessorCount", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
}
