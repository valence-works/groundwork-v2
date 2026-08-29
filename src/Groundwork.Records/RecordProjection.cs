using System.Linq.Expressions;
using Groundwork.Query.Model;

namespace Groundwork.Records;

/// <summary>A typed partial-record query whose result materializer is compiled once.</summary>
public sealed class RecordProjection<TResult>
{
    private readonly object owner;
    private readonly Func<RowValues, TResult> materializer;

    internal RecordProjection(object owner, QueryRequest request, Func<RowValues, TResult> materializer)
    {
        this.owner = owner;
        Request = request;
        this.materializer = materializer;
    }

    internal QueryRequest Request { get; }
    internal TResult Materialize(RowValues values) => materializer(values);

    internal void EnsureOwner(object expected)
    {
        if (!ReferenceEquals(owner, expected))
            throw new InvalidOperationException("A record projection must be executed by the table that created it.");
    }
}

internal static class RecordProjectionAccessor
{
    internal sealed record JoinedCompilation<TResult>(
        IReadOnlyList<ColumnRef> Columns,
        Func<RowValues, TResult> Materializer);

    public static Func<RowValues, TResult> Compile<T, TResult>(
        Expression<Func<T, TResult>> selector,
        IReadOnlyList<RecordMember> members)
    {
        var values = Expression.Parameter(typeof(RowValues), "values");
        var body = new ProjectionVisitor(selector.Parameters[0], values, members).Visit(selector.Body)!;
        return Expression.Lambda<Func<RowValues, TResult>>(body, values).Compile();
    }

