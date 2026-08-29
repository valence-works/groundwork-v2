using System.Collections.Immutable;
using System.Data.Common;
using Groundwork.Kernel;
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
        IReadOnlyList<string> appliedOrder) : this(
            commandText,
            parameters,
            includesTotalCount,
            isMatchNone,
            selectedIndex,
            indexHintApplied,
            appliedOrder,
            requiresCompositeMaterializer: false)
    {
    }

    internal RelationalQueryCommand(
        string commandText,
        IEnumerable<QueryRenderParameter> parameters,
        bool includesTotalCount,
        bool isMatchNone,
        string? selectedIndex,
        bool indexHintApplied,
        IReadOnlyList<string> appliedOrder,
        bool requiresCompositeMaterializer)
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
        RequiresCompositeMaterializer = requiresCompositeMaterializer;
    }

    public string CommandText { get; }
    public ImmutableArray<QueryRenderParameter> Parameters { get; }
    public bool IncludesTotalCount { get; }
    public bool IsMatchNone { get; }
    public string? SelectedIndex { get; }
    public bool IndexHintApplied { get; }
    public ImmutableArray<string> AppliedOrder { get; }
    internal bool RequiresCompositeMaterializer { get; }
}

/// <summary>Native SQL and parameter values produced for one set-based mutation.</summary>
/// <remarks>
/// <see cref="Parameters"/> carries only the predicate's own bound values. The assigned values are
/// left to the provider: it binds <see cref="AssignmentParameters"/> with the same encoder and the
/// same native parameter typing its keyed update uses, so a set-based update cannot write a value
/// a keyed update would have written differently.
/// </remarks>
public sealed class RelationalSetMutationCommand
{
    public RelationalSetMutationCommand(
        string commandText,
        IEnumerable<QueryRenderParameter> parameters,
        IEnumerable<string>? assignmentParameters = null)
    {
        CommandText = commandText ?? throw new ArgumentNullException(nameof(commandText));
        Parameters = (parameters ?? throw new ArgumentNullException(nameof(parameters))).ToImmutableArray();
        if (Parameters.Any(parameter => parameter is null))
            throw new ArgumentException("Set-mutation parameters cannot contain null references.", nameof(parameters));
        AssignmentParameters = (assignmentParameters ?? []).ToImmutableArray();
    }

    public string CommandText { get; }

    /// <summary>The predicate's bound values.</summary>
    public ImmutableArray<QueryRenderParameter> Parameters { get; }

    /// <summary>The unbound assignment parameter names, in the order the caller supplied columns.</summary>
    public ImmutableArray<string> AssignmentParameters { get; }
}

/// <summary>One provider-rendered predicate fragment and its bound values.</summary>
internal sealed class RelationalPredicateFragment
{
    public RelationalPredicateFragment(string commandText, IEnumerable<QueryRenderParameter> parameters)
    {
        CommandText = commandText ?? throw new ArgumentNullException(nameof(commandText));
        Parameters = (parameters ?? throw new ArgumentNullException(nameof(parameters))).ToImmutableArray();
        if (Parameters.Any(parameter => parameter is null))
            throw new ArgumentException("Predicate parameters cannot contain null references.", nameof(parameters));
    }

    public string CommandText { get; }
    public ImmutableArray<QueryRenderParameter> Parameters { get; }
}

/// <summary>Executes a rendered relational command while leaving value decoding to the provider.</summary>
public static class RelationalQueryResultReader
{
    internal static ColumnDefinition? ResolveColumnDefinition(
        StorageUnit source,
        QueryRequest request,
        QueryRenderOptions options,
        string fieldName)
    {
        ArgumentNullException.ThrowIfNull(source);
        var queryColumn = QueryRequestExecution.ResolveResultColumn(request, options, fieldName);
        if (queryColumn is null && request.Join is null && request.Projection.AllColumns)
        {
            return source.Columns.FirstOrDefault(column =>
                string.Equals(column.Name, fieldName, StringComparison.Ordinal));
        }
        if (queryColumn is null)
            return null;
        if (request.Join is null || queryColumn.Table == request.Join.SourceTable)
        {
            var declared = source.Columns.FirstOrDefault(column =>
                string.Equals(column.Name, queryColumn.Name, StringComparison.Ordinal));
            if (declared is not null)
                return declared;
        }

        return new ColumnDefinition
        {
            Name = queryColumn.Name,
            Type = queryColumn.Type switch
            {
                QueryType.Boolean => PortableType.Boolean,
                QueryType.Int32 => PortableType.Int32,
                QueryType.Int64 => PortableType.Int64,
                QueryType.Decimal => PortableType.Decimal,
                QueryType.String => PortableType.String,
                QueryType.DateTimeOffset => PortableType.DateTimeOffset,
                QueryType.Guid => PortableType.Guid,
                QueryType.Binary => PortableType.Binary,
                _ => throw new ArgumentOutOfRangeException(nameof(queryColumn), queryColumn.Type, null)
            },
            IsNullable = queryColumn.IsNullable,
            MaxLength = queryColumn.MaxLength,
            Precision = queryColumn.DecimalPrecision,
            Scale = queryColumn.DecimalScale,
            Collation = queryColumn.Type == QueryType.String
                ? queryColumn.StringComparison == QueryStringComparisonPolicy.UnicodeOrdinalIgnoreCase
                    ? PortableCollation.UnicodeOrdinalIgnoreCase
                    : PortableCollation.Ordinal
                : null
        };
    }

    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> Read(
        DbConnection connection,
        RelationalQueryCommand query,
        Func<string, object?, object?> decode) =>
        Read(connection, query, decode, transaction: null);

    /// <summary>Reads a rendered query using the caller-owned transaction, when one exists.</summary>
    internal static IReadOnlyList<IReadOnlyDictionary<string, object?>> Read(
        DbConnection connection,
        RelationalQueryCommand query,
        Func<string, object?, object?> decode,
        DbTransaction? transaction) =>
        Read(connection, query, decode, transaction, RelationalExecution.Synchronous)
            .GetAwaiter().GetResult();

    public static Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadAsync(
        DbConnection connection,
        RelationalQueryCommand query,
        Func<string, object?, object?> decode,
        CancellationToken cancellationToken = default) =>
        Read(connection, query, decode, transaction: null,
            RelationalExecution.Asynchronous(cancellationToken)).AsTask();

