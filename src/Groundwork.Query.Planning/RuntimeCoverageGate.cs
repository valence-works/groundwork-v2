using System.Collections.Immutable;
using Groundwork.Query.Model;

namespace Groundwork.Query.Planning;

/// <summary>A generated, already-verified query shape known to the runtime.</summary>
public sealed record RuntimeVerifiedShape
{
    public RuntimeVerifiedShape(string shapeFingerprint, CoverageIndex verifiedIndex)
    {
        if (string.IsNullOrWhiteSpace(shapeFingerprint))
            throw new ArgumentException("A verified shape requires a fingerprint.", nameof(shapeFingerprint));
        ShapeFingerprint = shapeFingerprint;
        VerifiedIndex = verifiedIndex ?? throw new ArgumentNullException(nameof(verifiedIndex));
    }

    public string ShapeFingerprint { get; }

    public CoverageIndex VerifiedIndex { get; }

    public static RuntimeVerifiedShape Covered(QueryRequest request, CoverageIndex verifiedIndex) =>
        new((request ?? throw new ArgumentNullException(nameof(request))).ShapeFingerprint, verifiedIndex);
}

/// <summary>Bound for runtime shape-verdict memory and its observability callback.</summary>
public sealed class RuntimeCoverageGateOptions
{
    public int MaximumCachedShapes { get; init; } = 1024;

    public RuntimeValueFenceOptions ValueFence { get; init; } = new();
}

/// <summary>A bounded-cache metric emitted when an unrecognized shape evicts an older verdict.</summary>
public sealed record RuntimeCoverageMetric(string Name, int CachedShapes, int MaximumCachedShapes);

/// <summary>The provider-neutral runtime result, including whether generated evidence was used.</summary>
public sealed record RuntimeCoverageDecision(
    string ShapeFingerprint,
    bool IsRecognized,
    bool WasCached,
    QueryCoverageResult Coverage);

/// <summary>
/// Runtime coverage gate. Declared indexes are intersected with the deployed declaration on each
/// query side before calling the single Q3 checker; database-only indexes are never candidates.
/// </summary>
public sealed class RuntimeCoverageGate
{
    private readonly QueryCoverageCandidates declaredCandidates;
    private readonly QueryCoverageCandidates deployedCandidates;
    private readonly ImmutableDictionary<string, RuntimeVerifiedShape> recognizedShapes;
    private readonly RuntimeCoverageGateOptions options;
    private readonly Action<RuntimeCoverageMetric>? metric;
    private readonly Dictionary<string, QueryCoverageResult> cache = new(StringComparer.Ordinal);
    private readonly Queue<string> cacheOrder = new();
    private readonly object gate = new();

    public RuntimeCoverageGate(
        IEnumerable<CoverageIndex> declaredIndexes,
        IEnumerable<CoverageIndex> deployedIndexes,
        IEnumerable<RuntimeVerifiedShape>? recognizedShapes = null,
        RuntimeCoverageGateOptions? options = null,
        Action<RuntimeCoverageMetric>? metric = null)
        : this(
            new QueryCoverageCandidates(declaredIndexes, []),
            new QueryCoverageCandidates(deployedIndexes, []),
            recognizedShapes,
            options,
            metric)
    {
    }

