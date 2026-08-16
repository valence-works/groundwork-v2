using System.Globalization;
using Groundwork.Kernel;
using Groundwork.Query.Model;

namespace Groundwork.Substrate.Relational;

/// <summary>Native command emitted for a closed aggregation profile.</summary>
public sealed class RelationalAggregationCommand
{
    public RelationalAggregationCommand(string commandText, AggregationProfile profile)
    {
        CommandText = commandText ?? throw new ArgumentNullException(nameof(commandText));
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    public string CommandText { get; }
    public AggregationProfile Profile { get; }
}

/// <summary>
/// Shared bounded grouped-reduction renderer. Dialects differ only in identifier quoting and the
/// closed set-union aggregate spelling; all budgets are projected as hidden evidence so callers
/// can refuse rather than truncate after execution.
/// </summary>
public static class RelationalAggregationRenderer
{
    public const string InputCount = "__groundwork_aggregation_input_count";
    public const string GroupCount = "__groundwork_aggregation_group_count";

    public static string SetCountAlias(string alias) => "__groundwork_aggregation_set_count_" + alias;

    public static string FirstRankAlias(string alias) => "__groundwork_aggregation_first_rank_" + alias;

    public static RelationalAggregationCommand Render(
        RelationalDialect dialect,
        StorageUnit unit,
        AggregationProfile profile,
        AggregationQuery? query = null)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(profile);
        AggregationProfileValidator.Validate(unit, profile);
        query ??= AggregationQuery.For(profile.Name);
        AggregationExecutor.ValidateQuery(unit, profile, query);

        var quote = dialect.QuoteIdentifier;
        var groups = profile.GroupByColumns.Select(quote).ToArray();
        var sourceColumns = profile.Aggregates.SelectMany(aggregate => aggregate switch
        {
            Aggregate.Min min => [min.Column],
            Aggregate.Max max => [max.Column],
            Aggregate.Sum sum => [sum.Column],
            Aggregate.SetUnion set => [set.Column],
            Aggregate.FirstBy first => [first.Column, first.OrderColumn],
            _ => Array.Empty<string>()
        }).Concat(unit.Key.Columns).Distinct(StringComparer.Ordinal).Select(quote).ToArray();
        var source = string.Join(", ", groups.Concat(sourceColumns).Distinct(StringComparer.Ordinal));
        var sourcePredicate = query.SourcePredicate is null
            ? null
            : RenderSourcePredicate(dialect, unit, query.SourcePredicate, quote);
        var ctes = RenderBoundedInputCtes(dialect, unit, profile, source, groups, includeFirstRanks: true, sourcePredicate);

        var selections = new List<string>(groups);
        const string groupedAlias = "__groundwork_aggregation_grouped";
        selections.AddRange(profile.Aggregates.Select(aggregate => RenderAggregate(dialect, quote, unit, profile, groupedAlias, aggregate)));
        selections.AddRange(profile.Aggregates.OfType<Aggregate.SetUnion>().Select(set =>
            $"{RenderSetCount(dialect, quote(set.Column))} AS {quote(SetCountAlias(set.Alias))}"));
        selections.Add($"MAX({quote(InputCount)}) AS {quote(InputCount)}");
        selections.Add($"COUNT(*) OVER() AS {quote(GroupCount)}");
        var grouped = $"SELECT {string.Join(", ", selections)} FROM __groundwork_aggregation_input AS {quote(groupedAlias)} GROUP BY {string.Join(", ", groups)}";
        var sql = query.PostPredicate is null
            ? $"WITH {ctes} {grouped}"
            : $"WITH {ctes}, {quote("__groundwork_aggregation_result")} AS ({grouped}) SELECT * FROM {quote("__groundwork_aggregation_result")} WHERE {RenderPredicate(dialect, unit, profile, query.PostPredicate, quote)}";
        if (query.OrderBy is not null)
            sql += " ORDER BY " + RenderOrderTerm(
                dialect,
                quote(query.OrderBy),
                query.OrderDirection);
        else
            sql += " ORDER BY " + string.Join(", ", groups.Select(column => RenderOrderTerm(dialect, column, SortDirection.Ascending)));
        var outputLimit = query.Take is int take ? take : (long)profile.MaxGroups + 1L;
        sql += IsSqlServer(dialect)
            ? $" OFFSET 0 ROWS FETCH NEXT {outputLimit.ToString(CultureInfo.InvariantCulture)} ROWS ONLY"
            : $" LIMIT {outputLimit.ToString(CultureInfo.InvariantCulture)}";
        return new RelationalAggregationCommand(sql + ";", profile);
    }

