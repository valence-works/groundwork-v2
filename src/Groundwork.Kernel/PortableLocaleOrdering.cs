using System.Globalization;

namespace Groundwork.Kernel;

/// <summary>Versioned ICU sort-key generation for persisted locale ordering.</summary>
public static class PortableLocaleOrdering
{
    public const string AlgorithmName = "groundwork-icu-sort-key-v1";

    public static string GetAlgorithmId(string cultureName)
    {
        var compareInfo = GetCompareInfo(cultureName);
        var version = compareInfo.Version;
        return FormattableString.Invariant(
            $"{AlgorithmName}:{compareInfo.Name}:{version.FullVersion}:{version.SortId:N}+{PortableStringComparison.SearchKeyAlgorithmId}");
    }

    public static string CreateSortKey(string value, string cultureName)
    {
        ArgumentNullException.ThrowIfNull(value);
        PortableSearchKeyEncoding.ValidateWellFormed(
            value,
            "Locale-ordered strings must be well-formed UTF-16.");
        var bytes = GetCompareInfo(cultureName).GetSortKey(value, CompareOptions.None).KeyData;
        return PortableSearchKeyEncoding.CreateSearchKeyFromComparisonKey(bytes);
    }

    internal static PortabilityRefusal? ValidateDeclaration(ColumnDefinition column, string path)
    {
        ArgumentNullException.ThrowIfNull(column);
        var declaration = column.LocaleSortKey;
        if (declaration is null)
            return null;
        if (column.Type != PortableType.String || column.MaxLength is not (> 0) ||
            declaration.MaximumExpansionFactor <= 0 ||
            SearchKeyProjection.IsFolded(SearchKeyProjection.LogicalCollation(column)))
        {
            return new(
                "GW-PORT-014",
                $"Column '{column.Name}' locale ordering requires a bounded String source, a positive MaximumExpansionFactor, and no folded collation.",
                path);
        }
        return ValidateRuntime(declaration.CultureName, path);
    }

    internal static LocaleSortKeyAlgorithmIdentity ParseAlgorithmId(string? value)
    {
        const string separator = "+" + PortableStringComparison.SearchKeyAlgorithmId;
        if (value is null || !value.EndsWith(separator, StringComparison.Ordinal))
            throw Stale(value);
        var identity = value[..^separator.Length].Split(':');
        if (identity.Length != 4 || identity[0] != AlgorithmName ||
            string.IsNullOrWhiteSpace(identity[1]) ||
            !int.TryParse(identity[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var fullVersion) ||
            !Guid.TryParseExact(identity[3], "N", out var sortId))
        {
            throw Stale(value);
        }

        var expected = GetAlgorithmId(identity[1]);
        if (!string.Equals(value, expected, StringComparison.Ordinal))
            throw Stale(value);
        return new(identity[1], fullVersion, sortId, value);
    }

    internal static PortabilityRefusal? ValidateRuntime(string? cultureName, string path)
    {
        if (string.IsNullOrWhiteSpace(cultureName))
            return Unsupported(path, cultureName, "a non-empty ICU culture name was not declared");
        var configured = ValidateRuntimeConfiguration(
            IsInvariantGlobalization(),
            OperatingSystem.IsWindows(),
            UsesNls(),
            cultureName,
            path);
        if (configured is not null)
            return configured;
        try
        {
            var compareInfo = CultureInfo.GetCultureInfo(cultureName ?? string.Empty).CompareInfo;
            // A linguistic probe catches invariant behavior even when a host did not expose the
            // configuration switch through AppContext.
            if (CultureInfo.GetCultureInfo("de-DE").CompareInfo.Compare("ä", "z", CompareOptions.None) >= 0)
            {
                return Unsupported(path, cultureName, "ordinal-equivalent globalization behavior");
            }
            _ = compareInfo.Version;
            return null;
        }
        catch (CultureNotFoundException)
        {
            return Unsupported(path, cultureName, "the requested ICU culture is unavailable");
        }
    }

    internal static PortabilityRefusal? ValidateRuntimeConfiguration(
        bool invariantGlobalization,
        bool isWindows,
        bool useNls,
        string? cultureName,
        string path)
    {
        if (invariantGlobalization)
            return Unsupported(path, cultureName, "InvariantGlobalization=true");
        if (isWindows && useNls)
            return Unsupported(path, cultureName, "System.Globalization.UseNls=true");
        return null;
    }

    private static CompareInfo GetCompareInfo(string cultureName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cultureName);
        var refusal = ValidateRuntime(cultureName, "cultureName");
        if (refusal is not null)
            throw new InvalidOperationException($"{refusal.Code} at {refusal.Path}: {refusal.Message}");
        return CultureInfo.GetCultureInfo(cultureName).CompareInfo;
    }

    private static bool IsInvariantGlobalization() =>
        SwitchEnabled("System.Globalization.Invariant") ||
        EnvironmentFlag("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT");

    private static bool UsesNls() =>
        SwitchEnabled("System.Globalization.UseNls") ||
        EnvironmentFlag("DOTNET_SYSTEM_GLOBALIZATION_USENLS");

    private static bool SwitchEnabled(string name) =>
        AppContext.TryGetSwitch(name, out var enabled) && enabled;

    private static bool EnvironmentFlag(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return value is "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static PortabilityRefusal Unsupported(string path, string? cultureName, string reason) => new(
        "GW-PORT-014",
        $"Locale sort key '{cultureName ?? "<missing>"}' requires real ICU collation; {reason} cannot produce portable persisted ordering. Disable invariant globalization and Windows NLS, then rebuild under a pinned ICU runtime.",
        path);

    private static InvalidOperationException Stale(string? value) => new(
        $"Locale sort-key algorithm identity '{value ?? "<missing>"}' is unknown, stale, or malformed. Rebuild the derived locale sort-key column before use.");
}

internal sealed record LocaleSortKeyAlgorithmIdentity(
    string CultureName,
    int FullVersion,
    Guid SortId,
    string Value);
