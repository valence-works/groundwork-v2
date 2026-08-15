using System.Collections.Immutable;
using System.Data.Common;
using Groundwork.Query.Model;

namespace Groundwork.Substrate.Relational;

/// <summary>Native SQL and parameter values produced for one normalized query.</summary>
public sealed class RelationalQueryCommand
{
    public RelationalQueryCommand(
        string commandText,
        IEnumerable<QueryRenderParameter> parameters,
        bool includesTotalCount,
        bool isMatchNone,
        string? selectedIndex,
        bool indexHintApplied,
        IReadOnlyList<string> appliedOrder)
    {
        CommandText = commandText ?? throw new ArgumentNullException(nameof(commandText));
        Parameters = (parameters ?? throw new ArgumentNullException(nameof(parameters))).ToImmutableArray();
        if (Parameters.Any(parameter => parameter is null))
            throw new ArgumentException("Query parameters cannot contain null references.", nameof(parameters));
        IncludesTotalCount = includesTotalCount;
        IsMatchNone = isMatchNone;
        SelectedIndex = selectedIndex;
        IndexHintApplied = indexHintApplied;
        AppliedOrder = (appliedOrder ?? throw new ArgumentNullException(nameof(appliedOrder))).ToImmutableArray();
    }

    public string CommandText { get; }
    public ImmutableArray<QueryRenderParameter> Parameters { get; }
    public bool IncludesTotalCount { get; }
    public bool IsMatchNone { get; }
    public string? SelectedIndex { get; }
    public bool IndexHintApplied { get; }
    public ImmutableArray<string> AppliedOrder { get; }
}

/// <summary>Executes a rendered relational command while leaving value decoding to the provider.</summary>
public static class RelationalQueryResultReader
{
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> Read(
        DbConnection connection,
        RelationalQueryCommand query,
        Func<string, object?, object?> decode)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(decode);
        using var command = connection.CreateCommand();
        command.CommandText = query.CommandText;
        foreach (var value in query.Parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@" + value.Name;
            parameter.Value = value.Value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
        using var reader = command.ExecuteReader();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var index = 0; index < reader.FieldCount; index++)
            {
                var name = reader.GetName(index);
                var value = reader.IsDBNull(index) ? null : reader.GetValue(index);
                row[name] = decode(name, value);
            }
            rows.Add(row);
        }
        return rows;
    }
}

/// <summary>
/// Shared SQL renderer for the public relational dialect seam. Provider assemblies supply the
/// dialect, budget, paging syntax, and (where supported) index-hint syntax.
/// </summary>
public abstract class RelationalQueryRenderer
{
    private readonly RelationalDialect dialect;
    private readonly int parameterBudget;
    private readonly bool supportsIndexHints;

    protected RelationalQueryRenderer(RelationalDialect dialect, int parameterBudget, bool supportsIndexHints)
    {
        this.dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
        if (parameterBudget <= 0)
            throw new ArgumentOutOfRangeException(nameof(parameterBudget));
        this.parameterBudget = parameterBudget;
        this.supportsIndexHints = supportsIndexHints;
    }

    protected RelationalDialect Dialect => dialect;

