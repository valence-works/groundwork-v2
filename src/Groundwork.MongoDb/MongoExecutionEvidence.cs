using System.Runtime.ExceptionServices;
using Groundwork.Kernel;
using Groundwork.Diagnostics;
using Groundwork.Query.Model;
using MongoDB.Bson;
using MongoDB.Bson.IO;

namespace Groundwork.MongoDb;

/// <summary>Internal lifecycle guard shared by native and provider-neutral Mongo owners.</summary>
internal interface IMongoStructuredEvidenceOwner
{
    void ThrowIfStructuredObserverReentry();
}

/// <summary>
/// Tracks structured-evidence owners without extending their lifetime through the provider
/// connection. The registry is allocated only when a structured observer is attached.
/// </summary>
internal sealed class MongoStructuredEvidenceOwnerRegistry
{
    private readonly object gate = new();
    private readonly List<WeakReference<IMongoStructuredEvidenceOwner>> owners = [];

    internal int EntryCount
    {
        get
        {
            lock (gate)
                return owners.Count;
        }
    }

    internal void RegisterIfStructured(
        IProviderCommandObserver? observer,
        IMongoStructuredEvidenceOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (observer is not IProviderExecutionObserver)
            return;

        lock (gate)
            owners.Add(new WeakReference<IMongoStructuredEvidenceOwner>(owner));
    }

    internal void EnsureNotReentered()
    {
        lock (gate)
        {
            for (var index = owners.Count - 1; index >= 0; index--)
            {
                if (!owners[index].TryGetTarget(out var owner))
                {
                    owners.RemoveAt(index);
                    continue;
                }

                owner.ThrowIfStructuredObserverReentry();
            }
        }
    }

    // Deterministic test seam for pruning without relying on GC scheduling.
    internal void AddExpiredEntryForTest()
    {
        lock (gate)
            owners.Add(new WeakReference<IMongoStructuredEvidenceOwner>(null!));
    }
}

/// <summary>
/// Owns the capture-local identities and terminal callback for one MongoDB storage session.
/// Provider code calls this only around a command that it is about to issue; rendering alone
/// never publishes an observation.
/// </summary>
internal sealed class MongoExecutionEvidenceCapture
{
    private readonly IProviderExecutionObserver observer;
    private readonly ProviderExecutionEvidenceOptions options;
    private readonly Dictionary<string, ProviderOpaqueIdentity> indexIdentities = new(StringComparer.Ordinal);
    private int callbackDepth;

    internal MongoExecutionEvidenceCapture(
        IProviderExecutionObserver observer,
        StorageUnit unit,
        MongoStorageAccess access)
    {
        this.observer = observer ?? throw new ArgumentNullException(nameof(observer));
        Unit = unit ?? throw new ArgumentNullException(nameof(unit));
        Access = access ?? throw new ArgumentNullException(nameof(access));
        CaptureId = NewIdentity();
        callbackDepth++;
        try
        {
            // Snapshot capability before any command can be issued. This is deliberately
            // callback-guarded: a capability getter must not re-enter an owner while the
            // capture is being established.
            options = observer.EvidenceOptions ?? throw new InvalidOperationException(
                "A structured execution observer must return non-null evidence options.");
        }
        finally
        {
            callbackDepth--;
        }
        Target = new ProviderExecutionTarget(
            unit.Id,
            NewIdentity(),
            access.IsPrivilegedAcrossScopes
                ? ProviderScopeBindingMode.PrivilegedAcrossScopes
                : access.Policy == ScopePolicy.Scoped
                    ? ProviderScopeBindingMode.PhysicalTarget
                    : ProviderScopeBindingMode.Unscoped);
    }

    internal StorageUnit Unit { get; }

    internal MongoStorageAccess Access { get; }

    internal ProviderOpaqueIdentity CaptureId { get; }

    internal ProviderExecutionTarget Target { get; }

    internal ProviderExecutionEvidenceOptions Options => options;

    internal ProviderOpaqueIdentity NewIdentity() => new(Guid.NewGuid());

