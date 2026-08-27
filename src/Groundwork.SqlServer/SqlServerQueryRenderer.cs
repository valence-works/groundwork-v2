using Groundwork.Kernel;
using Groundwork.Query.Model;
using Groundwork.Substrate.Relational;
using Groundwork.Store;

namespace Groundwork.SqlServer;

/// <summary>SQL Server's one native renderer for the normalized v2 query contract.</summary>
public sealed class SqlServerQueryRenderer : RelationalQueryRenderer
{
    /// <summary>SQL Server's real bound on bound parameters in one statement.</summary>
    public const int ParameterBudget = 2_100;

    public SqlServerQueryRenderer()
        : base(new SqlServerDialect(), ParameterBudget, supportsIndexHints: true)
    {
    }

    protected override string ProviderName => "SQL Server";

    protected override string RenderCountExpression() => "COUNT_BIG(*) OVER()";

    protected override string RenderCountAggregate() => "COUNT_BIG(*)";

    protected override bool RequiresOrderForOffset => true;

    protected override string RenderIndexHint(string indexName) =>
        "WITH (INDEX(" + Dialect.QuoteIdentifier(indexName) + "))";

    protected override string RenderColumn(ColumnRef column)
    {
        if (string.Equals(column.Name, CrossScopeQueryMaterializer.ScopeTokenColumn, StringComparison.Ordinal))
        {
            var scope = Dialect.QuoteIdentifier(SqlServerSchemaCoordinator.ScopeColumn);
            return "CONVERT(varchar(64), HASHBYTES('SHA2_256', CONVERT(varbinary(max), CONVERT(varchar(max), " +
                   scope + " COLLATE Latin1_General_100_BIN2_UTF8))), 2)";
        }
        return column.Type == QueryType.String
            ? base.RenderColumn(column) + " COLLATE Latin1_General_100_BIN2"
            : base.RenderColumn(column);
    }

    protected override bool RequiresExplicitSelection(ColumnRef column) =>
        string.Equals(column.Name, CrossScopeQueryMaterializer.ScopeTokenColumn, StringComparison.Ordinal);

    protected override string RenderOrderTerm(OrderTerm term)
    {
        if (term.Column.Type == QueryType.Guid)
        {
            var guidExpression = RenderGuidOrderKey(RenderColumn(term.Column));
            var guidDirection = term.Direction == OrderDirection.Ascending ? "ASC" : "DESC";
            var guidNullRank = term.NullOrder == NullOrder.First ? "0" : "1";
            var guidNonNullRank = term.NullOrder == NullOrder.First ? "1" : "0";
            return "CASE WHEN " + RenderColumn(term.Column) + " IS NULL THEN " + guidNullRank + " ELSE " + guidNonNullRank + " END ASC, " + guidExpression + " " + guidDirection;
        }
        var rendered = base.RenderOrderTerm(term);
        if (term.Column.Type != QueryType.String)
            return rendered;
        var direction = term.Direction == OrderDirection.Ascending ? "ASC" : "DESC";
        return rendered + ", DATALENGTH(" + RenderColumn(term.Column) + ") " + direction;
    }

    protected override string RenderEquality(
        ColumnRef column,
        QueryConstant value,
        ICollection<QueryRenderParameter> parameters,
        ref int parameterIndex)
    {
        if (column.Type != QueryType.String)
            return base.RenderEquality(column, value, parameters, ref parameterIndex);
        var expression = RenderColumn(column);
        if (value.Kind == QueryConstantKind.Null)
            return expression + " IS NULL";
        var parameter = AddParameter(column, value, parameters, ref parameterIndex);
        return "(" + expression + " IS NOT NULL AND DATALENGTH(" + expression + ") = DATALENGTH(@" + parameter + ") AND " + expression + " = @" + parameter + ")";
    }

