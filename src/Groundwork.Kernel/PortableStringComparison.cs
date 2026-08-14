using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Groundwork.Kernel;

public enum PortableStringComparisonPolicy
{
    Ordinal,
    AsciiIgnoreCase,
    UnicodeOrdinalIgnoreCase
}

public enum StringIdentityCasePolicy
{
    Ordinal,
    UnicodeOrdinalIgnoreCase
}

public readonly record struct PortableStringIdentityProjection(
    string OriginalValue,
    string ComparisonKey,
    string LookupKey,
    string ComparisonAlgorithmId,
    string LookupAlgorithmId)
{
    public string ComparisonKeyHash => LookupKey;
}

/// <summary>
/// Versioned provider-neutral comparison and lookup projections. Tables opt into these projections
/// through derived columns; no declaration pays for a comparison key unless it declares one.
/// </summary>
public static class PortableStringComparison
{
    public const int MaximumIdentityCodeUnits = 450;
    public const string OrdinalAlgorithmId = "groundwork-utf16-hex-v1";
    public const string AsciiIgnoreCaseAlgorithmId = "groundwork-ascii-lower-v1";
    public const string LookupHashAlgorithmId = "groundwork-sha256-utf8-lowerhex-v1";
    public const string SearchKeyAlgorithmId = "groundwork-boundary-delimited-search-key-v1";
    private const string UnicodeOrdinalIgnoreCaseAlgorithmName = "groundwork-unicode-ordinal-ignore-case-v1";

    private static readonly Lazy<UnicodeOrdinalIgnoreCaseState> UnicodeOrdinalIgnoreCase = new(
        CreateUnicodeOrdinalIgnoreCaseState);

    public static string UnicodeOrdinalIgnoreCaseAlgorithmId => UnicodeOrdinalIgnoreCase.Value.AlgorithmId;