    /// <summary>
    /// Renders a bounded cardinality probe. The executor runs this before any aggregate that
    /// materializes values, so an over-budget set or group is refused without producing a partial
    /// result or an arbitrarily large set payload.
    /// </summary>
    public static RelationalAggregationCommand RenderBudgetProbe(
        RelationalDialect dialect,
        StorageUnit unit,
        AggregationProfile profile,
        AggregationQuery? query = null)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(profile);
        AggregationProfileValidator.Validate(unit, profile);
        query ??= AggregationQuery.For(profile.Name);
        AggregationExecutor.ValidateQuery(unit, profile, query);
        var quote = dialect.QuoteIdentifier;
        var groups = profile.GroupByColumns.Select(quote).ToArray();
        var setColumns = profile.Aggregates.OfType<Aggregate.SetUnion>()
            .Select(set => quote(set.Column));
        var source = string.Join(", ", groups.Concat(setColumns).Distinct(StringComparer.Ordinal));
        var sourcePredicate = query.SourcePredicate is null
            ? null
            : RenderSourcePredicate(dialect, unit, query.SourcePredicate, quote);
        var ctes = RenderBoundedInputCtes(dialect, unit, profile, source, groups, includeFirstRanks: false, sourcePredicate);
        var selections = new List<string>(groups)
        {
            $"MAX({quote(InputCount)}) AS {quote(InputCount)}"
        };
        selections.AddRange(profile.Aggregates.OfType<Aggregate.SetUnion>().Select(set =>
            $"{RenderSetCount(dialect, quote(set.Column))} AS {quote(SetCountAlias(set.Alias))}"));
        var groupProbe = ((long)profile.MaxGroups + 1L).ToString(CultureInfo.InvariantCulture);
        var select = IsSqlServer(dialect)
            ? $"SELECT TOP ({groupProbe}) {string.Join(", ", selections)} FROM __groundwork_aggregation_input GROUP BY {string.Join(", ", groups)}"
            : $"SELECT {string.Join(", ", selections)} FROM __groundwork_aggregation_input GROUP BY {string.Join(", ", groups)} LIMIT {groupProbe}";
        return new RelationalAggregationCommand($"WITH {ctes} {select};", profile);
    }

    private static string RenderBoundedInputCtes(
        RelationalDialect dialect,
        StorageUnit unit,
        AggregationProfile profile,
        string source,
        IReadOnlyList<string> groups,
        bool includeFirstRanks,
        string? sourcePredicate)
    {
        var quote = dialect.QuoteIdentifier;
        var probeRows = ((long)profile.MaxInputRows + 1L).ToString(CultureInfo.InvariantCulture);
        var boundedSource = IsSqlServer(dialect)
            ? $"SELECT TOP ({probeRows}) {source} FROM {quote(unit.Name)}{Where(sourcePredicate)}"
            : $"SELECT {source} FROM {quote(unit.Name)}{Where(sourcePredicate)} LIMIT {probeRows}";
        var windowColumns = new List<string> { "*", $"COUNT(*) OVER() AS {quote(InputCount)}" };
        if (includeFirstRanks)
            windowColumns.AddRange(profile.Aggregates.OfType<Aggregate.FirstBy>().Select(first =>
                $"ROW_NUMBER() OVER (PARTITION BY {string.Join(", ", groups)} ORDER BY {FirstOrder(first, quote, unit)}) AS {quote(FirstRankAlias(first.Alias))}"));
        return $"__groundwork_aggregation_source AS ({boundedSource}), " +
            $"__groundwork_aggregation_input AS (SELECT {string.Join(", ", windowColumns)} FROM __groundwork_aggregation_source)";

        static string Where(string? predicate) => predicate is null ? string.Empty : " WHERE " + predicate;
    }

    private static string FirstOrder(Aggregate.FirstBy first, Func<string, string> quote, StorageUnit unit)
    {
        var direction = first.Direction == SortDirection.Descending ? "DESC" : "ASC";
        var tieBreaks = unit.Key.Columns
            .Where(column => !string.Equals(column, first.OrderColumn, StringComparison.Ordinal))
            .Select(column => quote(column) + " ASC")
            .ToArray();
        return quote(first.OrderColumn) + " " + direction +
            (tieBreaks.Length == 0 ? string.Empty : ", " + string.Join(", ", tieBreaks));
    }

    private static string RenderOrderTerm(RelationalDialect dialect, string expression, SortDirection direction)
    {
        var descending = direction == SortDirection.Descending;
        var order = descending ? "DESC" : "ASC";
        if (IsSqlServer(dialect))
        {
            var nullRank = descending ? 1 : 0;
            var nonNullRank = descending ? 0 : 1;
            return $"CASE WHEN {expression} IS NULL THEN {nullRank} ELSE {nonNullRank} END, {expression} {order}";
        }
        return $"{expression} {order} NULLS {(descending ? "LAST" : "FIRST")}";
    }

    private static string RenderAggregate(
        RelationalDialect dialect,
        Func<string, string> quote,
        StorageUnit unit,
        AggregationProfile profile,
        string groupedAlias,
        Aggregate aggregate)
    {
        var expression = aggregate switch
        {
            Aggregate.Min min => $"MIN({quote(min.Column)})",
            Aggregate.Max max => $"MAX({quote(max.Column)})",
            Aggregate.Sum sum => RenderSum(dialect, quote, unit, sum),
            Aggregate.SetUnion set => RenderSetUnion(dialect, quote, set),
            Aggregate.FirstBy first => RenderFirstBy(dialect, quote, profile, groupedAlias, first),
            _ => throw new ArgumentOutOfRangeException(nameof(aggregate))
        };
        return expression + " AS " + quote(aggregate.Alias);
    }

    private static string RenderSetUnion(RelationalDialect dialect, Func<string, string> quote, Aggregate.SetUnion set)
    {
        var column = quote(set.Column);
        if (IsPostgreSql(dialect))
            return $"ARRAY_AGG(DISTINCT {column} COLLATE \"C\" ORDER BY {column} COLLATE \"C\") FILTER (WHERE {column} IS NOT NULL)";
        if (IsSqlServer(dialect))
            return $"CASE WHEN COUNT({column}) = 0 THEN N'[]' ELSE CONCAT(N'[\"', STRING_AGG(STRING_ESCAPE(CAST({column} AS nvarchar(max)), 'json'), N'\",\"'), N'\"]') END";
        return $"COALESCE(json_group_array(DISTINCT {column} COLLATE BINARY) FILTER (WHERE {column} IS NOT NULL), '[]')";
    }

    private static string RenderSetCount(RelationalDialect dialect, string column) => IsPostgreSql(dialect)
        ? $"COUNT(DISTINCT {column} COLLATE \"C\")"
        : IsSqlServer(dialect)
            ? $"COUNT(DISTINCT {column} COLLATE Latin1_General_100_BIN2)"
            : $"COUNT(DISTINCT {column} COLLATE BINARY)";

    private static string RenderSum(
        RelationalDialect dialect,
        Func<string, string> quote,
        StorageUnit unit,
        Aggregate.Sum sum)
    {
        var column = quote(sum.Column);
        var type = unit.Columns.Single(item => item.Name == sum.Column).Type;
        var expression = IsSqlServer(dialect) && type == PortableType.Int32
            ? $"SUM(CAST({column} AS bigint))"
            : IsSqlite(dialect) && type == PortableType.Decimal
                ? $"groundwork_decimal_sum({column})"
                : $"SUM({column})";
        return $"CASE WHEN COUNT({column}) = 0 THEN NULL ELSE {expression} END";
    }

    private static bool IsSqlServer(RelationalDialect dialect) =>
        dialect.ProviderName.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Contains("SQLServer", StringComparison.OrdinalIgnoreCase);

    private static bool IsPostgreSql(RelationalDialect dialect) =>
        dialect.ProviderName.Contains("PostgreSQL", StringComparison.OrdinalIgnoreCase);

    private static bool IsSqlite(RelationalDialect dialect) =>
        dialect.ProviderName.Contains("SQLite", StringComparison.OrdinalIgnoreCase);

    private static string RenderFirstBy(
        RelationalDialect dialect,
        Func<string, string> quote,
        AggregationProfile profile,
        string groupedAlias,
        Aggregate.FirstBy first)
    {
        var correlations = profile.GroupByColumns.Select(column =>
            $"(first_input.{quote(column)} = {quote(groupedAlias)}.{quote(column)} OR (first_input.{quote(column)} IS NULL AND {quote(groupedAlias)}.{quote(column)} IS NULL))");
        var where = string.Join(" AND ", correlations.Append(
            $"first_input.{quote(FirstRankAlias(first.Alias))} = 1"));
        var value = $"first_input.{quote(first.Column)}";
        return IsSqlServer(dialect)
            ? $"(SELECT TOP (1) {value} FROM __groundwork_aggregation_input AS first_input WHERE {where})"
            : $"(SELECT {value} FROM __groundwork_aggregation_input AS first_input WHERE {where} LIMIT 1)";
    }

    private static string RenderPredicate(
        RelationalDialect dialect,
        StorageUnit unit,
        AggregationProfile profile,
        AggregationPredicate predicate,
        Func<string, string> quote) => predicate switch
    {
        AggregationPredicate.All all => "(" + string.Join(" AND ", all.Predicates.Select(child => RenderPredicate(dialect, unit, profile, child, quote))) + ")",
        AggregationPredicate.Any any => "(" + string.Join(" OR ", any.Predicates.Select(child => RenderPredicate(dialect, unit, profile, child, quote))) + ")",
        AggregationPredicate.Comparison comparison when comparison.Operator == AggregationPredicateOperator.Equal =>
            RenderEqual(dialect, unit, profile, comparison, quote),
        AggregationPredicate.Comparison comparison when comparison.Operator == AggregationPredicateOperator.In =>
            RenderIn(dialect, unit, profile, comparison, quote),
        AggregationPredicate.Comparison comparison when comparison.Operator == AggregationPredicateOperator.RangeInclusive =>
            quote(comparison.Alias) + " BETWEEN " + Literal(dialect, unit, profile, comparison.Alias, comparison.Values[0]) + " AND " + Literal(dialect, unit, profile, comparison.Alias, comparison.Values[1]),
        AggregationPredicate.Comparison comparison when comparison.Operator == AggregationPredicateOperator.Contains =>
            dialect.RenderAggregationContains(quote(comparison.Alias), Literal(dialect, unit, profile, comparison.Alias, comparison.Values.Single())),
        _ => throw new AggregationValidationException([new("GW-AGG-PRED-009", "The post-reduction predicate is not renderable.", "postPredicate")])
    };

    private static string RenderSourcePredicate(
        RelationalDialect dialect,
        StorageUnit unit,
        Predicate predicate,
        Func<string, string> quote) => predicate switch
    {
        Predicate.AlwaysTrue => "1 = 1",
        Predicate.AlwaysFalse => "1 = 0",
        Predicate.Equal equal => RenderSourceEqual(dialect, unit, equal, quote),
        Predicate.In membership => RenderSourceIn(dialect, unit, membership, quote),
        Predicate.Range range => RenderSourceRange(dialect, unit, range, quote),
        Predicate.Substring substring when substring.Anchor == Anchor.Contains =>
            RenderSourceSubstring(dialect, unit, substring, quote, contains: true),
        Predicate.Substring substring when substring.Anchor == Anchor.EndsWith =>
            RenderSourceSubstring(dialect, unit, substring, quote, contains: false),
        Predicate.ColumnCompare compare => RenderSourceColumnCompare(compare, quote),
        Predicate.Not not => "(CASE WHEN (" + RenderSourcePredicate(dialect, unit, not.Inner, quote) + ") THEN 0 ELSE 1 END = 1)",
        Predicate.And and => and.Terms.Length == 0
            ? "1 = 1"
            : "(" + string.Join(" AND ", and.Terms.Select(term => RenderSourcePredicate(dialect, unit, term, quote))) + ")",
        Predicate.Or or => or.Terms.Length == 0
            ? "1 = 0"
            : "(" + string.Join(" OR ", or.Terms.Select(term => RenderSourcePredicate(dialect, unit, term, quote))) + ")",
        _ => throw new AggregationValidationException([new("GW-AGG-SOURCE-007", "The source predicate is not renderable by the relational aggregation surface.", "sourcePredicate")])
    };

    private static string RenderSourceEqual(
        RelationalDialect dialect,
        StorageUnit unit,
        Predicate.Equal equal,
        Func<string, string> quote)
    {
        var expression = quote(equal.Column.Name);
        return equal.Value.Kind == QueryConstantKind.Null
            ? expression + " IS NULL"
            : "(" + expression + " IS NOT NULL AND " + expression + " = " + SourceLiteral(dialect, unit, equal.Column, equal.Value.Value) + ")";
    }

    private static string RenderSourceIn(
        RelationalDialect dialect,
        StorageUnit unit,
        Predicate.In membership,
        Func<string, string> quote)
    {
        if (membership.Values.Length == 0)
            return "1 = 0";
        var expression = quote(membership.Column.Name);
        var nonNull = membership.Values.Where(value => value.Kind != QueryConstantKind.Null).ToArray();
        var terms = new List<string>();
        if (nonNull.Length != 0)
            terms.Add("(" + expression + " IS NOT NULL AND " + expression + " IN (" +
                string.Join(", ", nonNull.Select(value => SourceLiteral(dialect, unit, membership.Column, value.Value))) + "))");
        if (membership.Values.Any(value => value.Kind == QueryConstantKind.Null))
            terms.Add(expression + " IS NULL");
        return terms.Count == 1 ? terms[0] : "(" + string.Join(" OR ", terms) + ")";
    }

    private static string RenderSourceRange(
        RelationalDialect dialect,
        StorageUnit unit,
        Predicate.Range range,
        Func<string, string> quote)
    {
        var expression = quote(range.Column.Name);
        var terms = new List<string> { expression + " IS NOT NULL" };
        if (range.Lower is { } lower)
            terms.Add(expression + (lower.IsInclusive ? " >= " : " > ") + SourceLiteral(dialect, unit, range.Column, lower.Value.Value));
        if (range.Upper is { } upper)
            terms.Add(expression + (upper.IsInclusive ? " <= " : " < ") + SourceLiteral(dialect, unit, range.Column, upper.Value.Value));
        return "(" + string.Join(" AND ", terms) + ")";
    }

    private static string RenderSourceColumnCompare(Predicate.ColumnCompare compare, Func<string, string> quote)
    {
        var left = quote(compare.Left.Name);
        var right = quote(compare.Right.Name);
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
        return "(" + left + " IS NOT NULL AND " + right + " IS NOT NULL AND " + left + " " + op + " " + right + ")";
    }

    private static string RenderSourceSubstring(
        RelationalDialect dialect,
        StorageUnit unit,
        Predicate.Substring substring,
        Func<string, string> quote,
        bool contains)
    {
        var expression = quote(substring.Column.Name);
        var literal = SourceLiteral(dialect, unit, substring.Column, substring.Needle);
        var operation = contains
            ? dialect.RenderAggregationSourceContains(expression, literal)
            : dialect.RenderAggregationSourceEndsWith(expression, literal);
        return "(" + expression + " IS NOT NULL AND " + operation + ")";
    }

    private static string SourceLiteral(RelationalDialect dialect, StorageUnit unit, ColumnRef column, object? value) =>
        dialect.RenderAggregationLiteral(value, unit.Columns.Single(item => item.Name == column.Name).Type);

    private static string RenderEqual(
        RelationalDialect dialect,
        StorageUnit unit,
        AggregationProfile profile,
        AggregationPredicate.Comparison comparison,
        Func<string, string> quote)
    {
        var expression = quote(comparison.Alias);
        return comparison.Values.Single() is null
            ? expression + " IS NULL"
            : expression + " = " + Literal(dialect, unit, profile, comparison.Alias, comparison.Values.Single());
    }

    private static string RenderIn(
        RelationalDialect dialect,
        StorageUnit unit,
        AggregationProfile profile,
        AggregationPredicate.Comparison comparison,
        Func<string, string> quote)
    {
        var expression = quote(comparison.Alias);
        var values = comparison.Values.Where(value => value is not null).ToArray();
        var hasNull = comparison.Values.Any(value => value is null);
        var nonNull = values.Length == 0
            ? string.Empty
            : expression + " IN (" + string.Join(", ", values.Select(value => Literal(dialect, unit, profile, comparison.Alias, value))) + ")";
        return hasNull
            ? values.Length == 0 ? expression + " IS NULL" : "(" + nonNull + " OR " + expression + " IS NULL)"
            : nonNull;
    }

    private static string Literal(
        RelationalDialect dialect,
        StorageUnit unit,
        AggregationProfile profile,
        string alias,
        object? value) => dialect.RenderAggregationLiteral(value, OutputType(unit, profile, alias));

    private static PortableType OutputType(StorageUnit unit, AggregationProfile profile, string alias)
    {
        var aggregate = profile.Aggregates.Single(item => item.Alias == alias);
        var source = aggregate switch
        {
            Aggregate.Min min => min.Column,
            Aggregate.Max max => max.Column,
            Aggregate.Sum sum => sum.Column,
            Aggregate.SetUnion set => set.Column,
            Aggregate.FirstBy first => first.Column,
            _ => throw new InvalidOperationException("Unknown aggregate declaration.")
        };
        var sourceType = unit.Columns.Single(column => column.Name == source).Type;
        return aggregate is Aggregate.Sum && sourceType is (PortableType.Int32 or PortableType.Int64)
            ? PortableType.Int64
            : sourceType;
    }

}
