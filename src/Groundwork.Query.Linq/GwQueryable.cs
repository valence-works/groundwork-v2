using System.Collections.Immutable;
using System.Linq.Expressions;
using Groundwork.Query.Model;

namespace Groundwork.Query.Linq;

internal sealed class GwQueryable<T> : IGwQueryable<T>
{
    private readonly GwTableModel<T>? model;
    private readonly IGwQueryExecutor? executor;
    private readonly GwQueryState state;

    internal GwQueryable(GwTableModel<T> model, IGwQueryExecutor? executor = null)
        : this(model, executor, new GwQueryState(model.Table))
    {
    }

    private GwQueryable(GwTableModel<T>? model, IGwQueryExecutor? executor, GwQueryState state)
    {
        this.model = model; this.executor = executor;
        this.state = state;
    }

    public QueryRequest ToQueryRequest() => state.Build();

    public IGwQueryable<T> Where(Expression<Func<T, bool>> predicate)
    {
        if (predicate is null) throw new ArgumentNullException(nameof(predicate));
        var model = RequireModel();
        var diagnostics = ExpressionLowerer.Diagnose(predicate, model);
        var lower = ExpressionLowerer.Lower(predicate, model);
        return New(state with
        {
            Where = state.Where is Predicate.AlwaysTrue ? lower : new Predicate.And(new[] { state.Where, lower }),
            RequiresScan = state.RequiresScan || diagnostics.Any(diagnostic => diagnostic.Code == "GW-LINQ-103")
        });
    }

    public IGwQueryable<T> WhereIf(bool condition, Expression<Func<T, bool>> predicate) =>
        condition ? Where(predicate) : this;

    public IGwQueryable<T> OrderBy<TKey>(Expression<Func<T, TKey>> selector) =>
        New(state with { Order = ImmutableArray.Create(Order(selector, OrderDirection.Ascending)) });

    public IGwQueryable<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> selector) =>
        New(state with { Order = ImmutableArray.Create(Order(selector, OrderDirection.Descending)) });

    public IGwQueryable<T> ThenBy<TKey>(Expression<Func<T, TKey>> selector) =>
        New(state with { Order = state.Order.Add(Order(selector, OrderDirection.Ascending)) });

    public IGwQueryable<T> ThenByDescending<TKey>(Expression<Func<T, TKey>> selector) =>
        New(state with { Order = state.Order.Add(Order(selector, OrderDirection.Descending)) });

    public IGwQueryable<T> Skip(int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        return New(state with { Skip = count });
    }

    public IGwQueryable<T> Take(int count)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        return New(state with { Take = count });
    }

    public IGwQueryable<TResult> Select<TResult>(Expression<Func<T, TResult>> selector)
    {
        if (selector is null) throw new ArgumentNullException(nameof(selector));
        var columns = ProjectionColumns(selector.Body, selector.Parameters[0]).ToArray();
        if (columns.Length == 0)
            throw new LinqTranslationException(new[] { new LinqDiagnostic("GW-LINQ-101", "Select must contain mapped columns; declare a computed column; expressions over columns are not portable", selector.Body) });
        return new GwQueryable<TResult>(null, executor, state with { Projection = Projection.ColumnsOnly(columns) });
    }

    public IGwQueryable<T> AcceptScan(string id, string reason, string owner, DateTimeOffset expiresOn) =>
        New(state with { AcceptedScan = ScanAcceptance.Allow(id, reason, owner, expiresOn) });

    public IGwQueryable<T> LatestPer<TKey, TTimestamp>(Expression<Func<T, TKey>> key, Expression<Func<T, TTimestamp>> timestamp) =>
        New(state with { LatestPerKey = new LatestPerKey(ExpressionLowerer.LowerColumn(key, RequireModel()), ExpressionLowerer.LowerColumn(timestamp, RequireModel())) });

    public LinqTerminal<T> ToList() => new(ToQueryRequest());
    public Task<IReadOnlyList<T>> ToListAsync(CancellationToken cancellationToken = default) =>
        (executor ?? throw new InvalidOperationException("Configure GwQueryDatabase with an IGwQueryExecutor before using ToListAsync."))
            .ToListAsync<T>(ToQueryRequest(), cancellationToken);

    public LinqTerminal<long> Count() => new(new QueryRequest(state.Table, state.Where, state.Order, state.Projection, Paging.None, ResultShape.TotalCount.Instance, state.LatestPerKey, state.AcceptedScan));

    public LinqTerminal<bool> Any() => new(new QueryRequest(state.Table, state.Where, state.Order, state.Projection, Paging.OffsetLimit(0, 1), ResultShape.Rows.Instance, state.LatestPerKey, state.AcceptedScan));

    private GwTableModel<T> RequireModel() => model ?? throw new InvalidOperationException("A projection is terminal; apply filters and ordering before Select.");
    private GwQueryable<T> New(GwQueryState next) => new(model, executor, next);

    private OrderTerm Order<TKey>(Expression<Func<T, TKey>> selector, OrderDirection direction)
    {
        var column = ExpressionLowerer.LowerColumn(selector ?? throw new ArgumentNullException(nameof(selector)), RequireModel());
        return new OrderTerm(column, direction, direction == OrderDirection.Ascending ? NullOrder.Last : NullOrder.First);
    }

    private IEnumerable<ColumnRef> ProjectionColumns(Expression expression, ParameterExpression parameter)
    {
        expression = Unwrap(expression);
        if (expression is MemberExpression)
        {
            yield return ExpressionLowerer.LowerColumn(Expression.Lambda<Func<T, object>>(Expression.Convert(expression, typeof(object)), parameter), RequireModel());
            yield break;
        }
        if (expression is NewExpression created)
        {
            foreach (var argument in created.Arguments)
                foreach (var column in ProjectionColumns(argument, parameter)) yield return column;
            yield break;
        }
        if (expression is MemberInitExpression initialized)
        {
            foreach (var binding in initialized.Bindings.OfType<MemberAssignment>())
                foreach (var column in ProjectionColumns(binding.Expression, parameter)) yield return column;
        }
    }

    private static Expression Unwrap(Expression expression)
    {
        while (expression is UnaryExpression unary && (unary.NodeType == ExpressionType.Convert || unary.NodeType == ExpressionType.ConvertChecked)) expression = unary.Operand;
        return expression;
    }

}

internal sealed record GwQueryState(
    TableId Table,
    Predicate Where,
    ImmutableArray<OrderTerm> Order,
    Projection Projection,
    int? Skip,
    int? Take,
    LatestPerKey? LatestPerKey,
    ScanAcceptance? AcceptedScan,
    bool RequiresScan = false)
{
    public GwQueryState(TableId table)
        : this(table, Predicate.AlwaysTrue.Instance, ImmutableArray<OrderTerm>.Empty, Projection.All, null, null, null, null, false)
    {
    }

    public QueryRequest Build()
    {
        if (RequiresScan && AcceptedScan?.Allowed != true)
            throw new LinqTranslationException(new[]
            {
                new LinqDiagnostic("GW-LINQ-103", "Column-to-column comparison is allowed, but never index-covered — add `.AcceptScan(...)`", Expression.Empty())
            });
        var paging = Skip is null && Take is null ? Paging.None : Paging.OffsetLimit(Skip ?? 0, Take ?? int.MaxValue);
        return new QueryRequest(Table, Where, Order, Projection, paging, ResultShape.Rows.Instance, LatestPerKey, AcceptedScan);
    }
}
