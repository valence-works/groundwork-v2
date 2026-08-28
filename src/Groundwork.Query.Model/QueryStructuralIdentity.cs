using System.Collections;
using System.Globalization;

namespace Groundwork.Query.Model;

/// <summary>Provides collision-safe identities for provider-neutral structural values.</summary>
internal static class QueryStructuralIdentity
{
    internal static string ForDistinct(object? value)
    {
        if (value is null)
            return "null";
        var text = value switch
        {
            byte[] bytes => Convert.ToBase64String(bytes),
            IReadOnlyDictionary<string, object?> dictionary => "{" + string.Join(",", dictionary
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Key + "=" + ForDistinct(pair.Value))) + "}",
            DateTimeOffset instant => instant.UtcTicks.ToString(CultureInfo.InvariantCulture),
            decimal number => number.ToString("G29", CultureInfo.InvariantCulture),
            Guid guid => guid.ToString("N"),
            IEnumerable sequence when value is not string => "[" + string.Join(",", sequence.Cast<object?>().Select(ForDistinct)) + "]",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => value.ToString() ?? string.Empty
        };
        return value.GetType().FullName + ":" + text.Length.ToString(CultureInfo.InvariantCulture) + ":" + text;
    }
}