    /// <summary>Reads a rendered query on the surface the caller selected, with its transaction when one exists.</summary>
    internal static async ValueTask<IReadOnlyList<IReadOnlyDictionary<string, object?>>> Read(
        DbConnection connection,
        RelationalQueryCommand query,
        Func<string, object?, object?> decode,
        DbTransaction? transaction,
        RelationalExecution mode)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(decode);
        if (query.RequiresCompositeMaterializer)
        {
            throw new QueryRenderException(
                "GW-QUERY-032",
                "Joined row materialization requires an explicit projection so source and target fields remain unambiguous.");
        }
        mode.CancellationToken.ThrowIfCancellationRequested();
        using var command = CreateCommand(connection, query, transaction);
        await using var readerScope = await mode.ExecuteReader(command).ConfigureAwait(false);
        var reader = readerScope.Reader;
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        while (await mode.Read(reader).ConfigureAwait(false))
            rows.Add(MaterializeRow(reader, decode));
        return rows;
    }

    private static DbCommand CreateCommand(DbConnection connection, RelationalQueryCommand query, DbTransaction? transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = query.CommandText;
        AddParameters(command, query);
        return command;
    }

    private static IReadOnlyDictionary<string, object?> MaterializeRow(DbDataReader reader, Func<string, object?, object?> decode)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var index = 0; index < reader.FieldCount; index++)
        {
            var name = reader.GetName(index);
            var value = reader.IsDBNull(index) ? null : reader.GetValue(index);
            row[name] = decode(name, value);
        }
        return row;
    }

    /// <summary>Adds the rendered values to a native command, including explain commands.</summary>
    public static void AddParameters(DbCommand command, RelationalQueryCommand query)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(query);
        AddParameters(command, query.Parameters);
    }

    /// <summary>Adds the bound values from a set-based mutation to a native command.</summary>
    public static void AddParameters(DbCommand command, RelationalSetMutationCommand mutation)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(mutation);
        AddParameters(command, mutation.Parameters);
    }

    /// <summary>Adds the bound values from an aggregation command to a native command.</summary>
    public static void AddParameters(DbCommand command, RelationalAggregationCommand query)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(query);
        AddParameters(command, query.Parameters);
    }

    private static void AddParameters(DbCommand command, IEnumerable<QueryRenderParameter> values)
    {
        foreach (var value in values)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@" + value.Name;
            parameter.Value = value.Value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
    }
}

/// <summary>
/// Shared SQL renderer for the public relational dialect seam. Provider assemblies supply the
/// dialect, budget, paging syntax, and (where supported) index-hint syntax.
/// </summary>
public abstract class RelationalQueryRenderer
{
    private const string SourceAlias = "__groundwork_source";
    private const string TargetAlias = "__groundwork_target";
    private readonly RelationalDialect dialect;
    private readonly int parameterBudget;
    private readonly bool supportsIndexHints;
    private readonly System.Threading.AsyncLocal<JoinedColumnScope?> joinedColumnScope = new();

    private sealed class JoinedColumnScope
    {
        public JoinedColumnScope(QueryRequest request)
        {
            Join = request.Join!;
            UsesDerivedSource = request.LatestPerKey is not null || request.Distinct ||
                request.Result.IncludesTotalCount || request.Result is ResultShape.Reduction;
        }

        public ReferenceJoin Join { get; }
        public bool UsesDerivedSource { get; }
        public bool IsDerived { get; set; }

        public string Render(ColumnRef column, RelationalDialect dialect)
        {
            if (column.Table != Join.SourceTable && column.Table != Join.TargetTable)
            {
                throw new QueryRenderException(
                    "GW-QUERY-032",
                    $"Joined column '{column}' is outside the declared source and target tables.");
            }
            if (IsDerived)
                return dialect.QuoteIdentifier(Field(column));
            var alias = column.Table == Join.SourceTable
                ? SourceAlias
                : TargetAlias;
            return dialect.QuoteIdentifier(alias) + "." + dialect.QuoteIdentifier(column.Name);
        }

        public string Field(ColumnRef column)
        {
            return QueryRequestExecution.ResultFieldName(Join, column);
        }
    }

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
        if (request.Join is not null)
            return RenderJoined(request, options);
        options ??= QueryRenderOptions.Default;
        request = QuerySearchKeyRewriter.Rewrite(request, options.SearchKeyColumns);
        if (options.InValueLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "The In value limit must be positive.");

        var validation = PortableQuerySemantics.Validate(request);
        if (!validation.IsPortable)
        {
            var refusal = validation.Refusals[0];
            throw new QueryRenderException(refusal.Code, refusal.Message + " (" + refusal.Path + ").");
        }
        var effectiveOrder = EffectiveOrder(request, options);
        var parameters = new List<QueryRenderParameter>();
        var parameterIndex = 0;
        var matchNone = request.Where is Predicate.AlwaysFalse;
        var where = RenderPredicate(request.Where, parameters, ref parameterIndex, options.InValueLimit, request.Table.Value);
        IReadOnlyList<QueryConstant>? cursor = null;
        if (request.Paging.ContinuationToken is not null)
        {
            if (effectiveOrder.Count == 0)
                throw new QueryRenderException("GW-QUERY-013", "Keyset continuation requires an explicit ordered query.");
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
            if (!request.Result.IncludesTotalCount && request.LatestPerKey is null && !request.Distinct)
                where = $"({where}) AND ({RenderContinuation(effectiveOrder, cursor, parameters, ref parameterIndex)})";
        }

        var pinnedIndex = options.FindPinnedIndex();
        var expectedIndex = options.FindSelectedIndex();
        var indexHintApplied = pinnedIndex is not null && supportsIndexHints;
        if (pinnedIndex is not null && !pinnedIndex.IncludesNulls)
        {
            if (matchNone)
            {
                // A contradiction matches no row, but SQL Server still requires a query using a
                // filtered index to restate that index's filter. Keep the logical contradiction and
                // make the null exclusion visible to the optimizer.
                where = $"({where}) AND ({string.Join(" AND ", pinnedIndex.Columns.Select(column => dialect.QuoteIdentifier(column) + " IS NOT NULL"))})";
            }
            var unproven = pinnedIndex.Columns
                .Where(column => pinnedIndex.NullableColumns.Contains(column) && CanMatchNull(request.Where, column))
                .ToArray();
            if (unproven.Length != 0)
                throw new QueryRenderException(
                    "GW-QUERY-009",
                    $"Query on '{request.Table.Value}' can match null values in sparse pinned index column(s) " +
                    $"{string.Join(", ", unproven)}; the declaration must include nulls or use an unpinned index.");
        }

        var selection = request.Projection.AllColumns
            ? "*"
            : string.Join(", ", request.Projection.Columns.Select(RenderSelection));
        if ((request.LatestPerKey is not null || request.Result.IncludesTotalCount || request.Distinct) && !request.Projection.AllColumns)
        {
            var required = (request.LatestPerKey is { } latest
                    ? new[] { latest.Key, latest.Timestamp }.Concat(options.LatestPartitionColumns)
                    : Array.Empty<ColumnRef>())
                .Concat(effectiveOrder.Select(term => term.Column))
                .Where(column => !request.Projection.Columns.Any(selected => string.Equals(selected.Name, column.Name, StringComparison.Ordinal)))
                .GroupBy(column => column.Name, StringComparer.Ordinal)
                .Select(group => group.First());
            foreach (var column in required)
                selection += ", " + RenderSelection(column);
        }
        if (request.Projection.AllColumns)
        {
            foreach (var column in effectiveOrder.Select(term => term.Column)
                         .Where(RequiresExplicitSelection)
                         .GroupBy(column => column.Name, StringComparer.Ordinal)
                         .Select(group => group.First()))
            {
                selection += ", " + RenderSelection(column);
            }
        }

