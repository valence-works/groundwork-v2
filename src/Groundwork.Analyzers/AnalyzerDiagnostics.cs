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

    private static DiagnosticDescriptor Create(string id, string title, string message) => new(
        id,
        title,
        message,
        "Groundwork.QueryCoverage",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