    public RelationalQueryCommand Render(QueryRequest request, QueryRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        options ??= QueryRenderOptions.Default;
        if (options.InValueLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "The In value limit must be positive.");

        var validation = PortableQuerySemantics.Validate(request);
        if (!validation.IsPortable)
        {
            var refusal = validation.Refusals[0];
            throw new QueryRenderException(refusal.Code, refusal.Message + " (" + refusal.Path + ").");
        }
        if (request.LatestPerKey is not null)
            throw new QueryRenderException("GW-QUERY-030", "Latest-per-key rendering is not part of the normalized relational query command yet.");

        var effectiveOrder = EffectiveOrder(request, options);
        var parameters = new List<QueryRenderParameter>();
        var parameterIndex = 0;
        var matchNone = request.Where is Predicate.AlwaysFalse;
        var where = RenderPredicate(request.Where, parameters, ref parameterIndex, options.InValueLimit, request.Table.Value);
        if (request.Paging.ContinuationToken is not null)
        {
            if (effectiveOrder.Count == 0)
                throw new QueryRenderException("GW-QUERY-013", "Keyset continuation requires an explicit ordered query.");
            IReadOnlyList<QueryConstant> cursor;
            try
            {
                cursor = QueryContinuationToken.Decode(
                    request.Paging.ContinuationToken,
                    request,
                    options);
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
            {
                throw new QueryRenderException("GW-QUERY-013", "The keyset continuation token is invalid: " + exception.Message);
            }
            where = $"({where}) AND ({RenderContinuation(effectiveOrder, cursor, parameters, ref parameterIndex)})";
        }

        var selectedIndex = options.FindPinnedIndex();
        var indexHintApplied = selectedIndex is not null && supportsIndexHints;
        if (selectedIndex is not null && !matchNone && !selectedIndex.IncludesNulls)
        {
            var unproven = selectedIndex.Columns
                .Where(column => selectedIndex.NullableColumns.Contains(column) && CanMatchNull(request.Where, column))
                .ToArray();
            if (unproven.Length != 0)
                throw new QueryRenderException(
                    "GW-QUERY-009",
                    $"Query on '{request.Table.Value}' can match null values in sparse pinned index column(s) " +
                    $"{string.Join(", ", unproven)}; the declaration must include nulls or use an unpinned index.");
        }

        var selection = request.Projection.AllColumns
            ? "*"
            : string.Join(", ", request.Projection.Columns.Select(column => dialect.QuoteIdentifier(column.Name)));
        if (request.Result.IncludesTotalCount)
            selection += ", " + RenderCountExpression() + " AS " + dialect.QuoteIdentifier("__groundwork_total_count");

        var from = dialect.QuoteIdentifier(request.Table.Value);
        if (indexHintApplied)
            from += " " + RenderIndexHint(options.ResolvePhysicalIndexName(selectedIndex!.Name));
        var sql = "SELECT " + selection + " FROM " + from + " WHERE " + where;
        if (effectiveOrder.Count != 0)
        {
            sql += " ORDER BY " + string.Join(", ", effectiveOrder.Select(RenderOrderTerm));
        }
        else if ((request.Paging.Offset is not null || request.Paging.Limit is not null) && RequiresOrderForOffset)
        {
            sql += " ORDER BY (SELECT 1)";
        }
        sql += RenderPaging(request.Paging, parameters, ref parameterIndex);
        sql += ";";

        if (parameters.Count > parameterBudget)
            throw new QueryRenderException(
                "GW-QUERY-015",
                $"Query on '{request.Table.Value}' requires {parameters.Count} parameters, exceeding the {ProviderName} provider budget of {parameterBudget}.");

        return new RelationalQueryCommand(
            sql,
            parameters,
            request.Result.IncludesTotalCount,
            matchNone,
            selectedIndex?.Name,
            indexHintApplied,
            effectiveOrder.Select(term => term.Column.Name).ToArray());
    }

    protected abstract string ProviderName { get; }

    protected virtual string RenderCountExpression() => "COUNT(*) OVER()";

    protected virtual bool RequiresOrderForOffset => false;

    protected virtual string RenderIndexHint(string indexName) =>
        throw new NotSupportedException($"{ProviderName} does not support index hints.");

    /// <summary>Returns the provider expression used for comparisons and ordering of one column.</summary>
    protected virtual string RenderColumn(ColumnRef column) => dialect.QuoteIdentifier(column.Name);

    /// <summary>Adapts a model value to the provider's declared physical representation.</summary>
    protected virtual object? AdaptParameter(QueryType type, object? value) => value;

    protected virtual string RenderPaging(
        Paging paging,
        ICollection<QueryRenderParameter> parameters,
        ref int parameterIndex)
    {
        if (paging.Offset is null && paging.Limit is null)
            return string.Empty;
        var text = string.Empty;
        if (paging.Limit is int limit)
        {
            var name = "p" + parameterIndex++;
            parameters.Add(new QueryRenderParameter(name, QueryType.Int32, limit));
            text += " LIMIT @" + name;
        }
        if (paging.Offset is int offset)
        {
            var name = "p" + parameterIndex++;
            parameters.Add(new QueryRenderParameter(name, QueryType.Int32, offset));
            text += " OFFSET @" + name;
        }
        return text;
    }

    private IReadOnlyList<OrderTerm> EffectiveOrder(QueryRequest request, QueryRenderOptions options) =>
        options.GetEffectiveOrder(request);

    private string RenderPredicate(
        Predicate predicate,
        ICollection<QueryRenderParameter> parameters,
        ref int parameterIndex,
        int inValueLimit,
        string table)
    {
        switch (predicate)
        {
            case Predicate.AlwaysTrue:
                return "1 = 1";
            case Predicate.AlwaysFalse:
                return "1 = 0";
            case Predicate.Equal equal:
                return RenderEquality(equal.Column, equal.Value, parameters, ref parameterIndex);
            case Predicate.In membership:
                if (membership.Values.Distinct().Count() > inValueLimit)
                    throw new QueryRenderException(
                        "GW-QUERY-015",
                        $"Query on '{table}' has an In predicate on '{membership.Column.Name}' with " +
                        $"{membership.Values.Distinct().Count()} distinct values, exceeding the configured maximum of {inValueLimit}.");
                return RenderMembership(membership, parameters, ref parameterIndex);
            case Predicate.Range range:
                return RenderRange(range, parameters, ref parameterIndex);
            case Predicate.ColumnCompare compare:
                return RenderColumnCompare(compare);
            case Predicate.Substring substring:
            {
                var parameter = AddElementParameter(substring.Column.Type, substring.Needle, parameters, ref parameterIndex);
                var expression = RenderColumn(substring.Column);
                var operation = substring.Anchor == Anchor.Contains
                    ? RenderContains(expression, parameter)
                    : RenderEndsWith(expression, parameter);
                return "(" + expression + " IS NOT NULL AND " + operation + ")";
            }
            case Predicate.ElementOf elementOf:
                return RenderElementOf(elementOf, parameters, ref parameterIndex);
            case Predicate.Not not:
                return "NOT (" + RenderPredicate(not.Inner, parameters, ref parameterIndex, inValueLimit, table) + ")";
            case Predicate.And and:
            {
                if (and.Terms.Length == 0)
                    return "1 = 1";
                var terms = new List<string>();
                foreach (var term in and.Terms)
                    terms.Add(RenderPredicate(term, parameters, ref parameterIndex, inValueLimit, table));
                return "(" + string.Join(" AND ", terms) + ")";
            }
            case Predicate.Or or:
            {
                if (or.Terms.Length == 0)
                    return "1 = 0";
                var terms = new List<string>();
                foreach (var term in or.Terms)
                    terms.Add(RenderPredicate(term, parameters, ref parameterIndex, inValueLimit, table));
                return "(" + string.Join(" OR ", terms) + ")";
            }
            case Predicate.StartsWith:
                throw new QueryRenderException("GW-QUERY-030", "This normalized predicate requires a provider-independent persisted projection and cannot be rendered directly.");
            default:
                throw new QueryRenderException("GW-QUERY-030", "The predicate node is outside the closed native query surface.");
        }
    }

    protected virtual string RenderEquality(
        ColumnRef column,
        QueryConstant value,
        ICollection<QueryRenderParameter> parameters,
        ref int parameterIndex)
    {
        var expression = RenderColumn(column);
        if (value.Kind == QueryConstantKind.Null)
            return expression + " IS NULL";
        var name = AddParameter(column, value, parameters, ref parameterIndex);
        return "(" + expression + " IS NOT NULL AND " + expression + " = @" + name + ")";
    }

    protected virtual string RenderMembership(
        Predicate.In membership,
        ICollection<QueryRenderParameter> parameters,
        ref int parameterIndex)
    {
        if (membership.Values.Length == 0)
            return "1 = 0";
        var expression = RenderColumn(membership.Column);
        var nullValue = membership.Values.Any(value => value.Kind == QueryConstantKind.Null);
        var values = new List<string>();
        foreach (var value in membership.Values.Where(value => value.Kind != QueryConstantKind.Null))
            values.Add("@" + AddParameter(membership.Column, value, parameters, ref parameterIndex));
        var parts = new List<string>();
        if (values.Count != 0)
            parts.Add("(" + expression + " IS NOT NULL AND " + expression + " IN (" + string.Join(", ", values) + "))");
        if (nullValue)
            parts.Add(expression + " IS NULL");
        return parts.Count == 1 ? parts[0] : "(" + string.Join(" OR ", parts) + ")";
    }

    protected virtual string RenderRange(Predicate.Range range, ICollection<QueryRenderParameter> parameters, ref int parameterIndex)
    {
        var expression = RenderColumn(range.Column);
        var parts = new List<string> { expression + " IS NOT NULL" };
        if (range.Lower is not null)
        {
            var name = AddParameter(range.Column, range.Lower.Value, parameters, ref parameterIndex);
            parts.Add(expression + (range.Lower.IsInclusive ? " >= @" : " > @") + name);
        }
        if (range.Upper is not null)
        {
            var name = AddParameter(range.Column, range.Upper.Value, parameters, ref parameterIndex);
            parts.Add(expression + (range.Upper.IsInclusive ? " <= @" : " < @") + name);
        }
        return "(" + string.Join(" AND ", parts) + ")";
    }

    private string RenderColumnCompare(Predicate.ColumnCompare compare)
    {
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
        if (compare.Op == CompareOp.NotEqual)
            return "(" + left + " IS NULL OR " + right + " IS NULL OR " + left + " <> " + right + ")";
        return "(" + left + " IS NOT NULL AND " + right + " IS NOT NULL AND " + left + " " + op + " " + right + ")";
    }

    private string RenderContinuation(
        IReadOnlyList<OrderTerm> order,
        IReadOnlyList<QueryConstant> cursor,
        ICollection<QueryRenderParameter> parameters,
        ref int parameterIndex)
    {
        var alternatives = new List<string>();
        for (var boundary = 0; boundary < order.Count; boundary++)
        {
            var terms = new List<string>();
            for (var prefix = 0; prefix < boundary; prefix++)
                terms.Add(RenderCursorEquality(order[prefix].Column, cursor[prefix], parameters, ref parameterIndex));
            terms.Add(RenderAfter(order[boundary], cursor[boundary], parameters, ref parameterIndex));
            alternatives.Add("(" + string.Join(" AND ", terms) + ")");
        }
        return alternatives.Count == 1 ? alternatives[0] : "(" + string.Join(" OR ", alternatives) + ")";
    }

    protected virtual string RenderCursorEquality(ColumnRef column, QueryConstant value, ICollection<QueryRenderParameter> parameters, ref int parameterIndex)
    {
        var expression = RenderColumn(column);
        if (value.Kind == QueryConstantKind.Null)
            return expression + " IS NULL";
        var name = AddParameter(column, value, parameters, ref parameterIndex);
        return "(" + expression + " IS NOT NULL AND " + expression + " = @" + name + ")";
    }

    protected virtual string RenderAfter(OrderTerm term, QueryConstant value, ICollection<QueryRenderParameter> parameters, ref int parameterIndex)
    {
        var expression = RenderColumn(term.Column);
        var nullsFirst = term.NullOrder == NullOrder.First;
        if (value.Kind == QueryConstantKind.Null)
            return nullsFirst ? expression + " IS NOT NULL" : "1 = 0";

        var name = AddParameter(term.Column, value, parameters, ref parameterIndex);
        var comparison = term.Direction == OrderDirection.Ascending ? ">" : "<";
        var strict = "(" + expression + " IS NOT NULL AND " + expression + " " + comparison + " @" + name + ")";
        return nullsFirst ? strict : "(" + strict + " OR " + expression + " IS NULL)";
    }

    protected virtual string RenderOrderTerm(OrderTerm term)
    {
        var expression = RenderColumn(term.Column);
        var direction = term.Direction == OrderDirection.Ascending ? "ASC" : "DESC";
        var nullRank = term.NullOrder == NullOrder.First ? "0" : "1";
        var nonNullRank = term.NullOrder == NullOrder.First ? "1" : "0";
        return "CASE WHEN " + expression + " IS NULL THEN " + nullRank + " ELSE " + nonNullRank + " END ASC, " + expression + " " + direction;
    }

    protected virtual string RenderContains(string expression, string parameter) =>
        throw new QueryRenderException("GW-QUERY-030", "This provider has no ordinal substring operation.");

    protected virtual string RenderEndsWith(string expression, string parameter) =>
        throw new QueryRenderException("GW-QUERY-030", "This provider has no ordinal suffix operation.");

    protected virtual string RenderElementOf(
        Predicate.ElementOf elementOf,
        ICollection<QueryRenderParameter> parameters,
        ref int parameterIndex)
    {
        if (elementOf.Set.Type is not QueryType)
            throw new QueryRenderException("GW-SEM-TYPE-007", "An element set must declare its exact element type before rendering.");
        if (elementOf.Values.Length == 0)
            return elementOf.Quantifier == SetQuantifier.Any ? "1 = 0" : "1 = 1";
        throw new QueryRenderException("GW-QUERY-030", $"ElementOf on '{elementOf.Set.Name}' requires a provider array representation; values were typed but no native array operation is available.");
    }

    protected string AddParameter(ColumnRef column, QueryConstant value, ICollection<QueryRenderParameter> parameters, ref int parameterIndex)
    {
        var name = "p" + parameterIndex++;
        parameters.Add(new QueryRenderParameter(name, column.Type, AdaptParameter(column.Type, value.Value)));
        return name;
    }

    protected string AddElementParameter(QueryType type, object? value, ICollection<QueryRenderParameter> parameters, ref int parameterIndex)
    {
        var name = "p" + parameterIndex++;
        parameters.Add(new QueryRenderParameter(name, type, AdaptParameter(type, value)));
        return name;
    }

    private static bool CanMatchNull(Predicate predicate, string column)
    {
        switch (predicate)
        {
            case Predicate.AlwaysFalse:
                return false;
            case Predicate.AlwaysTrue:
                return true;
            case Predicate.Equal equal when equal.Column.Name == column:
                return equal.Value.Kind == QueryConstantKind.Null;
            case Predicate.In membership when membership.Column.Name == column:
                return membership.Values.Any(value => value.Kind == QueryConstantKind.Null);
            case Predicate.Range range when range.Column.Name == column:
                return false;
            case Predicate.ColumnCompare compare when compare.Left.Name == column || compare.Right.Name == column:
                return false;
            case Predicate.Not not:
                return !CanMatchNull(not.Inner, column);
            case Predicate.And and:
                return and.Terms.All(term => CanMatchNull(term, column));
            case Predicate.Or or:
                return or.Terms.Any(term => CanMatchNull(term, column));
            default:
                return true;
        }
    }
}
