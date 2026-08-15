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
    public static Func<RowValues, TResult> Compile<T, TResult>(
        Expression<Func<T, TResult>> selector,
        IReadOnlyList<RecordMember> members)
    {
        var values = Expression.Parameter(typeof(RowValues), "values");
        var body = new ProjectionVisitor(selector.Parameters[0], values, members).Visit(selector.Body)!;
        return Expression.Lambda<Func<RowValues, TResult>>(body, values).Compile();
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
}
