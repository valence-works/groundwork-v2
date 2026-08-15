using System.Text;

namespace Groundwork.Query.Linq;

/// <summary>The versioned diagnostic vocabulary used by the corpus and documentation generator.</summary>
public static class LinqDiagnosticCatalog
{
    public static IReadOnlyList<(string Code, string AstEquivalent)> Entries { get; } = new[]
    {
        ("GW-LINQ-101", "declare a computed column; expressions over columns are not portable"),
        ("GW-LINQ-102", "declare a computed column; expressions over columns are not portable"),
        ("GW-LINQ-103", "add `.AcceptScan(...)`"),
        ("GW-LINQ-104", "v2 has no joins; use a declared element set or two queries"),
        ("GW-LINQ-105", "use `.LatestPer(...)` for grouped top-1"),
        ("GW-LINQ-106", "declare the element set"),
        ("GW-LINQ-107", "mark it `[GwQueryFragment]`"),
        ("GW-LINQ-108", "use Ordinal/OrdinalIgnoreCase matching the column's folding"),
        ("GW-LINQ-109", "use `DateTimeOffset.UtcNow`"),
        ("GW-LINQ-110", "the value has more scale/range than `decimal(10,2)`")
    };

    public static string GenerateMarkdownTable()
    {
        var builder = new StringBuilder("| Code | AST equivalent / fix |\n| --- | --- |\n");
        foreach (var entry in Entries)
            builder.Append("| ").Append(entry.Code).Append(" | ").Append(char.ToUpperInvariant(entry.AstEquivalent[0])).Append(entry.AstEquivalent.Substring(1)).Append(". |\n");
        return builder.ToString();
    }
}
