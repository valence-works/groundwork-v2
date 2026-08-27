using Groundwork.Query.Model;

namespace Groundwork.Substrate.Relational;

/// <summary>
/// The scope narrowing a relational set-based mutation carries. It is the same equality a scoped
/// query carries, expressed as a portable predicate so it is rendered by the same fragment renderer
/// rather than concatenated into the statement afterwards.
/// </summary>
public static class RelationalSetMutation
{
    /// <summary>
    /// Returns <paramref name="where"/> narrowed to one scope. A global unit is returned unchanged.
    /// </summary>
    public static Predicate WithScope(Predicate where, string table, string? scopeColumn, string? scope)
    {
        ArgumentNullException.ThrowIfNull(where);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        if (scopeColumn is null)
            return where;
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        var column = new ColumnRef(new TableId(table), scopeColumn, QueryType.String, isNullable: false);
        return new Predicate.And([where, new Predicate.Equal(column, QueryConstant.Of(column, scope))]);
    }
}
