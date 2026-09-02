using System.Globalization;
using Groundwork.Kernel;
using Groundwork.Query.Model;
using Groundwork.Store;
using Groundwork.Substrate.Relational;

namespace Groundwork.MySql;

/// <summary>MySQL/MariaDB renderer for Groundwork's normalized relational query contract.</summary>
public sealed class MySqlQueryRenderer : RelationalQueryRenderer
{
    public const int ParameterBudget = MySqlDialect.QueryParameterBudget;

    public MySqlQueryRenderer()
        : base(new MySqlDialect(), ParameterBudget, supportsIndexHints: false)
    {
    }

    protected override string ProviderName => "MySQL/MariaDB";

    protected override object? AdaptParameter(QueryType type, object? value) => type switch
    {
        QueryType.Boolean when value is bool boolean => boolean ? 1 : 0,
        QueryType.Guid when value is Guid guid => guid.ToString("D"),
        QueryType.DateTimeOffset when value is DateTimeOffset timestamp => timestamp.ToUniversalTime().Ticks,
        QueryType.Decimal when value is decimal decimalValue => decimalValue.ToString("G29", CultureInfo.InvariantCulture),
        _ => value
    };

    protected override string RenderColumn(ColumnRef column)
    {
        if (string.Equals(column.Name, CrossScopeQueryMaterializer.ScopeTokenColumn, StringComparison.Ordinal))
        {
            var scope = Dialect.QuoteIdentifier(ProviderOwnedColumns.Scope);
            return "UPPER(SHA2(CONVERT(" + scope + " USING utf8mb4), 256))";
        }
        return column.Type == QueryType.String
            ? base.RenderColumn(column) + " COLLATE " + MySqlDialect.OrdinalCollation
            : base.RenderColumn(column);
    }

    protected override bool RequiresExplicitSelection(ColumnRef column) =>
        string.Equals(column.Name, CrossScopeQueryMaterializer.ScopeTokenColumn, StringComparison.Ordinal);

    protected override string RenderDistinctPartition(ColumnRef column) => column.Type == QueryType.String
        ? RenderOrdinalKey(RenderColumn(column))
        : base.RenderDistinctPartition(column);

    protected override string RenderEquality(ColumnRef column, QueryConstant value, ICollection<QueryRenderParameter> parameters, ref int parameterIndex)
    {
        if (column.Type != QueryType.String)
            return base.RenderEquality(column, value, parameters, ref parameterIndex);
        var expression = RenderColumn(column);
        if (value.Kind == QueryConstantKind.Null)
            return expression + " IS NULL";
        var parameter = AddParameter(column, value, parameters, ref parameterIndex);
        return "(" + expression + " IS NOT NULL AND " + RenderOrdinalKey(expression) + " = " + RenderOrdinalKey("@" + parameter) + ")";
    }

