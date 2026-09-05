using Groundwork.Substrate.Relational;
using Groundwork.Query.Model;
using Groundwork.Store;

namespace Groundwork.PostgreSql;

/// <summary>PostgreSQL's one native renderer for the normalized v2 query contract.</summary>
public sealed class PostgreSqlQueryRenderer : RelationalQueryRenderer
{
    /// <summary>PostgreSQL's real bound on bound parameters in one statement.</summary>
    public const int ParameterBudget = 65_535;

    public PostgreSqlQueryRenderer()
        : base(new PostgreSqlDialect(), ParameterBudget, supportsIndexHints: false)
    {
    }

    protected override string ProviderName => "PostgreSQL";

    protected override string RenderReductionAggregate(ResultShape.Reduction reduction, string valueExpression)
    {
        if (reduction.Column.Type == QueryType.Guid && reduction is (ResultShape.Min or ResultShape.Max))
        {
            var aggregate = reduction is ResultShape.Min ? "MIN" : "MAX";
            return "CAST(" + aggregate + "((" + valueExpression + ")::text COLLATE \"C\") AS uuid)";
        }
        if (reduction is ResultShape.Sum && reduction.Column.Type is QueryType.Int32 or QueryType.Int64)
            return "CASE WHEN COUNT(" + valueExpression + ") = 0 THEN NULL ELSE CAST(SUM(" + valueExpression + ") AS bigint) END";
        return base.RenderReductionAggregate(reduction, valueExpression);
    }

    protected override object? AdaptParameter(QueryType type, object? value) => type == QueryType.DateTimeOffset && value is DateTimeOffset timestamp
        ? timestamp.ToUniversalTime().Ticks
        : value;

    protected override string RenderColumn(ColumnRef column)
    {
        if (string.Equals(column.Name, CrossScopeQueryMaterializer.ScopeTokenColumn, StringComparison.Ordinal))
            return "upper(encode(sha256(convert_to(" + Dialect.QuoteIdentifier(PostgreSqlSchemaCoordinator.ScopeColumn) + ", 'UTF8')), 'hex'))";
        return column.Type == QueryType.String
            ? "(" + base.RenderColumn(column) + " COLLATE \"C\")"
            : base.RenderColumn(column);
    }

    protected override bool RequiresExplicitSelection(ColumnRef column) =>
        string.Equals(column.Name, CrossScopeQueryMaterializer.ScopeTokenColumn, StringComparison.Ordinal);

    protected override string RenderOrderTerm(OrderTerm term) =>
        RenderPostgreSqlOrderTerm(term, persistedOrdinalIdentity: false);

    /// <summary>Renders ordering using the selected physical search-key mappings.</summary>
    protected override string RenderMappedOrderTerm(OrderTerm term, QueryRenderOptions options) =>
        RenderPostgreSqlOrderTerm(term, IsPersistedOrdinalIdentityColumn(term.Column, options));

