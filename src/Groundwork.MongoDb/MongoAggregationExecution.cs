using Groundwork.Kernel;
using Groundwork.Substrate.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;
using KernelSortDirection = Groundwork.Kernel.SortDirection;

namespace Groundwork.MongoDb;

internal sealed partial class MongoStorageSession
{
    private const string InputCountField = "__groundwork_aggregation_input_count";
    private const string SetValuesPrefix = "__groundwork_aggregation_set_values_";
    private const string SumCountPrefix = "__groundwork_aggregation_sum_count_";
    private const string MinValuePrefix = "__groundwork_aggregation_min_value_";
    private const string MaxValuePrefix = "__groundwork_aggregation_max_value_";

    private AggregationResult ExecuteNativeAggregation(
        AggregationProfile profile,
        AggregationQuery query)
    {
        var stages = new List<BsonDocument>
        {
            new("$limit", (long)profile.MaxInputRows + 1L)
        };

        var firstBy = profile.Aggregates.OfType<Aggregate.FirstBy>().ToArray();
        if (firstBy.Length != 0)
        {
            var sort = new BsonDocument();
            foreach (var first in firstBy)
                sort[first.OrderColumn] = first.Direction == KernelSortDirection.Descending ? -1 : 1;
            // _id is a stable provider-owned tie break for equal FirstBy order values.
            sort["_id"] = 1;
            stages.Add(new BsonDocument("$sort", sort));
        }

        var setStage = new BsonDocument();
        foreach (var aggregate in profile.Aggregates)
        {
            if (aggregate is Aggregate.Min min)
                setStage[MinValueField(min.Alias)] = NonNullField(min.Column);
            else if (aggregate is Aggregate.Max max)
                setStage[MaxValueField(max.Alias)] = NonNullField(max.Column);
        }
        if (setStage.ElementCount != 0)
            stages.Add(new BsonDocument("$set", setStage));

        var group = new BsonDocument
        {
            [InputCountField] = new BsonDocument("$sum", 1)
        };
        var identity = new BsonDocument();
        foreach (var column in profile.GroupByColumns)
            identity[column] = Field(column);
        group["_id"] = identity;

        foreach (var aggregate in profile.Aggregates)
        {
            switch (aggregate)
            {
                case Aggregate.Min min:
                    group[min.Alias] = new BsonDocument("$min", Field(MinValueField(min.Alias)));
                    break;
                case Aggregate.Max max:
                    group[max.Alias] = new BsonDocument("$max", Field(MaxValueField(max.Alias)));
                    break;
                case Aggregate.Sum sum:
                    group[sum.Alias] = new BsonDocument("$sum", Field(sum.Column));
                    group[SumCountField(sum.Alias)] = new BsonDocument("$sum", NonNullFlag(sum.Column));
                    break;
                case Aggregate.SetUnion set:
                    group[SetValuesField(set.Alias)] = new BsonDocument("$addToSet", Field(set.Column));
                    break;
                case Aggregate.FirstBy first:
                    group[first.Alias] = new BsonDocument("$first", Field(first.Column));
                    break;
                default:
                    throw new InvalidOperationException("Unknown aggregate declaration.");
            }
        }

        stages.Add(new BsonDocument("$group", group));
        // The extra group is a refusal probe.  It is deliberately before any output
        // page limit so a caller can never turn an over-budget result into truncation.
        stages.Add(new BsonDocument("$limit", (long)profile.MaxGroups + 1L));

        var projection = new BsonDocument { [InputCountField] = 1 };
        foreach (var column in profile.GroupByColumns)
            projection[column] = "$_id." + column;
        foreach (var aggregate in profile.Aggregates)
        {
            switch (aggregate)
            {
                case Aggregate.Sum sum:
                    projection[sum.Alias] = new BsonDocument("$cond", new BsonArray
                    {
                        new BsonDocument("$gt", new BsonArray { "$" + SumCountField(sum.Alias), 0 }),
                        "$" + sum.Alias,
                        BsonNull.Value
                    });
                    break;
                case Aggregate.SetUnion set:
                    projection[set.Alias] = "$" + SetValuesField(set.Alias);
                    break;
                default:
                    projection[aggregate.Alias] = 1;
                    break;
            }
        }
        stages.Add(new BsonDocument("$project", projection));

        var sortOutput = new BsonDocument();
        if (query.OrderBy is not null)
        {
            if (!profile.GroupByColumns.Contains(query.OrderBy, StringComparer.Ordinal) &&
                !profile.Aggregates.Any(aggregate => aggregate.Alias == query.OrderBy))
                throw new AggregationValidationException([new(
                    "GW-AGG-QUERY-002",
                    $"Order alias '{query.OrderBy}' is not declared by profile '{profile.Name}'.",
                    "orderBy")]);
            sortOutput[query.OrderBy] = query.OrderDirection == KernelSortDirection.Descending ? -1 : 1;
        }
        else
        {
            foreach (var column in profile.GroupByColumns)
                sortOutput[column] = 1;
        }
        if (sortOutput.ElementCount != 0)
            stages.Add(new BsonDocument("$sort", sortOutput));

        var pipeline = PipelineDefinition<BsonDocument, BsonDocument>.Create(stages);
        var documents = transactionSession is null
            ? collection.Aggregate(pipeline).ToList()
            : collection.Aggregate(transactionSession, pipeline).ToList();

        var inputCount = 0L;
        var rows = new List<AggregationRow>(documents.Count);
        foreach (var document in documents)
        {
            inputCount = checked(inputCount + document[InputCountField].ToInt64());
            if (inputCount > profile.MaxInputRows)
                throw new AggregationBudgetExceededException(
                    "GW-AGG-BOUND-004",
                    $"Aggregation profile '{profile.Name}' refused more than MaxInputRows={profile.MaxInputRows}; input was not truncated.");

            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var columnName in profile.GroupByColumns)
            {
                var column = Unit.Columns.Single(item => item.Name == columnName);
                values[columnName] = Decode(document.GetValue(columnName, BsonNull.Value), column);
            }
            foreach (var aggregate in profile.Aggregates)
            {
                var source = aggregate switch
                {
                    Aggregate.Min min => min.Column,
                    Aggregate.Max max => max.Column,
                    Aggregate.Sum sum => sum.Column,
                    Aggregate.SetUnion set => set.Column,
                    Aggregate.FirstBy first => first.Column,
                    _ => throw new InvalidOperationException("Unknown aggregate declaration.")
                };
                var sourceColumn = Unit.Columns.Single(column => column.Name == source);
                values[aggregate.Alias] = aggregate switch
                {
                    Groundwork.Kernel.Aggregate.SetUnion => DecodeSet(document.GetValue(aggregate.Alias, new BsonArray())),
                    Aggregate.Sum sum when document[aggregate.Alias].IsBsonNull => null,
                    Aggregate.Sum sum when sourceColumn.Type is PortableType.Int32 or PortableType.Int64 =>
                        document[aggregate.Alias].ToInt64(),
                    Aggregate.Sum sum when sourceColumn.Type == PortableType.Decimal =>
                        DecodeDecimalSum(document[aggregate.Alias], sum),
                    _ => Decode(document.GetValue(aggregate.Alias, BsonNull.Value), sourceColumn)
                };
                if (aggregate is Aggregate.SetUnion setAggregate &&
                    values[aggregate.Alias] is string[] setValues && setValues.Length > setAggregate.MaxValues)
                    throw new AggregationBudgetExceededException(
                        "GW-AGG-BOUND-007",
                        $"SetUnion '{setAggregate.Alias}' refused more than MaxValues={setAggregate.MaxValues}; values were not truncated.");
            }
            rows.Add(new AggregationRow(values));
        }

