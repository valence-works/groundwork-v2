using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

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
        bool indexHintApplied = false,
        bool sourceIncludesRequestedOffset = true,
        bool sourceIncludesContinuation = true)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (source is null) throw new ArgumentNullException(nameof(source));

        var totalCount = request.Result.IncludesTotalCount
            ? source.FirstOrDefault(row => row.TryGetValue("__groundwork_total_count", out var count) && count is not null) is { } counted &&
              counted.TryGetValue("__groundwork_total_count", out var count) && count is not null
                ? Convert.ToInt64(count, System.Globalization.CultureInfo.InvariantCulture)
                : 0L
            : (long?)null;
        var effectiveSource = source
            .Where(row => !row.TryGetValue("__groundwork_count_only", out var marker) || Convert.ToInt64(marker ?? 0, CultureInfo.InvariantCulture) == 0)
            .ToArray();
        if (!sourceIncludesContinuation && request.Paging.ContinuationToken is { } token)
        {
            IReadOnlyList<QueryConstant> cursor;
            try
            {
                cursor = QueryContinuationToken.Decode(token, request, options);
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
            {
                throw new QueryRenderException("GW-QUERY-013", "The keyset continuation token is invalid: " + exception.Message);
            }
            var order = options.GetEffectiveOrder(request);
            effectiveSource = source.Where(row => IsAfter(row, order, cursor)).ToArray();
        }
        var offset = sourceIncludesRequestedOffset ? 0 : request.Paging.Offset ?? 0;
        var limit = request.Paging.Limit;
        var hasMore = limit is int pageSize && effectiveSource.Count() > checked(offset + pageSize);
        var visible = effectiveSource.Skip(offset).Take(limit ?? int.MaxValue).ToArray();
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
                ? row.Where(pair => !IsInternalField(pair.Key))
                : request.Projection.Columns
                    .Where(column => !IsInternalField(column.Name) && row.ContainsKey(column.Name))
                    .Select(column => new KeyValuePair<string, object?>(column.Name, row[column.Name]));
            return (IReadOnlyDictionary<string, object?>)new ReadOnlyDictionary<string, object?>(fields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
        }).ToArray();
        return new QueryMaterializedResult(rows, totalCount, nextToken, selectedIndex, indexHintApplied);
    }

    private static bool IsInternalField(string name) =>
        name.StartsWith("__groundwork_", StringComparison.Ordinal) ||
        name.StartsWith("_groundwork_", StringComparison.Ordinal);

    private static bool IsAfter(
        IReadOnlyDictionary<string, object?> row,
        IReadOnlyList<OrderTerm> order,
        IReadOnlyList<QueryConstant> cursor)
    {
        for (var index = 0; index < order.Count; index++)
        {
            var term = order[index];
            row.TryGetValue(term.Column.Name, out var actual);
            var boundary = cursor[index].Kind == QueryConstantKind.Null ? null : cursor[index].Value;
            var comparison = CompareForOrder(actual, boundary, term);
            if (comparison > 0) return true;
            if (comparison < 0) return false;
        }
        return false;
    }

    private static int CompareForOrder(object? left, object? right, OrderTerm term)
    {
        if (left is null || right is null)
            return left is null && right is null ? 0 : left is null
                ? term.NullOrder == NullOrder.First ? -1 : 1
                : term.NullOrder == NullOrder.First ? 1 : -1;
        var comparison = left is string leftText && right is string rightText
            ? string.CompareOrdinal(leftText, rightText)
            : left is DateTimeOffset leftInstant && right is DateTimeOffset rightInstant
                ? leftInstant.UtcTicks.CompareTo(rightInstant.UtcTicks)
                : left is Guid leftGuid && right is Guid rightGuid
                    ? CompareBytes(GuidBytes(leftGuid), GuidBytes(rightGuid))
                    : left is byte[] leftBytes && right is byte[] rightBytes
                        ? CompareBytes(leftBytes, rightBytes)
                        : ((IComparable)left).CompareTo(right);
        return term.Direction == OrderDirection.Descending ? -comparison : comparison;
    }

    private static byte[] GuidBytes(Guid value)
    {
        var text = value.ToString("N");
        var bytes = new byte[16];
        for (var index = 0; index < bytes.Length; index++)
            bytes[index] = byte.Parse(text.Substring(index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return bytes;
    }

    private static int CompareBytes(byte[] left, byte[] right)
    {
        for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            var comparison = left[index].CompareTo(right[index]);
            if (comparison != 0) return comparison;
        }
        return left.Length.CompareTo(right.Length);
    }
}

/// <summary>Builds the internal execution request without changing the public query binding.</summary>
public static class QueryRequestExecution
{
    public static string ScopeBindingDiscriminator(string scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
            throw new ArgumentException("A scope binding discriminator requires a non-blank scope.", nameof(scope));
        using var hash = SHA256.Create();
        return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(scope))).Replace("-", string.Empty);
    }

    /// <summary>Builds a provider execution request while preserving the caller's continuation binding.</summary>
    public static QueryRequest WithProviderPredicate(QueryRequest request, Predicate predicate, string? bindingDiscriminator = null)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (predicate is null) throw new ArgumentNullException(nameof(predicate));
        return new QueryRequest(request.Table, predicate, request.Order, request.Projection, request.Paging,
            request.Result, request.LatestPerKey, request.AcceptedScan)
        {
            CanonicalPredicate = request.CanonicalPredicate,
            ContinuationFingerprint = request.ContinuationFingerprint,
            ContinuationBindingDiscriminator = bindingDiscriminator ?? request.ContinuationBindingDiscriminator
        };
    }

    /// <summary>Builds a provider execution request with additional internal projection columns.</summary>
    public static QueryRequest WithProjection(QueryRequest request, Projection projection)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (projection is null) throw new ArgumentNullException(nameof(projection));
        return new QueryRequest(request.Table, request.Where, request.Order, projection, request.Paging,
            request.Result, request.LatestPerKey, request.AcceptedScan)
        {
            CanonicalPredicate = request.CanonicalPredicate,
            ContinuationFingerprint = request.ContinuationFingerprint,
            ContinuationBindingDiscriminator = request.ContinuationBindingDiscriminator
        };
    }

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
                CanonicalPredicate = request.CanonicalPredicate,
                ContinuationFingerprint = request.ContinuationFingerprint,
                ContinuationBindingDiscriminator = request.ContinuationBindingDiscriminator
            };
    }
}