    private string RenderPostgreSqlOrderTerm(OrderTerm term, bool persistedOrdinalIdentity)
    {
        if (term.Column.Type == QueryType.Guid)
        {
            var guidExpression = RenderColumn(term.Column);
            var guidKey = "(" + guidExpression + "::text COLLATE \"C\")";
            if (!term.Column.IsNullable)
                return RenderNonNullableOrder(guidKey, term.Direction);
            var guidDirection = term.Direction == OrderDirection.Ascending ? "ASC" : "DESC";
            var guidNullRank = term.NullOrder == NullOrder.First ? "0" : "1";
            var guidNonNullRank = term.NullOrder == NullOrder.First ? "1" : "0";
            return "CASE WHEN " + guidExpression + " IS NULL THEN " + guidNullRank + " ELSE " + guidNonNullRank + " END ASC, " + guidKey + " " + guidDirection;
        }
        if (term.Column.Type != QueryType.String)
            return term.Column.IsNullable
                ? base.RenderOrderTerm(term)
                : RenderNonNullableOrder(RenderColumn(term.Column), term.Direction);

        var expression = RenderColumn(term.Column);
        if (persistedOrdinalIdentity)
        {
            var directDirection = term.Direction == OrderDirection.Ascending ? "ASC" : "DESC";
            if (!term.Column.IsNullable)
                return RenderNonNullableOrder(expression, term.Direction);
            var directNullRank = term.NullOrder == NullOrder.First ? "0" : "1";
            var directNonNullRank = term.NullOrder == NullOrder.First ? "1" : "0";
            return "CASE WHEN " + expression + " IS NULL THEN " + directNullRank + " ELSE " + directNonNullRank + " END ASC, " + expression + " " + directDirection;
        }
        var key = RenderOrdinalKey(expression);
        var direction = term.Direction == OrderDirection.Ascending ? "ASC" : "DESC";
        if (!term.Column.IsNullable)
            return RenderNonNullableOrder(key, term.Direction);
        var nullRank = term.NullOrder == NullOrder.First ? "0" : "1";
        var nonNullRank = term.NullOrder == NullOrder.First ? "1" : "0";
        return "CASE WHEN " + expression + " IS NULL THEN " + nullRank + " ELSE " + nonNullRank + " END ASC, " + key + " " + direction;
    }

    private static string RenderNonNullableOrder(string expression, OrderDirection direction) =>
        expression + (direction == OrderDirection.Ascending
            ? " ASC NULLS FIRST"
            : " DESC NULLS LAST");

    protected override string RenderRange(
        Predicate.Range range,
        ICollection<QueryRenderParameter> parameters,
        ref int parameterIndex)
    {
        if (range.Column.Type != QueryType.String)
            return base.RenderRange(range, parameters, ref parameterIndex);

        var expression = RenderColumn(range.Column);
        var key = RenderOrdinalKey(expression);
        var parts = new List<string> { expression + " IS NOT NULL" };
        if (range.Lower is { } lower)
        {
            var name = AddParameter(range.Column, lower.Value, parameters, ref parameterIndex);
            parts.Add(key + (lower.IsInclusive ? " >= " : " > ") + RenderOrdinalKey("@" + name));
        }
        if (range.Upper is { } upper)
        {
            var name = AddParameter(range.Column, upper.Value, parameters, ref parameterIndex);
            parts.Add(key + (upper.IsInclusive ? " <= " : " < ") + RenderOrdinalKey("@" + name));
        }
        return "(" + string.Join(" AND ", parts) + ")";
    }

    protected override string RenderCursorEquality(
        ColumnRef column,
        QueryConstant value,
        ICollection<QueryRenderParameter> parameters,
        ref int parameterIndex) =>
        RenderPostgreSqlCursorEquality(column, value, parameters, ref parameterIndex, persistedOrdinalIdentity: false);

    /// <summary>Renders cursor equality using the selected physical search-key mappings.</summary>
    protected override string RenderCursorEquality(
        ColumnRef column,
        QueryConstant value,
        ICollection<QueryRenderParameter> parameters,
        ref int parameterIndex,
        QueryRenderOptions options) =>
        RenderPostgreSqlCursorEquality(
            column,
            value,
            parameters,
            ref parameterIndex,
            IsPersistedOrdinalIdentityColumn(column, options));

