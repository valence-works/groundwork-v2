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
    private sealed class LookalikeElementSet
    {
        public bool Contains(int value) => true;
    }
    private sealed class ElementSetTicket
    {
        public int Id { get; set; }
        public LookalikeElementSet Values { get; set; } = new();
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
    public void Date_part_comparisons_lower_each_source_operator_to_exact_utc_bounds()
    {
        var year = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var date = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var yearCases = new (Expression<Func<Ticket, bool>> Expression, CompareOp Operation)[]
        {
            (ticket => ticket.CreatedAt.Year == 2026, CompareOp.Equal),
            (ticket => ticket.CreatedAt.Year != 2026, CompareOp.NotEqual),
            (ticket => ticket.CreatedAt.Year < 2026, CompareOp.LessThan),
            (ticket => ticket.CreatedAt.Year <= 2026, CompareOp.LessThanOrEqual),
            (ticket => ticket.CreatedAt.Year > 2026, CompareOp.GreaterThan),
            (ticket => ticket.CreatedAt.Year >= 2026, CompareOp.GreaterThanOrEqual),
            (ticket => 2026 == ticket.CreatedAt.Year, CompareOp.Equal),
            (ticket => 2026 != ticket.CreatedAt.Year, CompareOp.NotEqual),
            (ticket => 2026 < ticket.CreatedAt.Year, CompareOp.GreaterThan),
            (ticket => 2026 <= ticket.CreatedAt.Year, CompareOp.GreaterThanOrEqual),
            (ticket => 2026 > ticket.CreatedAt.Year, CompareOp.LessThan),
            (ticket => 2026 >= ticket.CreatedAt.Year, CompareOp.LessThanOrEqual)
        };
        var dateCases = new (Expression<Func<Ticket, bool>> Expression, CompareOp Operation)[]
        {
            (ticket => ticket.CreatedAt.Date == new DateTime(2026, 1, 2), CompareOp.Equal),
            (ticket => ticket.CreatedAt.Date != new DateTime(2026, 1, 2), CompareOp.NotEqual),
            (ticket => ticket.CreatedAt.Date < new DateTime(2026, 1, 2), CompareOp.LessThan),
            (ticket => ticket.CreatedAt.Date <= new DateTime(2026, 1, 2), CompareOp.LessThanOrEqual),
            (ticket => ticket.CreatedAt.Date > new DateTime(2026, 1, 2), CompareOp.GreaterThan),
            (ticket => ticket.CreatedAt.Date >= new DateTime(2026, 1, 2), CompareOp.GreaterThanOrEqual),
            (ticket => new DateTime(2026, 1, 2) == ticket.CreatedAt.Date, CompareOp.Equal),
            (ticket => new DateTime(2026, 1, 2) != ticket.CreatedAt.Date, CompareOp.NotEqual),
            (ticket => new DateTime(2026, 1, 2) < ticket.CreatedAt.Date, CompareOp.GreaterThan),
            (ticket => new DateTime(2026, 1, 2) <= ticket.CreatedAt.Date, CompareOp.GreaterThanOrEqual),
            (ticket => new DateTime(2026, 1, 2) > ticket.CreatedAt.Date, CompareOp.LessThan),
            (ticket => new DateTime(2026, 1, 2) >= ticket.CreatedAt.Date, CompareOp.LessThanOrEqual)
        };

        foreach (var (expression, operation) in yearCases)
        {
            var expected = ExpectedDatePart(Tickets.Columns[nameof(Ticket.CreatedAt)], year, year.AddYears(1), operation);
            Assert.Equal(expected.CanonicalForm, ExpressionLowerer.Lower(expression, Tickets).CanonicalForm);
        }
        foreach (var (expression, operation) in dateCases)
        {
            var expected = ExpectedDatePart(Tickets.Columns[nameof(Ticket.CreatedAt)], date, date.AddDays(1), operation);
            Assert.Equal(expected.CanonicalForm, ExpressionLowerer.Lower(expression, Tickets).CanonicalForm);
        }
    }

    [Fact]
    public void Date_part_comparisons_fail_closed_when_the_utc_interval_overflows()
    {
        var lastDate = DateTime.MaxValue.Date;

        var yearDiagnostics = ExpressionLowerer.Diagnose<Ticket>(
            ticket => ticket.CreatedAt.Year == 9999,
            Tickets);
        var dateDiagnostics = ExpressionLowerer.Diagnose<Ticket>(
            ticket => ticket.CreatedAt.Date == lastDate,
            Tickets);

        Assert.Equal("GW-LINQ-107", Assert.Single(yearDiagnostics).Code);
        Assert.Equal("GW-LINQ-107", Assert.Single(dateDiagnostics).Code);
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
    public void First_and_single_are_ordered_cardinality_terminals()
    {
        var query = new GwQueryDatabase().Table(Tickets).Query
            .Where(ticket => ticket.IsOpen)
            .OrderBy(ticket => ticket.CreatedAt);

        var first = query.First().Request;
        var firstOrDefault = query.FirstOrDefault().Request;
        var single = query.Single().Request;
        var singleOrDefault = query.SingleOrDefault().Request;

        Assert.IsType<ResultShape.First>(first.Result);
        Assert.IsType<ResultShape.FirstOrDefault>(firstOrDefault.Result);
        Assert.IsType<ResultShape.Single>(single.Result);
        Assert.IsType<ResultShape.SingleOrDefault>(singleOrDefault.Result);
        Assert.Equal(1, first.Paging.Limit);
        Assert.Equal(1, firstOrDefault.Paging.Limit);
        Assert.Equal(2, single.Paging.Limit);
        Assert.Equal(2, singleOrDefault.Paging.Limit);

        var afterSkip = query.Skip(3).Take(10).First().Request;
        Assert.Equal(3, afterSkip.Paging.Offset);
        Assert.Equal(1, afterSkip.Paging.Limit);
    }

    [Fact]
    public void First_requires_explicit_ordering_but_single_does_not()
    {
        var query = new GwQueryDatabase().Table(Tickets).Query.Where(ticket => ticket.IsOpen);

        var first = Assert.Throws<LinqTranslationException>(() => query.First());
        var single = query.Single().Request;

        Assert.Equal("GW-LINQ-111", Assert.Single(first.Diagnostics).Code);
        Assert.IsType<ResultShape.Single>(single.Result);

        var emptyFirst = query.OrderBy(ticket => ticket.CreatedAt).Take(0).First().Request;
        Assert.IsType<Predicate.AlwaysFalse>(emptyFirst.Where);
        Assert.Equal(1, emptyFirst.Paging.Limit);
    }

    [Fact]
    public void Distinct_marks_the_projected_query_request()
    {
        var request = new GwQueryDatabase().Table(Tickets).Query
            .Select(ticket => new { ticket.Status })
            .Distinct()
            .ToQueryRequest();

        Assert.True(request.Distinct);
        Assert.False(request.Projection.AllColumns);
        Assert.Equal(nameof(Ticket.Status), Assert.Single(request.Projection.Columns).Name);
    }

    [Fact]
    public void Reduction_terminals_lower_to_the_covered_column_and_preserve_result_type()
    {
        var query = new GwQueryDatabase().Table(Tickets).Query
            .Where(ticket => ticket.IsOpen);

        var sumInt = query.Sum(ticket => ticket.TenantId).Request;
        var sumLong = query.Sum(ticket => ticket.LongValue).Request;
        var sumDecimal = query.Sum(ticket => ticket.Amount).Request;
        var minimum = query.Min(ticket => ticket.CreatedAt).Request;
        var maximum = query.Max(ticket => ticket.Status).Request;

        Assert.IsType<ResultShape.Sum>(sumInt.Result);
        Assert.IsType<ResultShape.Sum>(sumLong.Result);
        Assert.IsType<ResultShape.Sum>(sumDecimal.Result);
        Assert.IsType<ResultShape.Min>(minimum.Result);
        Assert.IsType<ResultShape.Max>(maximum.Result);
        Assert.Equal(nameof(Ticket.TenantId), ((ResultShape.Sum)sumInt.Result).Column.Name);
        Assert.Equal(nameof(Ticket.LongValue), ((ResultShape.Sum)sumLong.Result).Column.Name);
        Assert.Equal(nameof(Ticket.Amount), ((ResultShape.Sum)sumDecimal.Result).Column.Name);
        Assert.Equal(nameof(Ticket.CreatedAt), ((ResultShape.Min)minimum.Result).Column.Name);
        Assert.Equal(nameof(Ticket.Status), ((ResultShape.Max)maximum.Result).Column.Name);
        Assert.Equal(nameof(Ticket.TenantId), Assert.Single(sumInt.Projection.Columns).Name);
        Assert.Equal(nameof(Ticket.Status), Assert.Single(maximum.Projection.Columns).Name);

        var paged = query.Skip(2).Take(4).Sum(ticket => ticket.TenantId).Request;
        Assert.Equal(2, paged.Paging.Offset);
        Assert.Equal(4, paged.Paging.Limit);
    }

    [Fact]
    public void Reduction_terminals_reject_projection_and_offset_only_pages()
    {
        var projection = new GwQueryDatabase().Table(Tickets).Query.Select(ticket => new { ticket.TenantId });
        Assert.Throws<InvalidOperationException>(() => projection.Min(ticket => ticket.TenantId));

        var query = new GwQueryDatabase().Table(Tickets).Query.Skip(3);
        Assert.Equal("GW-LINQ-113", Assert.Single(Assert.Throws<LinqTranslationException>(() => query.ToQueryRequest()).Diagnostics).Code);
        Assert.Equal("GW-LINQ-113", Assert.Single(Assert.Throws<LinqTranslationException>(() => query.Sum(ticket => ticket.TenantId).Request).Diagnostics).Code);
    }

    [Fact]
    public async Task Async_single_detects_an_over_one_result_from_the_adapter()
    {
        var query = new GwQueryDatabase().Table(Tickets).Query
            .OrderBy(ticket => ticket.CreatedAt);
        var executor = new RowsExecutor(
        [
            new Ticket { TenantId = 1, CreatedAt = DateTimeOffset.UtcNow },
            new Ticket { TenantId = 2, CreatedAt = DateTimeOffset.UtcNow.AddMinutes(1) }
        ]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => query.SingleAsync(executor));

        Assert.Contains("more than one", exception.Message, StringComparison.Ordinal);
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

    [Fact]
    public async Task Public_async_reductions_dispatch_through_the_scalar_executor_seam()
    {
        var executor = new RecordingExecutor();
        var query = new GwQueryDatabase().Table(Tickets).Query.Where(ticket => ticket.IsOpen);

        await query.SumAsync(executor, ticket => ticket.TenantId);

        Assert.NotNull(executor.LastReduction);
        Assert.IsType<ResultShape.Sum>(executor.LastReduction!.Result);
        Assert.Equal(nameof(Ticket.TenantId), ((ResultShape.Sum)executor.LastReduction.Result).Column.Name);
    }

    private sealed class RecordingExecutor : IGwQueryExecutor
    {
        public object? LastModel { get; private set; }
        public QueryRequest? LastReduction { get; private set; }
        public Task<IReadOnlyList<T>> ToListAsync<T>(QueryRequest request, GwTableModel<T>? model = null, CancellationToken cancellationToken = default)
        {
            LastModel = model;
            return Task.FromResult<IReadOnlyList<T>>(Array.Empty<T>());
        }
        public Task<long> CountAsync(QueryRequest request, CancellationToken cancellationToken = default) => Task.FromResult(1L);
        public Task<bool> AnyAsync(QueryRequest request, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<TResult> ReduceAsync<TResult>(QueryRequest request, CancellationToken cancellationToken = default)
        {
            LastReduction = request;
            return Task.FromResult(default(TResult)!);
        }
    }

    private sealed class RowsExecutor(IReadOnlyList<Ticket> rows) : IGwQueryExecutor
    {
        public Task<IReadOnlyList<T>> ToListAsync<T>(QueryRequest request, GwTableModel<T>? model = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<T>>(rows.Cast<T>().ToArray());

        public Task<long> CountAsync(QueryRequest request, CancellationToken cancellationToken = default) => Task.FromResult((long)rows.Count);

        public Task<bool> AnyAsync(QueryRequest request, CancellationToken cancellationToken = default) => Task.FromResult(rows.Count != 0);

        public Task<TResult> ReduceAsync<TResult>(QueryRequest request, CancellationToken cancellationToken = default) => Task.FromResult(default(TResult)!);
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
    public void Element_set_equality_requires_one_direct_nested_element_and_one_closed_value()
    {
        var constantEquality = ExpressionLowerer.Diagnose<Ticket>(ticket => ticket.TagIds.Any(value => 1 == 2), Tickets);
        var nestedElementExpression = ExpressionLowerer.Diagnose<Ticket>(ticket => ticket.TagIds.All(value => value == Math.Abs(value)), Tickets);
        var outerRowCapture = ExpressionLowerer.Diagnose<Ticket>(ticket => ticket.TagIds.Any(value => value == ticket.TenantId), Tickets);

        Assert.Equal("GW-LINQ-106", Assert.Single(constantEquality).Code);
        Assert.Equal("GW-LINQ-106", Assert.Single(nestedElementExpression).Code);
        Assert.Equal("GW-LINQ-106", Assert.Single(outerRowCapture).Code);
    }

    [Fact]
    public void Collection_membership_requires_a_closed_collection_and_direct_row_column()
    {
        var values = new[] { 1, 2 };
        var closedItem = ExpressionLowerer.Diagnose<Ticket>(ticket => values.Contains(7), Tickets);
        var rowCollection = ExpressionLowerer.Diagnose<Ticket>(ticket => ticket.TagIds.Contains(ticket.TenantId), Tickets);

        Assert.Contains(closedItem, diagnostic => diagnostic.Code == "GW-LINQ-107");
        Assert.Contains(rowCollection, diagnostic => diagnostic.Code == "GW-LINQ-107");
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
    public void Exact_name_enumerable_spoofs_are_not_silently_lowered()
    {
        var contains = ExpressionLowerer.Diagnose(Lookalikes.ExactNameEnumerableFactory.CreateContains(), Tickets);
        var any = ExpressionLowerer.Diagnose(Lookalikes.ExactNameEnumerableFactory.CreateAny(), Tickets);

        Assert.Contains(contains, diagnostic => diagnostic.Code == "GW-LINQ-107");
        Assert.Contains(any, diagnostic => diagnostic.Code == "GW-LINQ-107");
    }

    [Fact]
    public void Declared_element_sets_reject_instance_contains_lookalikes()
    {
        var model = new GwTableModel<ElementSetTicket>("element_sets", new[]
        {
            new GwColumn<ElementSetTicket>(nameof(ElementSetTicket.Id), "id", QueryType.Int32, false)
        }, new[]
        {
            new GwElementSet<ElementSetTicket>(nameof(ElementSetTicket.Values), "values", QueryType.Int32)
        });

        var diagnostics = ExpressionLowerer.Diagnose<ElementSetTicket>(ticket => ticket.Values.Contains(7), model);

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

    private static Predicate ExpectedDatePart(ColumnRef column, DateTimeOffset lower, DateTimeOffset upper, CompareOp operation) => operation switch
    {
        CompareOp.Equal => DatePartRange(column, lower, upper),
        CompareOp.NotEqual => new Predicate.Or(new Predicate[]
        {
            new Predicate.Range(column, Bound.Inclusive(QueryConstant.Of(column, upper)), null),
            new Predicate.Range(column, null, Bound.Exclusive(QueryConstant.Of(column, lower)))
        }),
        CompareOp.LessThan => new Predicate.Range(column, null, Bound.Exclusive(QueryConstant.Of(column, lower))),
        CompareOp.LessThanOrEqual => new Predicate.Range(column, null, Bound.Exclusive(QueryConstant.Of(column, upper))),
        CompareOp.GreaterThan => new Predicate.Range(column, Bound.Inclusive(QueryConstant.Of(column, upper)), null),
        CompareOp.GreaterThanOrEqual => new Predicate.Range(column, Bound.Inclusive(QueryConstant.Of(column, lower)), null),
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    private static Predicate.Range DatePartRange(ColumnRef column, DateTimeOffset lower, DateTimeOffset upper) =>
        new(column, Bound.Inclusive(QueryConstant.Of(column, lower)), Bound.Exclusive(QueryConstant.Of(column, upper)));

    private static class OtherClosure
    {
        public static Expression<Func<Ticket, bool>> Make(int tenant) => ticket => ticket.TenantId == tenant;
    }
}
