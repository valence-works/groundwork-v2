using Groundwork.Kernel;
using Groundwork.Query.Model;

namespace Groundwork.Substrate.Relational;

internal sealed record RelationalRenderedQuery(RelationalQueryCommand Command, ProviderBoundedQueryEvidence? Shape);

internal sealed record RelationalQueryPlanInspection(ProviderPlanEvidence Evidence, Action? Assert = null);

internal delegate ValueTask<RelationalQueryPlanInspection> RelationalQueryPlanCollector(
    RelationalQueryCommand command, QueryRenderOptions options, RelationalExecution execution, bool collectEvidence);

public abstract partial class RelationalQueryRenderer
{
    private readonly AsyncLocal<QueryShapeCollector?> executionShape = new();
    internal string ExecutionProviderName => ProviderName;

    // Opt-in requires an audit of every overridden native-emission branch. Unmapped renderers
    // cannot acquire a positive shape just because they inherit some of the standard SQL emitter.
    protected virtual bool SupportsExecutionEvidence => false;

    internal RelationalRenderedQuery RenderForExecution(QueryRequest request, QueryRenderOptions options, bool hasLookahead)
    {
        if (!SupportsExecutionEvidence)
            return new(Render(request, options), null);
        var previous = executionShape.Value;
        var collector = new QueryShapeCollector(hasLookahead);
        executionShape.Value = collector;
        try
        {
            var command = Render(request, options);
            return new(command, collector.Build());
        }
        finally
        {
            executionShape.Value = previous;
        }
    }

    /// <summary>Pairs a native column expression with its provider-owned comparison semantics.</summary>
    protected string EvidenceColumn(string native, ColumnRef column, ProviderPredicateComparison comparison)
    {
        executionShape.Value?.Column(column, comparison);
        return native;
    }

    internal void EvidenceComparisonPredicate(
        ColumnRef column,
        ProviderPredicateOperator @operator,
        ProviderPredicateBoundInclusivity inclusivity = ProviderPredicateBoundInclusivity.NotApplicable) =>
        executionShape.Value?.ComparisonPredicate(column, @operator, inclusivity);

    internal void EvidenceOrderTerm(
        OrderTerm term,
        IReadOnlyList<ProviderOrderingTransform> transforms) =>
        executionShape.Value?.Order(term, transforms);

    internal void EvidencePaging(int? offset, int? limit) =>
        executionShape.Value?.Paging(offset, limit);

    internal void EvidenceUnsupported() => executionShape.Value?.Unsupported();

