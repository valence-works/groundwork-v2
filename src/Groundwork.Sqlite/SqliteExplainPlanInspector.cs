using System.Text.RegularExpressions;

namespace Groundwork.Sqlite;

internal static class SqliteExplainPlanInspector
{
    internal static bool ChoseIndex(string rawPlan, string physicalIndex) => Regex.IsMatch(
        rawPlan,
        @"\bUSING\s+(?:COVERING\s+)?INDEX\s+[\""""`\[]?" + Regex.Escape(physicalIndex) + @"[\""""`\]]?(?=\s|\(|$)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
}