    internal ProviderOpaqueIdentity GetIndexIdentity(string physicalIndexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalIndexName);
        lock (indexIdentities)
            return indexIdentities.TryGetValue(physicalIndexName, out var identity)
                ? identity
                : indexIdentities[physicalIndexName] = NewIdentity();
    }

    internal MongoExecutionEvidenceInvocation BeginInvocation() =>
        new(this, NewIdentity());

    internal void ThrowIfCallbackReentry()
    {
        if (callbackDepth != 0)
            throw new InvalidOperationException(
                "A MongoDB structured execution observer cannot re-enter or dispose its owning session while the terminal callback is running.");
    }

    internal void Publish(
        MongoExecutionEvidenceInvocation invocation,
        int commandOrdinal,
        ProviderExecutionOperation operation,
        ProviderExecutionRole role,
        ProviderCommandKind commandKind,
        ProviderExecutionOutcome outcome,
        ProviderExecutionFailureCategory? failureCategory,
        ProviderBoundedQueryEvidence? boundedQuery,
        ProviderPointReadEvidence? pointRead,
        ProviderPlanEvidence? plan = null)
    {
        var shapeAvailability = boundedQuery is not null || pointRead is not null
            ? ProviderEvidenceAvailability.Collected
            : ProviderEvidenceAvailability.Unsupported;
        var effectivePlan = plan ?? (Options.CollectNativePlans
            ? new ProviderPlanEvidence(ProviderEvidenceAvailability.Unsupported)
            : ProviderPlanEvidence.NotRequested);
        var identity = new ProviderExecutionIdentity(
            CaptureId,
            invocation.InvocationId,
            NewIdentity(),
            NewIdentity(),
            commandOrdinal,
            statementOrdinal: 0);
        var evidence = new ProviderExecutionEvidence(
            MongoSchemaTargets.Provider,
            operation,
            commandKind,
            role,
            identity,
            Target,
            outcome,
            failureCategory,
            shapeAvailability,
            boundedQuery,
            pointRead,
            effectivePlan);

        callbackDepth++;
        try
        {
            observer.ObserveExecution(evidence);
        }
        finally
        {
            callbackDepth--;
        }
    }
}

/// <summary>Builds the explain command from the already emitted native Mongo command.</summary>
internal static class MongoNativeExplainCommand
{
    internal static BsonDocument Build(
        MongoQueryCommand query,
        string collectionName,
        string verbosity = "executionStats")
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(verbosity);

        var native = query.Pipeline.Length == 0
            ? new BsonDocument
            {
                { "find", collectionName },
                { "filter", query.Filter },
                { "sort", query.Sort, query.Sort.ElementCount != 0 },
                { "projection", query.Projection, query.Projection.ElementCount != 0 },
                { "skip", query.Skip.GetValueOrDefault(), query.Skip.HasValue },
                { "limit", query.Limit.GetValueOrDefault(), query.Limit.HasValue },
                { "hint", query.Hint ?? string.Empty, query.Hint is not null }
            }
            : new BsonDocument
            {
                { "aggregate", collectionName },
                { "pipeline", new BsonArray(query.Pipeline) },
                { "cursor", new BsonDocument() },
                { "hint", query.Hint ?? string.Empty, query.Hint is not null }
            };
        return new BsonDocument
        {
            { "explain", native },
            { "verbosity", verbosity }
        };
    }
}

/// <summary>Result of one optional executionStats explain replay, kept private to the provider.</summary>
internal sealed class MongoNativePlanCollectionResult
{
    private readonly string? logicalIndex;
    private readonly string? physicalIndex;
    private readonly bool hinted;
    private readonly string? rawPlan;
    private readonly bool chosen;
    private readonly ExceptionDispatchInfo? pendingFailure;

    private MongoNativePlanCollectionResult(
        ProviderPlanEvidence evidence,
        string? logicalIndex,
        string? physicalIndex,
        bool hinted,
        string? rawPlan,
        bool chosen,
        ExceptionDispatchInfo? pendingFailure)
    {
        Evidence = evidence;
        this.logicalIndex = logicalIndex;
        this.physicalIndex = physicalIndex;
        this.hinted = hinted;
        this.rawPlan = rawPlan;
        this.chosen = chosen;
        this.pendingFailure = pendingFailure;
    }

