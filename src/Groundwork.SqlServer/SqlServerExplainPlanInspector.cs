using System.Xml.Linq;

namespace Groundwork.SqlServer;

internal static class SqlServerExplainPlanInspector
{
    internal static bool ChoseIndex(string rawPlan, string physicalIndex)
    {
        try
        {
            var document = XDocument.Parse(rawPlan, LoadOptions.None);
            return document.Descendants()
                .Where(element => element.Name.LocalName == "RelOp" &&
                                  string.Equals((string?)element.Attribute("PhysicalOp"), "Index Seek", StringComparison.Ordinal))
                .SelectMany(element => element.Descendants().Where(descendant => descendant.Name.LocalName == "Object"))
                .Select(element => Normalize((string?)element.Attribute("Index")))
                .Any(index => string.Equals(index, physicalIndex, StringComparison.Ordinal));
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
    }

    private static string? Normalize(string? index) => index?.Trim().Trim('[', ']');
}
