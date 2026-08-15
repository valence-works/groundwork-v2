using System.Text.Json;

namespace Groundwork.PostgreSql;

internal static class PostgreSqlExplainPlanInspector
{
    internal static bool ChoseIndex(string rawPlan, string physicalIndex)
    {
        try
        {
            using var document = JsonDocument.Parse(rawPlan);
            return ContainsIndexScan(document.RootElement, physicalIndex);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ContainsIndexScan(JsonElement element, string physicalIndex)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("Node Type", out var nodeType) &&
                element.TryGetProperty("Index Name", out var indexName) &&
                nodeType.ValueKind == JsonValueKind.String &&
                indexName.ValueKind == JsonValueKind.String &&
                nodeType.GetString() is "Index Scan" or "Index Only Scan" &&
                string.Equals(indexName.GetString(), physicalIndex, StringComparison.Ordinal))
                return true;
            return element.EnumerateObject().Any(property => ContainsIndexScan(property.Value, physicalIndex));
        }
        return element.ValueKind == JsonValueKind.Array &&
               element.EnumerateArray().Any(item => ContainsIndexScan(item, physicalIndex));
    }
}