    internal ProviderPlanEvidence Evidence { get; }

    internal static MongoNativePlanCollectionResult NotRequested { get; } =
        new(ProviderPlanEvidence.NotRequested, null, null, false, null, false, null);

    internal static MongoNativePlanCollectionResult Unsupported { get; } =
        new(new ProviderPlanEvidence(ProviderEvidenceAvailability.Unsupported), null, null, false, null, false, null);

    internal static MongoNativePlanCollectionResult Failed(Exception failure, bool legacyAssertionRequested)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new(
            new ProviderPlanEvidence(
                ProviderEvidenceAvailability.Failed,
                failureCategory: ProviderExecutionFailureCategory.PlanCollection,
                collectionCommandCount: 1),
            null,
            null,
            false,
            null,
            false,
            ExceptionDispatchInfo.Capture(failure));
    }

    internal static MongoNativePlanCollectionResult LegacyFailure(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new(
            ProviderPlanEvidence.NotRequested,
            null,
            null,
            false,
            null,
            false,
            ExceptionDispatchInfo.Capture(failure));
    }

    internal static MongoNativePlanCollectionResult LegacyOnly(
        string logicalIndex,
        string physicalIndex,
        bool hinted,
        string rawPlan,
        bool chosen)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalIndex);
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalIndex);
        ArgumentNullException.ThrowIfNull(rawPlan);
        return new(
            ProviderPlanEvidence.NotRequested,
            logicalIndex,
            physicalIndex,
            hinted,
            rawPlan,
            chosen,
            null);
    }

    internal static MongoNativePlanCollectionResult Collected(
        ProviderPlanForest forest,
        MongoExecutionEvidenceCapture capture,
        string? logicalIndex,
        string? physicalIndex,
        bool hinted,
        string rawPlan,
        bool chosen,
        bool legacyAssertionRequested,
        bool legacyChosen)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(forest);
        ArgumentNullException.ThrowIfNull(rawPlan);
        return new(
            new ProviderPlanEvidence(
                ProviderEvidenceAvailability.Collected,
                ProviderPlanProvenance.ExplainReplay,
                choseExpectedIndex: logicalIndex is null ? null : chosen,
                expectedLogicalIndex: logicalIndex,
                chosenPhysicalIndexId: chosen && physicalIndex is not null
                    ? capture.GetIndexIdentity(physicalIndex)
                    : null,
                collectionCommandCount: 1,
                winningPlan: forest),
            legacyAssertionRequested ? logicalIndex : null,
            legacyAssertionRequested ? physicalIndex : null,
            legacyAssertionRequested && hinted,
            legacyAssertionRequested ? rawPlan : null,
            legacyAssertionRequested && legacyChosen,
            null);
    }

    internal static MongoNativePlanCollectionResult UnsupportedFromExplain(
        string? logicalIndex,
        string? physicalIndex,
        bool hinted,
        string rawPlan,
        bool legacyChosen,
        bool legacyAssertionRequested)
    {
        ArgumentNullException.ThrowIfNull(rawPlan);
        return new(
            new ProviderPlanEvidence(
                ProviderEvidenceAvailability.Unsupported,
                collectionCommandCount: 1),
            legacyAssertionRequested ? logicalIndex : null,
            legacyAssertionRequested ? physicalIndex : null,
            legacyAssertionRequested && hinted,
            legacyAssertionRequested ? rawPlan : null,
            legacyAssertionRequested && legacyChosen,
            null);
    }

    internal static MongoNativePlanCollectionResult FromExplain(
        BsonDocument explain,
        MongoExecutionEvidenceCapture capture,
        MongoQueryCommand query,
        string expectedNamespace,
        QueryRenderOptions options,
        bool legacyAssertionRequested)
    {
        ArgumentNullException.ThrowIfNull(explain);
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedNamespace);
        ArgumentNullException.ThrowIfNull(options);

        var logicalIndex = query.ExpectedIndex;
        var physicalIndex = logicalIndex is null ? null : options.ResolvePhysicalIndexName(logicalIndex);
        var legacyChosen = physicalIndex is not null &&
            MongoExplainPlanInspector.ChoseIndex(explain, physicalIndex);
        var rawPlan = explain.ToJson(new JsonWriterSettings { Indent = true });
        ProviderPlanForest? forest = null;
        if (TryBuildLogicalIndexesByPhysicalName(options, out var logicalIndexesByPhysicalName))
        {
            try
            {
                forest = MongoNativePlanMapper.Map(
                    explain,
                    expectedNamespace,
                    capture.Target.PhysicalTargetId,
                    capture.GetIndexIdentity,
                    logicalIndexesByPhysicalName,
                    LogicalColumnsByPhysical(options));
            }
            catch (Exception)
            {
                // Native explain grammar that the mapper cannot close is Unsupported. The
                // command itself succeeded, so this is not a provider failure.
            }
        }

        return forest is not null
            ? Collected(
                forest,
                capture,
                logicalIndex,
                physicalIndex,
                query.Hint is not null,
                rawPlan,
                physicalIndex is not null &&
                    forest.Nodes.Any(node =>
                        node.IndexId is { } indexId &&
                        indexId == capture.GetIndexIdentity(physicalIndex)),
                legacyAssertionRequested,
                legacyChosen)
            : UnsupportedFromExplain(
                logicalIndex,
                physicalIndex,
                query.Hint is not null,
                rawPlan,
                legacyChosen,
                legacyAssertionRequested);
    }

    /// <summary>
    /// The physical-to-logical field map the rendered query established through identity-preserving
    /// search-key mappings; a rewritten key that does not preserve identity stays unmapped so the plan
    /// mapper leaves such a sort field unobserved.
    /// </summary>
    private static IReadOnlyDictionary<string, string> LogicalColumnsByPhysical(QueryRenderOptions options)
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

    private static bool TryBuildLogicalIndexesByPhysicalName(
        QueryRenderOptions options,
        out IReadOnlyDictionary<string, string> logicalIndexesByPhysicalName)
    {
        var reverse = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var index in options.Indexes)
        {
            var physical = options.ResolvePhysicalIndexName(index.Name);
            if (reverse.TryGetValue(physical, out var existing) &&
                !string.Equals(existing, index.Name, StringComparison.Ordinal))
            {
                // A physical index mapped from more than one logical declaration cannot be
                // attributed safely. Withhold logical names from the provider-owned mapper.
                logicalIndexesByPhysicalName = new Dictionary<string, string>(StringComparer.Ordinal);
                return false;
            }
            reverse[physical] = index.Name;
        }
        logicalIndexesByPhysicalName = reverse;
        return true;
    }

    internal void ThrowPendingFailure()
    {
        if (pendingFailure is not null)
            pendingFailure.Throw();
        if (logicalIndex is null || physicalIndex is null || rawPlan is null)
            return;
        ExplainAssertionMode.AssertChosenIndex(
            "MongoDB",
            logicalIndex,
            physicalIndex,
            hinted,
            rawPlan,
            chosen);
    }

    internal void AssertLegacy() => ThrowPendingFailure();

}