    public static JoinedCompilation<TResult> CompileJoined<TSource, TTarget, TResult>(
        Expression<Func<TSource, TTarget, TResult>> selector,
        RecordTable<TSource> sourceTable,
        RecordTable<TTarget> targetTable,
        ReferenceJoin join)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(sourceTable);
        ArgumentNullException.ThrowIfNull(targetTable);
        ArgumentNullException.ThrowIfNull(join);
        var values = Expression.Parameter(typeof(RowValues), "values");
        var visitor = new JoinedProjectionVisitor<TSource, TTarget>(
            selector.Parameters[0],
            selector.Parameters[1],
            values,
            sourceTable,
            targetTable,
            join);
        var body = visitor.Visit(selector.Body)!;
        return new JoinedCompilation<TResult>(
            visitor.Columns,
            Expression.Lambda<Func<RowValues, TResult>>(body, values).Compile());
    }

    private sealed class ProjectionVisitor(
        ParameterExpression source,
        ParameterExpression values,
        IReadOnlyList<RecordMember> members) : ExpressionVisitor
    {
        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Expression == source)
            {
                var member = members.SingleOrDefault(candidate => candidate.Member == node.Member)
                    ?? throw new ArgumentException($"Member '{node.Member.Name}' is not declared by this record table.", nameof(node));
                return Expression.Call(
                    typeof(ProjectionVisitor),
                    nameof(Read),
                    [member.MemberType],
                    values,
                    Expression.Constant(member.ColumnName));
            }

            throw new ArgumentException("A record projection may read only direct members of its record parameter.", nameof(node));
        }

        protected override Expression VisitMethodCall(MethodCallExpression node) =>
            throw new ArgumentException("A record projection may contain only direct record members, constructors, member initializers, and constants.", nameof(node));

        protected override Expression VisitBinary(BinaryExpression node) =>
            throw new ArgumentException("Computed binary expressions are not portable record projections.", nameof(node));

        protected override Expression VisitConditional(ConditionalExpression node) =>
            throw new ArgumentException("Conditional expressions are not portable record projections.", nameof(node));

        protected override Expression VisitInvocation(InvocationExpression node) =>
            throw new ArgumentException("Invoked expressions are not portable record projections.", nameof(node));

        protected override Expression VisitParameter(ParameterExpression node) =>
            node == source
                ? throw new ArgumentException("A record projection must select individual record members.", nameof(node))
                : base.VisitParameter(node);

        private static TValue Read<TValue>(RowValues values, string column)
        {
            if (!values.Values.TryGetValue(column, out var value))
                throw new KeyNotFoundException($"The query result did not contain projected column '{column}'.");
            return value is null ? default! : (TValue)value;
        }
    }

    private sealed class JoinedProjectionVisitor<TSource, TTarget>(
        ParameterExpression source,
        ParameterExpression target,
        ParameterExpression values,
        RecordTable<TSource> sourceTable,
        RecordTable<TTarget> targetTable,
        ReferenceJoin join) : ExpressionVisitor
    {
        private readonly List<ColumnRef> columns = [];

        public IReadOnlyList<ColumnRef> Columns => columns;

        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Expression == source)
                return ReadMember(node.Member, sourceTable.Members, sourceTable.QueryModel.Columns);
            if (node.Expression == target)
                return ReadMember(node.Member, targetTable.Members, targetTable.QueryModel.Columns);
            throw new ArgumentException(
                "A joined record projection may read only direct members of its source and target parameters.",
                nameof(node));
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (node == source)
            {
                sourceTable.EnsureWholeRecordQueryable();
                AddAll(sourceTable.QueryModel.Columns.Values);
                return Expression.Call(
                    Expression.Constant(sourceTable),
                    nameof(RecordTable<TSource>.ReadQualified),
                    Type.EmptyTypes,
                    values,
                    Expression.Constant(join));
            }
            if (node == target)
            {
                targetTable.EnsureWholeRecordQueryable();
                AddAll(targetTable.QueryModel.Columns.Values);
                return Expression.Call(
                    Expression.Constant(targetTable),
                    nameof(RecordTable<TTarget>.ReadQualified),
                    Type.EmptyTypes,
                    values,
                    Expression.Constant(join));
            }
            return base.VisitParameter(node);
        }

        protected override Expression VisitMethodCall(MethodCallExpression node) =>
            throw new ArgumentException(
                "A joined record projection may contain only direct record members, constructors, member initializers, and constants.",
                nameof(node));

        protected override Expression VisitBinary(BinaryExpression node) =>
            throw new ArgumentException("Computed binary expressions are not portable joined record projections.", nameof(node));

        protected override Expression VisitConditional(ConditionalExpression node) =>
            throw new ArgumentException("Conditional expressions are not portable joined record projections.", nameof(node));

        protected override Expression VisitInvocation(InvocationExpression node) =>
            throw new ArgumentException("Invoked expressions are not portable joined record projections.", nameof(node));

        private Expression ReadMember(
            System.Reflection.MemberInfo selected,
            IReadOnlyList<RecordMember> members,
            IReadOnlyDictionary<string, ColumnRef> queryColumns)
        {
            var member = members.SingleOrDefault(candidate => candidate.Member == selected)
                ?? throw new ArgumentException($"Member '{selected.Name}' is not declared by this record table.", nameof(selected));
            if (!queryColumns.TryGetValue(member.Name, out var column))
                throw new ArgumentException($"Member '{selected.Name}' is not a queryable scalar column.", nameof(selected));
            Add(column);
            return Expression.Call(
                typeof(JoinedProjectionVisitor<TSource, TTarget>),
                nameof(Read),
                [member.MemberType],
                values,
                Expression.Constant(QueryRequestExecution.ResultFieldName(join, column)));
        }

        private void AddAll(IEnumerable<ColumnRef> selected)
        {
            foreach (var column in selected)
                Add(column);
        }

        private void Add(ColumnRef column)
        {
            if (!columns.Any(existing => existing.Table == column.Table &&
                string.Equals(existing.Name, column.Name, StringComparison.Ordinal)))
                columns.Add(column);
        }

        private static TValue Read<TValue>(RowValues row, string field)
        {
            if (!row.Values.TryGetValue(field, out var value))
                throw new KeyNotFoundException($"The joined query result did not contain projected field '{field}'.");
            return value is null ? default! : (TValue)value;
        }
    }
}
