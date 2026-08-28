using Microsoft.CodeAnalysis;

namespace Groundwork.Analyzers;

internal static class AnalyzerDiagnostics
{
    // Roslyn diagnostic identifiers cannot contain the hyphenated gate spelling. Keep the
    // published GW-COVER-* code in every message while using the compiler-valid identifier here.
    public const string UnresolvableId = "GW_COVER_900";
    public const string UnresolvableCode = "GW-COVER-900";

    private static readonly IReadOnlyDictionary<string, DiagnosticDescriptor> Descriptors =
        new Dictionary<string, DiagnosticDescriptor>(StringComparer.Ordinal)
        {
            ["GW-COVER-005"] = Create("GW_COVER_005", "Query has no portable bound", "{0}"),
            ["GW-COVER-006"] = Create("GW_COVER_006", "Query is not covered by a declared index", "{0}"),
            ["GW-COVER-009"] = Create("GW_COVER_009", "Sparse index coverage is not proven", "{0}"),
            ["GW-COVER-016"] = Create("GW_COVER_016", "Query shape is not portable", "{0}"),
            ["GW-COVER-901"] = Create("GW_COVER_901", "Accepted scan is stale", "{0}", DiagnosticSeverity.Error),
            ["GW-COVER-902"] = Create("GW_COVER_902", "Accepted scans are not enabled", "{0}", DiagnosticSeverity.Error),
            ["GW-COVER-903"] = Create("GW_COVER_903", "Accepted scan has expired", "{0}", DiagnosticSeverity.Error),
            ["GW-COVER-904"] = Create("GW_COVER_904", "Accepted scan is expiring", "{0}", DiagnosticSeverity.Warning),
            ["GW-COVER-905"] = Create("GW_COVER_905", "Accepted scan inventory", "{0}", DiagnosticSeverity.Info),
            ["GW-LINQ-101"] = Create("GW_LINQ_101", "Column expression is not portable", "{0}"),
            ["GW-LINQ-102"] = Create("GW_LINQ_102", "Column arithmetic is not portable", "{0}"),
            ["GW-LINQ-103"] = Create("GW_LINQ_103", "Column comparison requires scan acceptance", "{0}"),
            ["GW-LINQ-104"] = Create("GW_LINQ_104", "Joins are not portable", "{0}"),
            ["GW-LINQ-105"] = Create("GW_LINQ_105", "GroupBy is not portable", "{0}"),
            ["GW-LINQ-106"] = Create("GW_LINQ_106", "Nested collection predicate is not portable", "{0}"),
            ["GW-LINQ-107"] = Create("GW_LINQ_107", "Opaque helper is not portable", "{0}"),
            ["GW-LINQ-108"] = Create("GW_LINQ_108", "String comparison must be explicit", "{0}"),
            ["GW-LINQ-109"] = Create("GW_LINQ_109", "Instant must be UTC", "{0}"),
            ["GW-LINQ-110"] = Create("GW_LINQ_110", "Constant is outside the declared type", "{0}"),
            ["GW-LINQ-111"] = Create("GW_LINQ_111", "First/FirstOrDefault query requires deterministic order", "{0}"),
            ["GW-LINQ-112"] = Create("GW_LINQ_112", "Reduction terminal requires a mapped portable column", "{0}"),
            ["GW-LINQ-113"] = Create("GW_LINQ_113", "Skip requires a bounded Take", "{0}"),
        };

    public static DiagnosticDescriptor Unresolvable { get; } = new(
        UnresolvableId,
        "Query shape cannot be resolved statically",
        "{0}",
        "Groundwork.QueryCoverage",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The analyzer cannot prove a bounded query shape. Use WhereIf or a runtime coverage gate.");

    public static DiagnosticDescriptor For(string code) =>
        Descriptors.TryGetValue(code, out var descriptor) ? descriptor : Descriptors["GW-COVER-006"];

    private static DiagnosticDescriptor Create(
        string id,
        string title,
        string message,
        DiagnosticSeverity severity = DiagnosticSeverity.Error) => new(
        id,
        title,
        message,
        "Groundwork.QueryCoverage",
        severity,
        isEnabledByDefault: true);
}
