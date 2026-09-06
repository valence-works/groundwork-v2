using System.Text.Json;

namespace Groundwork.PostgreSql;

internal static class PostgreSqlExplainPlanInspector
{
    internal static bool ChoseIndex(string rawPlan, string physicalIndex)
    {
        try
        {
            using var document = JsonDocument.Parse(rawPlan);
            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.GetArrayLength() != 1 ||
                document.RootElement[0].ValueKind != JsonValueKind.Object ||
                !document.RootElement[0].TryGetProperty("Plan", out var plan) ||
                plan.ValueKind != JsonValueKind.Object)
                return false;
            return PostgreSqlNativePlanMapper.ContainsChosenIndex(plan, physicalIndex);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