    public static bool IsWellFormedUnicode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsLowSurrogate(character))
                return false;
            if (!char.IsHighSurrogate(character))
                continue;
            if (++index >= value.Length || !char.IsLowSurrogate(value[index]))
                return false;
        }
        return true;
    }

    public static string CreateOrdinal(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!IsWellFormedUnicode(value))
            throw new ArgumentException("Ordinal strings must be well-formed UTF-16.", nameof(value));
        return CreateUtf16Hex(value);
    }

    public static string CreateUnicodeOrdinalIgnoreCase(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!IsWellFormedUnicode(value))
            throw new ArgumentException("Unicode ordinal-ignore-case strings must be well-formed UTF-16.", nameof(value));

        var mappings = UnicodeOrdinalIgnoreCase.Value.Mappings;
        var normalized = new StringBuilder(value.Length * 6);
        Span<char> encoded = stackalloc char[6];
        foreach (var rune in value.EnumerateRunes())
        {
            var mapped = mappings.GetValueOrDefault(rune.Value, rune.Value);
            mapped.TryFormat(encoded, out _, "X6", CultureInfo.InvariantCulture);
            normalized.Append(encoded);
        }
        return normalized.ToString();
    }

    public static string Create(string value, PortableStringComparisonPolicy policy) => policy switch
    {
        PortableStringComparisonPolicy.Ordinal => CreateOrdinal(value),
        PortableStringComparisonPolicy.AsciiIgnoreCase => CreateAsciiIgnoreCase(value),
        PortableStringComparisonPolicy.UnicodeOrdinalIgnoreCase => CreateUnicodeOrdinalIgnoreCase(value),
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, null)
    };

    public static string CreateSearchKey(string value, PortableStringComparisonPolicy policy) =>
        CreateSearchKeyFromComparisonKey(Create(value, policy), policy);

    public static string CreateSearchKeyFromComparisonKey(
        string comparisonKey,
        PortableStringComparisonPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(comparisonKey);
        var encodedUnitLength = policy switch
        {
            PortableStringComparisonPolicy.Ordinal => 4,
            PortableStringComparisonPolicy.UnicodeOrdinalIgnoreCase => 6,
            PortableStringComparisonPolicy.AsciiIgnoreCase => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, null)
        };
        if (comparisonKey.Length % encodedUnitLength != 0)
        {
            throw new ArgumentException(
                "The comparison key does not contain complete encoded comparison units.",
                nameof(comparisonKey));
        }

        if (policy == PortableStringComparisonPolicy.AsciiIgnoreCase)
        {
            return string.Create(comparisonKey.Length * 5, comparisonKey, static (buffer, source) =>
            {
                const string hex = "0123456789ABCDEF";
                for (var index = 0; index < source.Length; index++)
                {
                    var character = source[index];
                    var offset = index * 5;
                    buffer[offset] = '|';
                    buffer[offset + 1] = hex[(character >> 12) & 0xF];
                    buffer[offset + 2] = hex[(character >> 8) & 0xF];
                    buffer[offset + 3] = hex[(character >> 4) & 0xF];
                    buffer[offset + 4] = hex[character & 0xF];
                }
            });
        }

        var unitCount = comparisonKey.Length / encodedUnitLength;
        return string.Create(
            comparisonKey.Length + unitCount,
            (comparisonKey, encodedUnitLength),
            static (buffer, state) =>
            {
                var target = 0;
                for (var source = 0; source < state.comparisonKey.Length; source += state.encodedUnitLength)
                {
                    buffer[target++] = '|';
                    state.comparisonKey.AsSpan(source, state.encodedUnitLength).CopyTo(buffer[target..]);
                    target += state.encodedUnitLength;
                }
            });
    }

    public static string CreateBoundedPrefix(string comparisonKey, int maximumLength)
    {
        ArgumentNullException.ThrowIfNull(comparisonKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLength);
        return comparisonKey.Length <= maximumLength ? comparisonKey : comparisonKey[..maximumLength];
    }

    public static string CreateHash(string comparisonKey)
    {
        ArgumentNullException.ThrowIfNull(comparisonKey);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(comparisonKey)));
    }

    public static PortableStringIdentityProjection ProjectIdentity(
        string value,
        PortableStringComparisonPolicy policy)
    {
        var comparisonKey = Create(value, policy);
        return new(value, comparisonKey, CreateHash(comparisonKey), GetAlgorithmId(policy), LookupHashAlgorithmId);
    }

    public static void ValidateIdentity(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > MaximumIdentityCodeUnits)
            throw new ArgumentException(
                $"Identities may contain at most {MaximumIdentityCodeUnits} UTF-16 code units.",
                nameof(value));
    }

    public static string GetAlgorithmId(PortableStringComparisonPolicy policy) => policy switch
    {
        PortableStringComparisonPolicy.Ordinal => OrdinalAlgorithmId,
        PortableStringComparisonPolicy.AsciiIgnoreCase => AsciiIgnoreCaseAlgorithmId,
        PortableStringComparisonPolicy.UnicodeOrdinalIgnoreCase => UnicodeOrdinalIgnoreCaseAlgorithmId,
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, null)
    };

    public static PortableStringComparisonPolicy ForIdentityPolicy(StringIdentityCasePolicy policy) => policy switch
    {
        StringIdentityCasePolicy.Ordinal => PortableStringComparisonPolicy.Ordinal,
        StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase => PortableStringComparisonPolicy.UnicodeOrdinalIgnoreCase,
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, null)
    };

    public static bool IsAsciiIgnoreCaseValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.All(character => character is >= ' ' and <= '~');
    }

    public static string CreateAsciiIgnoreCase(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!IsAsciiIgnoreCaseValue(value))
            throw new ArgumentException(
                "ASCII-ignore-case values may contain only U+0020 through U+007E.",
                nameof(value));
        return string.Create(value.Length, value, static (buffer, source) =>
        {
            for (var index = 0; index < source.Length; index++)
            {
                var character = source[index];
                buffer[index] = character is >= 'A' and <= 'Z' ? (char)(character + ('a' - 'A')) : character;
            }
        });
    }

    private static string CreateUtf16Hex(string value) =>
        string.Create(value.Length * 4, value, static (buffer, source) =>
        {
            const string hex = "0123456789ABCDEF";
            for (var index = 0; index < source.Length; index++)
            {
                var character = source[index];
                var offset = index * 4;
                buffer[offset] = hex[(character >> 12) & 0xF];
                buffer[offset + 1] = hex[(character >> 8) & 0xF];
                buffer[offset + 2] = hex[(character >> 4) & 0xF];
                buffer[offset + 3] = hex[character & 0xF];
            }
        });

    private static UnicodeOrdinalIgnoreCaseState CreateUnicodeOrdinalIgnoreCaseState()
    {
        var mappings = new Dictionary<int, int>();
        for (var scalar = 0; scalar <= 0x10FFFF; scalar++)
        {
            if (!Rune.IsValid(scalar))
                continue;
            var sourceRune = new Rune(scalar);
            var mapped = Rune.ToUpperInvariant(sourceRune).Value;
            if (mapped == scalar)
                continue;
            if (!StringComparer.OrdinalIgnoreCase.Equals(sourceRune.ToString(), new Rune(mapped).ToString()))
                continue;
            mappings.Add(scalar, mapped);
        }

        var supplementaryMappings = UnicodeOrdinalCasingData.SupplementarySimpleUppercaseMappings;
        for (var index = 0; index < supplementaryMappings.Length; index += 2)
        {
            var scalar = supplementaryMappings[index];
            var mapped = supplementaryMappings[index + 1];
            if (mappings.ContainsKey(scalar))
                continue;
            if (!StringComparer.OrdinalIgnoreCase.Equals(new Rune(scalar).ToString(), new Rune(mapped).ToString()))
                continue;
            mappings.Add(scalar, mapped);
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> pair = stackalloc byte[8];
        foreach (var (scalar, mapped) in mappings.OrderBy(mapping => mapping.Key))
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(pair, scalar);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(pair[4..], mapped);
            hash.AppendData(pair);
        }
        var fingerprint = Convert.ToHexStringLower(hash.GetHashAndReset());
        return new(mappings.ToFrozenDictionary(), $"{UnicodeOrdinalIgnoreCaseAlgorithmName}-{fingerprint}");
    }

    private sealed record UnicodeOrdinalIgnoreCaseState(FrozenDictionary<int, int> Mappings, string AlgorithmId);
}