    private string RenderPostgreSqlCursorEquality(
        ColumnRef column,
        QueryConstant value,
        ICollection<QueryRenderParameter> parameters,
        ref int parameterIndex,
        bool persistedOrdinalIdentity)
    {
        if (column.Type == QueryType.Guid)
        {
            var guidExpression = RenderColumn(column);
            if (value.Kind == QueryConstantKind.Null)
                return guidExpression + " IS NULL";
            var guidParameter = AddParameter(column, value, parameters, ref parameterIndex);
            return "(" + guidExpression + " IS NOT NULL AND (" + guidExpression + "::text COLLATE \"C\") = ((@" + guidParameter + ")::text COLLATE \"C\"))";
        }
        if (column.Type != QueryType.String)
            return base.RenderCursorEquality(column, value, parameters, ref parameterIndex);
        var expression = RenderColumn(column);
        if (value.Kind == QueryConstantKind.Null)
            return expression + " IS NULL";
        var name = AddParameter(column, value, parameters, ref parameterIndex);
        if (persistedOrdinalIdentity)
            return "(" + expression + " IS NOT NULL AND " + expression + " = @" + name + ")";
        return "(" + expression + " IS NOT NULL AND " + RenderOrdinalKey(expression) + " = " + RenderOrdinalKey("@" + name) + ")";
    }

    protected override string RenderAfter(
        OrderTerm term,
        QueryConstant value,
        ICollection<QueryRenderParameter> parameters,
        ref int parameterIndex) =>
        RenderPostgreSqlAfter(term, value, parameters, ref parameterIndex, persistedOrdinalIdentity: false);

    /// <summary>Renders a cursor boundary using the selected physical search-key mappings.</summary>
    protected override string RenderAfter(
        OrderTerm term,
        QueryConstant value,
        ICollection<QueryRenderParameter> parameters,
        ref int parameterIndex,
        QueryRenderOptions options) =>
        RenderPostgreSqlAfter(
            term,
            value,
            parameters,
            ref parameterIndex,
            IsPersistedOrdinalIdentityColumn(term.Column, options));

    private string RenderPostgreSqlAfter(
        OrderTerm term,
        QueryConstant value,
        ICollection<QueryRenderParameter> parameters,
        ref int parameterIndex,
        bool persistedOrdinalIdentity)
    {
        if (term.Column.Type == QueryType.Guid)
        {
            var guidExpression = RenderColumn(term.Column);
            if (value.Kind == QueryConstantKind.Null)
                return term.NullOrder == NullOrder.First ? guidExpression + " IS NOT NULL" : "1 = 0";
            var guidParameter = AddParameter(term.Column, value, parameters, ref parameterIndex);
            var guidComparison = term.Direction == OrderDirection.Ascending ? ">" : "<";
            var guidStrict = "(" + guidExpression + " IS NOT NULL AND (" + guidExpression + "::text COLLATE \"C\") " + guidComparison + " ((@" + guidParameter + ")::text COLLATE \"C\"))";
            return term.NullOrder == NullOrder.First || !term.Column.IsNullable
                ? guidStrict
                : "(" + guidStrict + " OR " + guidExpression + " IS NULL)";
        }
        if (term.Column.Type != QueryType.String)
            return base.RenderAfter(term, value, parameters, ref parameterIndex);
        var expression = RenderColumn(term.Column);
        if (value.Kind == QueryConstantKind.Null)
            return term.NullOrder == NullOrder.First ? expression + " IS NOT NULL" : "1 = 0";
        var name = AddParameter(term.Column, value, parameters, ref parameterIndex);
        var comparison = term.Direction == OrderDirection.Ascending ? ">" : "<";
        if (persistedOrdinalIdentity)
        {
            var directStrict = "(" + expression + " IS NOT NULL AND " + expression + " " + comparison + " @" + name + ")";
            return term.NullOrder == NullOrder.First || !term.Column.IsNullable
                ? directStrict
                : "(" + directStrict + " OR " + expression + " IS NULL)";
        }
        var strict = "(" + expression + " IS NOT NULL AND " + RenderOrdinalKey(expression) + " " + comparison + " " + RenderOrdinalKey("@" + name) + ")";
        return term.NullOrder == NullOrder.First || !term.Column.IsNullable
            ? strict
            : "(" + strict + " OR " + expression + " IS NULL)";
    }

    protected override string RenderContains(string expression, string parameter) =>
        "(length(@" + parameter + ") = 0 OR POSITION(@" + parameter + " IN " + expression + ") > 0)";