/// <summary>
/// Completes one successful actual query after optional plan collection. The terminal success
/// event is always attempted first; a plan or legacy diagnostic failure then takes precedence over
/// an observer callback failure, while an observer failure propagates when no diagnostic is pending.
/// </summary>
internal static class MongoExecutionEvidenceCompletion
{
    internal static void PublishSuccessfulQuery(
        MongoExecutionEvidenceCapture capture,
        MongoExecutionEvidenceInvocation invocation,
        int commandOrdinal,
        ProviderBoundedQueryEvidence? boundedQuery,
        MongoNativePlanCollectionResult planCollection)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(planCollection);

        ExceptionDispatchInfo? observerFailure = null;
        try
        {
            capture.Publish(
                invocation,
                commandOrdinal,
                ProviderExecutionOperation.BoundedQuery,
                ProviderExecutionRole.Statement,
                ProviderCommandKind.Read,
                ProviderExecutionOutcome.Succeeded,
                failureCategory: null,
                boundedQuery,
                pointRead: null,
                plan: planCollection.Evidence);
        }
        catch (Exception failure)
        {
            observerFailure = ExceptionDispatchInfo.Capture(failure);
        }

        // A diagnostic from the requested explain replay or legacy assertion is the original
        // provider-facing failure and must not be replaced by a terminal callback exception.
        planCollection.ThrowPendingFailure();
        observerFailure?.Throw();
    }

    internal static void ThrowPendingDiagnostic(
        ExceptionDispatchInfo? pendingDiagnostic,
        ExceptionDispatchInfo? observerFailure)
    {
        pendingDiagnostic?.Throw();
        observerFailure?.Throw();
    }
}

