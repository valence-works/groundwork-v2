using System.Globalization;
using System.Collections.Immutable;
using Groundwork.Kernel;
using Groundwork.Query.Model;

namespace Groundwork.Substrate.Relational;

/// <summary>Native command emitted for a closed aggregation profile.</summary>
public sealed class RelationalAggregationCommand
{
    public RelationalAggregationCommand(string commandText, AggregationProfile profile)
        : this(commandText, profile, [])
    {
    }

    public RelationalAggregationCommand(
        string commandText,
        AggregationProfile profile,
        IEnumerable<QueryRenderParameter> parameters)
    {
        CommandText = commandText ?? throw new ArgumentNullException(nameof(commandText));
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        Parameters = (parameters ?? throw new ArgumentNullException(nameof(parameters))).ToImmutableArray();
        if (Parameters.Any(parameter => parameter is null))
            throw new ArgumentException("Aggregation parameters cannot contain null references.", nameof(parameters));
    }

    public string CommandText { get; }
    public AggregationProfile Profile { get; }
    public ImmutableArray<QueryRenderParameter> Parameters { get; }
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
        AggregationQuery? query = null) =>
        RenderCore(dialect, unit, profile, query, providerPredicate: null);

    internal static RelationalAggregationCommand RenderWithProviderPredicate(
        RelationalDialect dialect,
        StorageUnit unit,
        AggregationProfile profile,
        AggregationQuery query,
        Predicate providerPredicate) =>
        RenderCore(dialect, unit, profile, query, providerPredicate);

    private static RelationalAggregationCommand RenderCore(
        RelationalDialect dialect,
        StorageUnit unit,
        AggregationProfile profile,
        AggregationQuery? query,
        Predicate? providerPredicate)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(profile);
        AggregationProfileValidator.Validate(unit, profile);
        query ??= AggregationQuery.For(profile.Name);
        AggregationExecutor.ValidateQuery(unit, profile, query);

        var quote = dialect.QuoteIdentifier;
        var groupDescriptors = AggregationGrouping.EffectiveGroups(profile);
        var groups = groupDescriptors.Select(group => RenderGroupExpression(dialect, unit, profile, query, group)).ToArray();
        var sourceColumns = profile.Aggregates.SelectMany(aggregate => aggregate switch
        {
            Aggregate.Min min => [min.Column],
            Aggregate.Max max => [max.Column],
            Aggregate.Count => Array.Empty<string>(),
            Aggregate.Sum sum => [sum.Column],
            Aggregate.SetUnion set => [set.Column],
            Aggregate.FirstBy first => [first.Column, first.OrderColumn],
            _ => Array.Empty<string>()
        }).Concat(unit.Key.Columns).Distinct(StringComparer.Ordinal).Select(quote).ToArray();
        var source = string.Join(", ", groupDescriptors.Select(AggregationGrouping.SourceColumn).Select(quote).Concat(sourceColumns).Distinct(StringComparer.Ordinal));
        var sourceFragment = RenderSourceFragment(dialect, unit, AggregationGrouping.EffectiveSourcePredicate(unit, profile, query), providerPredicate);
        var ctes = RenderBoundedInputCtes(dialect, unit, profile, source, groups, includeFirstRanks: true, sourceFragment?.CommandText);

        var selections = new List<string>(groups.Select((expression, index) => expression + " AS " + quote(groupDescriptors[index].Alias)));
        const string groupedAlias = "__groundwork_aggregation_grouped";
        selections.AddRange(profile.Aggregates.Select(aggregate => RenderAggregate(dialect, quote, unit, profile, query, groupedAlias, aggregate)));
        selections.AddRange(profile.Aggregates.OfType<Aggregate.SetUnion>().Select(set =>
            $"{RenderSetCount(dialect, quote(set.Column))} AS {quote(SetCountAlias(set.Alias))}"));
        selections.Add($"MAX({quote(InputCount)}) AS {quote(InputCount)}");
        selections.Add($"COUNT(*) OVER() AS {quote(GroupCount)}");
        var grouped = $"SELECT {string.Join(", ", selections)} FROM __groundwork_aggregation_input AS {quote(groupedAlias)} GROUP BY {string.Join(", ", groups)}";
        var resultAlias = quote("__groundwork_aggregation_result");
        var hasTimeBucket = AggregationGrouping.TimeBucket(profile) is not null;
        var boundedResult = IsSqlServer(dialect)
            ? $"SELECT TOP ({((long)profile.MaxGroups + 1L).ToString(CultureInfo.InvariantCulture)}) * FROM {resultAlias}"
            : $"SELECT * FROM {resultAlias} LIMIT {((long)profile.MaxGroups + 1L).ToString(CultureInfo.InvariantCulture)}";
        var sql = hasTimeBucket || query.PostPredicate is null
            ? hasTimeBucket
                ? $"WITH {ctes}, {resultAlias} AS ({grouped}) {boundedResult}"
                : $"WITH {ctes}, {resultAlias} AS ({grouped}) SELECT * FROM {resultAlias}"
            : $"WITH {ctes}, {resultAlias} AS ({grouped}) SELECT * FROM {resultAlias} WHERE {RenderPredicate(dialect, unit, profile, query.PostPredicate, quote)}";
        if (!hasTimeBucket)
        {
            var orderTerms = AggregationQueryFingerprint.EffectiveOrderTerms(query, profile);
            sql += " ORDER BY " + string.Join(", ", orderTerms.Select(term => dialect.RenderAggregationOrder(
                quote(term.Alias),
                OutputTypeForAlias(unit, profile, term.Alias),
                term.Direction)));
            var outputLimit = query.Take is int take ? take : (long)profile.MaxGroups + 1L;
            sql += IsSqlServer(dialect)
                ? $" OFFSET 0 ROWS FETCH NEXT {outputLimit.ToString(CultureInfo.InvariantCulture)} ROWS ONLY"
                : $" LIMIT {outputLimit.ToString(CultureInfo.InvariantCulture)}";
        }
        return new RelationalAggregationCommand(sql + ";", profile, sourceFragment?.Parameters ?? []);
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
        AggregationQuery? query = null) =>
        RenderBudgetProbeCore(dialect, unit, profile, query, providerPredicate: null);

    internal static RelationalAggregationCommand RenderBudgetProbeWithProviderPredicate(
        RelationalDialect dialect,
        StorageUnit unit,
        AggregationProfile profile,
        AggregationQuery query,
        Predicate providerPredicate) =>
        RenderBudgetProbeCore(dialect, unit, profile, query, providerPredicate);

    private static RelationalAggregationCommand RenderBudgetProbeCore(
        RelationalDialect dialect,
        StorageUnit unit,
        AggregationProfile profile,
        AggregationQuery? query,
        Predicate? providerPredicate)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(profile);
        AggregationProfileValidator.Validate(unit, profile);
        query ??= AggregationQuery.For(profile.Name);
        AggregationExecutor.ValidateQuery(unit, profile, query);
        var quote = dialect.QuoteIdentifier;
        var groupDescriptors = AggregationGrouping.EffectiveGroups(profile);
        var groups = groupDescriptors.Select(group => RenderGroupExpression(dialect, unit, profile, query, group)).ToArray();
        var setColumns = profile.Aggregates.OfType<Aggregate.SetUnion>()
            .Select(set => quote(set.Column));
        var source = string.Join(", ", groupDescriptors.Select(AggregationGrouping.SourceColumn).Select(quote).Concat(setColumns).Distinct(StringComparer.Ordinal));
        var sourceFragment = RenderSourceFragment(dialect, unit, AggregationGrouping.EffectiveSourcePredicate(unit, profile, query), providerPredicate);
        var ctes = RenderBoundedInputCtes(dialect, unit, profile, source, groups, includeFirstRanks: false, sourceFragment?.CommandText);
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
        return new RelationalAggregationCommand($"WITH {ctes} {select};", profile, sourceFragment?.Parameters ?? []);
    }

    private static RelationalPredicateFragment? RenderSourceFragment(
        RelationalDialect dialect,
        StorageUnit unit,
        Groundwork.Query.Model.Predicate? predicate,
        Groundwork.Query.Model.Predicate? providerPredicate) => predicate is null && providerPredicate is null
            ? null
            : dialect.CreateQueryRenderer().RenderPredicateFragment(
                predicate is null ? providerPredicate! : providerPredicate is null
                    ? predicate
                    : new Predicate.And([predicate, providerPredicate]),
                unit.Name);

    private static string RenderGroupExpression(
        RelationalDialect dialect,
        StorageUnit unit,
        AggregationProfile profile,
        AggregationQuery query,
        AggregationGroup group,
        string? qualifier = null)
    {
        var quote = dialect.QuoteIdentifier;
        if (group is AggregationGroup.Column column)
            return qualifier is null ? quote(column.Alias) : qualifier + "." + quote(column.Alias);

        var bucket = (AggregationGroup.TimeBucket)group;
        var source = qualifier is null ? quote(bucket.SourceColumn) : qualifier + "." + quote(bucket.SourceColumn);
        var origin = AggregationGrouping.FixedUtcOrigin(profile, query) ?? DateTimeOffset.UnixEpoch;
        var originTicks = origin.UtcTicks.ToString(CultureInfo.InvariantCulture);
        var sqlServerOrigin = $"CONVERT(datetimeoffset(7), '{SqlLiteral(origin.ToString("O", CultureInfo.InvariantCulture))}')";
        var sqlServerSeconds = $"DATEDIFF_BIG(SECOND, {sqlServerOrigin}, {source})";
        var sqlServerCoarseDays = $"CONVERT(int, FLOOR(CONVERT(decimal(38,0), {sqlServerSeconds}) / 86400))";
        var sqlServerCoarseRemainder = $"CONVERT(int, {sqlServerSeconds} - ({sqlServerCoarseDays} * CAST(86400 AS bigint)))";
        var sqlServerCoarseBase = $"DATEADD(SECOND, {sqlServerCoarseRemainder}, DATEADD(DAY, {sqlServerCoarseDays}, {sqlServerOrigin}))";
        // DATEDIFF_BIG(SECOND) supplies a safe coarse duration; the remainder is always below
        // one second, so the nanosecond calculation cannot overflow even for distant instants.
        var sqlServerElapsedTicks = $"({sqlServerSeconds} * 10000000 + DATEDIFF_BIG(NANOSECOND, {sqlServerCoarseBase}, {source}) / 100)";
        var timeZoneId = AggregationGrouping.LocalTimeZoneId(profile, query);
        var sqlServerBucketTicks = $"CONVERT(decimal(38,0), FLOOR(CONVERT(decimal(38,18), {sqlServerElapsedTicks}) / {bucket.Width.Ticks.ToString(CultureInfo.InvariantCulture)}) * {bucket.Width.Ticks.ToString(CultureInfo.InvariantCulture)})";
        var sqlServerBucketSeconds = $"CONVERT(bigint, FLOOR({sqlServerBucketTicks} / 10000000))";
        var sqlServerBucketRemainder = $"CONVERT(bigint, {sqlServerBucketTicks} - ({sqlServerBucketSeconds} * 10000000))";
        var sqlServerBucketDays = $"CONVERT(int, FLOOR(CONVERT(decimal(38,0), {sqlServerBucketSeconds}) / 86400))";
        var sqlServerDayRemainder = $"CONVERT(int, {sqlServerBucketSeconds} - ({sqlServerBucketDays} * CAST(86400 AS bigint)))";
        var postgresInstant = $"to_timestamp(FLOOR((CAST({source} AS numeric) - 621355968000000000) / 10000) / 1000.0)";
        var postgresLocalMidnight = $"date_trunc('day', {postgresInstant} AT TIME ZONE '{SqlLiteral(timeZoneId ?? string.Empty)}')";
        var postgresDefaultMidnight = $"({postgresLocalMidnight} AT TIME ZONE '{SqlLiteral(timeZoneId ?? string.Empty)}')";
        // PostgreSQL selects the post-transition occurrence of an ambiguous wall-clock time.
        // Reconstruct an alternate boundary with the prior local noon's offset, admit it only
        // when it maps back to the target local date, then select the earliest valid instant.
        var postgresPriorNoonCandidate = $"((({postgresLocalMidnight} - INTERVAL '12 hours') AT TIME ZONE '{SqlLiteral(timeZoneId ?? string.Empty)}') + INTERVAL '12 hours')";
        var postgresEarliestMidnight = $"CASE WHEN date_trunc('day', {postgresPriorNoonCandidate} AT TIME ZONE '{SqlLiteral(timeZoneId ?? string.Empty)}') = {postgresLocalMidnight} THEN LEAST({postgresDefaultMidnight}, {postgresPriorNoonCandidate}) ELSE {postgresDefaultMidnight} END";
        return bucket.Kind switch
        {
            AggregationTimeBucketKind.FixedUtc when IsSqlite(dialect) =>
                $"groundwork_time_bucket({source}, {bucket.Width.Ticks.ToString(CultureInfo.InvariantCulture)}, 0, NULL, {originTicks})",
            AggregationTimeBucketKind.LocalCalendarDay when IsSqlite(dialect) =>
                $"groundwork_time_bucket({source}, 0, 1, '{SqlLiteral(timeZoneId!)}', NULL)",
            AggregationTimeBucketKind.FixedUtc when IsPostgreSql(dialect) =>
                $"CAST(FLOOR((CAST({source} AS numeric) - {originTicks}) / {bucket.Width.Ticks.ToString(CultureInfo.InvariantCulture)}) * {bucket.Width.Ticks.ToString(CultureInfo.InvariantCulture)} + {originTicks} AS bigint)",
            AggregationTimeBucketKind.LocalCalendarDay when IsPostgreSql(dialect) =>
                $"CAST(EXTRACT(EPOCH FROM ({postgresEarliestMidnight})) * 10000000 + 621355968000000000 AS bigint)",
            AggregationTimeBucketKind.FixedUtc when IsSqlServer(dialect) =>
                $"DATEADD(NANOSECOND, {sqlServerBucketRemainder} * 100, DATEADD(SECOND, {sqlServerDayRemainder}, DATEADD(DAY, {sqlServerBucketDays}, {sqlServerOrigin})))",
            AggregationTimeBucketKind.LocalCalendarDay when IsSqlServer(dialect) =>
                $"CAST(CONVERT(date, {source} AT TIME ZONE '{SqlServerTimeZone(timeZoneId!)}') AS datetime2) AT TIME ZONE '{SqlServerTimeZone(timeZoneId!)}'",
            _ => throw new AggregationValidationException([new(
                "GW-AGG-GROUP-008",
                $"Time bucket kind '{bucket.Kind}' is not supported by the relational renderer.",
                "groupByExpressions")])
        };

        static string SqlLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

        static string SqlServerTimeZone(string value)
        {
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(value, out var windowsId))
                return windowsId;
            throw new AggregationValidationException([new(
                "GW-AGG-GROUP-009",
                $"IANA time zone '{value}' has no portable SQL Server mapping.",
                "timeZoneId")]);
        }
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
        AggregationQuery query,
        string groupedAlias,
        Aggregate aggregate)
    {
        var expression = aggregate switch
        {
            Aggregate.Min min => $"MIN({quote(min.Column)})",
            Aggregate.Max max => $"MAX({quote(max.Column)})",
            Aggregate.Count => IsSqlServer(dialect) ? "COUNT_BIG(*)" : "COUNT(*)",
            Aggregate.Sum sum => RenderSum(dialect, quote, unit, sum),
            Aggregate.SetUnion set => RenderSetUnion(dialect, quote, set),
            Aggregate.FirstBy first => RenderFirstBy(dialect, quote, unit, profile, query, groupedAlias, first),
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
        StorageUnit unit,
        AggregationProfile profile,
        AggregationQuery query,
        string groupedAlias,
        Aggregate.FirstBy first)
    {
        var correlations = AggregationGrouping.EffectiveGroups(profile).Select(group =>
        {
            var expression = RenderGroupExpression(dialect, unit, profile, query, group, "first_input");
            var output = quote(groupedAlias) + "." + quote(group.Alias);
            return $"({expression} = {output} OR ({expression} IS NULL AND {output} IS NULL))";
        });
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
            Aggregate.Count => null,
            _ => throw new InvalidOperationException("Unknown aggregate declaration.")
        };
        if (aggregate is Aggregate.Count)
            return PortableType.Int64;
        var sourceType = unit.Columns.Single(column => column.Name == source).Type;
        return aggregate is Aggregate.Sum && sourceType is (PortableType.Int32 or PortableType.Int64)
            ? PortableType.Int64
            : sourceType;
    }

    private static PortableType OutputTypeForAlias(StorageUnit unit, AggregationProfile profile, string alias)
    {
        // Aggregate aliases are allowed to reuse a source-column name. Resolve the declared
        // output first; otherwise Count("label") would be ordered as the source String column
        // instead of its native Int64 result.
        if (profile.Aggregates.Any(aggregate =>
                string.Equals(aggregate.Alias, alias, StringComparison.Ordinal)))
            return OutputType(unit, profile, alias);

        var group = AggregationGrouping.EffectiveGroups(profile).Single(group =>
            string.Equals(group.Alias, alias, StringComparison.Ordinal));
        return unit.Columns.Single(column =>
            string.Equals(column.Name, AggregationGrouping.SourceColumn(group), StringComparison.Ordinal)).Type;
    }

}
