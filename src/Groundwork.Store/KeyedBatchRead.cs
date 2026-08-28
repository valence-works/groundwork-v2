using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Text;
using Groundwork.Kernel;
using Groundwork.Query.Model;

namespace Groundwork.Store;

/// <summary>One matched row paired with the requested key that selected it.</summary>
public sealed record KeyedBatchReadRow(QueryConstant Key, IReadOnlyDictionary<string, object?> Values);

/// <summary>
/// The keyed batch-read outcome: matched rows grouped in the caller's key order, and every
/// requested key that matched no row. Every execution-equivalent key representative appears in at
/// least one of <see cref="Rows"/> (once per matching row, for a non-unique key column) or exactly
/// once in <see cref="MissingKeys"/>, never both and never neither.
/// </summary>
public sealed class KeyedBatchReadResult
{
    public KeyedBatchReadResult(IReadOnlyList<KeyedBatchReadRow> rows, IReadOnlyList<QueryConstant> missingKeys)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(missingKeys);
        Rows = Array.AsReadOnly(rows.Select(row => row ?? throw new ArgumentException(
            "Batch-read rows cannot contain null references.", nameof(rows))).ToArray());
        MissingKeys = Array.AsReadOnly(missingKeys.ToArray());
    }

    /// <summary>
    /// Matched rows grouped by the first occurrence of each execution-equivalent requested key —
    /// never by chunk or provider return order. A non-unique key can therefore contribute multiple
    /// adjacent rows.
    /// </summary>
    public IReadOnlyList<KeyedBatchReadRow> Rows { get; }

    /// <summary>Requested keys that matched no row, in the same first-occurrence order.</summary>
    public IReadOnlyList<QueryConstant> MissingKeys { get; }
}

/// <summary>
/// One keyed batch-read: every row whose <see cref="KeyColumn"/> equals one of <see cref="Keys"/>,
/// however many keys are supplied. The key set is deliberately unbounded — the 1,000-value `In` cap
/// (`GW-QUERY-015`) and each provider's real parameter/payload budgets are internal chunking concerns of
/// <see cref="KeyedBatchReadSessionExtensions.BatchRead"/>, never a caller refusal.
/// </summary>
public sealed record KeyedBatchReadRequest
{
    public KeyedBatchReadRequest(
        TableId table,
        ColumnRef keyColumn,
        IReadOnlyList<object?> keys,
        Projection? projection = null,
        ImmutableArray<OrderTerm> order = default)
    {
        Table = table ?? throw new ArgumentNullException(nameof(table));
        KeyColumn = keyColumn ?? throw new ArgumentNullException(nameof(keyColumn));
        if (keyColumn.Table != TableId.Empty && keyColumn.Table != table)
            throw new ArgumentException(
                "GW-BATCHREAD-001: the key column must belong to the requested table.", nameof(keyColumn));
        ArgumentNullException.ThrowIfNull(keys);
        var seen = new HashSet<QueryConstant>();
        var ordered = new List<QueryConstant>();
        foreach (var raw in keys)
        {
            if (raw is null)
                throw new ArgumentException(
                    "GW-BATCHREAD-002: batch-read keys cannot be null.", nameof(keys));
            var constant = QueryConstant.Of(keyColumn, raw);
            if (seen.Add(constant))
                ordered.Add(constant);
        }

        Keys = new ReadOnlyCollection<QueryConstant>(ordered);
        Projection = projection ?? Projection.All;
        Order = order.IsDefault ? ImmutableArray<OrderTerm>.Empty : order;
    }

    public TableId Table { get; }

    public ColumnRef KeyColumn { get; }

    /// <summary>The requested keys, deduplicated and kept in first-occurrence order.</summary>
    public IReadOnlyList<QueryConstant> Keys { get; }

    public Projection Projection { get; }