internal readonly struct MongoExecutionEvidenceInvocation
{
    internal MongoExecutionEvidenceInvocation(
        MongoExecutionEvidenceCapture capture,
        ProviderOpaqueIdentity invocationId)
    {
        Capture = capture;
        InvocationId = invocationId;
    }

    internal MongoExecutionEvidenceCapture Capture { get; }

    internal ProviderOpaqueIdentity InvocationId { get; }
}

/// <summary>
/// Collects facts only from the native Mongo emission branches. It is deliberately mutable while
/// rendering and produces one immutable shape only after every required branch has completed.
/// Any branch the first slice cannot map invalidates the whole shape.
/// </summary>
internal sealed class MongoExecutionEvidenceEmitter
{
    private readonly MongoExecutionEvidenceCapture capture;
    private readonly QueryRenderOptions options;
    private readonly TableId table;
    private readonly List<ProviderPredicateFact> predicateFacts = [];
    private readonly List<ProviderOrderTerm> ordering = [];
    private bool predicateEmitted;
    private bool projectionEmitted;
    private ProviderProjection? projection;
    private ProviderNativeBound nativeOffset = ProviderNativeBound.Unknown;
    private ProviderNativeBound nativeLimit = ProviderNativeBound.Unknown;
    private bool hasContinuation;
    private bool hasLookahead;
    private bool includesTotalCount;
    private bool unsupported;

    internal MongoExecutionEvidenceEmitter(
        MongoExecutionEvidenceCapture capture,
        QueryRenderOptions options,
        TableId table)
    {
        this.capture = capture ?? throw new ArgumentNullException(nameof(capture));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.table = table;

        var physicalMappings = options.SearchKeyColumns.Values
            .GroupBy(mapping => mapping.PhysicalColumn, StringComparer.Ordinal)
            .Any(group => group.Count() > 1);
        if (physicalMappings)
            unsupported = true;
    }

    internal void MarkUnsupported() => unsupported = true;

    internal void ConfigureRequest(QueryRequest request)
    {
        if (request.Join is not null ||
            request.Result is not ResultShape.Rows ||
            request.Result.IncludesTotalCount ||
            request.LatestPerKey is not null ||
            request.Distinct ||
            request.Paging.ContinuationToken is not null)
        {
            unsupported = true;
        }
    }

    internal void RecordAlwaysTrue() => predicateEmitted = true;

    internal void RecordAlwaysFalse()
    {
        predicateEmitted = true;
        unsupported = true;
    }

    internal void RecordEqual(ColumnRef column, QueryConstant value)
    {
        predicateEmitted = true;
        if (value.Kind == QueryConstantKind.Null)
        {
            unsupported = true;
            return;
        }
        AddFact(column, ProviderPredicateOperator.Equal, ProviderPredicateBoundInclusivity.NotApplicable);
    }

    internal void RecordIn(ColumnRef column, IReadOnlyList<QueryConstant> values)
    {
        predicateEmitted = true;
        if (values.Count == 0 || values.Any(value => value.Kind == QueryConstantKind.Null))
        {
            unsupported = true;
            return;
        }
        AddFact(column, ProviderPredicateOperator.In, ProviderPredicateBoundInclusivity.NotApplicable);
    }

