using System.Globalization;
using System.Text;

namespace Groundwork.Kernel;

/// <summary>
/// The one source of truth for persisted search-key bytes. This file is linked into the query
/// model project so renderers and write paths cannot silently acquire different casing rules.
/// </summary>
internal enum PortableSearchKeyPolicy
{
    Ordinal,
    AsciiIgnoreCase,
    UnicodeOrdinalIgnoreCase,
    IcuSortKey
}

internal static class PortableSearchKeyEncoding
{
    internal static string Create(string value, PortableSearchKeyPolicy policy)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        return CreateSearchKeyFromComparisonKey(CreateComparisonKey(value, policy), policy);
    }

    internal static string CreateComparisonKey(string value, PortableSearchKeyPolicy policy)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        return policy switch
        {
            PortableSearchKeyPolicy.Ordinal => Utf16Hex(value),
            PortableSearchKeyPolicy.AsciiIgnoreCase => AsciiLower(value),
            PortableSearchKeyPolicy.UnicodeOrdinalIgnoreCase => UnicodeUpper(value),
            _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, null)
        };
    }

    internal static string CreateSearchKeyFromComparisonKey(
        string comparisonKey,
        PortableSearchKeyPolicy policy)
    {
        if (comparisonKey is null) throw new ArgumentNullException(nameof(comparisonKey));
        var unitWidth = policy switch
        {
            PortableSearchKeyPolicy.Ordinal => 4,
            PortableSearchKeyPolicy.UnicodeOrdinalIgnoreCase => 6,
            PortableSearchKeyPolicy.AsciiIgnoreCase => 1,
            PortableSearchKeyPolicy.IcuSortKey => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, null)
        };
        if (comparisonKey.Length % unitWidth != 0)
            throw new ArgumentException("The comparison key does not contain complete encoded comparison units.", nameof(comparisonKey));

        var result = new StringBuilder(comparisonKey.Length + comparisonKey.Length / unitWidth);
        const string hex = "0123456789ABCDEF";
        for (var index = 0; index < comparisonKey.Length; index += unitWidth)
        {
            result.Append('|');
            if (policy == PortableSearchKeyPolicy.AsciiIgnoreCase)
            {
                var character = comparisonKey[index];
                result.Append(hex[(character >> 12) & 0xF]);
                result.Append(hex[(character >> 8) & 0xF]);
                result.Append(hex[(character >> 4) & 0xF]);
                result.Append(hex[character & 0xF]);
            }
            else
            {
                result.Append(comparisonKey, index, unitWidth);
            }
        }
        return result.ToString();
    }

    /// <summary>
    /// Encodes raw ICU comparison bytes through the same fixed-width text path as every other
    /// persisted search key. The resulting ordinal text order is the unsigned byte order ICU
    /// defines, without relying on provider-specific binary ordering.
    /// </summary>
    internal static string CreateSearchKeyFromComparisonKey(ReadOnlySpan<byte> comparisonKey)
    {
        const string hex = "0123456789ABCDEF";
        var encodedUnits = new StringBuilder(comparisonKey.Length * 2);
        foreach (var value in comparisonKey)
        {
            encodedUnits.Append(hex[value >> 4]);
            encodedUnits.Append(hex[value & 0x0F]);
        }
        return CreateSearchKeyFromComparisonKey(encodedUnits.ToString(), PortableSearchKeyPolicy.IcuSortKey);
    }

    internal static string? CreateSuccessor(string searchKey, PortableSearchKeyPolicy policy)
    {
        if (searchKey is null) throw new ArgumentNullException(nameof(searchKey));
        if (searchKey.Length == 0) return null;

        if (policy == PortableSearchKeyPolicy.Ordinal)
            return CreateOrdinalSuccessor(searchKey);

        var separator = searchKey.LastIndexOf('|');
        if (separator < 0 || separator == searchKey.Length - 1) return null;
        var unit = searchKey.Substring(separator + 1);
        if ((unit.Length != 4 && unit.Length != 6) ||
            unit.Any(character => character < '0' || character > '9' && character < 'A' || character > 'F'))
            return null;
        if (unit.All(character => character == 'F')) return null;

        var number = Convert.ToUInt32(unit, 16) + 1U;
        return searchKey.Substring(0, separator + 1) +
            number.ToString(unit.Length == 4 ? "X4" : "X6", CultureInfo.InvariantCulture);
    }

    private static string AsciiLower(string value)
    {
        var result = value.ToCharArray();
        for (var index = 0; index < result.Length; index++)
        {
            var character = result[index];
            if (character < ' ' || character > '~')
                throw new ArgumentException("ASCII-ignore-case values may contain only U+0020 through U+007E.", nameof(value));
            if (character is >= 'A' and <= 'Z')
                result[index] = (char)(character + ('a' - 'A'));
        }
        return new string(result);
    }

    private static string Utf16Hex(string value)
    {
        ValidateWellFormed(value, "Ordinal strings must be well-formed UTF-16.");
        const string hex = "0123456789ABCDEF";
        var result = new StringBuilder(value.Length * 4);
        foreach (var character in value)
        {
            result.Append(hex[(character >> 12) & 0xF]);
            result.Append(hex[(character >> 8) & 0xF]);
            result.Append(hex[(character >> 4) & 0xF]);
            result.Append(hex[character & 0xF]);
        }
        return result.ToString();
    }

    private static string UnicodeUpper(string value)
    {
        ValidateWellFormed(value, "Unicode ordinal-ignore-case strings must be well-formed UTF-16.");
        var result = new StringBuilder(value.Length * 6);
        const string format = "X6";
        for (var index = 0; index < value.Length;)
        {
            var scalar = char.ConvertToUtf32(value, index);
            index += scalar > 0xFFFF ? 2 : 1;
            var mapped = MapUnicodeScalar(scalar);
            result.Append(mapped.ToString(format, CultureInfo.InvariantCulture));
        }
        return result.ToString();
    }

    private static int MapUnicodeScalar(int scalar)
    {
        var mappings = UnicodeOrdinalCasingData.SimpleUppercaseMappings;
        var low = 0;
        var high = mappings.Length / 2 - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var index = middle * 2;
            var candidate = mappings[index];
            if (candidate == scalar)
                return mappings[index + 1];
            if (candidate < scalar)
                low = middle + 1;
            else
                high = middle - 1;
        }
        return scalar;
    }

    internal static string? CreateOrdinalSuccessor(string value)
    {
        ValidateWellFormed(value, "Ordinal strings must be well-formed UTF-16.");
        if (value.Length == 0)
            return null;

        var chars = value.ToCharArray();
        for (var index = chars.Length - 1; index >= 0; index--)
        {
            if (char.IsLowSurrogate(chars[index]))
            {
                // A low surrogate can be incremented within its pair. If it is already
                // maximal, carry across the complete scalar and retain the lowest valid
                // low surrogate for the incremented high surrogate.
                if (chars[index] != '\uDFFF')
                {
                    chars[index]++;
                    return new string(chars, 0, index + 1);
                }

                var highIndex = index - 1;
                if (highIndex >= 0 && char.IsHighSurrogate(chars[highIndex]) && chars[highIndex] != '\uDBFF')
                {
                    chars[highIndex]++;
                    chars[index] = '\uDC00';
                    return new string(chars, 0, index + 1);
                }

                if (highIndex >= 0 && chars[highIndex] == '\uDBFF')
                {
                    // In UTF-16 ordinal order the supplementary block is followed by
                    // the BMP range U+E000..U+FFFF. U+E000 is therefore the exact
                    // well-formed upper bound for a prefix ending in U+10FFFF.
                    return new string(chars, 0, highIndex) + "\uE000";
                }

                index = highIndex;
                continue;
            }

            if (chars[index] < '\uD7FF')
            {
                chars[index]++;
                return new string(chars, 0, index + 1);
            }

            if (chars[index] == '\uD7FF')
            {
                // The least valid string after the last non-surrogate BMP code unit is
                // the first supplementary scalar, not U+E000: UTF-16 ordinal ordering
                // places every well-formed surrogate pair before U+E000.
                return new string(chars, 0, index) + "\uD800\uDC00";
            }

            if (chars[index] is >= '\uE000' and < '\uFFFF')
            {
                chars[index]++;
                return new string(chars, 0, index + 1);
            }
        }

        return null;
    }

    internal static void ValidateWellFormed(string value, string message)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsLowSurrogate(value[index]))
                throw new ArgumentException(message, nameof(value));
            if (char.IsHighSurrogate(value[index]) &&
                (++index >= value.Length || !char.IsLowSurrogate(value[index])))
                throw new ArgumentException(message, nameof(value));
        }
    }
}