    /// <summary>Order applied within each chunk; irrelevant when <see cref="KeyColumn"/> is unique.</summary>
    public ImmutableArray<OrderTerm> Order { get; }
}

/// <summary>
/// Public entry point for the keyed batch-read primitive. Chunks the requested key set under the
/// provider's real admission budget, executes one <see cref="IStorageSession.Query"/> per chunk,
/// and merges the chunk results deterministically — the same ordering, dedup, and missing-key
/// semantics regardless of provider or key-set size.
/// </summary>
public static class KeyedBatchReadSessionExtensions
{
    public static KeyedBatchReadResult BatchRead(
        this IStorageSession session,
        KeyedBatchReadRequest request,
        IStorageProviderConnection? connection = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        StorageAccessValidation.EnsureOrdinaryQuery(session.Access);
        KeyedBatchReadPlanner.Validate(session, request);
        var plan = KeyedBatchReadPlanner.CreatePlan(session, request);
        var profile = connection?.GetQueryAdmission() ?? QueryAdmissionProfile.Default;
        var matched = new Dictionary<QueryConstant, List<IReadOnlyDictionary<string, object?>>>();
        foreach (var chunk in KeyedBatchReadPlanner.Chunk(
            plan.ExecutionKeys,
            profile,
            reserveScopedParameter: session.Unit.Scope == ScopePolicy.Scoped))
        {
            var chunkResult = session.Query(
                KeyedBatchReadPlanner.BuildQuery(plan, chunk),
                KeyedBatchReadPlanner.RenderOptions(chunk));
            KeyedBatchReadPlanner.Merge(plan, chunkResult, matched);
        }

        return KeyedBatchReadPlanner.Materialize(plan, matched);
    }

    public static async ValueTask<KeyedBatchReadResult> BatchReadAsync(
        this IStorageSession session,
        KeyedBatchReadRequest request,
        IStorageProviderConnection? connection = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        StorageAccessValidation.EnsureOrdinaryQuery(session.Access);
        KeyedBatchReadPlanner.Validate(session, request);
        var plan = KeyedBatchReadPlanner.CreatePlan(session, request);
        var profile = connection?.GetQueryAdmission() ?? QueryAdmissionProfile.Default;
        var matched = new Dictionary<QueryConstant, List<IReadOnlyDictionary<string, object?>>>();
        foreach (var chunk in KeyedBatchReadPlanner.Chunk(
            plan.ExecutionKeys,
            profile,
            reserveScopedParameter: session.Unit.Scope == ScopePolicy.Scoped))
        {
            var chunkResult = await session
                .QueryAsync(
                    KeyedBatchReadPlanner.BuildQuery(plan, chunk),
                    KeyedBatchReadPlanner.RenderOptions(chunk),
                    cancellationToken)
                .ConfigureAwait(false);
            KeyedBatchReadPlanner.Merge(plan, chunkResult, matched);
        }

        return KeyedBatchReadPlanner.Materialize(plan, matched);
    }
}

/// <summary>
/// The one chunk/merge implementation every provider's batch-read shares. Nothing here is
/// provider-specific: chunk size comes from the connection's advertised
/// <see cref="QueryAdmissionProfile"/>, and every chunk is executed through the provider's own
/// <see cref="IStorageSession.Query"/> — the same path an ordinary query already takes, including
/// MongoDB's native <c>$in</c>. This is what makes the four-way parity a property of the shared
/// code rather than a claim proven separately per provider.
/// </summary>
internal sealed record KeyedBatchReadExecutionKey(
    QueryConstant Requested,
    QueryConstant Execution);

internal sealed class KeyedBatchReadExecutionPlan
{
    internal KeyedBatchReadExecutionPlan(
        KeyedBatchReadRequest request,
        ColumnRef executionColumn,
        QuerySearchKeyColumn? mapping,
        IReadOnlyList<KeyedBatchReadExecutionKey> keys)
    {
        Request = request;
        ExecutionColumn = executionColumn;
        Mapping = mapping;
        Keys = keys;
        ExecutionKeys = keys.Select(key => key.Execution).ToArray();
        RequestedByExecutionKey = keys.ToDictionary(key => key.Execution, key => key.Requested);
    }

