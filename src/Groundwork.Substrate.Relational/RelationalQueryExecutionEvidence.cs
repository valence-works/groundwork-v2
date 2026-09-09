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

        internal void Begin(QueryRequest request, QueryRenderOptions options)
        {
            began = true;
            if (request.Join is not null || request.Result is not ResultShape.Rows ||
                request.Result.IncludesTotalCount || request.LatestPerKey is not null || request.Distinct ||
                request.Paging.ContinuationToken is not null || request.Paging.Limit is null)
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

        internal ProviderBoundedQueryEvidence? Build() => !began || !supported || !pagingEmitted ? null : new(
            new(predicates), ordering, new(allColumns, projection), offset, limit,
            hasContinuation: false, hasLookahead: hasLookahead, includesTotalCount: false);
    }
}