    protected override string RenderEndsWith(string expression, string parameter) =>
        "(length(@" + parameter + ") = 0 OR RIGHT(" + expression + ", length(@" + parameter + ")) = @" + parameter + ")";

    private static bool IsPersistedOrdinalIdentityColumn(ColumnRef column, QueryRenderOptions options) =>
        column.Type == QueryType.String &&
        options.SearchKeyColumns.Values.Any(mapping =>
            mapping.OrderByPhysicalColumn &&
            mapping.PreservesOrdinalIdentity &&
            !string.Equals(mapping.SourceColumn, mapping.PhysicalColumn, StringComparison.Ordinal) &&
            string.Equals(mapping.PhysicalColumn, column.Name, StringComparison.Ordinal));

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
                ? "FALSE"
                : "jsonb_typeof(" + expression + ") = 'array'";
        var clauses = new List<string>();
        foreach (var value in elementOf.Values)
        {
            if (value.Kind == QueryConstantKind.Null)
                clauses.Add("element.value = 'null'::jsonb");
            else
                clauses.Add("element.value = to_jsonb(@" + AddElementParameter(type, value.Value, parameters, ref parameterIndex) + ")");
        }
        var exists = elementOf.Quantifier == SetQuantifier.Any
            ? "EXISTS (SELECT 1 FROM jsonb_array_elements(" + expression + ") AS element(value) WHERE " + string.Join(" OR ", clauses) + ")"
            : string.Join(" AND ", clauses.Select(clause => "EXISTS (SELECT 1 FROM jsonb_array_elements(" + expression + ") AS element(value) WHERE " + clause + ")"));
        return "CASE WHEN jsonb_typeof(" + expression + ") = 'array' THEN (" + exists + ") ELSE FALSE END";
    }

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
        var elementText = "element.value #>> '{}'";
        var element = ApplyElementComparison(elementText, elementSubstring.StringComparison);
        var needle = ApplyElementComparison("@" + parameter, elementSubstring.StringComparison);
        var operation = elementSubstring.Anchor == Anchor.Contains
            ? "POSITION(" + needle + " IN " + element + ") > 0"
            : "RIGHT(" + element + ", length(" + needle + ")) = " + needle;
        var emptyNeedle = "length(@" + parameter + ") = 0";
        var exists = "EXISTS (SELECT 1 FROM jsonb_array_elements(" + expression + ") AS element(value) WHERE jsonb_typeof(element.value) = 'string' AND (" + emptyNeedle + " OR " + operation + "))";
        return "CASE WHEN jsonb_typeof(" + expression + ") = 'array' THEN (" + exists + ") ELSE FALSE END";
    }

    private static string ApplyElementComparison(string expression, QueryStringComparisonPolicy policy) => policy switch
    {
        QueryStringComparisonPolicy.Ordinal => "(" + expression + " COLLATE \"C\")",
        QueryStringComparisonPolicy.AsciiIgnoreCase => "(translate(" + expression + " COLLATE \"C\", 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz') COLLATE \"C\")",
        _ => throw new QueryRenderException("GW-SEM-TEXT-001", "Element substring matching requires an explicit portable comparison policy.")
    };

    internal static string RenderOrdinalKey(string expression) =>
        "COALESCE((SELECT string_agg(CASE WHEN ascii(chars.ch) <= 65535 THEN lpad(to_hex(ascii(chars.ch)), 4, '0') ELSE " +
        "lpad(to_hex(55296 + ((ascii(chars.ch) - 65536) >> 10)), 4, '0') || " +
        "lpad(to_hex(56320 + ((ascii(chars.ch) - 65536) & 1023)), 4, '0') END, '' ORDER BY chars.ord) " +
        "FROM unnest(string_to_array(" + expression + ", NULL)) WITH ORDINALITY AS chars(ch, ord)), '')";
}