    protected override string RenderMembership(
        Predicate.In membership,
        ICollection<QueryRenderParameter> parameters,
        ref int parameterIndex)
    {
        if (membership.Column.Type != QueryType.String)
            return base.RenderMembership(membership, parameters, ref parameterIndex);
        if (membership.Values.Length == 0)
            return "1 = 0";
        var expression = RenderColumn(membership.Column);
        var parts = new List<string>();
        foreach (var value in membership.Values)
        {
            if (value.Kind == QueryConstantKind.Null)
            {
                parts.Add(expression + " IS NULL");
                continue;
            }
            var parameter = AddParameter(membership.Column, value, parameters, ref parameterIndex);
            parts.Add("(" + expression + " IS NOT NULL AND DATALENGTH(" + expression + ") = DATALENGTH(@" + parameter + ") AND " + expression + " = @" + parameter + ")");
        }
        return parts.Count == 1 ? parts[0] : "(" + string.Join(" OR ", parts) + ")";
    }

    protected override string RenderRange(
        Predicate.Range range,
        ICollection<QueryRenderParameter> parameters,
        ref int parameterIndex)
    {
        if (range.Column.Type == QueryType.Guid)
        {
            var guidExpression = RenderColumn(range.Column);
            var guidKey = RenderGuidOrderKey(guidExpression);
            var guidParts = new List<string> { guidExpression + " IS NOT NULL" };
            if (range.Lower is { } guidLower)
            {
                var parameter = AddParameter(range.Column, guidLower.Value, parameters, ref parameterIndex);
                guidParts.Add(guidKey + (guidLower.IsInclusive ? " >= " : " > ") + RenderGuidOrderKey("@" + parameter));
            }
            if (range.Upper is { } guidUpper)
            {
                var parameter = AddParameter(range.Column, guidUpper.Value, parameters, ref parameterIndex);
                guidParts.Add(guidKey + (guidUpper.IsInclusive ? " <= " : " < ") + RenderGuidOrderKey("@" + parameter));
            }
            return "(" + string.Join(" AND ", guidParts) + ")";
        }
        if (range.Column.Type != QueryType.String)
            return base.RenderRange(range, parameters, ref parameterIndex);

        if (range.Column.Name.StartsWith(SearchKeyProjection.Prefix, StringComparison.Ordinal))
            return RenderSearchKeyRange(range, parameters, ref parameterIndex);

        var expression = RenderColumn(range.Column);
        var parts = new List<string> { expression + " IS NOT NULL" };
        if (range.Lower is { } lower)
            parts.Add(RenderStringBound(expression, range.Column, lower, isLower: true, parameters, ref parameterIndex));
        if (range.Upper is { } upper)
            parts.Add(RenderStringBound(expression, range.Column, upper, isLower: false, parameters, ref parameterIndex));
        return "(" + string.Join(" AND ", parts) + ")";
    }

    protected override string RenderColumnCompare(Predicate.ColumnCompare compare)
    {
        if (compare.Left.Type != QueryType.Guid || compare.Right.Type != QueryType.Guid)
            return base.RenderColumnCompare(compare);

        var left = RenderColumn(compare.Left);
        var right = RenderColumn(compare.Right);
        var op = compare.Op switch
        {
            CompareOp.Equal => "=",
            CompareOp.NotEqual => "<>",
            CompareOp.LessThan => "<",
            CompareOp.LessThanOrEqual => "<=",
            CompareOp.GreaterThan => ">",
            CompareOp.GreaterThanOrEqual => ">=",
            _ => throw new ArgumentOutOfRangeException(nameof(compare.Op), compare.Op, null)
        };
        return "(" + left + " IS NOT NULL AND " + right + " IS NOT NULL AND " +
            RenderGuidOrderKey(left) + " " + op + " " + RenderGuidOrderKey(right) + ")";
    }

