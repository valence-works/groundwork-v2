using System.Collections.Immutable;
using Groundwork.Query.Model;

namespace Groundwork.Query.Planning;

/// <summary>Provider parameter and membership bounds applied to every runtime-built request.</summary>
public sealed class RuntimeValueFenceOptions
{
    public int MaximumInValues { get; init; } = 1000;

    public int MaximumParameters { get; init; } = 2100;

    public RuntimeContinuationBinding? ContinuationBinding { get; init; }
}

public sealed class RuntimeValueFenceException : InvalidOperationException
{
    public RuntimeValueFenceException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>
/// Re-checks runtime-composed values before provider execution. QueryConstant construction already
/// enforces type, decimal, length, and UTF-16 rules; this fence adds request-wide cardinality and
/// provider-parameter limits.
/// </summary>
public static class RuntimeValueFence
{
    public static void Validate(QueryRequest request, RuntimeValueFenceOptions? options = null)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        options ??= new RuntimeValueFenceOptions();
        if (options.MaximumInValues <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaximumInValues must be positive.");
        if (options.MaximumParameters <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaximumParameters must be positive.");

        var parameters = 0;
        Visit(request.Where, ref parameters, options);
        if (parameters > options.MaximumParameters)
            throw new RuntimeValueFenceException(
                "GW-RUNTIME-011",
                $"The query has {parameters} provider parameters, exceeding the configured limit of {options.MaximumParameters}.");

        if (request.Paging.ContinuationToken is not null && request.Order.Length == 0)
            throw new RuntimeValueFenceException(
                "GW-RUNTIME-012",
                "A keyset continuation requires the same non-empty order as its originating plan.");
        options.ContinuationBinding?.Validate(request);
    }

    private static void Visit(Predicate predicate, ref int parameters, RuntimeValueFenceOptions options)
    {
        switch (predicate)
        {
            case Predicate.Equal:
                parameters++;
                return;
            case Predicate.In membership:
                if (membership.Values.Length > options.MaximumInValues)
                    throw new RuntimeValueFenceException(
                        "GW-RUNTIME-010",
                        $"In contains {membership.Values.Length} values, exceeding the configured limit of {options.MaximumInValues}.");
                parameters += membership.Values.Length;
                return;
            case Predicate.Range range:
                parameters += (range.Lower is null ? 0 : 1) + (range.Upper is null ? 0 : 1);
                return;
            case Predicate.StartsWith:
            case Predicate.Substring:
                parameters++;
                return;
            case Predicate.ElementOf elementOf:
                if (elementOf.Values.Length > options.MaximumInValues)
                    throw new RuntimeValueFenceException(
                        "GW-RUNTIME-010",
                        $"ElementOf contains {elementOf.Values.Length} values, exceeding the configured limit of {options.MaximumInValues}.");
                parameters += elementOf.Values.Length;
                return;
            case Predicate.ElementSubstring:
                parameters++;
                return;
            case Predicate.ColumnCompare:
            case Predicate.AlwaysTrue:
            case Predicate.AlwaysFalse:
                return;
            case Predicate.Not not:
                Visit(not.Inner, ref parameters, options);
                return;
            case Predicate.And and:
                foreach (var term in and.Terms)
                    Visit(term, ref parameters, options);
                return;
            case Predicate.Or or:
                foreach (var term in or.Terms)
                    Visit(term, ref parameters, options);
                return;
            default:
                throw new RuntimeValueFenceException(
                    "GW-RUNTIME-013",
                    "The runtime predicate contains an unrecognized node.");
        }
    }
}

/// <summary>Plan/order identity captured when a keyset continuation is issued.</summary>
public sealed record RuntimeContinuationBinding
{
    private RuntimeContinuationBinding(string continuationFingerprint, ImmutableArray<OrderTerm> order)
    {
        ContinuationFingerprint = continuationFingerprint;
        Order = order;
    }

    public string ContinuationFingerprint { get; }

    public ImmutableArray<OrderTerm> Order { get; }

    public static RuntimeContinuationBinding Create(QueryRequest request)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        if (request.Paging.ContinuationToken is null || request.Order.Length == 0)
            throw new ArgumentException("A continuation binding requires a continuation token and order.", nameof(request));
        return new(request.ContinuationFingerprint, request.Order);
    }

    public void Validate(QueryRequest request)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        if (request.Paging.ContinuationToken is null || request.Order.Length == 0 ||
            !string.Equals(request.ContinuationFingerprint, ContinuationFingerprint, StringComparison.Ordinal) ||
            request.Order.Length != Order.Length ||
            request.Order.Select((term, index) => (term, index)).Any(pair =>
                pair.term.Column.Name != Order[pair.index].Column.Name ||
                pair.term.Direction != Order[pair.index].Direction ||
                pair.term.NullOrder != Order[pair.index].NullOrder))
        {
            throw new RuntimeValueFenceException(
                "GW-RUNTIME-012",
                "The keyset continuation does not match the current order and plan fingerprint.");
        }
    }
}