    internal void RecordRange(
        ColumnRef column,
        Bound? lower,
        Bound? upper,
        bool excludesNull = false)
    {
        predicateEmitted = true;
        // String ranges are rendered with a native "$ne: null" guard. The request's
        // nullability flag is not a native nullability witness, so this first slice withholds
        // every such shape rather than claiming a complete predicate from caller metadata.
        if (excludesNull ||
            lower is { Value.Kind: QueryConstantKind.Null } || upper is { Value.Kind: QueryConstantKind.Null })
        {
            unsupported = true;
            return;
        }
        if (lower is { } low)
            AddFact(column, ProviderPredicateOperator.LowerBound,
                low.IsInclusive ? ProviderPredicateBoundInclusivity.Inclusive : ProviderPredicateBoundInclusivity.Exclusive);
        if (upper is { } high)
            AddFact(column, ProviderPredicateOperator.UpperBound,
                high.IsInclusive ? ProviderPredicateBoundInclusivity.Inclusive : ProviderPredicateBoundInclusivity.Exclusive);
    }

    internal void RecordOrder(
        OrderTerm term,
        bool nullRankEmitted,
        bool ordinalStringKeyEmitted,
        bool physicalSearchKeyEmitted)
    {
        if (!ValidateColumn(term.Column))
            return;

        var transforms = new List<ProviderOrderingTransform>();
        if (nullRankEmitted)
            transforms.Add(ProviderOrderingTransform.NullRank);
        if (ordinalStringKeyEmitted)
            transforms.Add(ProviderOrderingTransform.OrdinalStringKey);
        if (physicalSearchKeyEmitted)
        {
            transforms.Add(ProviderOrderingTransform.PhysicalSearchKey);
            // A rewritten provider-owned search key is not yet mapped back to a complete
            // logical ordering contract in this bounded slice.
            unsupported = true;
        }

        ordering.Add(new ProviderOrderTerm(
            EmittedLogicalColumn(term.Column),
            term.Direction,
            nullRankEmitted && term.NullOrder != NullOrder.ProviderDefault ? term.NullOrder : null,
            transforms,
            term.Column.Type == QueryType.String && ordinalStringKeyEmitted
                ? ProviderPredicateComparison.Ordinal
                : ProviderPredicateComparison.Exact));
    }

    internal void RecordProjection(bool allColumns, IEnumerable<string> emittedColumns)
    {
        if (projectionEmitted)
        {
            unsupported = true;
            return;
        }

        var columns = (emittedColumns ?? throw new ArgumentNullException(nameof(emittedColumns))).ToArray();
        if (allColumns && columns.Length != 0)
        {
            unsupported = true;
            return;
        }

        var logicalColumns = columns.Select(EmittedLogicalColumn).ToArray();
        projection = new ProviderProjection(allColumns, logicalColumns);
        projectionEmitted = true;
    }

    internal void RecordPaging(
        int? offset,
        int? limit,
        bool continuation,
        bool lookahead,
        bool totalCount)
    {
        if (nativeLimit.Kind != ProviderNativeBoundKind.Unknown)
        {
            unsupported = true;
            return;
        }

        hasContinuation = continuation;
        hasLookahead = lookahead;
        includesTotalCount = totalCount;
        if (continuation || totalCount || offset is < 0 || limit is not > 0)
            unsupported = true;

        nativeOffset = offset is int value
            ? ProviderNativeBound.Explicit(value)
            : ProviderNativeBound.Absent;
        nativeLimit = limit is int bounded
            ? ProviderNativeBound.Explicit(bounded)
            : ProviderNativeBound.Unknown;
    }

    internal ProviderBoundedQueryEvidence? Complete()
    {
        if (unsupported || !predicateEmitted || !projectionEmitted || nativeLimit.Kind != ProviderNativeBoundKind.Explicit)
            return null;

        return new ProviderBoundedQueryEvidence(
            new ProviderConjunctionPredicate(predicateFacts),
            ordering,
            projection!,
            nativeOffset,
            nativeLimit,
            hasContinuation,
            hasLookahead,
            includesTotalCount);
    }