    private string RenderSearchKeyRange(
        Predicate.Range range,
        ICollection<QueryRenderParameter> parameters,
        ref int parameterIndex)
    {
        var expression = RenderColumn(range.Column);
        var ansiType = range.Column.MaxLength is { } length
            ? $"varchar({length})"
            : "varchar(max)";
        var parts = new List<string> { expression + " IS NOT NULL" };
        if (range.Lower is { } lower)
        {
            var parameter = AddParameter(range.Column, lower.Value, parameters, ref parameterIndex);
            parts.Add(expression + (lower.IsInclusive ? " >= " : " > ") + $"CAST(@{parameter} AS {ansiType})");
        }
        if (range.Upper is { } upper)
        {
            var parameter = AddParameter(range.Column, upper.Value, parameters, ref parameterIndex);
            parts.Add(expression + (upper.IsInclusive ? " <= " : " < ") + $"CAST(@{parameter} AS {ansiType})");
        }
        return "(" + string.Join(" AND ", parts) + ")";
    }

    protected override string RenderCursorEquality(
        ColumnRef column,
        QueryConstant value,
        ICollection<QueryRenderParameter> parameters,
        ref int parameterIndex)
    {
        if (column.Type == QueryType.String)
            return RenderEquality(column, value, parameters, ref parameterIndex);
        if (column.Type != QueryType.Guid || value.Kind == QueryConstantKind.Null)
            return base.RenderCursorEquality(column, value, parameters, ref parameterIndex);
        var parameter = AddParameter(column, value, parameters, ref parameterIndex);
        return RenderGuidOrderKey(RenderColumn(column)) + " = " + RenderGuidOrderKey("@" + parameter);
    }

    protected override string RenderAfter(
        OrderTerm term,
        QueryConstant value,
        ICollection<QueryRenderParameter> parameters,
        ref int parameterIndex)
    {
        if (term.Column.Type == QueryType.Guid && value.Kind != QueryConstantKind.Null)
        {
            var guidParameter = AddParameter(term.Column, value, parameters, ref parameterIndex);
            var guidExpression = RenderGuidOrderKey(RenderColumn(term.Column));
            var guidBoundary = RenderGuidOrderKey("@" + guidParameter);
            var guidComparison = term.Direction == OrderDirection.Ascending ? ">" : "<";
            var guidStrict = "(" + RenderColumn(term.Column) + " IS NOT NULL AND " + guidExpression + " " + guidComparison + " " + guidBoundary + ")";
            return term.NullOrder == NullOrder.Last
                ? "(" + guidStrict + " OR " + RenderColumn(term.Column) + " IS NULL)"
                : guidStrict;
        }
        if (term.Column.Type != QueryType.String)
            return base.RenderAfter(term, value, parameters, ref parameterIndex);
        var expression = RenderColumn(term.Column);
        if (value.Kind == QueryConstantKind.Null)
            return term.NullOrder == NullOrder.First ? expression + " IS NOT NULL" : "1 = 0";

        var parameter = AddParameter(term.Column, value, parameters, ref parameterIndex);
        var comparison = term.Direction == OrderDirection.Ascending ? ">" : "<";
        var lengthComparison = term.Direction == OrderDirection.Ascending ? ">" : "<";
        var strict = "((" + expression + " IS NOT NULL AND " + expression + " " + comparison + " @" + parameter + ") OR (" +
            expression + " = @" + parameter + " AND DATALENGTH(" + expression + ") " + lengthComparison + " DATALENGTH(@" + parameter + ")))";
        return term.NullOrder == NullOrder.First
            ? strict
            : "(" + strict + " OR " + expression + " IS NULL)";
    }

    protected override string RenderContains(string expression, string parameter) =>
        "(DATALENGTH(@" + parameter + ") = 0 OR CHARINDEX(@" + parameter + ", " + expression + ") > 0)";

    internal static string RenderGuidOrderKey(string expression) =>
        "CONVERT(char(36), " + expression + ") COLLATE Latin1_General_100_BIN2";

