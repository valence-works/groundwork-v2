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
    private const string InputCount = "__groundwork_aggregation_input_count";
    private const string GroupCount = "__groundwork_aggregation_group_count";

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
        if (!string.Equals(query.ProfileName, profile.Name, StringComparison.Ordinal))
            throw new AggregationValidationException([new("GW-AGG-QUERY-001", "The selected query profile does not match the declaration.", "profileName")]);
        if (query.PostPredicate is not null)
            ValidatePredicateAliases(profile, query.PostPredicate);

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
        var hasFirstBy = profile.Aggregates.Any(aggregate => aggregate is Aggregate.FirstBy);
        var source = string.Join(", ", groups.Concat(sourceColumns).Distinct(StringComparer.Ordinal));
        var input = $"SELECT {source}, COUNT(*) OVER() AS {quote(InputCount)} FROM {quote(unit.Name)}";
        var ctes = hasFirstBy
            ? $"__groundwork_aggregation_source AS ({input}), __groundwork_aggregation_input AS (SELECT *, ROW_NUMBER() OVER (PARTITION BY {string.Join(", ", groups)} ORDER BY {FirstOrder(profile, quote, unit)}) AS {quote("__groundwork_aggregation_first_rank")} FROM __groundwork_aggregation_source)"
            : $"__groundwork_aggregation_input AS ({input})";

        var selections = new List<string>(groups);
        selections.AddRange(profile.Aggregates.Select(aggregate => RenderAggregate(dialect, quote, aggregate, hasFirstBy)));
        selections.Add($"MAX({quote(InputCount)}) AS {quote(InputCount)}");
        selections.Add($"COUNT(*) OVER() AS {quote(GroupCount)}");
        var grouped = $"SELECT {string.Join(", ", selections)} FROM __groundwork_aggregation_input GROUP BY {string.Join(", ", groups)}";
        var sql = $"WITH {ctes} {grouped}";
        if (query.PostPredicate is not null)
            sql = $"SELECT * FROM ({sql}) AS {quote("__groundwork_aggregation_result")} WHERE {RenderPredicate(query.PostPredicate, quote)}";
        if (query.OrderBy is not null)
            sql += $" ORDER BY {quote(query.OrderBy)} {(query.OrderDirection == SortDirection.Descending ? "DESC" : "ASC")}";
        if (query.Take is int take)
            sql += dialect.ProviderName.Contains("SQL Server", StringComparison.OrdinalIgnoreCase)
                ? $" OFFSET 0 ROWS FETCH NEXT {take.ToString(CultureInfo.InvariantCulture)} ROWS ONLY"
                : $" LIMIT {take.ToString(CultureInfo.InvariantCulture)}";
        return new RelationalAggregationCommand(sql + ";", profile);
    }

    private static string FirstOrder(AggregationProfile profile, Func<string, string> quote, StorageUnit unit)
    {
        var first = profile.Aggregates.OfType<Aggregate.FirstBy>().First();
        var direction = first.Direction == SortDirection.Descending ? "DESC" : "ASC";
        return quote(first.OrderColumn) + " " + direction + ", " + string.Join(", ", unit.Key.Columns.Select(column => quote(column) + " ASC"));
    }

    private static string RenderAggregate(RelationalDialect dialect, Func<string, string> quote, Aggregate aggregate, bool hasFirstBy)
    {
        var expression = aggregate switch
        {
            Aggregate.Min min => $"MIN({quote(min.Column)})",
            Aggregate.Max max => $"MAX({quote(max.Column)})",
            Aggregate.Sum sum => $"SUM({quote(sum.Column)})",
            Aggregate.SetUnion set => RenderSetUnion(dialect, quote, set),
            Aggregate.FirstBy first => $"MAX(CASE WHEN {quote("__groundwork_aggregation_first_rank")} = 1 THEN {quote(first.Column)} END)",
            _ => throw new ArgumentOutOfRangeException(nameof(aggregate))
        };
        return expression + " AS " + quote(aggregate.Alias);
    }

    private static string RenderSetUnion(RelationalDialect dialect, Func<string, string> quote, Aggregate.SetUnion set)
    {
        var column = quote(set.Column);
        var delimiter = dialect.ProviderName.Contains("PostgreSQL", StringComparison.OrdinalIgnoreCase)
            ? "E'\\x1F'"
            : dialect.ProviderName.Contains("SQL Server", StringComparison.OrdinalIgnoreCase) ? "NCHAR(31)" : "char(31)";
        return dialect.ProviderName.Contains("PostgreSQL", StringComparison.OrdinalIgnoreCase) ||
               dialect.ProviderName.Contains("SQL Server", StringComparison.OrdinalIgnoreCase)
            ? $"STRING_AGG(DISTINCT {column}, {delimiter})"
            : $"GROUP_CONCAT({column}, {delimiter})";
    }

    private static string RenderPredicate(AggregationPredicate predicate, Func<string, string> quote) => predicate switch
    {
        AggregationPredicate.All all => "(" + string.Join(" AND ", all.Predicates.Select(child => RenderPredicate(child, quote))) + ")",
        AggregationPredicate.Any any => "(" + string.Join(" OR ", any.Predicates.Select(child => RenderPredicate(child, quote))) + ")",
        AggregationPredicate.Comparison comparison when comparison.Operator == AggregationPredicateOperator.Equal =>
            quote(comparison.Alias) + " = " + Literal(comparison.Values.Single()),
        AggregationPredicate.Comparison comparison when comparison.Operator == AggregationPredicateOperator.In =>
            quote(comparison.Alias) + " IN (" + string.Join(", ", comparison.Values.Select(Literal)) + ")",
        AggregationPredicate.Comparison comparison when comparison.Operator == AggregationPredicateOperator.RangeInclusive =>
            quote(comparison.Alias) + " BETWEEN " + Literal(comparison.Values[0]) + " AND " + Literal(comparison.Values[1]),
        AggregationPredicate.Comparison comparison when comparison.Operator == AggregationPredicateOperator.Contains =>
            "INSTR(" + quote(comparison.Alias) + ", " + Literal(comparison.Values.Single()) + ") > 0",
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

    private static void ValidatePredicateAliases(AggregationProfile profile, AggregationPredicate predicate)
    {
        var aliases = profile.AllowedPredicates.ToDictionary(item => item.Alias, StringComparer.Ordinal);
        switch (predicate)
        {
            case AggregationPredicate.All all:
                foreach (var child in all.Predicates) ValidatePredicateAliases(profile, child);
                break;
            case AggregationPredicate.Any any:
                foreach (var child in any.Predicates) ValidatePredicateAliases(profile, child);
                break;
            case AggregationPredicate.Comparison comparison:
                if (!aliases.TryGetValue(comparison.Alias, out var allowance) || !allowance.SupportedPredicates.Contains(comparison.Operator))
                    throw new AggregationValidationException([new("GW-AGG-PRED-007", $"Predicate '{comparison.Operator}' is not declared for output '{comparison.Alias}'.", "postPredicate")]);
                break;
        }
    }
}
