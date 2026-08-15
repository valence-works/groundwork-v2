using System.Globalization;

namespace Groundwork.Kernel.Schema;

/// <summary>Injective, provider-neutral canonicalization for a retention declaration.</summary>
public static class RetentionCanonicalization
{
    public static string Canonicalize(RetentionDeclaration? retention) => retention is null
        ? "retention:none"
        : SchemaFingerprint.Canonicalize(
        [
            "retention",
            retention.KeepNewest.ToString(CultureInfo.InvariantCulture),
            retention.OrderColumn,
            retention.Trigger.ToString(),
            .. retention.PartitionColumns
        ]);
}
