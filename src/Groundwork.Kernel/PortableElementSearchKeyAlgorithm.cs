namespace Groundwork.Kernel;

/// <summary>Versioned identity for the positional JSON array search-key transform.</summary>
internal static class PortableElementSearchKeyAlgorithm
{
    internal const string Name = "groundwork-element-search-key-array-v1";

    internal static string ForPolicy(PortableStringComparisonPolicy policy, int? maximumElementCodeUnits) =>
        $"{Name}+max-{(maximumElementCodeUnits?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unbounded")}" +
        $"+{PortableStringComparison.GetSearchKeyAlgorithmId(policy)}";

    internal static ElementSearchKeyAlgorithmIdentity Parse(string? value)
    {
        var prefix = Name + "+max-";
        if (value?.StartsWith(prefix, StringComparison.Ordinal) != true)
            return Refuse(value);
        var algorithmSeparator = value.IndexOf('+', prefix.Length);
        if (algorithmSeparator < 0)
            return Refuse(value);
        var maximumText = value[prefix.Length..algorithmSeparator];
        int? maximum = null;
        if (!string.Equals(maximumText, "unbounded", StringComparison.Ordinal))
        {
            if (!int.TryParse(
                maximumText,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsedMaximum) || parsedMaximum <= 0)
            {
                return Refuse(value);
            }
            maximum = parsedMaximum;
        }

        var algorithm = value[(algorithmSeparator + 1)..];
        foreach (var policy in new[]
        {
            PortableStringComparisonPolicy.Ordinal,
            PortableStringComparisonPolicy.AsciiIgnoreCase,
            PortableStringComparisonPolicy.UnicodeOrdinalIgnoreCase
        })
        {
            if (string.Equals(algorithm, PortableStringComparison.GetSearchKeyAlgorithmId(policy), StringComparison.Ordinal))
                return new ElementSearchKeyAlgorithmIdentity(policy, maximum);
        }

        return Refuse(value);
    }

    private static ElementSearchKeyAlgorithmIdentity Refuse(string? value) =>
        throw new InvalidOperationException(
            $"Element search-key algorithm identity '{value ?? "<missing>"}' is unknown, stale, or malformed. Rebuild the derived element search-key column before use.");
}

internal readonly record struct ElementSearchKeyAlgorithmIdentity(
    PortableStringComparisonPolicy Policy,
    int? MaximumElementCodeUnits);
