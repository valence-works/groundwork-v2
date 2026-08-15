using System.Security.Cryptography;
using System.Text;

namespace Groundwork.Kernel;

public enum PortableStringComparisonPolicy
{
    Ordinal,
    AsciiIgnoreCase,
    UnicodeOrdinalIgnoreCase
}

/// <summary>A validated, versioned identity for one persisted boundary search-key algorithm.</summary>
public sealed record PortableSearchKeyAlgorithmIdentity
{
    private PortableSearchKeyAlgorithmIdentity(PortableStringComparisonPolicy policy, string value)
    {
        Policy = policy;
        Value = value;
    }

    public PortableStringComparisonPolicy Policy { get; }

    public string Value { get; }

    public static PortableSearchKeyAlgorithmIdentity ForPolicy(PortableStringComparisonPolicy policy) =>
        new(policy, PortableStringComparison.GetSearchKeyAlgorithmId(policy));

    public static PortableSearchKeyAlgorithmIdentity Parse(string? value)
    {
        foreach (var policy in Enum.GetValues<PortableStringComparisonPolicy>())
        {
            var expected = ForPolicy(policy);
            if (string.Equals(value, expected.Value, StringComparison.Ordinal))
                return expected;
        }

        throw new InvalidOperationException(
            $"Search-key algorithm identity '{value ?? "<missing>"}' is unknown, stale, or malformed. Rebuild the derived search-key column before use.");
    }

    public override string ToString() => Value;
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
    public const string SearchKeySuccessorAlgorithmId = "groundwork-search-key-successor-v1";
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
        return PortableSearchKeyEncoding.CreateComparisonKey(value, PortableSearchKeyPolicy.Ordinal);
    }

    public static string CreateUnicodeOrdinalIgnoreCase(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return PortableSearchKeyEncoding.CreateComparisonKey(value, PortableSearchKeyPolicy.UnicodeOrdinalIgnoreCase);
    }

    public static string Create(string value, PortableStringComparisonPolicy policy) => policy switch
    {
        PortableStringComparisonPolicy.Ordinal => CreateOrdinal(value),
        PortableStringComparisonPolicy.AsciiIgnoreCase => CreateAsciiIgnoreCase(value),
        PortableStringComparisonPolicy.UnicodeOrdinalIgnoreCase => CreateUnicodeOrdinalIgnoreCase(value),
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, null)
    };

    public static string CreateSearchKey(string value, PortableStringComparisonPolicy policy) =>
        PortableSearchKeyEncoding.Create(value, ToSearchKeyPolicy(policy));

    /// <summary>
    /// Returns the exclusive upper bound for a non-empty encoded prefix. A null result means the
    /// prefix ends at the maximum representable encoded unit and therefore has no finite successor.
    /// </summary>
    public static string? CreateSearchKeySuccessor(string searchKey)
    {
        return PortableSearchKeyEncoding.CreateSuccessor(searchKey, PortableSearchKeyPolicy.AsciiIgnoreCase);
    }

    /// <summary>Returns the composite identity for a folded search-key projection.</summary>
    public static string GetSearchKeyAlgorithmId(PortableStringComparisonPolicy policy) =>
        $"{GetAlgorithmId(policy)}+{SearchKeyAlgorithmId}+{SearchKeySuccessorAlgorithmId}";

    public static string CreateSearchKeyFromComparisonKey(
        string comparisonKey,
        PortableStringComparisonPolicy policy)
        => PortableSearchKeyEncoding.CreateSearchKeyFromComparisonKey(comparisonKey, ToSearchKeyPolicy(policy));

    private static PortableSearchKeyPolicy ToSearchKeyPolicy(PortableStringComparisonPolicy policy) => policy switch
    {
        PortableStringComparisonPolicy.Ordinal => PortableSearchKeyPolicy.Ordinal,
        PortableStringComparisonPolicy.AsciiIgnoreCase => PortableSearchKeyPolicy.AsciiIgnoreCase,
        PortableStringComparisonPolicy.UnicodeOrdinalIgnoreCase => PortableSearchKeyPolicy.UnicodeOrdinalIgnoreCase,
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, null)
    };

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
        return PortableSearchKeyEncoding.CreateComparisonKey(value, PortableSearchKeyPolicy.AsciiIgnoreCase);
    }

    private static UnicodeOrdinalIgnoreCaseState CreateUnicodeOrdinalIgnoreCaseState()
    {
        var generatedMappings = UnicodeOrdinalCasingData.SimpleUppercaseMappings;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> pair = stackalloc byte[8];
        for (var index = 0; index < generatedMappings.Length; index += 2)
        {
            var scalar = generatedMappings[index];
            var mapped = generatedMappings[index + 1];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(pair, scalar);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(pair[4..], mapped);
            hash.AppendData(pair);
        }
        var fingerprint = Convert.ToHexStringLower(hash.GetHashAndReset());
        return new($"{UnicodeOrdinalIgnoreCaseAlgorithmName}-{fingerprint}");
    }

    private sealed record UnicodeOrdinalIgnoreCaseState(string AlgorithmId);
}
