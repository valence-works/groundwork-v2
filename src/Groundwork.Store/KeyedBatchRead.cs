using System.Collections.Immutable;
using System.Collections.ObjectModel;
using Groundwork.Query.Model;

namespace Groundwork.Store;

/// <summary>One requested key paired with the row it matched.</summary>
public sealed record KeyedBatchReadRow(QueryConstant Key, IReadOnlyDictionary<string, object?> Values);

/// <summary>
/// The keyed batch-read outcome: matched rows in the caller's key order, and every requested key
/// that matched no row. Every requested key appears in exactly one of <see cref="Rows"/> (once per
/// matching row, for a non-unique key column) or <see cref="MissingKeys"/>, never both and never
/// neither.
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
    /// Matched rows ordered by the position of their key in the caller's deduplicated request — the
    /// first-occurrence order of <see cref="KeyedBatchReadRequest.Keys"/> — never by chunk or
    /// provider return order, so every provider yields byte-identical ordering for the same request.
    /// </summary>
    public IReadOnlyList<KeyedBatchReadRow> Rows { get; }

    /// <summary>Requested keys that matched no row, in the same first-occurrence order.</summary>
    public IReadOnlyList<QueryConstant> MissingKeys { get; }
}

/// <summary>
/// One keyed batch-read: every row whose <see cref="KeyColumn"/> equals one of <see cref="Keys"/>,
/// however many keys are supplied. The key set is deliberately unbounded — the 1,000-value `In` cap
/// (`GW-QUERY-015`) and each provider's real parameter budget are internal chunking concerns of
/// <see cref="KeyedBatchReadSessionExtensions.BatchRead"/>, never a caller refusal.
/// </summary>
public sealed record KeyedBatchReadRequest
{
    public KeyedBatchReadRequest(
        TableId table,
        ColumnRef keyColumn,
        IReadOnlyList<object?> keys,
        Projection? projection = null,
        Predicate? additionalPredicate = null,
        int additionalPredicateParameterCount = 0,
        ImmutableArray<OrderTerm> order = default)
    {
        Table = table ?? throw new ArgumentNullException(nameof(table));
        KeyColumn = keyColumn ?? throw new ArgumentNullException(nameof(keyColumn));
        if (keyColumn.Table != TableId.Empty && keyColumn.Table != table)
            throw new ArgumentException(
                "GW-BATCHREAD-001: the key column must belong to the requested table.", nameof(keyColumn));
        ArgumentNullException.ThrowIfNull(keys);
        if (additionalPredicateParameterCount < 0)
            throw new ArgumentOutOfRangeException(
                nameof(additionalPredicateParameterCount),
                additionalPredicateParameterCount,
                "The additional predicate's parameter cost cannot be negative.");

        var seen = new HashSet<QueryConstant>();
        var ordered = new List<QueryConstant>();
        foreach (var raw in keys)
        {
            var constant = QueryConstant.Of(keyColumn, raw);
            if (constant.Kind == QueryConstantKind.Null)
                throw new ArgumentException(
                    "GW-BATCHREAD-002: batch-read keys cannot be null.", nameof(keys));
            if (seen.Add(constant))
                ordered.Add(constant);
        }

        Keys = new ReadOnlyCollection<QueryConstant>(ordered);
        Projection = projection ?? Projection.All;
        AdditionalPredicate = additionalPredicate;
        AdditionalPredicateParameterCount = additionalPredicateParameterCount;
        Order = order.IsDefault ? ImmutableArray<OrderTerm>.Empty : order;
    }

    public TableId Table { get; }

    public ColumnRef KeyColumn { get; }

    /// <summary>The requested keys, deduplicated and kept in first-occurrence order.</summary>
    public IReadOnlyList<QueryConstant> Keys { get; }

    public Projection Projection { get; }

    /// <summary>An additional predicate ANDed into every chunk's membership test, e.g. a scope filter.</summary>
    public Predicate? AdditionalPredicate { get; }

