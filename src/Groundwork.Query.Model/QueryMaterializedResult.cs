using System.Collections;
using System.Collections.ObjectModel;

namespace Groundwork.Query.Model;

/// <summary>A defensive provider-neutral result from an executed query.</summary>
public sealed class QueryMaterializedResult
{
    public QueryMaterializedResult(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        long? totalCount,
        string? nextContinuationToken,
        string? selectedIndex = null,
        bool indexHintApplied = false)
    {
        if (rows is null) throw new ArgumentNullException(nameof(rows));
        Rows = Array.AsReadOnly(rows.Select(row =>
            (IReadOnlyDictionary<string, object?>)new ReadOnlyDictionary<string, object?>(
                (row ?? throw new ArgumentException("Query rows cannot contain null references.", nameof(rows)))
                    .ToDictionary(pair => pair.Key, pair => CloneValue(pair.Value), StringComparer.Ordinal))).ToArray());
        TotalCount = totalCount;
        NextContinuationToken = nextContinuationToken;
        SelectedIndex = selectedIndex;
        IndexHintApplied = indexHintApplied;
    }

    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows { get; }

    public long? TotalCount { get; }

    public string? NextContinuationToken { get; }

    public string? SelectedIndex { get; }

    public bool IndexHintApplied { get; }

    private static object? CloneValue(object? value) => value switch
    {
        null => null,
        byte[] bytes => bytes.ToArray(),
        IReadOnlyDictionary<string, object?> dictionary => new ReadOnlyDictionary<string, object?>(
            dictionary.ToDictionary(pair => pair.Key, pair => CloneValue(pair.Value), StringComparer.Ordinal)),
        IEnumerable sequence when value is not string => Array.AsReadOnly(sequence.Cast<object?>().Select(CloneValue).ToArray()),
        _ when value.GetType().IsValueType || value is string => value,
        _ => throw new ArgumentException($"Cannot snapshot query value of type '{value.GetType().FullName}'.", nameof(value))
    };
}

/// <summary>Shared result shaping for provider query implementations.</summary>
public static class QueryResultMaterializer
{
    public static QueryMaterializedResult Materialize(
        QueryRequest request,
        QueryRenderOptions options,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> source,
        string? selectedIndex = null,
        bool indexHintApplied = false)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (source is null) throw new ArgumentNullException(nameof(source));

        var totalCount = request.Result.IncludesTotalCount
            ? source.FirstOrDefault() is { } first &&
              first.TryGetValue("__groundwork_total_count", out var count) && count is not null
                ? Convert.ToInt64(count, System.Globalization.CultureInfo.InvariantCulture)
                : 0L
            : (long?)null;
        var hasMore = request.Paging.Limit is int limit && source.Count > limit;
        var visible = hasMore ? source.Take(request.Paging.Limit!.Value).ToArray() : source.ToArray();
        var effectiveOrder = options.GetEffectiveOrder(request);
        string? nextToken = null;
        if (hasMore && effectiveOrder.Length != 0 && visible.Length != 0)
        {
            var last = visible[visible.Length - 1];
            var values = new List<QueryConstant>(effectiveOrder.Length);
            foreach (var term in effectiveOrder)
            {
                if (!last.TryGetValue(term.Column.Name, out var value))
                {
                    values.Clear();
                    break;
                }
                values.Add(QueryConstant.Of(term.Column, value));
            }
            if (values.Count == effectiveOrder.Length)
                nextToken = QueryContinuationToken.Encode(request, options, values);
        }

        var rows = visible.Select(row =>
        {
            IEnumerable<KeyValuePair<string, object?>> fields = request.Projection.AllColumns
                ? row.Where(pair => !pair.Key.StartsWith("_groundwork_null_rank_", StringComparison.Ordinal) && pair.Key != "__groundwork_total_count")
                : request.Projection.Columns
                    .Where(column => row.ContainsKey(column.Name))
                    .Select(column => new KeyValuePair<string, object?>(column.Name, row[column.Name]));
            return (IReadOnlyDictionary<string, object?>)new ReadOnlyDictionary<string, object?>(fields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
        }).ToArray();
        return new QueryMaterializedResult(rows, totalCount, nextToken, selectedIndex, indexHintApplied);
    }
}

/// <summary>Builds the internal execution request without changing the public query binding.</summary>
public static class QueryRequestExecution
{
    public static QueryRequest ForPage(QueryRequest request, QueryRenderOptions options)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (options is null) throw new ArgumentNullException(nameof(options));
        var order = options.GetEffectiveOrder(request);
        var projection = request.Projection;
        if (!projection.AllColumns)
        {
            var columns = projection.Columns.ToList();
            foreach (var term in order)
            {
                if (!columns.Any(column => string.Equals(column.Name, term.Column.Name, StringComparison.Ordinal)))
                    columns.Add(term.Column);
            }
            projection = Projection.ColumnsOnly(columns);
        }

        var paging = request.Paging;
        if (request.Paging.Limit is int limit)
        {
            var expandedLimit = checked(limit + 1);
            paging = request.Paging.ContinuationToken is { } token
                ? Paging.Continuation(token, expandedLimit)
                : request.Paging.Offset is int offset
                    ? Paging.OffsetLimit(offset, expandedLimit)
                    : Paging.Keyset(expandedLimit);
        }
        return ReferenceEquals(projection, request.Projection) && ReferenceEquals(paging, request.Paging)
            ? request
            : new QueryRequest(request.Table, request.Where, request.Order, projection, paging, request.Result, request.LatestPerKey, request.AcceptedScan)
            {
                // The extra projected tie-break fields are an execution detail, not a new
                // continuation identity. Keep the token bound to the caller's projection.
                ContinuationFingerprint = request.ContinuationFingerprint
            };
    }
}