    public RuntimeCoverageGate(
        QueryCoverageCandidates declaredCandidates,
        QueryCoverageCandidates deployedCandidates,
        IEnumerable<RuntimeVerifiedShape>? recognizedShapes = null,
        RuntimeCoverageGateOptions? options = null,
        Action<RuntimeCoverageMetric>? metric = null)
    {
        this.declaredCandidates = declaredCandidates ?? throw new ArgumentNullException(nameof(declaredCandidates));
        this.deployedCandidates = deployedCandidates ?? throw new ArgumentNullException(nameof(deployedCandidates));
        this.recognizedShapes = (recognizedShapes ?? [])
            .ToImmutableDictionary(shape => shape.ShapeFingerprint, StringComparer.Ordinal);
        this.options = options ?? new RuntimeCoverageGateOptions();
        if (this.options.MaximumCachedShapes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaximumCachedShapes must be positive.");
        this.metric = metric;
    }

    public int CachedShapeCount
    {
        get
        {
            lock (gate)
                return cache.Count;
        }
    }

    public ImmutableArray<CoverageIndex> DeclaredIndexes => declaredCandidates.Driving;

    public ImmutableArray<CoverageIndex> DeployedIndexes => deployedCandidates.Driving;

    public QueryCoverageCandidates DeclaredCandidates => declaredCandidates;

    public QueryCoverageCandidates DeployedCandidates => deployedCandidates;

    public RuntimeCoverageDecision Check(QueryRequest request)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        RuntimeValueFence.Validate(request, options.ValueFence);
        var shapeFingerprint = request.ShapeFingerprint;
        var candidates = EffectiveIndexes();

        if (request.Join is null && recognizedShapes.TryGetValue(shapeFingerprint, out var recognized))
        {
            var deployedIndex = candidates.Driving.FirstOrDefault(index => SameIndex(index, recognized.VerifiedIndex));
            if (deployedIndex is not null)
            {
                // The generated evidence is authoritative only while its physical index remains.
                // The checker still proves the request against that exact deployed definition.
                return new(
                    shapeFingerprint,
                    IsRecognized: true,
                    WasCached: false,
                    QueryCoverageChecker.Check(request, [deployedIndex]));
            }
        }

        lock (gate)
        {
            if (cache.TryGetValue(shapeFingerprint, out var cached))
                return new(shapeFingerprint, IsRecognized: false, WasCached: true, cached);
        }

        var result = QueryCoverageChecker.Check(request, candidates);
        lock (gate)
        {
            if (!cache.ContainsKey(shapeFingerprint))
            {
                cache[shapeFingerprint] = result;
                cacheOrder.Enqueue(shapeFingerprint);
                if (cache.Count > options.MaximumCachedShapes)
                {
                    var evicted = cacheOrder.Dequeue();
                    cache.Remove(evicted);
                    metric?.Invoke(new RuntimeCoverageMetric(
                        "groundwork.runtime.coverage.cache.eviction",
                        cache.Count,
                        options.MaximumCachedShapes));
                }
            }
        }
        return new(shapeFingerprint, IsRecognized: recognizedShapes.ContainsKey(shapeFingerprint), WasCached: false, result);
    }

    public void EnsureCovered(QueryRequest request, DateTimeOffset now)
    {
        var decision = Check(request);
        if (decision.Coverage.IsCovered)
            return;

        var refusal = decision.Coverage.Refusal;
        if (refusal?.Code == "GW-COVER-901")
            throw new QueryCoverageException(refusal.Code, refusal.Message, decision.Coverage);

        var acceptance = request.AcceptedScan;
        if (acceptance?.Allowed != true)
            throw new QueryCoverageException(
                refusal?.Code ?? "GW-COVER-006",
                refusal?.Message ?? decision.Coverage.Reason,
                decision.Coverage);
        if (acceptance.IsExpiredAt(now))
            throw new QueryCoverageException(
                "GW-COVER-903",
                "Accepted scan '" + acceptance.Id + "' expired on " +
                acceptance.ExpiresOn!.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ".",
                decision.Coverage);
    }

    private QueryCoverageCandidates EffectiveIndexes() =>
        new(
            Intersect(declaredCandidates.Driving, deployedCandidates.Driving),
            Intersect(declaredCandidates.Target, deployedCandidates.Target));

    private static ImmutableArray<CoverageIndex> Intersect(
        ImmutableArray<CoverageIndex> declared,
        ImmutableArray<CoverageIndex> deployed) =>
        declared.Where(item => deployed.Any(candidate => SameIndex(item, candidate))).ToImmutableArray();

    private static bool SameIndex(CoverageIndex left, CoverageIndex right) =>
        string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
        left.MissingValues == right.MissingValues &&
        left.Columns.Length == right.Columns.Length &&
        left.Columns.Select((column, index) => (column, index)).All(pair =>
            string.Equals(pair.column.Column, right.Columns[pair.index].Column, StringComparison.Ordinal) &&
            pair.column.Direction == right.Columns[pair.index].Direction);

}
