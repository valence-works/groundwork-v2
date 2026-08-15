using System.Globalization;
using Groundwork.Kernel;

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
        var ctes = RenderBoundedInputCtes(dialect, unit, profile, source, groups, includeFirstRanks: true);

        var selections = new List<string>(groups);
        selections.AddRange(profile.Aggregates.Select(aggregate => RenderAggregate(dialect, quote, unit, aggregate)));
        selections.AddRange(profile.Aggregates.OfType<Aggregate.SetUnion>().Select(set =>
            $"{RenderSetCount(dialect, quote(set.Column))} AS {quote(SetCountAlias(set.Alias))}"));
        selections.Add($"MAX({quote(InputCount)}) AS {quote(InputCount)}");
        selections.Add($"COUNT(*) OVER() AS {quote(GroupCount)}");
        var grouped = $"SELECT {string.Join(", ", selections)} FROM __groundwork_aggregation_input GROUP BY {string.Join(", ", groups)}";
        var sql = query.PostPredicate is null
            ? $"WITH {ctes} {grouped}"
            : $"WITH {ctes}, {quote("__groundwork_aggregation_result")} AS ({grouped}) SELECT * FROM {quote("__groundwork_aggregation_result")} WHERE {RenderPredicate(dialect, query.PostPredicate, quote)}";
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
        AggregationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(profile);
        AggregationProfileValidator.Validate(unit, profile);
        var quote = dialect.QuoteIdentifier;
        var groups = profile.GroupByColumns.Select(quote).ToArray();
        var setColumns = profile.Aggregates.OfType<Aggregate.SetUnion>()
            .Select(set => quote(set.Column));
        var source = string.Join(", ", groups.Concat(setColumns).Distinct(StringComparer.Ordinal));
        var ctes = RenderBoundedInputCtes(dialect, unit, profile, source, groups, includeFirstRanks: false);
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
        bool includeFirstRanks)
    {
        var quote = dialect.QuoteIdentifier;
        var probeRows = ((long)profile.MaxInputRows + 1L).ToString(CultureInfo.InvariantCulture);
        var boundedSource = IsSqlServer(dialect)
            ? $"SELECT TOP ({probeRows}) {source} FROM {quote(unit.Name)}"
            : $"SELECT {source} FROM {quote(unit.Name)} LIMIT {probeRows}";
        var windowColumns = new List<string> { "*", $"COUNT(*) OVER() AS {quote(InputCount)}" };
        if (includeFirstRanks)
            windowColumns.AddRange(profile.Aggregates.OfType<Aggregate.FirstBy>().Select(first =>
                $"ROW_NUMBER() OVER (PARTITION BY {string.Join(", ", groups)} ORDER BY {FirstOrder(first, quote, unit)}) AS {quote(FirstRankAlias(first.Alias))}"));
        return $"__groundwork_aggregation_source AS ({boundedSource}), " +
            $"__groundwork_aggregation_input AS (SELECT {string.Join(", ", windowColumns)} FROM __groundwork_aggregation_source)";
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
        Aggregate aggregate)
    {
        var expression = aggregate switch
        {
            Aggregate.Min min => $"MIN({quote(min.Column)})",
            Aggregate.Max max => $"MAX({quote(max.Column)})",
            Aggregate.Sum sum => RenderSum(dialect, quote, unit, sum),
            Aggregate.SetUnion set => RenderSetUnion(dialect, quote, set),
            Aggregate.FirstBy first => $"MAX(CASE WHEN {quote(FirstRankAlias(first.Alias))} = 1 THEN {quote(first.Column)} END)",
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

    private static string RenderPredicate(RelationalDialect dialect, AggregationPredicate predicate, Func<string, string> quote) => predicate switch
    {
        AggregationPredicate.All all => "(" + string.Join(" AND ", all.Predicates.Select(child => RenderPredicate(dialect, child, quote))) + ")",
        AggregationPredicate.Any any => "(" + string.Join(" OR ", any.Predicates.Select(child => RenderPredicate(dialect, child, quote))) + ")",
        AggregationPredicate.Comparison comparison when comparison.Operator == AggregationPredicateOperator.Equal =>
            quote(comparison.Alias) + " = " + Literal(comparison.Values.Single()),
        AggregationPredicate.Comparison comparison when comparison.Operator == AggregationPredicateOperator.In =>
            quote(comparison.Alias) + " IN (" + string.Join(", ", comparison.Values.Select(Literal)) + ")",
        AggregationPredicate.Comparison comparison when comparison.Operator == AggregationPredicateOperator.RangeInclusive =>
            quote(comparison.Alias) + " BETWEEN " + Literal(comparison.Values[0]) + " AND " + Literal(comparison.Values[1]),
        AggregationPredicate.Comparison comparison when comparison.Operator == AggregationPredicateOperator.Contains =>
            dialect.RenderAggregationContains(quote(comparison.Alias), Literal(comparison.Values.Single())),
        _ => throw new AggregationValidationException([new("GW-AGG-PRED-009", "The post-reduction predicate is not renderable.", "postPredicate")])
    };

    private static string Literal(object? value) => value switch
    {
        null => "NULL",
        string text => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'",
        bool boolean => boolean ? "1" : "0",
        DateTimeOffset instant => "'" + instant.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) + "'",
        Guid guid => "'" + guid.ToString("D") + "'",
        byte[] bytes => "X'" + Convert.ToHexString(bytes) + "'",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? "NULL",
        _ => throw new AggregationValidationException([new("GW-AGG-PRED-011", "The predicate value is not a portable scalar.", "postPredicate.values")])
    };

}