        var from = dialect.QuoteIdentifier(request.Table.Value);
        if (indexHintApplied)
            from += " " + RenderIndexHint(options.ResolvePhysicalIndexName(pinnedIndex!.Name));
        string sql;
        if (request.Result is ResultShape.Reduction reduction)
        {
            sql = RenderReduction(reduction, request, effectiveOrder, cursor, parameters, ref parameterIndex, from, where, options);
        }
        else if (request.Result.IncludesTotalCount)
        {
            var latestSource = RenderLatestSource(request.LatestPerKey, selection, from, where, options);
            // RenderLatestSource has already applied the caller predicate to the base CTE.
            // Reapplying it here would require predicate-only columns in a columns-only
            // projection and would duplicate its parameters.
            var pageSource = "__groundwork_base";
            var pageWhere = request.LatestPerKey is null ? "1 = 1" : "__groundwork_latest_rank = 1";
            if (request.Distinct)
            {
                var distinctWhere = pageWhere;
                if (request.Projection.AllColumns)
                {
                    latestSource += ", __groundwork_distinct AS (SELECT DISTINCT * FROM __groundwork_base WHERE " + distinctWhere + ")";
                }
                else
                {
                    var partition = string.Join(", ", request.Projection.Columns
                        .Where(column => !IsExecutionOnlySearchColumn(column, options))
                        .Select(RenderDistinctPartition));
                    var rankOrder = effectiveOrder.Count == 0
                        ? "(SELECT 1)"
                        : string.Join(", ", effectiveOrder.Select(RenderOrderTerm));
                    var inputAlias = dialect.QuoteIdentifier("__groundwork_distinct_input");
                    var rankAlias = dialect.QuoteIdentifier("__groundwork_distinct_rank");
                    latestSource += ", __groundwork_distinct_ranked AS (SELECT " + inputAlias + ".*, ROW_NUMBER() OVER (PARTITION BY " +
                        partition + " ORDER BY " + rankOrder + ") AS " + rankAlias + " FROM __groundwork_base AS " + inputAlias +
                        " WHERE " + distinctWhere + "), __groundwork_distinct AS (SELECT * FROM __groundwork_distinct_ranked WHERE " + rankAlias + " = 1)";
                }
                pageSource = "__groundwork_distinct";
                pageWhere = "1 = 1";
            }
            var countWhere = request.Distinct || request.LatestPerKey is null ? string.Empty : " WHERE __groundwork_latest_rank = 1";
            var countSource = request.Distinct ? pageSource : "__groundwork_base";
            var aggregate = RenderCountAggregate();
            if (request.Distinct && request.Paging.Offset is null && request.Paging.Limit is null && cursor is null)
            {
                sql = latestSource + ", __groundwork_total AS (SELECT " + aggregate + " AS __groundwork_total_count FROM " + countSource + countWhere + ") " +
                    "SELECT __groundwork_total.__groundwork_total_count, 1 AS __groundwork_count_only FROM __groundwork_total;";
            }
            else
            {
                if (cursor is not null)
                    pageWhere += " AND (" + RenderContinuation(effectiveOrder, cursor, parameters, ref parameterIndex) + ")";
                var page = "SELECT * FROM " + pageSource + " WHERE " + pageWhere;
                if (effectiveOrder.Count != 0)
                {
                    page += " ORDER BY " + string.Join(", ", effectiveOrder.Select(RenderOrderTerm));
                    if (RequiresOrderForOffset && request.Paging.Offset is null && request.Paging.Limit is null)
                        page += " OFFSET 0 ROWS";
                }
                else if ((request.Paging.Offset is not null || request.Paging.Limit is not null) && RequiresOrderForOffset)
                    page += " ORDER BY (SELECT 1)";
                page += RenderPaging(request.Paging, parameters, ref parameterIndex);
                sql = latestSource + ", __groundwork_page AS (" + page + "), __groundwork_total AS (SELECT " + aggregate + " AS __groundwork_total_count FROM " + countSource + countWhere + ") " +
                    "SELECT __groundwork_page.*, __groundwork_total.__groundwork_total_count, CASE WHEN __groundwork_page.__groundwork_has_row IS NULL THEN 1 ELSE 0 END AS __groundwork_count_only " +
                    "FROM __groundwork_total LEFT JOIN __groundwork_page ON 1 = 1 ORDER BY " +
                    dialect.QuoteIdentifier("__groundwork_count_only") + " ASC" +
                    (effectiveOrder.Count == 0 ? string.Empty : ", " + string.Join(", ", effectiveOrder.Select(RenderOrderTerm))) + ";";
            }
        }
        else
        {
            string source;
            if (!request.Distinct)
            {
                source = request.LatestPerKey is null
                    ? "SELECT " + selection + " FROM " + from + " WHERE " + where
                    : RenderLatestSource(request.LatestPerKey, selection, from, where, options) + " SELECT " + selection + " FROM __groundwork_base WHERE __groundwork_latest_rank = 1";
            }
            else
            {
                // Distinct is a provider operation, and its unit is the projected value rather
                // than the raw row. A ranked CTE preserves the requested order while allowing
                // order-only columns to be selected without making them part of the distinct key.
                var baseCte = RenderLatestSource(request.LatestPerKey, selection, from, where, options);
                var baseWhere = request.LatestPerKey is null ? "1 = 1" : "__groundwork_latest_rank = 1";
                if (request.Projection.AllColumns)
                {
                    source = baseCte + ", __groundwork_distinct AS (SELECT DISTINCT * FROM __groundwork_base WHERE " + baseWhere + ") " +
                        "SELECT * FROM __groundwork_distinct WHERE 1 = 1";
                }
                else
                {
                    var partition = string.Join(", ", request.Projection.Columns
                        .Where(column => !IsExecutionOnlySearchColumn(column, options))
                        .Select(RenderDistinctPartition));
                    var rankOrder = effectiveOrder.Count == 0
                        ? "(SELECT 1)"
                        : string.Join(", ", effectiveOrder.Select(RenderOrderTerm));
                    var inputAlias = dialect.QuoteIdentifier("__groundwork_distinct_input");
                    var rankAlias = dialect.QuoteIdentifier("__groundwork_distinct_rank");
                    source = baseCte + ", __groundwork_distinct_ranked AS (SELECT " + inputAlias + ".*, ROW_NUMBER() OVER (PARTITION BY " +
                        partition + " ORDER BY " + rankOrder + ") AS " + rankAlias + " FROM __groundwork_base AS " + inputAlias +
                        " WHERE " + baseWhere + ") SELECT * FROM __groundwork_distinct_ranked WHERE " + rankAlias + " = 1";
                }
            }
            sql = source;
            if (cursor is not null && (request.LatestPerKey is not null || request.Distinct))
                sql += " AND (" + RenderContinuation(effectiveOrder, cursor, parameters, ref parameterIndex) + ")";
            if (effectiveOrder.Count != 0)
                sql += " ORDER BY " + string.Join(", ", effectiveOrder.Select(RenderOrderTerm));
            else if ((request.Paging.Offset is not null || request.Paging.Limit is not null) && RequiresOrderForOffset)
                sql += " ORDER BY (SELECT 1)";
            sql += RenderPaging(request.Paging, parameters, ref parameterIndex) + ";";
        }
        if (parameters.Count > parameterBudget)
            throw new QueryRenderException(
                "GW-QUERY-015",
                $"Query on '{request.Table.Value}' requires {parameters.Count} parameters, exceeding the {ProviderName} provider budget of {parameterBudget}.");