    internal KeyedBatchReadRequest Request { get; }

    internal ColumnRef ExecutionColumn { get; }

    internal QuerySearchKeyColumn? Mapping { get; }

    internal IReadOnlyList<KeyedBatchReadExecutionKey> Keys { get; }

    internal IReadOnlyList<QueryConstant> ExecutionKeys { get; }

    internal IReadOnlyDictionary<QueryConstant, QueryConstant> RequestedByExecutionKey { get; }
}

internal static class KeyedBatchReadPlanner
{
    internal static KeyedBatchReadExecutionPlan CreatePlan(
        IStorageSession session,
        KeyedBatchReadRequest request)
    {
        var mapping = SearchKeyQueryMappings.For(session.Unit)
            .GetValueOrDefault(request.KeyColumn.Name);
        var isProjected = RequiresSearchKey(mapping, request.KeyColumn.Name);
        var executionColumn = isProjected
            ? new ColumnRef(
                new TableId(session.Unit.Name),
                mapping!.PhysicalColumn,
                QueryType.String,
                isNullable: true,
                maxLength: mapping.MaxLength)
            : request.KeyColumn;
        var keys = new List<KeyedBatchReadExecutionKey>();
        var seen = new HashSet<QueryConstant>();
        foreach (var requested in request.Keys)
        {
            var execution = ToExecutionKey(requested, executionColumn, mapping, isProjected);
            if (seen.Add(execution))
                keys.Add(new KeyedBatchReadExecutionKey(requested, execution));
        }

        return new KeyedBatchReadExecutionPlan(request, executionColumn, mapping, keys);
    }

    internal static IEnumerable<IReadOnlyList<QueryConstant>> Chunk(
        KeyedBatchReadRequest request,
        QueryAdmissionProfile profile,
        bool reserveScopedParameter = false) =>
        Chunk(request.Keys, profile, reserveScopedParameter);

    internal static IEnumerable<IReadOnlyList<QueryConstant>> Chunk(
        IReadOnlyList<QueryConstant> keys,
        QueryAdmissionProfile profile,
        bool reserveScopedParameter = false)
    {
        if (keys.Count == 0)
            yield break;

        var chunkSize = ChunkSize(profile, reserveScopedParameter);
        var payloadBudget = profile.MaximumBatchReadPayloadBytes;
        if (payloadBudget is <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(profile),
                payloadBudget,
                "MaximumBatchReadPayloadBytes must be positive when supplied.");

        var chunk = new List<QueryConstant>();
        var chunkBytes = PayloadContainerOverheadBytes;
        foreach (var key in keys)
        {
            var keyBytes = EstimateKeyPayloadBytes(key);
            if (payloadBudget is { } budget &&
                AddSaturated(PayloadContainerOverheadBytes, keyBytes) > budget)
            {
                throw new ArgumentException(
                    $"GW-BATCHREAD-004: key '{key.ToCanonicalString(false)}' is estimated at " +
                    $"{AddSaturated(PayloadContainerOverheadBytes, keyBytes)} bytes, exceeding the " +
                    $"configured batch-read payload budget of {budget} bytes.",
                    nameof(keys));
            }

            if (chunk.Count != 0 &&
                (chunk.Count >= chunkSize ||
                 payloadBudget is { } currentBudget && AddSaturated(chunkBytes, keyBytes) > currentBudget))
            {
                yield return chunk.ToArray();
                chunk.Clear();
                chunkBytes = PayloadContainerOverheadBytes;
            }

            chunk.Add(key);
            chunkBytes = AddSaturated(chunkBytes, keyBytes);
        }

        if (chunk.Count != 0)
            yield return chunk.ToArray();
    }

