using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Groundwork.Kernel;
using Groundwork.Query.Model;

namespace Groundwork.Store;

/// <summary>One query row paired with the provider-owned scope that contained it.</summary>
public sealed class CrossScopeQueryRow
{
    public CrossScopeQueryRow(StorageScope scope, IReadOnlyDictionary<string, object?> values)
    {
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        ArgumentNullException.ThrowIfNull(values);
        Values = new ReadOnlyDictionary<string, object?>(values.ToDictionary(
            pair => pair.Key,
            pair => StorageValues.CloneValue(pair.Value),
            StringComparer.Ordinal));
    }

    public StorageScope Scope { get; }

    public IReadOnlyDictionary<string, object?> Values { get; }
}

/// <summary>A scope-preserving materialized result from a privileged query.</summary>
public sealed class CrossScopeQueryResult
{
    public CrossScopeQueryResult(
        IReadOnlyList<CrossScopeQueryRow> rows,
        long? totalCount,
        string? nextContinuationToken,
        string? selectedIndex = null,
        bool indexHintApplied = false)
    {
        ArgumentNullException.ThrowIfNull(rows);
        Rows = Array.AsReadOnly(rows.Select(row => row is null
            ? throw new ArgumentException("Cross-scope query rows cannot contain null references.", nameof(rows))
            : new CrossScopeQueryRow(row.Scope, row.Values)).ToArray());
        TotalCount = totalCount;
        NextContinuationToken = nextContinuationToken;
        SelectedIndex = selectedIndex;
        IndexHintApplied = indexHintApplied;
    }

    public IReadOnlyList<CrossScopeQueryRow> Rows { get; }

    public long? TotalCount { get; }

    public string? NextContinuationToken { get; }

    public string? SelectedIndex { get; }

    public bool IndexHintApplied { get; }
}

/// <summary>Shared materialization rules used by provider cross-scope query implementations.</summary>
public static class CrossScopeQueryMaterializer
{
    public const string RawScopeColumn = "__groundwork_scope";
    public const string ScopeTokenColumn = "__groundwork_scope_token";

    public static string BindingDiscriminator(StorageAccess access)
    {
        ArgumentNullException.ThrowIfNull(access);
        var audit = access.Audit ?? throw new InvalidOperationException(
            "Privileged cross-scope access requires audit metadata.");
        return Hash(LengthPrefix(audit.Identity) + LengthPrefix(audit.Purpose));
    }

    public static string ScopeToken(StorageScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return Hash(scope.Value);
    }

    public static CrossScopeQueryResult Materialize(
        QueryRequest request,
        QueryRenderOptions options,
        IReadOnlyList<CrossScopeQueryRow> source,
        string? selectedIndex = null,
        bool indexHintApplied = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(source);

        var rows = source.Select(row =>
        {
            var values = row.Values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            values[ScopeTokenColumn] = ScopeToken(row.Scope);
            return new SourceRow(row.Scope, values);
        }).ToArray();
        if (request.Distinct)
            rows = DistinctRows(request, rows);
        var totalCount = request.Result.IncludesTotalCount ? rows.LongLength : (long?)null;

        if (request.Paging.ContinuationToken is { } token)
        {
            IReadOnlyList<QueryConstant> cursor;
            try
            {
                cursor = QueryContinuationToken.Decode(token, request, options);
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
            {
                throw new QueryRenderException(
                    "GW-QUERY-013",
                    "The keyset continuation token is invalid: " + exception.Message);
            }

            var order = options.GetEffectiveOrder(request);
            rows = rows.Where(row => IsAfter(row.Values, order, cursor)).ToArray();
        }

        var offset = request.Paging.Offset ?? 0;
        var limit = request.Result.MaxRows is int maxRows
            ? request.Paging.Limit is int requestedLimit
                ? Math.Min(requestedLimit, maxRows)
                : maxRows
            : request.Paging.Limit;
        var hasMore = limit is int pageSize && rows.Length > checked(offset + pageSize);
        var visible = rows.Skip(offset).Take(limit ?? int.MaxValue).ToArray();
        if (request.Result is ResultShape.Single or ResultShape.SingleOrDefault && visible.Length > 1)
            throw new InvalidOperationException("Sequence contains more than one element.");
        var orderTerms = options.GetEffectiveOrder(request);
        string? nextToken = null;
        if (hasMore && orderTerms.Length != 0 && visible.Length != 0)
        {
            var last = visible[^1].Values;
            var values = orderTerms.Select(term =>
            {
                last.TryGetValue(term.Column.Name, out var value);
                return QueryConstant.Of(term.Column, value);
            }).ToArray();
            nextToken = QueryContinuationToken.Encode(request, options, values);
        }

        var resultRows = visible.Select(row => new CrossScopeQueryRow(
            row.Scope,
            Project(request, row.Values))).ToArray();
        return new CrossScopeQueryResult(
            resultRows,
            totalCount,
            nextToken,
            selectedIndex,
            indexHintApplied);
    }

    /// <summary>
    /// Pairs a provider-native page with the internal scope projection used to produce it. For a
    /// distinct request, matching is performed after projected-value de-duplication so a scope
    /// cannot drift when duplicate raw rows precede the visible row.
    /// </summary>
    public static CrossScopeQueryResult FromNativePage(
        QueryMaterializedResult materialized,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> nativeRows,
        string scopeColumn)
    {
        ArgumentNullException.ThrowIfNull(materialized);
        ArgumentNullException.ThrowIfNull(nativeRows);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeColumn);
        var pageRows = nativeRows
            .Where(row => !row.TryGetValue("__groundwork_count_only", out var marker) ||
                          Convert.ToInt64(marker ?? 0, CultureInfo.InvariantCulture) == 0)
            .ToArray();
        var used = new HashSet<int>();
        pageRows = materialized.Rows.Select(resultRow =>
        {
            for (var index = 0; index < pageRows.Length; index++)
                if (!used.Contains(index) && SameMaterializedRow(resultRow, pageRows[index]))
                {
                    used.Add(index);
                    return pageRows[index];
                }
            return null;
        }).Where(row => row is not null).Select(row => row!).ToArray();
        if (pageRows.Length != materialized.Rows.Count)
            throw new InvalidOperationException("The native cross-scope page did not retain one scope for every materialized row.");

        var rows = materialized.Rows.Select((row, index) =>
        {
            if (!pageRows[index].TryGetValue(scopeColumn, out var scopeValue) || scopeValue is not string scope)
                throw new InvalidOperationException("The native cross-scope page omitted its provider scope projection.");
            return new CrossScopeQueryRow(new StorageScope(scope), row);
        }).ToArray();
        return new CrossScopeQueryResult(
            rows,
            materialized.TotalCount,
            materialized.NextContinuationToken,
            materialized.SelectedIndex,
            materialized.IndexHintApplied);
    }

