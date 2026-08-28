using System.Collections.Immutable;
using Groundwork.Query.Model;

namespace Groundwork.Query.Planning;

/// <summary>
/// The one provider-neutral implementation of query index coverage. It is deliberately
/// independent of providers, ADO.NET, and runtime-specific storage libraries.
/// </summary>
public static class QueryCoverageChecker
{
    public static QueryCoverageResult Check(QueryRequest request, IEnumerable<CoverageIndex> indexes)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        if (indexes is null)
            throw new ArgumentNullException(nameof(indexes));

        var candidates = indexes.ToImmutableArray();
        if (candidates.Any(index => index is null))
            throw new ArgumentException("Index candidates cannot contain null references.", nameof(indexes));

        var constraints = ConstraintSet.Create(request.Where);
        var hasNonportableOrder = request.Order.Any(order =>
            order.NullOrder == NullOrder.ProviderDefault ||
            order.Column.Type is QueryType.Boolean or QueryType.Double or QueryType.Binary);
        if (request.Where is Predicate.AlwaysFalse && !hasNonportableOrder &&
            !(request.Result.RequiresDeterministicOrder && request.Order.Length == 0))
        {
            if (request.AcceptedScan?.Allowed == true)
                return StaleAcceptance(request, null, null);

            return new QueryCoverageResult(
                CoverageDecision.Covered,
                null,
                Array.Empty<CoverageRefusal>(),
                "The normalized predicate is always false and requires no provider read.");
        }
        var declaredKey = candidates.FirstOrDefault(index => index.IsDeclaredKey);
        // A point read answers exactly one predicate shape: a conjunction of single-value
        // equalities over every column of the declared key, which can match at most one row. It
        // does not answer a disjunction, a range, or a partial-key equality — all of which can
        // mention precisely the key's columns. Only the shape the remedy is true of loses its
        // index suggestion. Nonportable shapes lose the suggestion as well, because declaring an
        // ordered index cannot clear a GW-COVER-016 refusal; actionable coverage refusals keep it.
        var isPointRead = declaredKey is not null &&
            constraints.AreSingleValueEqualities(declaredKey.Columns.Length, declaredKey);

        var refusals = new List<Refusal>();
        foreach (var order in request.Order)
        {
            if (order.NullOrder == NullOrder.ProviderDefault)
                refusals.Add(new Refusal(
                    "GW-COVER-016",
                    "Provider-default null ordering is not portable; choose explicit nulls-first or nulls-last ordering."));
            if (order.Column.Type is QueryType.Boolean or QueryType.Double or QueryType.Binary)
                refusals.Add(new Refusal(
                    "GW-COVER-016",
                    "Ordering this type is not portable; order a declared portable projection or key instead."));
        }
        if (request.Result.RequiresDeterministicOrder && request.Order.Length == 0)
            refusals.Add(new Refusal(
                "GW-COVER-016",
                "First and FirstOrDefault queries require an explicit deterministic order; add an OrderBy term."));
        if (request.Result is ResultShape.Sum sum && !IsSumType(sum.Column.Type))
            refusals.Add(new Refusal("GW-COVER-016", "Sum requires an Int32, Int64, or Decimal reduction column."));
        if (request.Result is ResultShape.Reduction reduction &&
            (request.Result is ResultShape.Min || request.Result is ResultShape.Max) &&
            !IsOrderable(reduction.Column.Type))
            refusals.Add(new Refusal("GW-COVER-016", "Min and Max require an orderable reduction column."));
        if (constraints.HasUnsupportedRange)
            refusals.Add(new Refusal(
                "GW-COVER-016",
                "Range ordering for column '" + constraints.UnsupportedRangeColumn + "' is not portable; use equality/membership or a declared orderable projection instead."));
        if (constraints.HasCrossColumnDisjunction)
            refusals.Add(new Refusal("GW-COVER-016", "A cross-column Or is not index-covered; only a single-column Or folded to In is portable."));
        if (constraints.HasNonCoveringPredicate)
            refusals.Add(new Refusal("GW-COVER-016", "The query contains a predicate that cannot be represented by an ordered index."));
        if (request.Result.IncludesTotalCount && !constraints.HasBound && !request.Distinct)
            refusals.Add(new Refusal("GW-COVER-005", "An unbounded Count is not index-covered; full counts are scans."));
        if (request.Distinct && !constraints.HasBound)
            refusals.Add(new Refusal("GW-COVER-005", "An unbounded Distinct is not index-covered; add an equality/range predicate or explicitly accept the scan."));
        if (!constraints.HasBound && request.Order.Length == 0 && !request.Distinct)
            refusals.Add(new Refusal("GW-COVER-005", "An unfiltered query has no index bound."));