    /// <summary>
    /// The physical-to-logical column map a rendered query established through its search-key
    /// mappings, in the same identity/ordinary sense the emitted-shape collector admits. Native plan
    /// mappers resolve observed physical sort columns through it; a rewritten search key that does not
    /// preserve identity is deliberately absent, so such a column stays unobserved.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> LogicalColumnsByPhysical(QueryRenderOptions options)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var mapping in options.SearchKeyColumns.Values)
        {
            var identityMapping = mapping.Policy == QuerySearchKeyPolicy.Ordinal && mapping.PreservesOrdinalIdentity;
            var ordinaryMapping = mapping.Policy == QuerySearchKeyPolicy.Ordinal &&
                string.Equals(mapping.SourceColumn, mapping.PhysicalColumn, StringComparison.Ordinal);
            if (!identityMapping && !ordinaryMapping)
                continue;
            if (map.TryGetValue(mapping.PhysicalColumn, out var existing) &&
                !string.Equals(existing, mapping.SourceColumn, StringComparison.Ordinal))
            {
                map.Remove(mapping.PhysicalColumn);
                continue;
            }
            map[mapping.PhysicalColumn] = mapping.SourceColumn;
        }
        return map;
    }

    private sealed class QueryShapeCollector(bool hasLookahead)
    {
        private bool supported = true;
        private bool began;
        private bool pagingEmitted;
        private bool allColumns;
        private readonly List<string> projection = [];
        private readonly List<ProviderPredicateFact> predicates = [];
        private readonly List<ProviderOrderTerm> ordering = [];
        private readonly Dictionary<ColumnRef, ProviderPredicateComparison> comparisons = [];
        private readonly Dictionary<string, string> logicalColumnsByPhysical = new(StringComparer.Ordinal);
        private readonly HashSet<string> withheldColumns = new(StringComparer.Ordinal);
        private ProviderNativeBound offset = ProviderNativeBound.Absent;
        private ProviderNativeBound limit = ProviderNativeBound.Absent;
        private bool continuationRequested;
        private readonly List<ProviderContinuationBranch> continuationBranches = [];
        private List<ProviderPredicateFact>? branchEqualities;
        private ProviderContinuationPredicate? continuation;

        internal void Begin(QueryRequest request, QueryRenderOptions options)
        {
            began = true;
            continuationRequested = request.Paging.ContinuationToken is not null;
            if (request.Join is not null || request.Result is not ResultShape.Rows ||
                request.Result.IncludesTotalCount || request.LatestPerKey is not null || request.Distinct ||
                request.Paging.Limit is null)
                Unsupported();
            // Only an ordinal identity (or ordinary) mapping orders and compares by the logical column's own
            // ordinal order, so only those are recorded as logical columns. A unit may declare other search
            // keys and element search keys without withholding every shape rendered against it: a query
            // that emits such a provider-owned physical column fails closed in Column and Order (#432).
            foreach (var mapping in options.ElementSearchKeyColumns.Values)
            {
                withheldColumns.Add(mapping.SourceColumn);
                withheldColumns.Add(mapping.PhysicalColumn);
            }
            foreach (var mapping in options.SearchKeyColumns.Values)
            {
                var identityMapping = mapping.Policy == QuerySearchKeyPolicy.Ordinal &&
                    mapping.PreservesOrdinalIdentity;
                var ordinaryMapping = mapping.Policy == QuerySearchKeyPolicy.Ordinal &&
                    string.Equals(mapping.SourceColumn, mapping.PhysicalColumn, StringComparison.Ordinal);
                if (!identityMapping && !ordinaryMapping)
                {
                    withheldColumns.Add(mapping.SourceColumn);
                    withheldColumns.Add(mapping.PhysicalColumn);
                    continue;
                }

                if (logicalColumnsByPhysical.TryGetValue(mapping.PhysicalColumn, out var existing) &&
                    !string.Equals(existing, mapping.SourceColumn, StringComparison.Ordinal))
                {
                    Unsupported();
                    continue;
                }
                logicalColumnsByPhysical[mapping.PhysicalColumn] = mapping.SourceColumn;
            }
            allColumns = request.Projection.AllColumns;
        }

        internal void Unsupported() => supported = false;
        internal void Column(ColumnRef column, ProviderPredicateComparison comparison)
        {
            if (withheldColumns.Contains(column.Name) ||
                (SearchKeyProjection.IsProviderOwnedColumn(column.Name) &&
                 !logicalColumnsByPhysical.ContainsKey(column.Name)))
            {
                Unsupported();
                return;
            }
            comparisons[column] = comparison;
        }

        internal void ComparisonPredicate(ColumnRef column, ProviderPredicateOperator @operator,
            ProviderPredicateBoundInclusivity inclusivity = ProviderPredicateBoundInclusivity.NotApplicable)
        {
            var comparison = Comparison(column);
            if (comparison == ProviderPredicateComparison.Unknown)
                return;
            var logicalColumn = LogicalColumn(column);
            predicates.Add(new(logicalColumn, @operator, column.Type, comparison, inclusivity,
                logicalColumn == ProviderOwnedColumns.Scope ? ProviderPredicateBindingRole.Scope : ProviderPredicateBindingRole.Caller,
                new(Guid.NewGuid())));
        }

        /// <summary>Opens the next lexicographic keyset branch; its equalities follow, then its boundary.</summary>
        internal void ContinuationBranch()
        {
            if (branchEqualities is not null || continuation is not null)
                Unsupported();
            branchEqualities = [];
        }

        internal void ContinuationEquality(ColumnRef column, bool nullCursor)
        {
            if (branchEqualities is null || ContinuationFact(column, nullCursor ? ProviderPredicateOperator.IsNull : ProviderPredicateOperator.Equal) is not { } fact)
            {
                Unsupported();
                return;
            }
            branchEqualities.Add(fact);
        }

        /// <summary>
        /// Closes the open branch with its boundary: a strict bound in the term's direction (optionally
        /// admitting null rows), a non-null test for a null cursor ordered nulls-first, or a contradiction.
        /// </summary>
        internal void ContinuationBoundary(OrderTerm term, bool nullCursor, bool admitsNull)
        {
            var @operator = nullCursor
                ? term.NullOrder == NullOrder.First ? ProviderPredicateOperator.IsNotNull : ProviderPredicateOperator.None
                : term.Direction == OrderDirection.Ascending ? ProviderPredicateOperator.LowerBound : ProviderPredicateOperator.UpperBound;
            if (branchEqualities is null || ContinuationFact(term.Column, @operator) is not { } boundary)
            {
                Unsupported();
                return;
            }
            continuationBranches.Add(new ProviderContinuationBranch(branchEqualities, boundary, admitsNull && !nullCursor));
            branchEqualities = null;
        }

        /// <summary>Records one native tuple comparison over the order terms, in order.</summary>
        internal void ContinuationTuple(IReadOnlyList<OrderTerm> order)
        {
            if (branchEqualities is not null || continuation is not null || continuationBranches.Count != 0 || order.Count < 2)
            {
                Unsupported();
                return;
            }
            var @operator = order[0].Direction == OrderDirection.Ascending ? ProviderPredicateOperator.LowerBound : ProviderPredicateOperator.UpperBound;
            var bounds = new List<ProviderPredicateFact>();
            foreach (var term in order)
            {
                if (ContinuationFact(term.Column, @operator) is not { } bound)
                {
                    Unsupported();
                    return;
                }
                bounds.Add(bound);
            }
            continuation = ProviderContinuationPredicate.Tuple(bounds);
        }

        private ProviderPredicateFact? ContinuationFact(ColumnRef column, ProviderPredicateOperator @operator)
        {
            var comparison = Comparison(column);
            if (comparison == ProviderPredicateComparison.Unknown)
                return null;
            var bindsValue = @operator is not (ProviderPredicateOperator.IsNull or ProviderPredicateOperator.IsNotNull or ProviderPredicateOperator.None);
            var inclusivity = @operator is ProviderPredicateOperator.LowerBound or ProviderPredicateOperator.UpperBound
                ? ProviderPredicateBoundInclusivity.Exclusive
                : ProviderPredicateBoundInclusivity.NotApplicable;
            return new(LogicalColumn(column), @operator, column.Type, comparison, inclusivity,
                ProviderPredicateBindingRole.Continuation, bindsValue ? new(Guid.NewGuid()) : null);
        }

        internal void Selection(ColumnRef column)
        {
            if (!allColumns)
                projection.Add(LogicalColumn(column));
        }

        internal void Order(OrderTerm term, IReadOnlyList<ProviderOrderingTransform> transforms)
        {
            if (withheldColumns.Contains(term.Column.Name))
                Unsupported();
            var comparison = Comparison(term.Column);
            var logicalColumn = LogicalColumn(term.Column);
            var transformSnapshot = transforms.ToArray();
            if (!string.Equals(logicalColumn, term.Column.Name, StringComparison.Ordinal) &&
                !transformSnapshot.Contains(ProviderOrderingTransform.PhysicalSearchKey))
            {
                transformSnapshot = transformSnapshot
                    .Append(ProviderOrderingTransform.PhysicalSearchKey)
                    .ToArray();
            }
            if (transformSnapshot.Contains(ProviderOrderingTransform.PhysicalSearchKey) &&
                string.Equals(logicalColumn, term.Column.Name, StringComparison.Ordinal))
                Unsupported();
            ordering.Add(new(logicalColumn, term.Direction, term.Column.IsNullable ? term.NullOrder : null,
                transformSnapshot, comparison));
        }

        private string LogicalColumn(ColumnRef column) =>
            logicalColumnsByPhysical.TryGetValue(column.Name, out var logical) ? logical : column.Name;

        private ProviderPredicateComparison Comparison(ColumnRef column)
        {
            if (comparisons.TryGetValue(column, out var comparison))
                return comparison;
            Unsupported();
            return ProviderPredicateComparison.Unknown;
        }

        internal void Paging() => pagingEmitted = true;
        internal void Paging(int? actualOffset, int? actualLimit)
        {
            pagingEmitted = true;
            if (actualOffset is int offset)
                this.offset = ProviderNativeBound.Explicit(offset);
            if (actualLimit is int limit)
                this.limit = ProviderNativeBound.Explicit(limit);
        }
        internal void Limit(int value) => limit = ProviderNativeBound.Explicit(value);
        internal void Offset(int value) => offset = ProviderNativeBound.Explicit(value);

        internal ProviderBoundedQueryEvidence? Build()
        {
            if (!began || !supported || !pagingEmitted || branchEqualities is not null)
                return null;
            if (continuation is null && continuationBranches.Count != 0)
                continuation = ProviderContinuationPredicate.Lexicographic(continuationBranches);
            // A page that asked for continuation but emitted no represented predicate fails closed.
            if (continuationRequested != (continuation is not null))
                return null;
            return new(
                new(predicates), ordering, new(allColumns, projection), offset, limit,
                hasContinuation: continuation is not null, hasLookahead: hasLookahead, includesTotalCount: false,
                continuation);
        }
    }
}