    /// <summary>
    /// How many bound parameters <see cref="AdditionalPredicate"/> costs per chunk, so the chunk
    /// size leaves it room instead of exceeding the provider's real parameter budget alongside it.
    /// </summary>
    public int AdditionalPredicateParameterCount { get; }

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
        var profile = connection?.GetQueryAdmission() ?? QueryAdmissionProfile.Default;
        var matched = new Dictionary<QueryConstant, List<IReadOnlyDictionary<string, object?>>>();
        foreach (var chunk in KeyedBatchReadPlanner.Chunk(request, profile))
        {
            var chunkResult = session.Query(KeyedBatchReadPlanner.BuildQuery(request, chunk));
            KeyedBatchReadPlanner.Merge(request, chunkResult, matched);
        }

        return KeyedBatchReadPlanner.Materialize(request, matched);
    }

    public static async ValueTask<KeyedBatchReadResult> BatchReadAsync(
        this IStorageSession session,
        KeyedBatchReadRequest request,
        IStorageProviderConnection? connection = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        var profile = connection?.GetQueryAdmission() ?? QueryAdmissionProfile.Default;
        var matched = new Dictionary<QueryConstant, List<IReadOnlyDictionary<string, object?>>>();
        foreach (var chunk in KeyedBatchReadPlanner.Chunk(request, profile))
        {
            var chunkResult = await session
                .QueryAsync(KeyedBatchReadPlanner.BuildQuery(request, chunk), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            KeyedBatchReadPlanner.Merge(request, chunkResult, matched);
        }

        return KeyedBatchReadPlanner.Materialize(request, matched);
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
internal static class KeyedBatchReadPlanner
{
    internal static IEnumerable<IReadOnlyList<QueryConstant>> Chunk(
        KeyedBatchReadRequest request,
        QueryAdmissionProfile profile)
    {
        if (request.Keys.Count == 0)
            yield break;

        var chunkSize = ChunkSize(request, profile);
        for (var offset = 0; offset < request.Keys.Count; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, request.Keys.Count - offset);
            yield return request.Keys.Skip(offset).Take(length).ToArray();
        }
    }

    /// <summary>
    /// The largest chunk that fits both the portable `In` cap and the provider's real parameter
    /// budget, net of what the caller's additional predicate costs. A caller who never learns the
    /// real budget still gets a safe, if conservative, chunk under the portable defaults.
    /// </summary>
    internal static int ChunkSize(KeyedBatchReadRequest request, QueryAdmissionProfile profile)
    {
        var inCap = Math.Max(1, profile.MaximumInValues);
        var paramCap = Math.Max(1, profile.MaximumParameters - request.AdditionalPredicateParameterCount);
        return Math.Max(1, Math.Min(inCap, paramCap));
    }

    internal static QueryRequest BuildQuery(KeyedBatchReadRequest request, IReadOnlyList<QueryConstant> chunk)
    {
        Predicate where = new Predicate.In(request.KeyColumn, chunk);
        if (request.AdditionalPredicate is not null)
            where = new Predicate.And(new[] { where, request.AdditionalPredicate });
        var projection = EnsureKeyColumnProjected(request.Projection, request.KeyColumn);
        return new QueryRequest(request.Table, where, request.Order, projection, Paging.None);
    }

    internal static void Merge(
        KeyedBatchReadRequest request,
        QueryMaterializedResult chunkResult,
        Dictionary<QueryConstant, List<IReadOnlyDictionary<string, object?>>> matched)
    {
        foreach (var row in chunkResult.Rows)
        {
            if (!row.TryGetValue(request.KeyColumn.Name, out var rawKey))
                throw new InvalidOperationException(
                    "GW-BATCHREAD-003: the provider's query result omitted the batch-read key column.");
            var key = QueryConstant.Of(request.KeyColumn, rawKey);
            if (!matched.TryGetValue(key, out var rows))
            {
                rows = new List<IReadOnlyDictionary<string, object?>>();
                matched[key] = rows;
            }

            rows.Add(ProjectRow(request, row));
        }
    }

    internal static KeyedBatchReadResult Materialize(
        KeyedBatchReadRequest request,
        Dictionary<QueryConstant, List<IReadOnlyDictionary<string, object?>>> matched)
    {
        var rows = new List<KeyedBatchReadRow>();
        var missing = new List<QueryConstant>();
        foreach (var key in request.Keys)
        {
            if (matched.TryGetValue(key, out var group))
            {
                foreach (var values in group)
                    rows.Add(new KeyedBatchReadRow(key, values));
            }
            else
            {
                missing.Add(key);
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
