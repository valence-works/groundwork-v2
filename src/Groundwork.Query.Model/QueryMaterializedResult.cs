using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Immutable;
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
        bool sourceIncludesContinuation = true,
        bool sourceIncludesDistinct = false)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (source is null) throw new ArgumentNullException(nameof(source));
        var executionRequest = QueryRequestExecution.ForProviderPage(request, options);
        var requireQualifiedContinuationFields = executionRequest.Join is not null;

        var effectiveSource = source
            .Where(row => !row.TryGetValue("__groundwork_count_only", out var marker) || Convert.ToInt64(marker ?? 0, CultureInfo.InvariantCulture) == 0)
            .ToArray();

        // A reduction is already a one-row provider result. Its input paging, ordering, distinct
        // and latest-per-key semantics were applied inside the native reduction command, so the
        // result row must not be paged or reduced a second time here. Keeping this branch ahead of
        // the ordinary row materializer also means a scalar result can never silently turn into
        // the first raw source row when a provider forgets to render its reduction shape.
        if (request.Result is ResultShape.Reduction)
        {
            if (effectiveSource.Length != 1)
                throw new InvalidOperationException(
                    "A native reduction must materialize exactly one provider result row.");
            var reduced = effectiveSource[0];

            // An Int32 Sum has an Int64 public result even though the source column remains
            // Int32. Providers widen the native aggregate before decoding where possible; keep
            // this final seam defensive so a provider returning a boxed Int32 cannot reintroduce
            // overflow or leak the source type through the scalar result.
            if (request.Result is ResultShape.Sum { Column.Type: QueryType.Int32 } sum &&
                reduced.TryGetValue(sum.Column.Name, out var sumValue) && sumValue is not null)
            {
                var normalized = Convert.ToInt64(sumValue, CultureInfo.InvariantCulture);
                var normalizedRow = reduced.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                normalizedRow[sum.Column.Name] = normalized;
                reduced = normalizedRow;
            }

            var fields = request.Projection.AllColumns
                ? reduced.Where(pair => !IsInternalField(pair.Key))
                : request.Projection.Columns
                    .Where(column => !IsInternalField(column.Name) && reduced.ContainsKey(column.Name))
                    .Select(column => new KeyValuePair<string, object?>(column.Name, reduced[column.Name]));
            return new QueryMaterializedResult(
                new[] { (IReadOnlyDictionary<string, object?>)new ReadOnlyDictionary<string, object?>(
                    fields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)) },
                null,
                null,
                selectedIndex,
                indexHintApplied);
        }

        if (request.Distinct && !sourceIncludesDistinct)
            effectiveSource = DistinctRows(request, effectiveSource).ToArray();

        var totalCount = request.Result.IncludesTotalCount &&
            source.FirstOrDefault(row => row.TryGetValue("__groundwork_total_count", out var value) && value is not null) is { } counted &&
            counted.TryGetValue("__groundwork_total_count", out var count) && count is not null
                ? request.Distinct && !sourceIncludesDistinct && effectiveSource.Length != 0
                    ? effectiveSource.Length
                    : Convert.ToInt64(count, CultureInfo.InvariantCulture)
                : (long?)null;
        if ((!sourceIncludesContinuation || (request.Distinct && !sourceIncludesDistinct)) && request.Paging.ContinuationToken is { } token)
        {
            IReadOnlyList<QueryConstant> cursor;
            try
            {
                cursor = QueryContinuationToken.Decode(token, executionRequest, options);
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
            {
                throw new QueryRenderException("GW-QUERY-013", "The keyset continuation token is invalid: " + exception.Message);
            }
            var order = options.GetEffectiveOrder(executionRequest);
            effectiveSource = effectiveSource
                .Where(row => IsAfter(row, order, cursor, requireQualifiedContinuationFields))
                .ToArray();
        }
        // Providers apply Distinct and its page natively and return one look-ahead row; the
        // materializer owns only the final public projection and continuation token.
        var providerAppliedDistinctWindow = request.Distinct && sourceIncludesDistinct;
        var offset = sourceIncludesRequestedOffset && (!request.Distinct || providerAppliedDistinctWindow)
            ? 0
            : request.Paging.Offset ?? 0;
        var limit = request.Result.MaxRows is int maxRows
            ? request.Paging.Limit is int requestedLimit ? Math.Min(requestedLimit, maxRows) : maxRows
            : request.Paging.Limit;
        var hasMore = limit is int pageSize && effectiveSource.Count() > checked(offset + pageSize);
        var visible = effectiveSource.Skip(offset).Take(limit ?? int.MaxValue).ToArray();
        if (request.Result is ResultShape.Single or ResultShape.SingleOrDefault && visible.Length > 1)
            throw new InvalidOperationException("Sequence contains more than one element.");
        var effectiveOrder = options.GetEffectiveOrder(executionRequest);
        string? nextToken = null;
        if (hasMore && effectiveOrder.Length != 0 && visible.Length != 0)
        {
            var last = visible[visible.Length - 1];
            var values = new List<QueryConstant>(effectiveOrder.Length);
            for (var index = 0; index < effectiveOrder.Length; index++)
            {
                var term = effectiveOrder[index];
                if (!TryGetOrderValue(
                        last,
                        effectiveOrder,
                        index,
                        requireQualifiedContinuationFields,
                        out var value))
                {
                    values.Clear();
                    break;
                }
                values.Add(QueryConstant.Of(term.Column, value));
            }
            if (values.Count == effectiveOrder.Length)
                nextToken = QueryContinuationToken.Encode(executionRequest, options, values);
        }

        var rows = visible.Select(row =>
        {
            IEnumerable<KeyValuePair<string, object?>> fields = request.Projection.AllColumns
                ? row.Where(pair => !IsInternalField(pair.Key))
                : request.Projection.Columns
                    .Select(column => (Column: column, Field: QueryRequestExecution.ResultFieldName(request, column)))
                    .Where(item => !IsInternalField(item.Field) && row.ContainsKey(item.Field))
                    .Select(item => new KeyValuePair<string, object?>(item.Field, row[item.Field]));
            return (IReadOnlyDictionary<string, object?>)new ReadOnlyDictionary<string, object?>(fields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
        }).ToArray();
        return new QueryMaterializedResult(rows, totalCount, nextToken, selectedIndex, indexHintApplied);
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> DistinctRows(
        QueryRequest request,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> source)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<IReadOnlyDictionary<string, object?>>(source.Count);
        foreach (var row in source)
        {
            var fields = request.Projection.AllColumns
                ? row.Where(pair => !IsInternalField(pair.Key)).OrderBy(pair => pair.Key, StringComparer.Ordinal)
                : request.Projection.Columns
                    .Select(column => QueryRequestExecution.ResultFieldName(request, column))
                    .Where(field => !IsInternalField(field))
                    .Select(field => new KeyValuePair<string, object?>(field, row.TryGetValue(field, out var value) ? value : null));
            var key = string.Join("|", fields.Select(pair => pair.Key + "=" + QueryStructuralIdentity.ForDistinct(pair.Value)));
            if (seen.Add(key))
                result.Add(row);
        }
        return result;
    }

    private static bool IsInternalField(string name) =>
        name.StartsWith("__groundwork_", StringComparison.Ordinal) ||
        name.StartsWith("_groundwork_", StringComparison.Ordinal);

    private static bool IsAfter(
        IReadOnlyDictionary<string, object?> row,
        IReadOnlyList<OrderTerm> order,
        IReadOnlyList<QueryConstant> cursor,
        bool requireQualifiedContinuationFields)
    {
        for (var index = 0; index < order.Count; index++)
        {
            var term = order[index];
            TryGetOrderValue(row, order, index, requireQualifiedContinuationFields, out var actual);
            var boundary = cursor[index].Kind == QueryConstantKind.Null ? null : cursor[index].Value;
            var comparison = CompareForOrder(actual, boundary, term);
            if (comparison > 0) return true;
            if (comparison < 0) return false;
        }
        return false;
    }

    private static bool TryGetOrderValue(
        IReadOnlyDictionary<string, object?> row,
        IReadOnlyList<OrderTerm> order,
        int index,
        bool requireQualifiedContinuationFields,
        out object? value)
    {
        var fieldName = QueryRequestExecution.ContinuationFieldName(index);
        if (row.TryGetValue(fieldName, out value))
            return true;

        var column = order[index].Column;
        if (requireQualifiedContinuationFields &&
            row.TryGetValue(column.Table.Value + "." + column.Name, out value))
        {
            return true;
        }
        if (requireQualifiedContinuationFields &&
            order.Count(term => ColumnRefIdentity.SameName(term.Column, column)) > 1)
        {
            throw new InvalidOperationException(
                $"A joined provider result must expose continuation field '{fieldName}' for '{column}'.");
        }
        return row.TryGetValue(column.Name, out value);
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
    /// <summary>
    /// Returns the stable public result field for a projected column. Joined fields retain their
    /// logical table qualification so same-named source and target columns cannot collide.
    /// </summary>
    public static string ResultFieldName(QueryRequest request, ColumnRef column)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (column is null) throw new ArgumentNullException(nameof(column));
        return request.Join is null ? column.Name : ResultFieldName(request.Join, column);
    }

    /// <summary>Returns the stable qualified result field for one side of a declared join.</summary>
    public static string ResultFieldName(ReferenceJoin join, ColumnRef column)
    {
        if (join is null) throw new ArgumentNullException(nameof(join));
        if (column is null) throw new ArgumentNullException(nameof(column));
        if (column.Table != join.SourceTable && column.Table != join.TargetTable)
            throw new ArgumentException("A joined result column must belong to its declared source or target table.", nameof(column));
        return column.Table.Value + "." + column.Name;
    }

    /// <summary>
    /// Returns the stable provider-result field name for one effective continuation-order value.
    /// Joined renderers use these internal aliases when qualified columns share a logical name.
    /// </summary>
    public static string ContinuationFieldName(int orderIndex)
    {
        if (orderIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(orderIndex));
        return "__groundwork_continuation_" + orderIndex.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Resolves a provider result field back to its portable column metadata. This covers public
    /// projection fields and the internal continuation aliases emitted by every joined renderer.
    /// </summary>
    public static ColumnRef? ResolveResultColumn(
        QueryRequest request,
        QueryRenderOptions options,
        string fieldName)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(fieldName))
            throw new ArgumentException("A provider result field name is required.", nameof(fieldName));

        const string continuationPrefix = "__groundwork_continuation_";
        if (fieldName.StartsWith(continuationPrefix, StringComparison.Ordinal) &&
            int.TryParse(fieldName.Substring(continuationPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            var order = options.GetEffectiveOrder(request);
            return index >= 0 && index < order.Length ? order[index].Column : null;
        }

        if (request.Result is ResultShape.Reduction reduction &&
            string.Equals(fieldName, reduction.Column.Name, StringComparison.Ordinal))
            return reduction.Column;

        return request.Projection.Columns.FirstOrDefault(column =>
            string.Equals(ResultFieldName(request, column), fieldName, StringComparison.Ordinal));
    }

    /// <summary>Bounds a direct model request for a cardinality result shape.</summary>
    public static QueryRequest ForResultShape(QueryRequest request)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (request.Result.MaxRows is not int maxRows)
            return request;
        var limit = request.Paging.Limit is int requested
            ? Math.Min(requested, maxRows)
            : maxRows;
        var paging = request.Paging.ContinuationToken is { } token
            ? Paging.Continuation(token, limit)
            : Paging.OffsetLimit(request.Paging.Offset ?? 0, limit);
        return ReferenceEquals(paging, request.Paging)
            ? request
            : new QueryRequest(request.Table, request.Where, request.Order, request.Projection, paging,
                request.Result, request.LatestPerKey, request.AcceptedScan, request.Distinct, request.Join)
            {
                CanonicalPredicate = request.CanonicalPredicate,
                ContinuationFingerprint = request.ContinuationFingerprint,
                ContinuationBindingDiscriminator = request.ContinuationBindingDiscriminator
            };
    }

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
            request.Result, request.LatestPerKey, request.AcceptedScan, request.Distinct, request.Join)
        {
            CanonicalPredicate = request.CanonicalPredicate,
            ContinuationFingerprint = request.ContinuationFingerprint,
            ContinuationBindingDiscriminator = bindingDiscriminator ?? request.ContinuationBindingDiscriminator
        };
    }

    /// <summary>
    /// Builds a provider execution request that answers a count with the provider's total-count
    /// shape. Distinct counts retain their Distinct flag unless a joined all-column projection is
    /// already unique by its source row; ordinary counts use a single-row probe.
    /// </summary>
    public static QueryRequest ForProviderCount(QueryRequest request)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        var distinct = ScalarProbeDistinct(request);
        return new QueryRequest(request.Table, request.Where, request.Order,
            ScalarProbeProjection(request),
            distinct ? Paging.None : ProbePaging(request.Paging, keepOffset: false),
            ResultShape.TotalCount.Instance, request.LatestPerKey, request.AcceptedScan, distinct, request.Join)
        {
            CanonicalPredicate = request.CanonicalPredicate,
            ContinuationFingerprint = request.ContinuationFingerprint,
            ContinuationBindingDiscriminator = request.ContinuationBindingDiscriminator
        };
    }

    /// <summary>Builds a limit-1 existence probe over the caller's page window instead of its full page.</summary>
    public static QueryRequest ForExistenceProbe(QueryRequest request)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        var distinct = ScalarProbeDistinct(request);
        return new QueryRequest(request.Table, request.Where, request.Order,
            ScalarProbeProjection(request),
            ProbePaging(request.Paging, keepOffset: true), ResultShape.Rows.Instance, request.LatestPerKey, request.AcceptedScan, distinct, request.Join)
        {
            CanonicalPredicate = request.CanonicalPredicate,
            ContinuationFingerprint = request.ContinuationFingerprint,
            ContinuationBindingDiscriminator = request.ContinuationBindingDiscriminator
        };
    }

    /// <summary>Returns the provider-side total count or refuses; a page is never counted client-side.</summary>
    public static long RequireTotalCount(QueryRequest request, long? totalCount)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        return totalCount ?? throw new InvalidOperationException(
            $"Query on '{request.Table.Value}' returned no provider-side total count; a materialized page is never counted client-side.");
    }

    /// <summary>Bounds a page to one row while keeping the caller's continuation window.</summary>
    private static Paging ProbePaging(Paging paging, bool keepOffset) =>
        paging.ContinuationToken is { } token
            ? Paging.Continuation(token, 1)
            : Paging.OffsetLimit(keepOffset ? paging.Offset ?? 0 : 0, 1);

    private static Projection ScalarProbeProjection(QueryRequest request)
    {
        if (request.Join is null ||
            !request.Projection.AllColumns)
        {
            return request.Projection;
        }

        return Projection.ColumnsOnly(request.Join.ColumnPairs[0].Source);
    }

    private static bool ScalarProbeDistinct(QueryRequest request)
    {
        // A declared reference resolves at most one target row per source row, and Projection.All
        // includes the source key. Every joined row is therefore already unique. Removing Distinct
        // lets the scalar probe project one qualified source column without changing its answer.
        return request.Distinct && (request.Join is null || !request.Projection.AllColumns);
    }

    /// <summary>Builds a provider execution request with additional internal projection columns.</summary>
    public static QueryRequest WithProjection(QueryRequest request, Projection projection)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (projection is null) throw new ArgumentNullException(nameof(projection));
        return new QueryRequest(request.Table, request.Where, request.Order, projection, request.Paging,
            request.Result, request.LatestPerKey, request.AcceptedScan, request.Distinct, request.Join)
        {
            CanonicalPredicate = request.CanonicalPredicate,
            ContinuationFingerprint = request.ContinuationFingerprint,
            ContinuationBindingDiscriminator = request.ContinuationBindingDiscriminator
        };
    }

    /// <summary>
    /// Builds the provider page request. Native providers apply Distinct before the page window;
    /// the extra row remains the look-ahead used to issue a continuation token.
    /// </summary>
    public static QueryRequest ForProviderPage(QueryRequest request, QueryRenderOptions options)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (options is null) throw new ArgumentNullException(nameof(options));
        request = QueryElementSearchKeyRewriter.Rewrite(
            QuerySearchKeyRewriter.Rewrite(request, options.SearchKeyColumns),
            options.ElementSearchKeyColumns);

        // Reduction commands apply their input window before the provider-side aggregate. Adding
        // the ordinary page look-ahead here would change Sum/Min/Max semantics (Take(n) would
        // become Take(n+1)), while a client-side re-page would aggregate the wrong input.
        if (request.Result is ResultShape.Reduction)
            return request;

        var order = options.GetEffectiveOrder(request);
        // A columns-only DISTINCT result is ordered by its complete projected tuple. Keep that
        // effective order while replacing any marked source terms with their hidden identities;
        // starting from request.Order would drop projected tie-breaks when the caller supplied a
        // partial order.
        var executionOrder = request.Distinct && !request.Projection.AllColumns
            ? order
            : request.Order;
        var projection = request.Projection;
        if (!projection.AllColumns)
        {
            var columns = projection.Columns.ToList();
            if (request.Distinct)
            {
                // An explicitly persisted ordinal identity is an execution detail. Synthesize its
                // physical projection and order term here so callers continue to request only the
                // logical tuple while native DISTINCT can use the injective physical key.
                foreach (var mapping in options.SearchKeyColumns.Values.Where(mapping =>
                             mapping.PreservesOrdinalIdentity &&
                             mapping.OrderByPhysicalColumn &&
                             !string.Equals(mapping.SourceColumn, mapping.PhysicalColumn, StringComparison.Ordinal)))
                {
                    var source = projection.Columns.FirstOrDefault(column =>
                        column.Type == QueryType.String &&
                        !column.IsNullable &&
                        column.StringComparison == QueryStringComparisonPolicy.Ordinal &&
                        string.Equals(column.Name, mapping.SourceColumn, StringComparison.Ordinal));
                    if (source is null)
                        continue;

                    var physical = new ColumnRef(
                        request.Table,
                        mapping.PhysicalColumn,
                        QueryType.String,
                        isNullable: false,
                        mapping.MaxLength);
                    if (!columns.Any(column => string.Equals(column.Name, physical.Name, StringComparison.Ordinal)))
                        columns.Add(physical);

                    if (!executionOrder.Any(term => string.Equals(term.Column.Name, physical.Name, StringComparison.Ordinal)))
                    {
                        executionOrder = (executionOrder.Length == 0 ? order : executionOrder)
                            .Select(term => string.Equals(term.Column.Name, source.Name, StringComparison.Ordinal)
                                ? new OrderTerm(physical, term.Direction, term.NullOrder)
                                : term)
                            .ToImmutableArray();
                    }
                }
            }
            foreach (var term in order.Where(term => !request.Distinct ||
                         options.SearchKeyColumns.Values.Any(mapping =>
                             !string.Equals(mapping.SourceColumn, mapping.PhysicalColumn, StringComparison.Ordinal) &&
                             string.Equals(mapping.PhysicalColumn, term.Column.Name, StringComparison.Ordinal))))
            {
                if (!columns.Any(column =>
                        request.Join is null
                            ? ColumnRefIdentity.SameName(column, term.Column)
                            : ColumnRefIdentity.SameQualifiedColumn(column, term.Column)))
                    columns.Add(term.Column);
            }
            projection = Projection.ColumnsOnly(columns);
        }

        var paging = request.Paging;
        if (request.Paging.Limit is int limit)
        {
            var expandedLimit = request.Result.MaxRows is int maxRows
                ? Math.Min(limit, maxRows)
                : limit == int.MaxValue ? limit : limit + 1;
            paging = request.Paging.ContinuationToken is { } token
                ? Paging.Continuation(token, expandedLimit)
                : request.Paging.Offset is int offset
                    ? Paging.OffsetLimit(offset, expandedLimit)
                    : Paging.Keyset(expandedLimit);
        }
        else if (request.Result.MaxRows is int maxRows)
        {
            paging = request.Paging.ContinuationToken is { } token
                ? Paging.Continuation(token, maxRows)
                : Paging.OffsetLimit(request.Paging.Offset ?? 0, maxRows);
        }
        return ReferenceEquals(projection, request.Projection) && ReferenceEquals(paging, request.Paging) &&
               executionOrder.Equals(request.Order)
            ? request
            : new QueryRequest(request.Table, request.Where, executionOrder, projection, paging, request.Result, request.LatestPerKey, request.AcceptedScan, request.Distinct, request.Join)
            {
                // The extra projected tie-break fields are an execution detail, not a new
                // continuation identity. Keep the token bound to the caller's projection.
                CanonicalPredicate = request.CanonicalPredicate,
                ContinuationFingerprint = request.ContinuationFingerprint,
                ContinuationBindingDiscriminator = request.ContinuationBindingDiscriminator
            };
    }

    public static QueryRequest ForPage(QueryRequest request, QueryRenderOptions options) =>
        ForProviderPage(request, options);
}
