using Groundwork.Substrate.Relational;
using Groundwork.Query.Model;
using Groundwork.Store;
using System.Globalization;

namespace Groundwork.Sqlite;

/// <summary>SQLite's one native renderer for the normalized v2 query contract.</summary>
public sealed class SqliteQueryRenderer : RelationalQueryRenderer
{
    /// <summary>SQLite's real bound on bound parameters in one statement.</summary>
    public const int ParameterBudget = 999;

    public SqliteQueryRenderer()
        : base(new SqliteDialect(), ParameterBudget, supportsIndexHints: false)
    {
    }

    protected override string ProviderName => "SQLite";

    protected override string RenderColumn(ColumnRef column)
    {
        if (string.Equals(column.Name, CrossScopeQueryMaterializer.ScopeTokenColumn, StringComparison.Ordinal))
            return "groundwork_scope_token(" + Dialect.QuoteIdentifier(SqliteSchemaCoordinator.ScopeColumn) + ") COLLATE GROUNDWORK_UTF16_ORDINAL";
        return column.Type switch
        {
            QueryType.Decimal => Dialect.QuoteIdentifier(column.Name) + " COLLATE GROUNDWORK_DECIMAL_18_4",
            QueryType.String => base.RenderColumn(column) + " COLLATE GROUNDWORK_UTF16_ORDINAL",
            _ => base.RenderColumn(column)
        };
    }

    protected override string RenderReductionAggregate(ResultShape.Reduction reduction, string valueExpression)
    {
        if (reduction is ResultShape.Sum && reduction.Column.Type == QueryType.Decimal)
            return "CASE WHEN COUNT(" + valueExpression + ") = 0 THEN NULL ELSE groundwork_decimal_sum(" + valueExpression + ") END";
        return base.RenderReductionAggregate(reduction, valueExpression);
    }

    protected override bool RequiresExplicitSelection(ColumnRef column) =>
        string.Equals(column.Name, CrossScopeQueryMaterializer.ScopeTokenColumn, StringComparison.Ordinal);

    protected override object? AdaptParameter(QueryType type, object? value) => type switch
    {
        QueryType.Boolean when value is bool boolean => boolean ? 1 : 0,
        QueryType.Guid when value is Guid guid => guid.ToString("D"),
        QueryType.DateTimeOffset when value is DateTimeOffset timestamp => timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        QueryType.Decimal when value is decimal decimalValue => decimalValue.ToString("G29", CultureInfo.InvariantCulture),
        _ => value
    };

    protected override string RenderContains(string expression, string parameter) =>
        "(length(@" + parameter + ") = 0 OR instr(" + expression + ", @" + parameter + ") > 0)";

    protected override string RenderEndsWith(string expression, string parameter) =>
        "(length(@" + parameter + ") = 0 OR substr(" + expression + ", -length(@" + parameter + ")) = @" + parameter + ")";

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
                : "(json_valid(" + expression + ") = 1 AND json_type(" + expression + ") = 'array')";
        var clauses = new List<string>();
        foreach (var value in elementOf.Values)
        {
            if (value.Kind == QueryConstantKind.Null)
                clauses.Add("json_each.value IS NULL");
            else
                clauses.Add("json_each.value = @" + AddElementParameter(type, value.Value, parameters, ref parameterIndex));
        }
        var arrayGuard = "json_valid(" + expression + ") = 1 AND json_type(" + expression + ") = 'array'";
        return elementOf.Quantifier == SetQuantifier.Any
            ? "(" + arrayGuard + " AND EXISTS (SELECT 1 FROM json_each(" + expression + ") WHERE " + string.Join(" OR ", clauses) + "))"
            : "(" + arrayGuard + " AND " + string.Join(" AND ", clauses.Select(clause => "EXISTS (SELECT 1 FROM json_each(" + expression + ") WHERE " + clause + ")")) + ")";
    }
}