    protected override string RenderEndsWith(string expression, string parameter) =>
        "(DATALENGTH(@" + parameter + ") = 0 OR (DATALENGTH(RIGHT(" + expression + ", DATALENGTH(@" + parameter + ") / 2)) = DATALENGTH(@" + parameter + ") AND RIGHT(" + expression + ", DATALENGTH(@" + parameter + ") / 2) = @" + parameter + "))";

    protected override string RenderElementOf(
        Predicate.ElementOf elementOf,
        ICollection<QueryRenderParameter> parameters,
        ref int parameterIndex)
    {
        if (elementOf.Set.Type is not QueryType type)
            throw new QueryRenderException("GW-SEM-TYPE-007", "An element set must declare its exact element type before rendering.");
        var expression = Dialect.QuoteIdentifier(elementOf.Set.Name);
        if (elementOf.Values.Length == 0)
            return elementOf.Quantifier == SetQuantifier.Any
                ? "1 = 0"
                : "(ISJSON(" + expression + ") = 1 AND LEFT(LTRIM(" + expression + "), 1) = '[')";
        var clauses = new List<string>();
        foreach (var value in elementOf.Values)
        {
            if (value.Kind == QueryConstantKind.Null)
                clauses.Add("element.[value] IS NULL");
            else
                clauses.Add("element.[value] = @" + AddElementParameter(type, value.Value, parameters, ref parameterIndex));
        }
        var arrayGuard = "ISJSON(" + expression + ") = 1 AND LEFT(LTRIM(" + expression + "), 1) = '['";
        return elementOf.Quantifier == SetQuantifier.Any
            ? "(" + arrayGuard + " AND EXISTS (SELECT 1 FROM OPENJSON(" + expression + ") AS element WHERE " + string.Join(" OR ", clauses) + "))"
            : "(" + arrayGuard + " AND " + string.Join(" AND ", clauses.Select(clause => "EXISTS (SELECT 1 FROM OPENJSON(" + expression + ") AS element WHERE " + clause + ")")) + ")";
    }

    protected override string RenderPaging(
        Paging paging,
        ICollection<QueryRenderParameter> parameters,
        ref int parameterIndex)
    {
        if (paging.Offset is null && paging.Limit is null)
            return string.Empty;

        var offset = paging.Offset is int suppliedOffset
            ? AddPagingParameter(parameters, ref parameterIndex, suppliedOffset)
            : "0";
        var text = " OFFSET " + (paging.Offset is int ? "@" + offset : offset) + " ROWS";
        if (paging.Limit is int limit)
            text += " FETCH NEXT @" + AddPagingParameter(parameters, ref parameterIndex, limit) + " ROWS ONLY";
        return text;
    }

    private static string AddPagingParameter(
        ICollection<QueryRenderParameter> parameters,
        ref int parameterIndex,
        int value)
    {
        var name = "p" + parameterIndex++;
        parameters.Add(new QueryRenderParameter(name, QueryType.Int32, value));
        return name;
    }

    private string RenderStringBound(
        string expression,
        ColumnRef column,
        Bound bound,
        bool isLower,
        ICollection<QueryRenderParameter> parameters,
        ref int parameterIndex)
    {
        var parameter = AddParameter(column, bound.Value, parameters, ref parameterIndex);
        // SQL Server treats trailing spaces as equal in ordinary comparisons. Use a
        // strict lexical arm plus an explicit length tie-break for portable ordinal text.
        var operatorText = isLower ? ">" : "<";
        var lengthOperator = isLower
            ? bound.IsInclusive ? ">=" : ">"
            : bound.IsInclusive ? "<=" : "<";
        return "((" + expression + " " + operatorText + " @" + parameter + ") OR (" + expression +
            " = @" + parameter + " AND DATALENGTH(" + expression + ") " + lengthOperator +
            " DATALENGTH(@" + parameter + ")))";
    }
}