    /// <summary>
    /// The largest chunk that fits the provider's batch-read key budget and parameter budget. A
    /// scoped session reserves one key slot for its provider-injected scope parameter. A caller who
    /// never learns the real budget still gets a safe, if conservative, chunk under the portable
    /// defaults.
    /// </summary>
    internal static int ChunkSize(
        QueryAdmissionProfile profile,
        bool reserveScopedParameter = false)
    {
        var keyCap = Math.Max(1, profile.MaximumBatchReadKeys);
        var parameterCap = Math.Max(1, profile.MaximumParameters);
        if (reserveScopedParameter)
        {
            if (keyCap > 1)
                keyCap--;
            if (parameterCap > 1)
                parameterCap--;
        }

        return Math.Max(1, Math.Min(keyCap, parameterCap));
    }

    internal static QueryRequest BuildQuery(
        KeyedBatchReadExecutionPlan plan,
        IReadOnlyList<QueryConstant> chunk)
    {
        Predicate where = new Predicate.In(plan.ExecutionColumn, chunk);
        var projection = EnsureKeyColumnProjected(plan.Request.Projection, plan.Request.KeyColumn);
        return new QueryRequest(plan.Request.Table, where, plan.Request.Order, projection, Paging.None);
    }

    internal static QueryRenderOptions RenderOptions(IReadOnlyList<QueryConstant> chunk) =>
        new() { InValueLimit = Math.Max(1, chunk.Count) };

    internal static void Validate(IStorageSession session, KeyedBatchReadRequest request)
    {
        if (request.Table == TableId.Empty ||
            !string.Equals(request.Table.Value, session.Unit.Name, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"GW-BATCHREAD-001: table '{request.Table.Value}' is not the session unit '{session.Unit.Name}'.",
                nameof(request));
        }

        var declared = session.Unit.Columns.FirstOrDefault(column =>
            string.Equals(column.Name, request.KeyColumn.Name, StringComparison.Ordinal));
        if (declared is null || QueryTypeFor(declared.Type) is not { } declaredType ||
            declaredType != request.KeyColumn.Type)
        {
            throw new ArgumentException(
                $"GW-BATCHREAD-001: key column '{request.KeyColumn.Name}' is not declared with " +
                $"type '{request.KeyColumn.Type}' on session unit '{session.Unit.Name}'.",
                nameof(request));
        }

        var mapping = SearchKeyQueryMappings.For(session.Unit)
            .GetValueOrDefault(request.KeyColumn.Name);
        ValidateSearchKeyPolicy(request.KeyColumn, mapping);
    }

    private const long PayloadContainerOverheadBytes = 128;
    private const long PayloadValueOverheadBytes = 64;

    private static long EstimateKeyPayloadBytes(QueryConstant key) =>
        AddSaturated(PayloadValueOverheadBytes,
            Encoding.UTF8.GetByteCount(key.ToCanonicalString()));

    private static long AddSaturated(long left, long right) =>
        right > long.MaxValue - left ? long.MaxValue : left + right;

    private static QueryConstant ToExecutionKey(
        QueryConstant requested,
        ColumnRef executionColumn,
        QuerySearchKeyColumn? mapping,
        bool isProjected)
    {
        if (!isProjected)
            return requested;

        return QueryConstant.Of(
            executionColumn,
            QuerySearchKeys.Encode((string)requested.Value!, mapping!.Policy));
    }

    private static QueryConstant ToExecutionKey(
        object? rawValue,
        KeyedBatchReadExecutionPlan plan)
    {
        var logical = QueryConstant.Of(plan.Request.KeyColumn, rawValue);
        if (!RequiresSearchKey(plan.Mapping, plan.Request.KeyColumn.Name))
            return logical;

        return QueryConstant.Of(
            plan.ExecutionColumn,
            QuerySearchKeys.Encode((string)logical.Value!, plan.Mapping!.Policy));
    }

