using System.Globalization;

namespace Groundwork.Kernel.Schema;

/// <summary>Injective, provider-neutral canonicalization for declared aggregation profiles.</summary>
public static class AggregationProfileCanonicalization
{
    public static string Canonicalize(AggregationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return SchemaFingerprint.Canonicalize(
        [
            profile.Name,
            SchemaFingerprint.Canonicalize((profile.GroupByColumns ?? []).OrderBy(value => value, StringComparer.Ordinal)),
            SchemaFingerprint.Canonicalize((profile.Aggregates ?? []).Select(CanonicalAggregate).OrderBy(value => value, StringComparer.Ordinal)),
            SchemaFingerprint.Canonicalize((profile.AllowedPredicates ?? []).Select(CanonicalAllowance).OrderBy(value => value, StringComparer.Ordinal)),
            profile.MaxGroups.ToString(CultureInfo.InvariantCulture),
            profile.MaxInputRows.ToString(CultureInfo.InvariantCulture)
        ]);
    }

    private static string CanonicalAllowance(AggregationPredicateAllowance allowance)
    {
        ArgumentNullException.ThrowIfNull(allowance);
        return SchemaFingerprint.Canonicalize(
        [
            allowance.Alias,
            .. allowance.SupportedPredicates.OrderBy(value => value).Select(value => value.ToString())
        ]);
    }

    private static string CanonicalAggregate(Aggregate aggregate) => aggregate switch
    {
        Aggregate.Min min => SchemaFingerprint.Canonicalize(["min", min.Alias, min.Column]),
        Aggregate.Max max => SchemaFingerprint.Canonicalize(["max", max.Alias, max.Column]),
        Aggregate.Count count => SchemaFingerprint.Canonicalize(["count", count.Alias]),
        Aggregate.Sum sum => SchemaFingerprint.Canonicalize(["sum", sum.Alias, sum.Column]),
        Aggregate.SetUnion set => SchemaFingerprint.Canonicalize(["setUnion", set.Alias, set.Column, set.MaxValues.ToString(CultureInfo.InvariantCulture)]),
        Aggregate.FirstBy first => SchemaFingerprint.Canonicalize(["firstBy", first.Alias, first.Column, first.OrderColumn, first.Direction.ToString()]),
        _ => throw new ArgumentOutOfRangeException(nameof(aggregate))
    };
}