        return new RelationalQueryCommand(
            sql,
            parameters,
            request.Result.IncludesTotalCount,
            matchNone,
            expectedIndex?.Name,
            indexHintApplied,
            effectiveOrder.Select(term => term.Column.Name).ToArray());
    }

    private RelationalQueryCommand RenderJoined(QueryRequest request, QueryRenderOptions? options)
    {
        options ??= QueryRenderOptions.Default;
        request = QuerySearchKeyRewriter.Rewrite(request, options.SearchKeyColumns);
        if (options.InValueLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "The In value limit must be positive.");

        var validation = PortableQuerySemantics.Validate(request);
        if (!validation.IsPortable)
        {
            var refusal = validation.Refusals[0];
            throw new QueryRenderException(refusal.Code, refusal.Message + " (" + refusal.Path + ").");
        }

        var previousScope = joinedColumnScope.Value;
        var scope = new JoinedColumnScope(request);
        joinedColumnScope.Value = scope;
        try
        {
            var effectiveOrder = EffectiveOrder(request, options);
            var parameters = new List<QueryRenderParameter>();
            var parameterIndex = 0;
            var matchNone = request.Where is Predicate.AlwaysFalse;
            var where = RenderPredicate(
                request.Where,
                parameters,
                ref parameterIndex,
                options.InValueLimit,
                request.Table.Value);
            IReadOnlyList<QueryConstant>? cursor = null;
            if (request.Paging.ContinuationToken is not null)
            {
                if (effectiveOrder.Count == 0)
                    throw new QueryRenderException("GW-QUERY-013", "Keyset continuation requires an explicit ordered query.");
                try
                {
                    cursor = QueryContinuationToken.Decode(request.Paging.ContinuationToken, request, options);
                }
                catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
                {
                    throw new QueryRenderException("GW-QUERY-013", "The keyset continuation token is invalid: " + exception.Message);
                }
                if (!scope.UsesDerivedSource)
                    where = "(" + where + ") AND (" + RenderContinuation(effectiveOrder, cursor, parameters, ref parameterIndex) + ")";
            }

            var pinnedIndex = options.FindPinnedIndex();
            var expectedIndex = options.FindSelectedIndex();
            var indexHintApplied = pinnedIndex is not null && supportsIndexHints;
            if (pinnedIndex is not null && !pinnedIndex.IncludesNulls)
            {
                if (matchNone)
                {
                    var exclusions = pinnedIndex.Columns.Select(column =>
                        scope.Render(SourceColumn(request, pinnedIndex, column), dialect) + " IS NOT NULL");
                    where = "(" + where + ") AND (" + string.Join(" AND ", exclusions) + ")";
                }
                var unproven = pinnedIndex.Columns
                    .Where(column => pinnedIndex.NullableColumns.Contains(column) &&
                        CanMatchNull(request.Where, SourceColumn(request, pinnedIndex, column)))
                    .ToArray();
                if (unproven.Length != 0)
                {
                    throw new QueryRenderException(
                        "GW-QUERY-009",
                        $"Query on '{request.Table.Value}' can match null values in sparse pinned index column(s) " +
                        $"{string.Join(", ", unproven)}; the declaration must include nulls or use an unpinned index.");
                }
            }

            var selection = JoinedSelection(request, effectiveOrder, options);
            var from = JoinedFrom(request.Join!, pinnedIndex, indexHintApplied, options);
            string sql;
            if (request.Result is ResultShape.Reduction reduction)
            {
                sql = RenderJoinedReduction(
                    reduction,
                    request,
                    effectiveOrder,
                    cursor,
                    parameters,
                    ref parameterIndex,
                    selection,
                    from,
                    where,
                    options,
                    scope);
            }
            else if (request.Result.IncludesTotalCount)
            {
                var baseCte = JoinedBaseCte(request, selection, from, where, options);
                scope.IsDerived = true;
                var pageSource = "__groundwork_base";
                var pageWhere = request.LatestPerKey is null ? "1 = 1" : "__groundwork_latest_rank = 1";
                if (request.Distinct)
                {
                    baseCte += JoinedDistinctCtes(request, effectiveOrder, options, pageWhere);
                    pageSource = "__groundwork_distinct";
                    pageWhere = "1 = 1";
                }
                var countSource = pageSource;
                var countWhere = request.Distinct || request.LatestPerKey is null
                    ? string.Empty
                    : " WHERE __groundwork_latest_rank = 1";
                if (request.Distinct && request.Paging.Offset is null && request.Paging.Limit is null && cursor is null)
                {
                    sql = baseCte + ", __groundwork_total AS (SELECT " + RenderCountAggregate() +
                        " AS __groundwork_total_count FROM " + countSource + countWhere + ") " +
                        "SELECT __groundwork_total.__groundwork_total_count, 1 AS __groundwork_count_only FROM __groundwork_total;";
                }
                else
                {
                    if (cursor is not null)
                        pageWhere += " AND (" + RenderContinuation(effectiveOrder, cursor, parameters, ref parameterIndex) + ")";
                    var page = "SELECT * FROM " + pageSource + " WHERE " + pageWhere;
                    page += JoinedOrderAndPaging(request.Paging, effectiveOrder, parameters, ref parameterIndex);
                    sql = baseCte + ", __groundwork_page AS (" + page + "), __groundwork_total AS (SELECT " +
                        RenderCountAggregate() + " AS __groundwork_total_count FROM " + countSource + countWhere + ") " +
                        "SELECT __groundwork_page.*, __groundwork_total.__groundwork_total_count, " +
                        "CASE WHEN __groundwork_page.__groundwork_has_row IS NULL THEN 1 ELSE 0 END AS __groundwork_count_only " +
                        "FROM __groundwork_total LEFT JOIN __groundwork_page ON 1 = 1 ORDER BY " +
                        dialect.QuoteIdentifier("__groundwork_count_only") + " ASC" +
                        (effectiveOrder.Count == 0 ? string.Empty : ", " + string.Join(", ", effectiveOrder.Select(RenderOrderTerm))) + ";";
                }
            }
            else if (!scope.UsesDerivedSource)
            {
                sql = "SELECT " + selection + " FROM " + from + " WHERE " + where +
                    JoinedOrderAndPaging(request.Paging, effectiveOrder, parameters, ref parameterIndex) + ";";
            }
            else
            {
                var baseCte = JoinedBaseCte(request, selection, from, where, options);
                scope.IsDerived = true;
                var source = "__groundwork_base";
                var sourceWhere = request.LatestPerKey is null ? "1 = 1" : "__groundwork_latest_rank = 1";
                if (request.Distinct)
                {
                    baseCte += JoinedDistinctCtes(request, effectiveOrder, options, sourceWhere);
                    source = "__groundwork_distinct";
                    sourceWhere = "1 = 1";
                }
                if (cursor is not null)
                    sourceWhere += " AND (" + RenderContinuation(effectiveOrder, cursor, parameters, ref parameterIndex) + ")";
                sql = baseCte + " SELECT * FROM " + source + " WHERE " + sourceWhere +
                    JoinedOrderAndPaging(request.Paging, effectiveOrder, parameters, ref parameterIndex) + ";";
            }

            if (parameters.Count > parameterBudget)
            {
                throw new QueryRenderException(
                    "GW-QUERY-015",
                    $"Query on '{request.Table.Value}' requires {parameters.Count} parameters, exceeding the {ProviderName} provider budget of {parameterBudget}.");
            }
            return new RelationalQueryCommand(
                sql,
                parameters,
                request.Result.IncludesTotalCount,
                matchNone,
                expectedIndex?.Name,
                indexHintApplied,
                effectiveOrder.Select(term => term.Column.Name).ToArray(),
                requiresCompositeMaterializer: request.Projection.AllColumns && request.Result is not ResultShape.Reduction);
        }
        finally
        {
            joinedColumnScope.Value = previousScope;
        }
    }

    private ColumnRef SourceColumn(QueryRequest request, QueryIndexDeclaration index, string name) =>
        new(
            request.Table,
            name,
            index.ColumnTypes.TryGetValue(name, out var type) ? type ?? QueryType.String : QueryType.String,
            index.NullableColumns.Contains(name));

    private string JoinedFrom(
        ReferenceJoin join,
        QueryIndexDeclaration? pinnedIndex,
        bool indexHintApplied,
        QueryRenderOptions options)
    {
        var source = dialect.QuoteIdentifier(join.SourceTable.Value) + " AS " + dialect.QuoteIdentifier(SourceAlias);
        if (indexHintApplied)
            source += " " + RenderIndexHint(options.ResolvePhysicalIndexName(pinnedIndex!.Name));
        var target = dialect.QuoteIdentifier(join.TargetTable.Value) + " AS " + dialect.QuoteIdentifier(TargetAlias);
        var equality = string.Join(" AND ", join.ColumnPairs.Select(pair =>
            RenderColumn(pair.Source) + " = " + RenderColumn(pair.Target)));
        return source + " INNER JOIN " + target + " ON " + equality;
    }

    private string JoinedSelection(
        QueryRequest request,
        IReadOnlyList<OrderTerm> effectiveOrder,
        QueryRenderOptions options)
    {
        var scope = joinedColumnScope.Value!;
        var selection = request.Projection.AllColumns
            ? dialect.QuoteIdentifier(SourceAlias) + ".*, " + dialect.QuoteIdentifier(TargetAlias) + ".*"
            : string.Join(", ", request.Projection.Columns.Select(RenderSelection));
        if (scope.UsesDerivedSource)
        {
            var required = (request.LatestPerKey is { } latest
                    ? new[] { latest.Key, latest.Timestamp }.Concat(options.LatestPartitionColumns)
                    : Array.Empty<ColumnRef>())
                .Concat(effectiveOrder.Select(term => term.Column))
                .Concat(request.Result is ResultShape.Reduction reduction ? [reduction.Column] : [])
                .Where(column => request.Projection.AllColumns || !request.Projection.Columns.Any(selected =>
                    SameQualifiedColumn(selected, column)))
                .GroupBy(column => (column.Table, column.Name))
                .Select(group => group.First());
            foreach (var column in required)
                selection += ", " + RenderSelection(column);
        }
        foreach (var item in effectiveOrder.Select((term, index) => (term, index)))
        {
            selection += ", " + RenderColumn(item.term.Column) + " AS " +
                dialect.QuoteIdentifier(QueryRequestExecution.ContinuationFieldName(item.index));
        }
        return selection;
    }

    private string JoinedBaseCte(
        QueryRequest request,
        string selection,
        string from,
        string where,
        QueryRenderOptions options)
    {
        if (request.LatestPerKey is not { } latest)
            return "WITH __groundwork_base AS (SELECT " + selection + ", 1 AS __groundwork_has_row FROM " + from + " WHERE " + where + ")";
        var latestOrder = new List<string> { RenderColumn(latest.Timestamp) + " DESC" };
        IEnumerable<ColumnRef> tieBreaks = options.TieBreakColumns.Length == 0
            ? new[] { latest.Key }
            : options.TieBreakColumns;
        latestOrder.AddRange(tieBreaks
            .Where(column => !SameQualifiedColumn(column, latest.Timestamp))
            .Select(column => RenderOrderTerm(new OrderTerm(column, OrderDirection.Ascending, NullOrder.First))));
        var partitions = new[] { latest.Key }
            .Concat(options.LatestPartitionColumns)
            .GroupBy(column => (column.Table, column.Name))
            .Select(group => RenderColumn(group.First()));
        return "WITH __groundwork_base AS (SELECT " + selection + ", ROW_NUMBER() OVER (PARTITION BY " +
            string.Join(", ", partitions) + " ORDER BY " + string.Join(", ", latestOrder) +
            ") AS __groundwork_latest_rank, 1 AS __groundwork_has_row FROM " + from + " WHERE " + where + ")";
    }

    private string JoinedDistinctCtes(
        QueryRequest request,
        IReadOnlyList<OrderTerm> effectiveOrder,
        QueryRenderOptions options,
        string sourceWhere)
    {
        if (request.Projection.AllColumns)
            return ", __groundwork_distinct AS (SELECT DISTINCT * FROM __groundwork_base WHERE " + sourceWhere + ")";
        var partition = string.Join(", ", request.Projection.Columns
            .Where(column => !IsExecutionOnlySearchColumn(column, options))
            .Select(RenderDistinctPartition));
        var rankOrder = effectiveOrder.Count == 0
            ? "(SELECT 1)"
            : string.Join(", ", effectiveOrder.Select(RenderOrderTerm));
        var inputAlias = dialect.QuoteIdentifier("__groundwork_distinct_input");
        var rankAlias = dialect.QuoteIdentifier("__groundwork_distinct_rank");
        return ", __groundwork_distinct_ranked AS (SELECT " + inputAlias +
            ".*, ROW_NUMBER() OVER (PARTITION BY " + partition + " ORDER BY " + rankOrder + ") AS " +
            rankAlias + " FROM __groundwork_base AS " + inputAlias + " WHERE " + sourceWhere +
            "), __groundwork_distinct AS (SELECT * FROM __groundwork_distinct_ranked WHERE " + rankAlias + " = 1)";
    }

    private string JoinedOrderAndPaging(
        Paging paging,
        IReadOnlyList<OrderTerm> effectiveOrder,
        ICollection<QueryRenderParameter> parameters,
        ref int parameterIndex)
    {
        var text = effectiveOrder.Count == 0
            ? paging.Offset is not null || paging.Limit is not null
                ? RequiresOrderForOffset ? " ORDER BY (SELECT 1)" : string.Empty
                : string.Empty
            : " ORDER BY " + string.Join(", ", effectiveOrder.Select(RenderOrderTerm));
        return text + RenderPaging(paging, parameters, ref parameterIndex);
    }

    private string RenderJoinedReduction(
        ResultShape.Reduction reduction,
        QueryRequest request,
        IReadOnlyList<OrderTerm> effectiveOrder,
        IReadOnlyList<QueryConstant>? cursor,
        ICollection<QueryRenderParameter> parameters,
        ref int parameterIndex,
        string selection,
        string from,
        string where,
        QueryRenderOptions options,
        JoinedColumnScope scope)
    {
        var baseCte = JoinedBaseCte(request, selection, from, where, options);
        scope.IsDerived = true;
        var sourceWhere = request.LatestPerKey is null ? "1 = 1" : "__groundwork_latest_rank = 1";
        if (cursor is not null && !request.Distinct)
            sourceWhere += " AND (" + RenderContinuation(effectiveOrder, cursor, parameters, ref parameterIndex) + ")";
        var source = "SELECT * FROM __groundwork_base WHERE " + sourceWhere;
        var inputAlias = dialect.QuoteIdentifier("__groundwork_reduction_input");
        string windowed;
        if (request.Distinct)
        {
            var rankOrder = effectiveOrder.Count == 0
                ? "(SELECT 1)"
                : string.Join(", ", effectiveOrder.Select(RenderOrderTerm));
            var rankAlias = dialect.QuoteIdentifier("__groundwork_reduction_distinct_rank");
            windowed = "SELECT * FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY " +
                RenderDistinctPartition(reduction.Column) + " ORDER BY " + rankOrder + ") AS " + rankAlias +
                " FROM (" + source + ") AS " + inputAlias + ") AS " + inputAlias + " WHERE " + rankAlias + " = 1";
            if (cursor is not null)
            {
                var cursorAlias = dialect.QuoteIdentifier("__groundwork_reduction_distinct_cursor");
                windowed = "SELECT * FROM (" + windowed + ") AS " + cursorAlias + " WHERE (" +
                    RenderContinuation(effectiveOrder, cursor, parameters, ref parameterIndex) + ")";
            }
        }
        else
        {
            windowed = "SELECT * FROM (" + source + ") AS " + inputAlias;
        }
        windowed += JoinedOrderAndPaging(request.Paging, effectiveOrder, parameters, ref parameterIndex);

        var pageAlias = dialect.QuoteIdentifier("__groundwork_reduction_page");
        var valueExpression = pageAlias + "." + dialect.QuoteIdentifier(scope.Field(reduction.Column));
        string aggregateSource = windowed;
        string aggregate;
        if (UsesPortableReductionOrder(reduction))
        {
            var orderedAlias = dialect.QuoteIdentifier("__groundwork_reduction_ordered");
            var rankName = dialect.QuoteIdentifier("__groundwork_reduction_value_rank");
            var direction = reduction is ResultShape.Min ? OrderDirection.Ascending : OrderDirection.Descending;
            aggregateSource = "SELECT " + orderedAlias + ".*, ROW_NUMBER() OVER (ORDER BY " +
                RenderOrderTerm(new OrderTerm(reduction.Column, direction, NullOrder.Last)) + ") AS " + rankName +
                " FROM (" + windowed + ") AS " + orderedAlias;
            aggregate = RenderReductionAggregate(
                reduction,
                "CASE WHEN " + pageAlias + "." + rankName + " = 1 THEN " + valueExpression + " END");
        }
        else
        {
            aggregate = RenderReductionAggregate(reduction, valueExpression);
        }
        return baseCte + " SELECT " + aggregate + " AS " + dialect.QuoteIdentifier(reduction.Column.Name) +
            " FROM (" + aggregateSource + ") AS " + pageAlias + ";";
    }

    /// <summary>
    /// Renders a normalized portable predicate using the same provider hooks, parameter adaptation,
    /// and literal semantics as an ordinary query. The returned fragment is safe to place after a
    /// relational <c>WHERE</c> keyword; callers remain responsible for admission and table binding.
    /// </summary>
    internal RelationalPredicateFragment RenderPredicateFragment(
        Predicate predicate,
        string table,
        int inValueLimit = 1_000)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        if (string.IsNullOrWhiteSpace(table))
            throw new ArgumentException("A source table is required.", nameof(table));
        if (inValueLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(inValueLimit));

        var parameters = new List<QueryRenderParameter>();
        var parameterIndex = 0;
        var normalized = PredicateNormalizer.Normalize(predicate);
        var commandText = RenderPredicate(normalized, parameters, ref parameterIndex, inValueLimit, table);
        if (parameters.Count > parameterBudget)
            throw new QueryRenderException(
                "GW-QUERY-015",
                $"Query on '{table}' requires {parameters.Count} parameters, exceeding the {ProviderName} provider budget of {parameterBudget}.");
        return new RelationalPredicateFragment(commandText, parameters);
    }

    /// <summary>
    /// Renders <c>DELETE FROM &lt;table&gt; WHERE &lt;predicate&gt;</c> from the same fragment, and
    /// therefore the same comparison and literal semantics, an ordinary query renders.
    /// </summary>
    public RelationalSetMutationCommand RenderDeleteWhere(
        string table,
        Predicate where,
        int inValueLimit = 1_000)
    {
        var fragment = RenderPredicateFragment(where, table, inValueLimit);
        return new RelationalSetMutationCommand(
            "DELETE FROM " + dialect.QuoteIdentifier(table) + " WHERE " + fragment.CommandText + ";",
            fragment.Parameters);
    }

    /// <summary>
    /// Renders <c>UPDATE &lt;table&gt; SET &lt;assignments&gt; WHERE &lt;predicate&gt;</c>.
    /// </summary>
    /// <param name="assignments">
    /// Already-physical values, encoded by the provider's own write encoder rather than adapted
    /// here, so a set-based update writes the byte-for-byte representation a keyed update writes.
    /// </param>
    /// <param name="incrementColumn">
    /// The optimistic token column, incremented in the same statement exactly as a keyed update
    /// increments it. Null for a unit that declares no token.
    /// </param>
    public RelationalSetMutationCommand RenderUpdateWhere(
        string table,
        Predicate where,
        IReadOnlyList<string> assignmentColumns,
        string? incrementColumn = null,
        int inValueLimit = 1_000)
    {
        ArgumentNullException.ThrowIfNull(assignmentColumns);
        if (assignmentColumns.Count == 0)
            throw new ArgumentException("A set-based update requires at least one assignment.", nameof(assignmentColumns));
        var fragment = RenderPredicateFragment(where, table, inValueLimit);
        var names = new List<string>(assignmentColumns.Count);
        var sets = new List<string>(assignmentColumns.Count + 1);
        for (var index = 0; index < assignmentColumns.Count; index++)
        {
            // A distinct prefix from the fragment's own p0..pn names: the two sets are numbered
            // independently and would otherwise collide on the first assignment.
            var name = "s" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            names.Add(name);
            sets.Add(dialect.QuoteIdentifier(assignmentColumns[index]) + " = @" + name);
        }
        if (incrementColumn is not null)
        {
            var quoted = dialect.QuoteIdentifier(incrementColumn);
            sets.Add(quoted + " = " + quoted + " + 1");
        }
        var total = fragment.Parameters.Length + names.Count;
        if (total > parameterBudget)
            throw new QueryRenderException(
                "GW-QUERY-015",
                $"Set-based update on '{table}' requires {total} parameters, exceeding the {ProviderName} provider budget of {parameterBudget}.");
        return new RelationalSetMutationCommand(
            "UPDATE " + dialect.QuoteIdentifier(table) + " SET " + string.Join(", ", sets) +
            " WHERE " + fragment.CommandText + ";",
            fragment.Parameters,
            names);
    }

    protected abstract string ProviderName { get; }

    protected virtual string RenderCountExpression() => "COUNT(*) OVER()";

    protected virtual string RenderCountAggregate() => "COUNT(*)";

    protected virtual bool RequiresOrderForOffset => false;

    /// <summary>Renders the native aggregate expression over one already-windowed value.</summary>
    protected virtual string RenderReductionAggregate(ResultShape.Reduction reduction, string valueExpression) =>
        reduction switch
        {
            ResultShape.Sum => "CASE WHEN COUNT(" + valueExpression + ") = 0 THEN NULL ELSE SUM(" + valueExpression + ") END",
            ResultShape.Min => "MIN(" + valueExpression + ")",
            ResultShape.Max => "MAX(" + valueExpression + ")",
            _ => throw new ArgumentOutOfRangeException(nameof(reduction), reduction, null)
        };

    /// <summary>
    /// Whether a reduction must select its value through the renderer's portable order key. Raw
    /// MIN/MAX comparisons inherit provider collation or native identifier ordering, which is not
    /// the public ordinal string/Guid contract.
    /// </summary>
    protected virtual bool UsesPortableReductionOrder(ResultShape.Reduction reduction) =>
        reduction is ResultShape.Min or ResultShape.Max &&
        reduction.Column.Type is QueryType.String or QueryType.Guid;

    protected virtual string RenderIndexHint(string indexName) =>
        throw new NotSupportedException($"{ProviderName} does not support index hints.");

    private string RenderReduction(
        ResultShape.Reduction reduction,
        QueryRequest request,
        IReadOnlyList<OrderTerm> effectiveOrder,
        IReadOnlyList<QueryConstant>? cursor,
        ICollection<QueryRenderParameter> parameters,
        ref int parameterIndex,
        string from,
        string where,
        QueryRenderOptions options)
    {
        // A reduction is a scalar over the caller's selected input, not an ordinary row query.
        // Materialize the source columns needed for latest/order semantics into a CTE, then apply
        // distinct and the input window before the provider aggregate. This keeps the whole
        // operation server-side and makes Take/Skip affect the reduced input rather than the
        // single aggregate row.
        var sourceColumns = new[] { reduction.Column }
            .Concat(request.LatestPerKey is { } latest
                ? new[] { latest.Key, latest.Timestamp }.Concat(options.LatestPartitionColumns)
                : Array.Empty<ColumnRef>())
            .Concat(effectiveOrder.Select(term => term.Column))
            .GroupBy(column => column.Name, StringComparer.Ordinal)
            .Select(group => RenderSelection(group.First()))
            .ToArray();
        var selection = string.Join(", ", sourceColumns);
        var latestSource = RenderLatestSource(request.LatestPerKey, selection, from, where, options);
        var sourceWhere = request.LatestPerKey is null ? "1 = 1" : "__groundwork_latest_rank = 1";
        if (cursor is not null && request.LatestPerKey is not null && !request.Distinct)
            sourceWhere += " AND (" + RenderContinuation(effectiveOrder, cursor, parameters, ref parameterIndex) + ")";

        var source = "SELECT * FROM __groundwork_base WHERE " + sourceWhere;
        var inputAlias = dialect.QuoteIdentifier("__groundwork_reduction_input");
        string windowed;
        if (request.Distinct)
        {
            // Keep the first source row for each projected value. This is the relational analogue
            // of the ordinary materializer's stable DistinctRows operation: an ORDER BY on a
            // different source column still determines which duplicate survives, and the page is
            // applied only after that row-number filter. A plain SELECT DISTINCT cannot preserve
            // that ordering portably (nor can SQL Server order by the renderer's CASE expression
            // unless it is selected literally).
            var rankOrder = effectiveOrder.Count == 0
                ? "(SELECT 1)"
                : string.Join(", ", effectiveOrder.Select(RenderOrderTerm));
            windowed = "SELECT * FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY " +
                RenderDistinctPartition(reduction.Column) + " ORDER BY " + rankOrder + ") AS " +
                dialect.QuoteIdentifier("__groundwork_reduction_distinct_rank") + " FROM (" + source +
                ") AS " + inputAlias + ") AS " + inputAlias + " WHERE " +
                dialect.QuoteIdentifier("__groundwork_reduction_distinct_rank") + " = 1";

            if (cursor is not null)
            {
                var cursorAlias = dialect.QuoteIdentifier("__groundwork_reduction_distinct_cursor");
                windowed = "SELECT * FROM (" + windowed + ") AS " + cursorAlias + " WHERE (" +
                    RenderContinuation(effectiveOrder, cursor, parameters, ref parameterIndex) + ")";
            }
        }
        else
        {
            windowed = "SELECT * FROM (" + source + ") AS " + inputAlias;
        }

        var hasPaging = request.Paging.Offset is not null || request.Paging.Limit is not null;
        if (hasPaging && effectiveOrder.Count != 0)
            windowed += " ORDER BY " + string.Join(", ", effectiveOrder.Select(RenderOrderTerm));
        else if (hasPaging && RequiresOrderForOffset)
            windowed += " ORDER BY (SELECT 1)";
        windowed += RenderPaging(request.Paging, parameters, ref parameterIndex);

        var pageAlias = dialect.QuoteIdentifier("__groundwork_reduction_page");
        var valueExpression = pageAlias + "." + dialect.QuoteIdentifier(reduction.Column.Name);
        string aggregateSource = windowed;
        string aggregate;
        if (UsesPortableReductionOrder(reduction))
        {
            var orderedAlias = dialect.QuoteIdentifier("__groundwork_reduction_ordered");
            var rankName = dialect.QuoteIdentifier("__groundwork_reduction_value_rank");
            var direction = reduction is ResultShape.Min ? OrderDirection.Ascending : OrderDirection.Descending;
            var orderTerm = RenderOrderTerm(new OrderTerm(reduction.Column, direction, NullOrder.Last));
            aggregateSource = "SELECT " + orderedAlias + ".*, ROW_NUMBER() OVER (ORDER BY " + orderTerm + ") AS " + rankName +
                " FROM (" + windowed + ") AS " + orderedAlias;
            var orderedValue = pageAlias + "." + dialect.QuoteIdentifier(reduction.Column.Name);
            aggregate = RenderReductionAggregate(reduction,
                "CASE WHEN " + pageAlias + "." + rankName + " = 1 THEN " + orderedValue + " END");
        }
        else
        {
            aggregate = RenderReductionAggregate(reduction, valueExpression);
        }
        return latestSource + " SELECT " + aggregate + " AS " + dialect.QuoteIdentifier(reduction.Column.Name) +
            " FROM (" + aggregateSource + ") AS " + pageAlias + ";";
    }

    private string RenderLatestSource(
        LatestPerKey? latest,
        string selection,
        string from,
        string where,
        QueryRenderOptions options)
    {
        if (latest is null)
            return "WITH __groundwork_base AS (SELECT " + selection + ", 1 AS __groundwork_has_row FROM " + from + " WHERE " + where + ")";

        IEnumerable<ColumnRef> tieBreak = options.TieBreakColumns.Length == 0
            ? new[] { latest.Key }
            : options.TieBreakColumns;
        var latestOrder = new List<string> { RenderColumn(latest.Timestamp) + " DESC" };
        latestOrder.AddRange(tieBreak
            .Where(column => !string.Equals(column.Name, latest.Timestamp.Name, StringComparison.Ordinal))
            .Select(column => RenderOrderTerm(new OrderTerm(column, OrderDirection.Ascending, NullOrder.First))));
        var partitions = new[] { latest.Key }
            .Concat(options.LatestPartitionColumns)
            .GroupBy(column => column.Name, StringComparer.Ordinal)
            .Select(group => RenderColumn(group.First()));
        return "WITH __groundwork_base AS (SELECT " + selection + ", ROW_NUMBER() OVER (PARTITION BY " + string.Join(", ", partitions) +
            " ORDER BY " + string.Join(", ", latestOrder) + ") AS __groundwork_latest_rank, 1 AS __groundwork_has_row FROM " + from + " WHERE " + where + ")";
    }

    /// <summary>Returns the provider expression used for comparisons and ordering of one column.</summary>
    protected virtual string RenderColumn(ColumnRef column) =>
        joinedColumnScope.Value?.Render(column, dialect) ?? dialect.QuoteIdentifier(column.Name);

    /// <summary>Renders one projected column's native equality key for provider-side Distinct.</summary>
    protected virtual string RenderDistinctPartition(ColumnRef column) => RenderColumn(column);

    private static bool IsExecutionOnlySearchColumn(ColumnRef column, QueryRenderOptions options) =>
        options.SearchKeyColumns.Values.Any(mapping =>
            !string.Equals(mapping.SourceColumn, mapping.PhysicalColumn, StringComparison.Ordinal) &&
            string.Equals(mapping.PhysicalColumn, column.Name, StringComparison.Ordinal));

    /// <summary>Renders one selected expression and preserves the model column name as its result alias.</summary>
    protected virtual string RenderSelection(ColumnRef column) =>
        RenderColumn(column) + " AS " + dialect.QuoteIdentifier(
            joinedColumnScope.Value is { } scope
                ? scope.Field(column)
                : column.Name);

    /// <summary>True when a computed order column must be selected even for <see cref="Projection.All"/>.</summary>
    protected virtual bool RequiresExplicitSelection(ColumnRef column) => false;

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

    protected string RenderPredicate(
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
            {
                var inner = RenderPredicate(not.Inner, parameters, ref parameterIndex, inValueLimit, table);
                // SQL's UNKNOWN must complement to TRUE for the total Q2 predicate algebra.
                return "(CASE WHEN (" + inner + ") THEN 0 ELSE 1 END = 1)";
            }
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

    protected virtual string RenderColumnCompare(Predicate.ColumnCompare compare)
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

    private static bool CanMatchNull(Predicate predicate, string column) =>
        CanMatchNull(predicate, new ColumnRef(column, QueryType.String));

    private static bool CanMatchNull(Predicate predicate, ColumnRef column)
    {
        switch (predicate)
        {
            case Predicate.AlwaysFalse:
                return false;
            case Predicate.AlwaysTrue:
                return true;
            case Predicate.Equal equal when SameColumn(equal.Column, column):
                return equal.Value.Kind == QueryConstantKind.Null;
            case Predicate.In membership when SameColumn(membership.Column, column):
                return membership.Values.Any(value => value.Kind == QueryConstantKind.Null);
            case Predicate.Range range when SameColumn(range.Column, column):
                return false;
            case Predicate.ColumnCompare compare when SameColumn(compare.Left, column) || SameColumn(compare.Right, column):
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

    private static bool SameColumn(ColumnRef candidate, ColumnRef column) =>
        string.Equals(candidate.Name, column.Name, StringComparison.Ordinal) &&
        (candidate.Table == TableId.Empty || column.Table == TableId.Empty || candidate.Table == column.Table);

    private static bool SameQualifiedColumn(ColumnRef left, ColumnRef right) =>
        left.Table == right.Table && string.Equals(left.Name, right.Name, StringComparison.Ordinal);
}