        // An index suggestion is useful only when declaring it can clear the refusal. Point reads
        // have their own direct-read remedy, and GW-COVER-016 means the shape itself is not
        // representable by an ordered index. GW-COVER-005 remains actionable with an index.
        var suggested = isPointRead || refusals.Any(refusal => refusal.Code == "GW-COVER-016")
            ? null
            : SuggestIndex(request, constraints);

        var evaluated = candidates
            .Select(index => new
            {
                Index = index,
                Score = Score(index, request, constraints),
                Failure = refusals.Any() ? null : CheckIndex(request, constraints, index)
            })
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Failure?.Priority ?? int.MaxValue)
            .ThenBy(candidate => candidate.Index.Name, StringComparer.Ordinal)
            .ToArray();
        var winner = refusals.Any() ? null : evaluated.FirstOrDefault(candidate => candidate.Failure is null);
        if (winner is not null)
        {
            if (request.AcceptedScan?.Allowed == true)
                return StaleAcceptance(request, winner.Index, suggested);

            return new QueryCoverageResult(
                CoverageDecision.Covered,
                winner.Index,
                Array.Empty<CoverageRefusal>(),
                "The query has a portable equality/range bound or a bounded fully-covered order.");
        }

        var nearestFailure = evaluated.FirstOrDefault(candidate => candidate.Failure is not null);
        var nearest = nearestFailure?.Index ?? evaluated.FirstOrDefault()?.Index;
        var failure = refusals.FirstOrDefault() ?? nearestFailure?.Failure ??
            new Refusal("GW-COVER-006", "No candidate index covers the query shape.");
        var refusal = new CoverageRefusal(
            failure.Code,
            BuildMessage(request, failure.Message, nearest, suggested, isPointRead),
            nearest,
            suggested);
        return new QueryCoverageResult(CoverageDecision.Refuse, null, [refusal], failure.Message);
    }

    private static QueryCoverageResult StaleAcceptance(
        QueryRequest request,
        CoverageIndex? index,
        CoverageIndex? suggested) =>
        new(
            CoverageDecision.Refuse,
            null,
            [new CoverageRefusal(
                "GW-COVER-901",
                "AcceptScan '" + request.AcceptedScan!.Id + "' is no longer needed because the query is index-covered.",
                index,
                suggested)],
            "The accepted scan is stale because the query is index-covered.");

    private static string BuildMessage(
        QueryRequest request,
        string reason,
        CoverageIndex? nearest,
        CoverageIndex? suggested,
        bool isPointRead)
    {
        var nearestText = nearest is null
            ? "Nearest index: <none>."
            : "Nearest index '" + nearest.Name + "' (" + Describe(nearest) + ").";
        var fix = isPointRead
            ? "The predicate pins every column of the declared key, so at most one row can match and" +
              " no index would improve on that. Read it directly instead: session.Read(key), or the" +
              " typed Records read."
            : suggested is null ? string.Empty : "Add: " + suggested.Declaration;
        return "Query on '" + request.Table.Value + "' is not index-covered. " + reason + " " +
               nearestText + (fix.Length == 0 ? string.Empty : " " + fix) +
               " Or mark the read: .AcceptScan(\"GW-SCAN-nnnn\", reason: \"reason\", owner: \"team\", expiresOn: \"yyyy-MM-dd\").";
    }

    private static Refusal? CheckIndex(QueryRequest request, ConstraintSet constraints, CoverageIndex index)
    {
        if (index.MissingValues == IndexMissingValueBehavior.Excluded &&
            index.Columns.Any(column => column.IsNullable && !constraints.ProvesNonNull(column.Column)))
        {
            return new Refusal(
                "GW-COVER-009",
                "A sparse index excludes null/missing values on an indexed column that the predicate can match.",
                Priority: 0);
        }

        foreach (var constraint in constraints.Constraints)
        {
            if (!index.Columns.Any(column => string.Equals(column.Column, constraint.Column, StringComparison.Ordinal)))
            {
                return new Refusal(
                    "GW-COVER-006",
                    "Predicate column '" + constraint.Column + "' is not present in the candidate index.",
                    Priority: 2);
            }
        }

        var prefixLength = EqualityPrefixLength(index, constraints);
        for (var position = prefixLength; position < index.Columns.Length; position++)
        {
            if (constraints.Constraints.Any(constraint =>
                    string.Equals(constraint.Column, index.Columns[position].Column, StringComparison.Ordinal)))
            {
                if (position == prefixLength &&
                    constraints.RangeColumn is not null &&
                    string.Equals(constraints.RangeColumn, index.Columns[position].Column, StringComparison.Ordinal))
                    continue;
                return new Refusal(
                    "GW-COVER-006",
                    "Predicate columns must form a compound index prefix; an indexed column is skipped before its predicate.",
                    Priority: 2);
            }
        }
        var rangeIndex = constraints.RangeColumn is null
            ? -1
            : IndexPosition(index, constraints.RangeColumn);
        if (constraints.RangeColumn is not null && rangeIndex != prefixLength)
        {
            return new Refusal(
                "GW-COVER-006",
                "The range must be the first index column after the complete equality prefix.",
                Priority: 2);
        }

        if (constraints.RangeColumn is not null && constraints.RangeCount > 1)
        {
            return new Refusal(
                "GW-COVER-006",
                "Only one range column may participate in a covered query.",
                Priority: 2);
        }

        if (request.Order.Length != 0)
        {
            var sort = ResolveSortStart(index, request.Order, constraints, prefixLength);
            if (sort is null)
            {
                return new Refusal(
                    "GW-COVER-006",
                    "Requested ordering is not a compound-index prefix, equality suffix, or range-led suffix.",
                    Priority: 1);
            }
            if (sort.SkippedEqualityCount > 0 &&
                !constraints.AreSingleValueEqualities(sort.SkippedEqualityCount, index))
            {
                return new Refusal(
                    "GW-COVER-006",
                    "An ordered compound-index suffix requires single-value equality on every skipped prefix field.",
                    Priority: 1);
            }
        }

        if (request.Distinct && !ProjectionIsCovered(request.Projection, index))
        {
            return new Refusal(
                "GW-COVER-006",
                "Distinct requires every projected column to be present in the candidate index, or an explicit accepted scan.",
                Priority: 1);
        }

        if (request.Result is ResultShape.Reduction reduction && !ColumnIsCovered(reduction.Column, index))
        {
            return new Refusal(
                "GW-COVER-006",
                "The reduction column '" + reduction.Column.Name + "' is not present in the candidate index; add it to the index or explicitly accept the scan.",
                Priority: 1);
        }

        var hasBoundedOrder = request.Order.Length != 0 &&
            ((request.Paging.Limit is int limit && limit > 0) || request.Result.MaxRows is int resultLimit && resultLimit > 0);
        if (!constraints.HasBound && !hasBoundedOrder)
        {
            return new Refusal(
                "GW-COVER-005",
                "Coverage requires an equality/range bound, or a bounded Take over a fully-covered order.",
                Priority: 3);
        }

        return null;
    }

    private static bool ProjectionIsCovered(Projection projection, CoverageIndex index) =>
        !projection.AllColumns && projection.Columns.Length != 0 &&
        projection.Columns.All(column => index.Columns.Any(indexColumn =>
            string.Equals(indexColumn.Column, column.Name, StringComparison.Ordinal)));

    private static bool ColumnIsCovered(ColumnRef column, CoverageIndex index) =>
        index.Columns.Any(indexColumn => string.Equals(indexColumn.Column, column.Name, StringComparison.Ordinal));

    private static bool IsSumType(QueryType type) => type is QueryType.Int32 or QueryType.Int64 or QueryType.Decimal;

    private static bool IsOrderable(QueryType type) => type is
        QueryType.Int32 or QueryType.Int64 or QueryType.Decimal or QueryType.String or
        QueryType.DateTimeOffset or QueryType.Guid;

    private static SortResolution? ResolveSortStart(
        CoverageIndex index,
        ImmutableArray<OrderTerm> order,
        ConstraintSet constraints,
        int equalityPrefixLength)
    {
        var paths = order.Select(term => term.Column.Name).ToArray();
        foreach (var start in new[] { 0, equalityPrefixLength })
        {
            if (start > index.Columns.Length || !NamesEqual(index.Columns, start, paths))
                continue;
            if (DirectionsMatch(index.Columns, start, order))
                return new SortResolution(start, start == 0 ? 0 : start);
        }

        if (constraints.RangeColumn is not null)
        {
            var rangeStart = IndexPosition(index, constraints.RangeColumn);
            if (rangeStart >= 0 && NamesEqual(index.Columns, rangeStart, paths) && DirectionsMatch(index.Columns, rangeStart, order))
                return new SortResolution(rangeStart, rangeStart);
        }
        return null;
    }

    private static bool NamesEqual(ImmutableArray<CoverageIndexColumn> columns, int start, IReadOnlyList<string> requested)
    {
        if (requested.Count > columns.Length - start)
            return false;
        for (var index = 0; index < requested.Count; index++)
        {
            if (!string.Equals(columns[start + index].Column, requested[index], StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static bool DirectionsMatch(ImmutableArray<CoverageIndexColumn> columns, int start, ImmutableArray<OrderTerm> requested)
    {
        if (requested.Length == 0)
            return true;
        var same = true;
        var opposite = true;
        for (var index = 0; index < requested.Length; index++)
        {
            same &= columns[start + index].Direction == requested[index].Direction;
            opposite &= columns[start + index].Direction != requested[index].Direction;
        }
        return same || opposite;
    }

    private static int EqualityPrefixLength(CoverageIndex index, ConstraintSet constraints)
    {
        var count = 0;
        while (count < index.Columns.Length && constraints.IsEquality(index.Columns[count].Column))
            count++;
        return count;
    }

    private static int IndexPosition(CoverageIndex index, string column) =>
        index.Columns.FindIndex(item => string.Equals(item.Column, column, StringComparison.Ordinal));

    private static CoverageIndex SuggestIndex(QueryRequest request, ConstraintSet constraints)
    {
        var columns = new List<CoverageIndexColumn>();
        foreach (var constraint in constraints.Constraints.Where(constraint => constraint.Kind == ConstraintKind.Equality))
            AddSuggestedColumn(columns, constraint.Column, request.Order);
        foreach (var constraint in constraints.Constraints.Where(constraint => constraint.Kind == ConstraintKind.Range))
            AddSuggestedColumn(columns, constraint.Column, request.Order);
        foreach (var column in constraints.ReferencedColumns)
            AddSuggestedColumn(columns, column, request.Order);
        foreach (var order in request.Order)
        {
            if (!columns.Any(column => string.Equals(column.Column, order.Column.Name, StringComparison.Ordinal)))
                columns.Add(new CoverageIndexColumn(order.Column.Name, order.Direction));
        }
        if (request.Distinct && !request.Projection.AllColumns)
        {
            foreach (var projection in request.Projection.Columns)
                AddSuggestedColumn(columns, projection.Name, request.Order);
        }
        if (request.Result is ResultShape.Reduction reduction)
            AddSuggestedColumn(columns, reduction.Column.Name, request.Order);
        if (columns.Count == 0)
            columns.Add(new CoverageIndexColumn("<query-bound>"));
        return new CoverageIndex("ix_" + request.Table.Value.Replace(' ', '_'), columns);
    }

    private static void AddSuggestedColumn(
        ICollection<CoverageIndexColumn> columns,
        string column,
        ImmutableArray<OrderTerm> order)
    {
        if (columns.Any(existing => string.Equals(existing.Column, column, StringComparison.Ordinal)))
            return;
        var orderIndex = order.FindIndex(term => string.Equals(term.Column.Name, column, StringComparison.Ordinal));
        columns.Add(new CoverageIndexColumn(
            column,
            orderIndex < 0 ? OrderDirection.Ascending : order[orderIndex].Direction));
    }

    private static int Score(CoverageIndex index, QueryRequest request, ConstraintSet constraints)
    {
        var score = 0;
        foreach (var constraint in constraints.Constraints)
            if (index.Columns.Any(column => string.Equals(column.Column, constraint.Column, StringComparison.Ordinal)))
                score += 10;
        for (var i = 0; i < request.Order.Length && i < index.Columns.Length; i++)
            if (string.Equals(request.Order[i].Column.Name, index.Columns[i].Column, StringComparison.Ordinal))
                score += 5;
        if (request.Result is ResultShape.Reduction reduction &&
            index.Columns.Any(column => string.Equals(column.Column, reduction.Column.Name, StringComparison.Ordinal)))
            score += 10;
        return score;
    }

    private static string Describe(CoverageIndex index) => string.Join(", ", index.Columns.Select(column => column.Column + " " + (column.Direction == OrderDirection.Ascending ? "ASC" : "DESC")));

    private sealed record SortResolution(int Start, int SkippedEqualityCount);

    private sealed record Refusal(string Code, string Message, int Priority = 10);

    private sealed class ConstraintSet
    {
        private ConstraintSet(
            ImmutableArray<Constraint> constraints,
            ImmutableArray<string> referencedColumns,
            bool crossColumn,
            bool nonCovering,
            bool unsupportedRange,
            string? unsupportedRangeColumn)
        {
            Constraints = constraints;
            ReferencedColumns = referencedColumns;
            HasCrossColumnDisjunction = crossColumn;
            HasNonCoveringPredicate = nonCovering;
            HasUnsupportedRange = unsupportedRange;
            UnsupportedRangeColumn = unsupportedRangeColumn;
            RangeColumn = constraints.FirstOrDefault(item => item.Kind == ConstraintKind.Range)?.Column;
            RangeCount = constraints.Count(item => item.Kind == ConstraintKind.Range);
        }

        public ImmutableArray<Constraint> Constraints { get; }
        public ImmutableArray<string> ReferencedColumns { get; }
        public bool HasCrossColumnDisjunction { get; }
        public bool HasNonCoveringPredicate { get; }
        public bool HasUnsupportedRange { get; }
        public string? UnsupportedRangeColumn { get; }
        public string? RangeColumn { get; }
        public int RangeCount { get; }
        public bool HasBound => Constraints.Any(item => item.Kind is ConstraintKind.Equality or ConstraintKind.Range);

        public bool IsEquality(string column) => Constraints.Any(item => item.Column == column && item.Kind == ConstraintKind.Equality);

        public bool ProvesNonNull(string column)
        {
            var constraints = Constraints.Where(item => item.Column == column).ToArray();
            if (constraints.Length == 0)
                return false;
            return constraints.All(item => item.ProvesNonNull);
        }

        public bool AreSingleValueEqualities(int count, CoverageIndex index)
        {
            for (var i = 0; i < count; i++)
            {
                var constraint = Constraints.FirstOrDefault(item => item.Column == index.Columns[i].Column);
                if (constraint is null || constraint.Kind != ConstraintKind.Equality || !constraint.SingleValue)
                    return false;
            }
            return true;
        }

        public static ConstraintSet Create(Predicate predicate)
        {
            var constraints = new List<Constraint>();
            var referencedColumns = CollectReferencedColumns(predicate).ToImmutableArray();
            var crossColumn = false;
            var nonCovering = false;
            var unsupportedRange = false;
            string? unsupportedRangeColumn = null;
            foreach (var term in Flatten(predicate))
            {
                switch (term)
                {
                    case Predicate.Equal equal:
                        constraints.Add(Constraint.Equality(equal.Column.Name, singleValue: true, equal.Value.Kind != QueryConstantKind.Null));
                        break;
                    case Predicate.In membership:
                        constraints.Add(Constraint.Equality(membership.Column.Name, membership.Values.Length == 1, membership.Values.All(value => value.Kind != QueryConstantKind.Null)));
                        break;
                    case Predicate.Range range:
                        if (!IsPortableRangeType(range.Column.Type))
                        {
                            unsupportedRange = true;
                            unsupportedRangeColumn ??= range.Column.Name;
                        }
                        constraints.Add(Constraint.Range(range.Column.Name, provesNonNull: true));
                        break;
                    case Predicate.StartsWith startsWith:
                        constraints.Add(Constraint.Range(startsWith.Column.Name, provesNonNull: true));
                        break;
                    case Predicate.Or or:
                    {
                        var leaves = or.Terms.ToArray();
                        var columns = leaves.Select(ColumnOf).Where(column => column is not null).Distinct(StringComparer.Ordinal).ToArray();
                        if (columns.Length != 1 || leaves.Any(leaf => leaf is not Predicate.Equal && leaf is not Predicate.In))
                        {
                            crossColumn |= columns.Length > 1;
                            nonCovering = true;
                        }
                        else
                        {
                            var column = columns[0]!;
                            var values = leaves.SelectMany(ValuesOf).ToArray();
                            constraints.Add(Constraint.Equality(column, values.Length == 1, values.All(value => value.Kind != QueryConstantKind.Null)));
                        }
                        break;
                    }
                    case Predicate.AlwaysTrue:
                        break;
                    case Predicate.AlwaysFalse:
                        nonCovering = true;
                        break;
                    default:
                        nonCovering = true;
                        break;
                }
            }

            var duplicateRanges = constraints.GroupBy(item => item.Column, StringComparer.Ordinal)
                .Where(group => group.Count(item => item.Kind == ConstraintKind.Range) > 1)
                .SelectMany(group => group.Where(item => item.Kind == ConstraintKind.Range))
                .ToArray();
            if (duplicateRanges.Length > 1)
                nonCovering = true;
            return new ConstraintSet(
                constraints.ToImmutableArray(),
                referencedColumns,
                crossColumn,
                nonCovering,
                unsupportedRange,
                unsupportedRangeColumn);
        }

        private static bool IsPortableRangeType(QueryType type) => type is
            QueryType.Int32 or QueryType.Int64 or QueryType.Decimal or QueryType.String or
            QueryType.DateTimeOffset or QueryType.Guid;

        private static IEnumerable<string> CollectReferencedColumns(Predicate predicate)
        {
            switch (predicate)
            {
                case Predicate.Equal equal:
                    yield return equal.Column.Name;
                    yield break;
                case Predicate.In membership:
                    yield return membership.Column.Name;
                    yield break;
                case Predicate.Range range:
                    yield return range.Column.Name;
                    yield break;
                case Predicate.StartsWith startsWith:
                    yield return startsWith.Column.Name;
                    yield break;
                case Predicate.Substring substring:
                    yield return substring.Column.Name;
                    yield break;
                case Predicate.ColumnCompare compare:
                    yield return compare.Left.Name;
                    yield return compare.Right.Name;
                    yield break;
                case Predicate.And and:
                    foreach (var column in and.Terms.SelectMany(CollectReferencedColumns))
                        yield return column;
                    yield break;
                case Predicate.Or or:
                    foreach (var column in or.Terms.SelectMany(CollectReferencedColumns))
                        yield return column;
                    yield break;
                case Predicate.Not not:
                    foreach (var column in CollectReferencedColumns(not.Inner))
                        yield return column;
                    yield break;
                case Predicate.ElementOf elementOf:
                    yield return elementOf.Set.Name;
                    yield break;
            }
        }

        private static IEnumerable<Predicate> Flatten(Predicate predicate)
        {
            if (predicate is Predicate.And and)
            {
                foreach (var term in and.Terms.SelectMany(Flatten))
                    yield return term;
                yield break;
            }
            yield return predicate;
        }

        private static string? ColumnOf(Predicate predicate) => predicate switch
        {
            Predicate.Equal equal => equal.Column.Name,
            Predicate.In membership => membership.Column.Name,
            Predicate.Range range => range.Column.Name,
            Predicate.StartsWith startsWith => startsWith.Column.Name,
            _ => null
        };

        private static IEnumerable<QueryConstant> ValuesOf(Predicate predicate) => predicate switch
        {
            Predicate.Equal equal => new[] { equal.Value },
            Predicate.In membership => membership.Values,
            _ => Array.Empty<QueryConstant>()
        };
    }

    private sealed record Constraint(string Column, ConstraintKind Kind, bool SingleValue, bool ProvesNonNull)
    {
        public static Constraint Equality(string column, bool singleValue, bool provesNonNull) => new(column, ConstraintKind.Equality, singleValue, provesNonNull);
        public static Constraint Range(string column, bool provesNonNull) => new(column, ConstraintKind.Range, false, provesNonNull);
    }

    private enum ConstraintKind
    {
        Equality,
        Range
    }
}

internal static class ImmutableArrayCoverageExtensions
{
    public static int FindIndex<T>(this ImmutableArray<T> values, Func<T, bool> predicate)
    {
        for (var index = 0; index < values.Length; index++)
            if (predicate(values[index]))
                return index;
        return -1;
    }
}