    private void AddFact(
        ColumnRef column,
        ProviderPredicateOperator @operator,
        ProviderPredicateBoundInclusivity boundInclusivity)
    {
        if (!ValidateColumn(column))
            return;

        predicateFacts.Add(new ProviderPredicateFact(
            EmittedLogicalColumn(column),
            @operator,
            column.Type,
            Comparison(column),
            boundInclusivity,
            ProviderPredicateBindingRole.Caller,
            capture.NewIdentity()));
    }

    private bool ValidateColumn(ColumnRef column)
    {
        if (column.Table != TableId.Empty && column.Table != table)
        {
            unsupported = true;
            return false;
        }
        if (options.SearchKeyColumns.Values.Any(mapping =>
                string.Equals(mapping.PhysicalColumn, column.Name, StringComparison.Ordinal) &&
                !string.Equals(mapping.SourceColumn, mapping.PhysicalColumn, StringComparison.Ordinal)))
        {
            unsupported = true;
            return false;
        }
        return true;
    }

    private string EmittedLogicalColumn(ColumnRef column)
    {
        var mappings = options.SearchKeyColumns.Values
            .Where(mapping => string.Equals(mapping.PhysicalColumn, column.Name, StringComparison.Ordinal))
            .ToArray();
        if (mappings.Length > 1)
            unsupported = true;
        return mappings.Length == 1 ? mappings[0].SourceColumn : column.Name;
    }

    private string EmittedLogicalColumn(string physicalColumn)
    {
        var mappings = options.SearchKeyColumns.Values
            .Where(mapping => string.Equals(mapping.PhysicalColumn, physicalColumn, StringComparison.Ordinal))
            .ToArray();
        if (mappings.Length > 1)
            unsupported = true;
        if (mappings.Any(mapping => !string.Equals(mapping.SourceColumn, mapping.PhysicalColumn, StringComparison.Ordinal)))
            unsupported = true;
        return mappings.Length == 1 ? mappings[0].SourceColumn : physicalColumn;
    }

    private static ProviderPredicateComparison Comparison(ColumnRef column) => column.Type switch
    {
        QueryType.String when column.StringComparison == QueryStringComparisonPolicy.Ordinal => ProviderPredicateComparison.Ordinal,
        QueryType.String when column.StringComparison == QueryStringComparisonPolicy.UnicodeOrdinalIgnoreCase => ProviderPredicateComparison.UnicodeOrdinalIgnoreCase,
        QueryType.String when column.StringComparison == QueryStringComparisonPolicy.AsciiIgnoreCase => ProviderPredicateComparison.AsciiIgnoreCase,
        _ => ProviderPredicateComparison.Exact
    };
}

/// <summary>Builds Mongo's point-read shape from the actual key-read emitter.</summary>
internal static class MongoExecutionEvidenceBuilder
{
    internal static ProviderPointReadEvidence CreatePointRead(
        StorageUnit unit,
        MongoExecutionEvidenceCapture capture)
    {
        var keyBounds = unit.Key.Columns.Select(name =>
        {
            var column = unit.Columns.Single(item => string.Equals(item.Name, name, StringComparison.Ordinal));
            var type = QueryTypeOf(column.Type) ?? throw new InvalidOperationException(
                $"MongoDB point-read key column '{name}' has no structured-evidence query type.");
            return new ProviderPointReadKeyBound(
                name,
                type,
                ProviderPointReadBindingRole.Key,
                capture.NewIdentity());
        });
        return new ProviderPointReadEvidence(
            keyBounds,
            new ProviderPointReadUniqueness(ProviderPointReadUniquenessStatus.NotObserved),
            ProviderNativeBound.Explicit(1),
            materializerReadsAtMostOne: true,
            ProviderPointReadLockMode.None);
    }

    private static QueryType? QueryTypeOf(PortableType type) => type switch
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

}