        if (rows.Count > profile.MaxGroups)
            throw new AggregationBudgetExceededException(
                "GW-AGG-BOUND-005",
                $"Aggregation profile '{profile.Name}' refused more than MaxGroups={profile.MaxGroups}; groups were not truncated.");
        if (query.Take is <= 0)
            throw new AggregationValidationException([new("GW-AGG-QUERY-003", "Aggregation Take must be positive when specified.", "take")]);
        if (query.Take is int take && take > profile.MaxGroups)
            throw new AggregationBudgetExceededException("GW-AGG-BOUND-006", $"Aggregation Take={take} exceeds MaxGroups={profile.MaxGroups}.");
        if (query.Take is int pageSize)
            rows = rows.Take(pageSize).ToList();
        return new AggregationResult(rows);
    }

    private static BsonDocument NonNullField(string name) =>
        new("$cond", new BsonArray { new BsonDocument("$ne", new BsonArray { Field(name), BsonNull.Value }), Field(name), "$$REMOVE" });

    private static BsonDocument NonNullFlag(string name) =>
        new("$cond", new BsonArray { new BsonDocument("$ne", new BsonArray { Field(name), BsonNull.Value }), 1, 0 });

    private static BsonString Field(string name) => new("$" + name);

    private static string MinValueField(string alias) => MinValuePrefix + alias;

    private static string MaxValueField(string alias) => MaxValuePrefix + alias;

    private static string SumCountField(string alias) => SumCountPrefix + alias;

    private static string SetValuesField(string alias) => SetValuesPrefix + alias;

    private static object? Decode(BsonValue value, ColumnDefinition column) =>
        value.IsBsonNull ? null : MongoValueCodec.Decode(value, column);

    private static decimal DecodeDecimalSum(BsonValue value, Aggregate.Sum sum)
    {
        var decoded = MongoValueCodec.Decode(value, new ColumnDefinition
        {
            Name = sum.Column,
            Type = PortableType.Decimal,
            IsNullable = true,
            Precision = 34,
            Scale = 28
        });
        if (decoded is decimal result)
            return result;
        throw new AggregationBudgetExceededException(
            "GW-AGG-SUM-001",
            $"Sum '{sum.Column}' overflowed the declared portable result type.");
    }

    private static string[] DecodeSet(BsonValue value) => value is not BsonArray array
        ? []
        : array.Where(item => !item.IsBsonNull).Select(item => item.AsString)
            .Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal).ToArray();
}
