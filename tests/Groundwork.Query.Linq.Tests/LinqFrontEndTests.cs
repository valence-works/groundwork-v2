using System.Linq.Expressions;
using Groundwork.Query.Linq;
using Groundwork.Query.Model;
using Xunit;
using Groundwork.Query.Linq.Fragments;

namespace Groundwork.Query.Linq.Tests;

public sealed class LinqFrontEndTests
{
    private sealed class FoldedTicket
    {
        [GwStringComparison(StringComparison.OrdinalIgnoreCase)] public string Name { get; set; } = string.Empty;
        [GwStringComparison(StringComparison.OrdinalIgnoreCase)] public string Code = string.Empty;
    }
    private sealed class MappedTicket
    {
        public string Display { get; set; } = string.Empty;
    }
    private static int sideEffectReads;
    private static int SideEffectValue => Interlocked.Increment(ref sideEffectReads);
    private static int evilInitializerReads;
    private static class EvilDisplayClass
    {
        public static int Value = Interlocked.Increment(ref evilInitializerReads);
    }
    public sealed class Ticket
    {
        public int TenantId { get; set; }
        public string? Status { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public bool IsOpen { get; set; }
        public decimal Amount { get; set; }
        public int OtherTenant { get; set; }
        public int Marker { get; set; }
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
        new GwColumn<Ticket>(nameof(Ticket.Marker), nameof(Ticket.Marker), QueryType.Int32, false),
        new GwColumn<Ticket>(nameof(Ticket.LongValue), nameof(Ticket.LongValue), QueryType.Int64, false),
        new GwColumn<Ticket>(nameof(Ticket.OptionalAt), nameof(Ticket.OptionalAt), QueryType.DateTimeOffset, true)
    }, new[] { new GwElementSet<Ticket>(nameof(Ticket.TagIds), "tag_ids", QueryType.Int32) });

    private static class Fragments
    {
        [GwQueryFragment]
        public static Expression<Func<Ticket, bool>> Open => ticket => ticket.IsOpen;
    }

    private static class EnumerableLookalike
    {
        public static bool Contains(IEnumerable<int> values, int value) => values.Contains(value);
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
    public void Infer_reads_string_comparison_metadata_from_properties_and_fields()
    {
        var model = GwTableModel<FoldedTicket>.Infer("folded");
        Assert.Equal(QueryStringComparisonPolicy.UnicodeOrdinalIgnoreCase, model.Columns[nameof(FoldedTicket.Name)].StringComparison);
        Assert.Equal(QueryStringComparisonPolicy.UnicodeOrdinalIgnoreCase, model.Columns[nameof(FoldedTicket.Code)].StringComparison);
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

    [Fact]
    public async Task Public_to_list_extension_passes_the_source_model_to_the_executor()
    {
        var executor = new RecordingExecutor();
        await new GwQueryDatabase().Table(Tickets).Query.Where(ticket => ticket.TenantId == 7).ToListAsync(executor);
        Assert.Same(Tickets, executor.LastModel);
    }

    [Fact]
    public async Task Public_to_list_extension_rejects_mapped_projection_without_adapter_materializer()
    {
        var model = new GwTableModel<MappedTicket>("mapped", new[]
        {
            new GwColumn<MappedTicket>(nameof(MappedTicket.Display), "value_col", QueryType.String, false)
        });
        var query = new GwQueryDatabase().Table(model).Query.Select(item => new { item.Display });
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => query.ToListAsync(new RecordingExecutor()));
        Assert.Contains("model-aware adapter", exception.Message, StringComparison.Ordinal);
    }

    private sealed class RecordingExecutor : IGwQueryExecutor
    {
        public object? LastModel { get; private set; }
        public Task<IReadOnlyList<T>> ToListAsync<T>(QueryRequest request, GwTableModel<T>? model = null, CancellationToken cancellationToken = default)
        {
            LastModel = model;
            return Task.FromResult<IReadOnlyList<T>>(Array.Empty<T>());
        }
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
    public void Rejected_closed_properties_are_not_invoked()
    {
        sideEffectReads = 0;
        var diagnostics = ExpressionLowerer.Diagnose<Ticket>(ticket => ticket.TenantId == SideEffectValue, Tickets);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "GW-LINQ-107");
        Assert.Equal(0, sideEffectReads);
    }

    [Fact]
    public void Closed_accessor_reads_fresh_closure_values()
    {
        var first = Assert.IsType<Predicate.Equal>(ExpressionLowerer.Lower(ClosedTenant(7), Tickets));
        var second = Assert.IsType<Predicate.Equal>(ExpressionLowerer.Lower(ClosedTenant(8), Tickets));
        Assert.Equal(7, first.Value.Value);
        Assert.Equal(8, second.Value.Value);
    }

    [Fact]
    public void User_types_named_like_closures_are_not_initialized_or_read()
    {
        evilInitializerReads = 0;
        var diagnostics = ExpressionLowerer.Diagnose<Ticket>(ticket => ticket.TenantId == EvilDisplayClass.Value, Tickets);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "GW-LINQ-107");
        Assert.Equal(0, evilInitializerReads);
    }

    private static Expression<Func<Ticket, bool>> ClosedTenant(int value) => ticket => ticket.TenantId == value;

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
        Expression<Func<ExternalTicket, bool>> unmarked = ticket => ExternalFragments.Unmarked.Compile()(ticket);
        var rejected = ExpressionLowerer.Diagnose(unmarked, model);
        Assert.Contains(rejected, diagnostic => diagnostic.Code == "GW-LINQ-107");
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
    public void Static_contains_lookalikes_are_not_silently_lowered()
    {
        var diagnostics = ExpressionLowerer.Diagnose<Ticket>(ticket => EnumerableLookalike.Contains(new[] { 1, 2 }, ticket.TenantId), Tickets);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "GW-LINQ-107");
    }

    [Fact]
    public void Reduced_extension_contains_lookalikes_are_not_silently_lowered()
    {
        var diagnostics = ExpressionLowerer.Diagnose(Lookalikes.EnumerableExtensionFactory.Create(new Lookalikes.EnumerableExtensionFactory.CustomValues()), Tickets);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "GW-LINQ-107");
    }

    [Fact]
    public void Closed_accessors_read_fresh_values_across_expression_shapes()
    {
        var first = ExpressionLowerer.Lower<Ticket>(MakeTenantPredicate(7), Tickets);
        var second = ExpressionLowerer.Lower<Ticket>(MakeTenantPredicate(8), Tickets);
        var other = ExpressionLowerer.Lower<Ticket>(OtherClosure.Make(9), Tickets);
        Assert.Equal(7, Assert.IsType<Predicate.Equal>(first).Value.Value);
        Assert.Equal(8, Assert.IsType<Predicate.Equal>(second).Value.Value);
        Assert.Equal(9, Assert.IsType<Predicate.Equal>(other).Value.Value);
    }

    private static Expression<Func<Ticket, bool>> MakeTenantPredicate(int tenant) => ticket => ticket.TenantId == tenant;
    private static class OtherClosure
    {
        public static Expression<Func<Ticket, bool>> Make(int tenant) => ticket => ticket.TenantId == tenant;
    }
}