    protected override string RenderMembership(Predicate.In membership, ICollection<QueryRenderParameter> parameters, ref int parameterIndex)
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
                parts.Add(expression + " IS NULL");
            else
            {
                var parameter = AddParameter(membership.Column, value, parameters, ref parameterIndex);
                parts.Add("(" + RenderOrdinalKey(expression) + " = " + RenderOrdinalKey("@" + parameter) + ")");
            }
        }
        return "(" + string.Join(" OR ", parts) + ")";
    }

    protected override string RenderRange(Predicate.Range range, ICollection<QueryRenderParameter> parameters, ref int parameterIndex)
    {
        if (range.Column.Type != QueryType.String)
            return base.RenderRange(range, parameters, ref parameterIndex);
        var expression = RenderColumn(range.Column);
        var parts = new List<string> { expression + " IS NOT NULL" };
        if (range.Lower is { } lower)
        {
            var name = AddParameter(range.Column, lower.Value, parameters, ref parameterIndex);
            parts.Add(RenderOrdinalKey(expression) + (lower.IsInclusive ? " >= " : " > ") + RenderOrdinalKey("@" + name));
        }
        if (range.Upper is { } upper)
        {
            var name = AddParameter(range.Column, upper.Value, parameters, ref parameterIndex);
            parts.Add(RenderOrdinalKey(expression) + (upper.IsInclusive ? " <= " : " < ") + RenderOrdinalKey("@" + name));
        }
        return "(" + string.Join(" AND ", parts) + ")";
    }

    protected override string RenderOrderTerm(OrderTerm term)
    {
        if (term.Column.Type != QueryType.String)
            return base.RenderOrderTerm(term);
        var expression = RenderColumn(term.Column);
        var direction = term.Direction == OrderDirection.Ascending ? "ASC" : "DESC";
        if (!term.Column.IsNullable)
            return RenderOrdinalKey(expression) + " " + direction;
        var nullRank = term.NullOrder == NullOrder.First ? "0" : "1";
        var nonNullRank = term.NullOrder == NullOrder.First ? "1" : "0";
        return $"CASE WHEN {expression} IS NULL THEN {nullRank} ELSE {nonNullRank} END ASC, {RenderOrdinalKey(expression)} {direction}";
    }

    protected override string RenderCursorEquality(ColumnRef column, QueryConstant value, ICollection<QueryRenderParameter> parameters, ref int parameterIndex)
    {
        if (column.Type != QueryType.String)
            return base.RenderCursorEquality(column, value, parameters, ref parameterIndex);
        var expression = RenderColumn(column);
        if (value.Kind == QueryConstantKind.Null)
            return expression + " IS NULL";
        var parameter = AddParameter(column, value, parameters, ref parameterIndex);
        return "(" + expression + " IS NOT NULL AND " + RenderOrdinalKey(expression) + " = " + RenderOrdinalKey("@" + parameter) + ")";
    }

    protected override string RenderAfter(OrderTerm term, QueryConstant value, ICollection<QueryRenderParameter> parameters, ref int parameterIndex)
    {
        if (term.Column.Type != QueryType.String)
            return base.RenderAfter(term, value, parameters, ref parameterIndex);
        var expression = RenderColumn(term.Column);
        if (value.Kind == QueryConstantKind.Null)
            return term.NullOrder == NullOrder.First ? expression + " IS NOT NULL" : "1 = 0";
        var parameter = AddParameter(term.Column, value, parameters, ref parameterIndex);
        var comparison = term.Direction == OrderDirection.Ascending ? ">" : "<";
        var strict = "(" + expression + " IS NOT NULL AND " + RenderOrdinalKey(expression) + " " + comparison + " " + RenderOrdinalKey("@" + parameter) + ")";
        return term.NullOrder == NullOrder.First ? strict : "(" + strict + " OR " + expression + " IS NULL)";
    }

    protected override string RenderContains(string expression, string parameter) =>
        "(CHAR_LENGTH(@" + parameter + ") = 0 OR INSTR(BINARY " + expression + ", BINARY @" + parameter + ") > 0)";

    protected override string RenderEndsWith(string expression, string parameter) =>
        "(CHAR_LENGTH(@" + parameter + ") = 0 OR BINARY RIGHT(" + expression + ", CHAR_LENGTH(@" + parameter + ")) = BINARY @" + parameter + ")";

    protected override string RenderElementSubstring(
        Predicate.ElementSubstring elementSubstring,
        ICollection<QueryRenderParameter> parameters,
        ref int parameterIndex)
    {
        if (elementSubstring.Set.Type != QueryType.String)
            throw new QueryRenderException("GW-SEM-TYPE-005", "Element substring matching requires a typed string element set.");
        if (elementSubstring.Anchor is not (Anchor.Contains or Anchor.EndsWith))
            throw new QueryRenderException("GW-SEM-TEXT-003", "The requested element substring anchor is not portable; use Contains or EndsWith.");
        var expression = Dialect.QuoteIdentifier(elementSubstring.Set.Name);
        var parameter = AddElementParameter(QueryType.String, elementSubstring.Needle, parameters, ref parameterIndex);
        var element = ApplyElementComparison("element.element_value", elementSubstring.StringComparison);
        var needle = ApplyElementComparison("@" + parameter, elementSubstring.StringComparison);
        var operation = elementSubstring.Anchor == Anchor.Contains
            ? "INSTR(" + element + ", " + needle + ") > 0"
            : "RIGHT(" + element + ", CHAR_LENGTH(" + needle + ")) = " + needle;
        var emptyNeedle = "CHAR_LENGTH(@" + parameter + ") = 0";
        var validJson = "CASE WHEN JSON_VALID(" + expression + ") = 1 THEN " + expression + " ELSE JSON_ARRAY() END";
        var validArray = "CASE WHEN JSON_TYPE(" + validJson + ") = 'ARRAY' THEN " + validJson + " ELSE JSON_ARRAY() END";
        var arrayGuard = "JSON_TYPE(" + validJson + ") = 'ARRAY'";
        var table = "JSON_TABLE(" + validArray + ", '$[*]' COLUMNS (element_index FOR ORDINALITY, element_value LONGTEXT CHARACTER SET utf8mb4 PATH '$' NULL ON ERROR)) AS element";
        var stringGuard = "JSON_TYPE(JSON_EXTRACT(" + validArray + ", CONCAT('$[', element.element_index - 1, ']'))) = 'STRING'";
        return "(" + arrayGuard + " AND EXISTS (SELECT 1 FROM " + table + " WHERE " + stringGuard + " AND (" + emptyNeedle + " OR " + operation + ")))";
    }

    private static string ApplyElementComparison(string expression, QueryStringComparisonPolicy policy) => policy switch
    {
        QueryStringComparisonPolicy.Ordinal => "BINARY " + expression,
        QueryStringComparisonPolicy.AsciiIgnoreCase => ApplyAsciiFold(expression),
        _ => throw new QueryRenderException("GW-SEM-TEXT-001", "Element substring matching requires an explicit portable comparison policy.")
    };

    private static string ApplyAsciiFold(string expression)
    {
        foreach (var letter in "ABCDEFGHIJKLMNOPQRSTUVWXYZ")
            expression = "REPLACE(" + expression + " COLLATE utf8mb4_bin, '" + letter + "', '" + char.ToLowerInvariant(letter) + "')";
        return expression + " COLLATE utf8mb4_bin";
    }

    internal static string RenderOrdinalKey(string expression) =>
        "HEX(CONVERT(" + expression + " USING utf16))";
}