    private static bool RequiresSearchKey(QuerySearchKeyColumn? mapping, string sourceColumn) =>
        mapping is not null &&
        mapping.Policy != QuerySearchKeyPolicy.Ordinal &&
        !string.Equals(mapping.PhysicalColumn, sourceColumn, StringComparison.Ordinal);

    private static void ValidateSearchKeyPolicy(ColumnRef keyColumn, QuerySearchKeyColumn? mapping)
    {
        if (mapping is null || QuerySearchKeys.MatchesPolicy(keyColumn.StringComparison, mapping.Policy))
            return;

        throw new QueryRenderException(
            "GW-QUERY-031",
            $"Batch-read key column '{keyColumn.Name}' declares comparison policy " +
            $"'{keyColumn.StringComparison}', but its schema search-key mapping declares '{mapping.Policy}'. " +
            "Build the ColumnRef from the schema and use its matching comparison policy.");
    }

    private static QueryType? QueryTypeFor(PortableType type) => type switch
    {
        PortableType.Boolean => QueryType.Boolean,
        PortableType.Int32 => QueryType.Int32,
        PortableType.Int64 => QueryType.Int64,
        PortableType.Decimal => QueryType.Decimal,
        PortableType.String => QueryType.String,
        PortableType.DateTimeOffset => QueryType.DateTimeOffset,
        PortableType.Guid => QueryType.Guid,
        PortableType.Binary => QueryType.Binary,
        _ => null
    };

    internal static void Merge(
        KeyedBatchReadExecutionPlan plan,
        QueryMaterializedResult chunkResult,
        Dictionary<QueryConstant, List<IReadOnlyDictionary<string, object?>>> matched)
    {
        foreach (var row in chunkResult.Rows)
        {
            if (!row.TryGetValue(plan.Request.KeyColumn.Name, out var rawKey))
                throw new InvalidOperationException(
                    "GW-BATCHREAD-003: the provider's query result omitted the batch-read key column.");
            var executionKey = ToExecutionKey(rawKey, plan);
            if (!plan.RequestedByExecutionKey.ContainsKey(executionKey))
                continue;
            if (!matched.TryGetValue(executionKey, out var rows))
            {
                rows = new List<IReadOnlyDictionary<string, object?>>();
                matched[executionKey] = rows;
            }

            rows.Add(ProjectRow(plan.Request, row));
        }
    }

    internal static KeyedBatchReadResult Materialize(
        KeyedBatchReadExecutionPlan plan,
        Dictionary<QueryConstant, List<IReadOnlyDictionary<string, object?>>> matched)
    {
        var rows = new List<KeyedBatchReadRow>();
        var missing = new List<QueryConstant>();
        foreach (var key in plan.Keys)
        {
            if (matched.TryGetValue(key.Execution, out var group))
            {
                foreach (var values in group)
                    rows.Add(new KeyedBatchReadRow(key.Requested, values));
            }
            else
            {
                missing.Add(key.Requested);
            }
        }

        return new KeyedBatchReadResult(rows, missing);
    }

    private static Projection EnsureKeyColumnProjected(Projection projection, ColumnRef keyColumn)
    {
        if (projection.AllColumns)
            return projection;
        return projection.Columns.Any(column => string.Equals(column.Name, keyColumn.Name, StringComparison.Ordinal))
            ? projection
            : Projection.ColumnsOnly(projection.Columns.Append(keyColumn));
    }

    private static IReadOnlyDictionary<string, object?> ProjectRow(
        KeyedBatchReadRequest request,
        IReadOnlyDictionary<string, object?> row)
    {
        if (request.Projection.AllColumns)
            return row;
        var requested = request.Projection.Columns.Select(column => column.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (requested.Contains(request.KeyColumn.Name))
            return row;
        return new ReadOnlyDictionary<string, object?>(row
            .Where(pair => requested.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
    }
}