    private static SourceRow[] DistinctRows(QueryRequest request, IReadOnlyList<SourceRow> source)
    {
        var result = new List<SourceRow>(source.Count);
        foreach (var row in source)
        {
            if (!result.Any(existing => SameProjection(request, existing.Values, row.Values)))
                result.Add(row);
        }
        return result.ToArray();
    }

    private static bool SameMaterializedRow(
        IReadOnlyDictionary<string, object?> materialized,
        IReadOnlyDictionary<string, object?> native)
    {
        foreach (var (column, value) in materialized)
        {
            native.TryGetValue(column, out var nativeValue);
            if (!SameValue(value, nativeValue))
                return false;
        }
        return true;
    }

    private static bool SameProjection(
        QueryRequest request,
        IReadOnlyDictionary<string, object?> left,
        IReadOnlyDictionary<string, object?> right)
    {
        var columns = request.Projection.AllColumns
            ? left.Keys.Where(name => !IsInternalField(name)).Union(
                right.Keys.Where(name => !IsInternalField(name)), StringComparer.Ordinal)
            : request.Projection.Columns.Where(column => !IsInternalField(column.Name)).Select(column => column.Name);
        foreach (var column in columns)
        {
            left.TryGetValue(column, out var leftValue);
            right.TryGetValue(column, out var rightValue);
            if (!SameValue(leftValue, rightValue))
                return false;
        }
        return true;
    }

    private static bool SameValue(object? left, object? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;
        if (left is byte[] leftBytes && right is byte[] rightBytes)
            return leftBytes.SequenceEqual(rightBytes);
        if (left is IReadOnlyDictionary<string, object?> leftDictionary && right is IReadOnlyDictionary<string, object?> rightDictionary)
            return leftDictionary.Count == rightDictionary.Count && leftDictionary.All(pair =>
                rightDictionary.TryGetValue(pair.Key, out var value) && SameValue(pair.Value, value));
        if (left is IEnumerable leftSequence && right is IEnumerable rightSequence && left is not string && right is not string)
            return leftSequence.Cast<object?>().SequenceEqual(rightSequence.Cast<object?>(), new StructuralValueComparer());
        return Equals(left, right);
    }

    private sealed class StructuralValueComparer : IEqualityComparer<object?>
    {
        public new bool Equals(object? left, object? right) => SameValue(left, right);
        public int GetHashCode(object? value) => value?.GetHashCode() ?? 0;
    }

    private static IReadOnlyDictionary<string, object?> Project(
        QueryRequest request,
        IReadOnlyDictionary<string, object?> row)
    {
        IEnumerable<KeyValuePair<string, object?>> fields = request.Projection.AllColumns
            ? row.Where(pair => !IsInternalField(pair.Key))
            : request.Projection.Columns
                .Where(column => !IsInternalField(column.Name) && row.ContainsKey(column.Name))
                .Select(column => new KeyValuePair<string, object?>(column.Name, row[column.Name]));
        return new ReadOnlyDictionary<string, object?>(fields.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal));
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
            var comparison = Compare(actual, boundary, term);
            if (comparison > 0) return true;
            if (comparison < 0) return false;
        }
        return false;
    }

    private static int Compare(object? left, object? right, OrderTerm term)
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

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string LengthPrefix(string value) =>
        value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value;

    private sealed record SourceRow(
        StorageScope Scope,
        IReadOnlyDictionary<string, object?> Values);
}
