using Groundwork.Query.Model;

namespace Groundwork.Query.Planning;

/// <summary>Runtime guard that keeps analyzer-suppressed uncovered queries fail-closed.</summary>
public static class QueryCoverageEnforcer
{
    public static void EnsureCovered(
        QueryRequest request,
        IEnumerable<CoverageIndex> indexes) =>
        EnsureCovered(request, indexes, DateTimeOffset.UtcNow);

    public static void EnsureCovered(
        QueryRequest request,
        IEnumerable<CoverageIndex> indexes,
        DateTimeOffset now)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        if (indexes is null)
            throw new ArgumentNullException(nameof(indexes));

        EnsureCovered(request, QueryCoverageChecker.Check(request, indexes), now);
    }

    public static void EnsureCovered(
        QueryRequest request,
        QueryCoverageCandidates candidates) =>
        EnsureCovered(request, candidates, DateTimeOffset.UtcNow);

    public static void EnsureCovered(
        QueryRequest request,
        QueryCoverageCandidates candidates,
        DateTimeOffset now)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        if (candidates is null)
            throw new ArgumentNullException(nameof(candidates));

        EnsureCovered(request, QueryCoverageChecker.Check(request, candidates), now);
    }

    private static void EnsureCovered(
        QueryRequest request,
        QueryCoverageResult result,
        DateTimeOffset now)
    {
        if (result.IsCovered)
            return;

        var refusal = result.Refusal;
        if (refusal?.Code == "GW-COVER-901")
            throw new QueryCoverageException(refusal.Code, refusal.Message, result);

        var acceptance = request.AcceptedScan;
        if (acceptance?.Allowed != true)
            throw new QueryCoverageException(
                refusal?.Code ?? "GW-COVER-006",
                refusal?.Message ?? result.Reason,
                result);

        if (acceptance.IsExpiredAt(now))
            throw new QueryCoverageException(
                "GW-COVER-903",
                "Accepted scan '" + acceptance.Id + "' expired on " + acceptance.ExpiresOn!.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ".",
                result);
    }
}

/// <summary>Raised when runtime coverage refuses a query shape or its scan acceptance.</summary>
public sealed class QueryCoverageException : InvalidOperationException
{
    public QueryCoverageException(string code, string message, QueryCoverageResult result)
        : base(message)
    {
        Code = code ?? throw new ArgumentNullException(nameof(code));
        Result = result ?? throw new ArgumentNullException(nameof(result));
    }

    public string Code { get; }

    public QueryCoverageResult Result { get; }
}
