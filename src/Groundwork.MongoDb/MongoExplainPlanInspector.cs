using MongoDB.Bson;

namespace Groundwork.MongoDb;

internal static class MongoExplainPlanInspector
{
    internal static bool ChoseIndex(BsonDocument explain, string physicalIndex) =>
        FindWinningPlans(explain).Any(plan => ContainsIndexScan(plan, physicalIndex));

    internal static bool WinningPlanContainsStage(BsonDocument explain, string stage) =>
        FindWinningPlans(explain).Any(plan => ContainsStage(plan, stage));

    private static IEnumerable<BsonValue> FindWinningPlans(BsonValue value)
    {
        if (value is BsonDocument document)
        {
            foreach (var element in document)
            {
                if (string.Equals(element.Name, "winningPlan", StringComparison.Ordinal))
                    yield return element.Value;
                foreach (var nested in FindWinningPlans(element.Value))
                    yield return nested;
            }
        }
        else if (value is BsonArray array)
        {
            foreach (var item in array)
            foreach (var nested in FindWinningPlans(item))
                yield return nested;
        }
    }

    private static bool ContainsIndexScan(BsonValue value, string physicalIndex)
    {
        if (value is BsonDocument document)
        {
            if (document.TryGetValue("stage", out var stage) && stage.IsString && stage.AsString == "IXSCAN" &&
                document.TryGetValue("indexName", out var indexName) && indexName.IsString &&
                string.Equals(indexName.AsString, physicalIndex, StringComparison.Ordinal))
                return true;
            return document.Any(element => ContainsIndexScan(element.Value, physicalIndex));
        }
        return value is BsonArray array && array.Any(item => ContainsIndexScan(item, physicalIndex));
    }

    private static bool ContainsStage(BsonValue value, string expectedStage)
    {
        if (value is BsonDocument document)
        {
            if (document.TryGetValue("stage", out var stage) && stage.IsString &&
                string.Equals(stage.AsString, expectedStage, StringComparison.Ordinal))
                return true;
            return document.Any(element => ContainsStage(element.Value, expectedStage));
        }
        return value is BsonArray array && array.Any(item => ContainsStage(item, expectedStage));
    }
}
