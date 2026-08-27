namespace Groundwork.Kernel;

/// <summary>
/// Lowercase hexadecimal encoding for values that are persisted or compared as text —
/// schema fingerprints, comparison-key hashes, idempotency keys.
/// </summary>
/// <remarks>
/// <para>
/// The BCL grew <c>Convert.ToHexStringLower</c> in .NET 9, so it is unavailable on the
/// <c>net8.0</c> target. Rather than branch on the target framework at every call site — which
/// would give the two targets two encoders and therefore two possible fingerprints for the same
/// declaration — every Groundwork assembly funnels through this single implementation on both
/// targets. Hex digits are ASCII, so the invariant lowering is exact and culture-independent.
/// </para>
/// </remarks>
internal static class PortableHex
{
    internal static string Lower(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(bytes).ToLowerInvariant();
}
