using Groundwork.Kernel;
using Groundwork.Query.Model;
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
    private const string SetProbeValueField = "__groundwork_aggregation_set_probe_value";
    private const string SetProbeCountField = "__groundwork_aggregation_set_probe_count";

    private AggregationResult ExecuteNativeAggregation(
        AggregationProfile profile,
        AggregationQuery query)
    {
        try
        {
            return ExecuteNativeAggregationCore(profile, query);
        }
        catch (AggregationBudgetExceededException)
        {
            throw;
        }
        catch (OverflowException exception)
        {
            throw SumOverflow(profile, exception);
        }
        catch (MongoCommandException exception) when (
            profile.Aggregates.Any(aggregate => aggregate is Aggregate.Sum) &&
            exception.Message.Contains("overflow", StringComparison.OrdinalIgnoreCase))
        {
            throw SumOverflow(profile, exception);
        }
    }

    private AggregationResult ExecuteNativeAggregationCore(
        AggregationProfile profile,
        AggregationQuery query)
    {
        AggregationExecutor.ValidateQuery(Unit, profile, query);
        var sourceFilter = query.SourcePredicate is null
            ? null
            : new MongoQueryRenderer().RenderAggregationSourcePredicate(query.SourcePredicate, Unit.Name);
        VerifyNativeAggregationBudgets(profile, sourceFilter);
        var stages = RenderNativeAggregationPipeline(Unit, profile, query, sourceFilter);

        var documents = RunAggregationPipeline(stages);

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
                    Groundwork.Kernel.Aggregate.Count => null,
                    _ => throw new InvalidOperationException("Unknown aggregate declaration.")
                };
                var sourceColumn = source is null
                    ? null
                    : Unit.Columns.Single(column => column.Name == source);
                values[aggregate.Alias] = aggregate switch
                {
                    Groundwork.Kernel.Aggregate.SetUnion => DecodeSet(document.GetValue(aggregate.Alias, new BsonArray())),
                    Aggregate.Sum sum when document[aggregate.Alias].IsBsonNull => null,
                    Aggregate.Sum sum when sourceColumn!.Type is PortableType.Int32 or PortableType.Int64 =>
                        document[aggregate.Alias].ToInt64(),
                    Aggregate.Sum sum when sourceColumn!.Type == PortableType.Decimal =>
                        DecodeDecimalSum(document[aggregate.Alias], sum),
                    Groundwork.Kernel.Aggregate.Count => document[aggregate.Alias].ToInt64(),
                    _ => Decode(document.GetValue(aggregate.Alias, BsonNull.Value), sourceColumn!)
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
        return new AggregationResult(
            rows,
            AggregationQueryFingerprint.CreateShapeFingerprint(Unit, profile, query),
            Access.Scope is null
                ? AggregationQueryFingerprint.Create(Unit, profile, query)
                : AggregationQueryFingerprint.Create(Unit, profile, query, Access.Scope));
    }

    internal static IReadOnlyList<BsonDocument> RenderNativeAggregationPipeline(
        StorageUnit unit,
        AggregationProfile profile,
        AggregationQuery query,
        BsonDocument? sourceFilter = null)
    {
        AggregationExecutor.ValidateQuery(unit, profile, query);
        var stages = new List<BsonDocument>();
        if (sourceFilter is not null)
            stages.Add(new BsonDocument("$match", sourceFilter));
        stages.Add(new BsonDocument("$limit", (long)profile.MaxInputRows + 1L));

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
                case Groundwork.Kernel.Aggregate.Count count:
                    group[count.Alias] = new BsonDocument("$sum", 1);
                    break;
                case Aggregate.Sum sum:
                    group[sum.Alias] = new BsonDocument("$sum", Field(sum.Column));
                    group[SumCountField(sum.Alias)] = new BsonDocument("$sum", NonNullFlag(sum.Column));
                    break;
                case Aggregate.SetUnion set:
                    group[SetValuesField(set.Alias)] = new BsonDocument("$addToSet", Field(set.Column));
                    break;
                case Aggregate.FirstBy first:
                {
                    var firstSort = new BsonDocument
                    {
                        [first.OrderColumn] = first.Direction == KernelSortDirection.Descending ? -1 : 1
                    };
                    foreach (var keyColumn in unit.Key.Columns)
                        if (!string.Equals(keyColumn, first.OrderColumn, StringComparison.Ordinal))
                            firstSort[keyColumn] = 1;
                    group[first.Alias] = new BsonDocument("$top", new BsonDocument
                    {
                        ["sortBy"] = firstSort,
                        ["output"] = Field(first.Column)
                    });
                    break;
                }
                default:
                    throw new InvalidOperationException("Unknown aggregate declaration.");
            }
        }

        stages.Add(new BsonDocument("$group", group));
        // The extra group is a refusal probe. It is deliberately before any output
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
        if (query.PostPredicate is not null)
            stages.Add(new BsonDocument("$match", RenderPredicate(query.PostPredicate, unit, profile)));

        var sortOutput = new BsonDocument();
        foreach (var term in AggregationQueryFingerprint.EffectiveOrderTerms(query, profile))
        {
            var sortField = term.Alias;
            if (OutputType(unit, profile, term.Alias) == PortableType.String)
            {
                sortField = OrderKeyField(term.Alias);
                stages.Add(new BsonDocument("$set", new BsonDocument
                {
                    [sortField] = MongoQueryRenderer.RenderOrdinalKey("$" + term.Alias)
                }));
            }
            sortOutput[sortField] = term.Direction == KernelSortDirection.Descending ? -1 : 1;
        }
        if (sortOutput.ElementCount != 0)
            stages.Add(new BsonDocument("$sort", sortOutput));
        if (query.Take is int pageLimit)
            stages.Add(new BsonDocument("$limit", pageLimit));
        return stages;
    }

    private void VerifyNativeAggregationBudgets(AggregationProfile profile, BsonDocument? sourceFilter)
    {
        var inputStages = new List<BsonDocument>();
        if (sourceFilter is not null)
            inputStages.Add(new BsonDocument("$match", sourceFilter));
        inputStages.Add(new BsonDocument("$limit", (long)profile.MaxInputRows + 1L));
        inputStages.Add(new BsonDocument("$count", "count"));
        var inputEvidence = RunAggregationPipeline(inputStages).SingleOrDefault();
        if (inputEvidence?.GetValue("count", 0).ToInt64() > profile.MaxInputRows)
            throw new AggregationBudgetExceededException(
                "GW-AGG-BOUND-004",
                $"Aggregation profile '{profile.Name}' refused more than MaxInputRows={profile.MaxInputRows}; input was not truncated.");

        var groupIdentity = new BsonDocument();
        foreach (var column in profile.GroupByColumns)
            groupIdentity[column] = Field(column);
        var groupStages = new List<BsonDocument>();
        if (sourceFilter is not null)
            groupStages.Add(new BsonDocument("$match", sourceFilter));
        groupStages.Add(new BsonDocument("$limit", (long)profile.MaxInputRows + 1L));
        groupStages.Add(new BsonDocument("$group", new BsonDocument { ["_id"] = groupIdentity }));
        groupStages.Add(new BsonDocument("$limit", (long)profile.MaxGroups + 1L));
        var groups = RunAggregationPipeline(groupStages);
        if (groups.Count > profile.MaxGroups)
            throw new AggregationBudgetExceededException(
                "GW-AGG-BOUND-005",
                $"Aggregation profile '{profile.Name}' refused more than MaxGroups={profile.MaxGroups}; groups were not truncated.");

        foreach (var set in profile.Aggregates.OfType<Aggregate.SetUnion>())
        {
            var evidence = RunAggregationPipeline(RenderSetBudgetProbe(profile, set, sourceFilter));
            if (evidence.Count != 0)
                throw new AggregationBudgetExceededException(
                    "GW-AGG-BOUND-007",
                    $"SetUnion '{set.Alias}' refused more than MaxValues={set.MaxValues}; values were not truncated.");
        }
    }

    internal static IReadOnlyList<BsonDocument> RenderSetBudgetProbe(
        AggregationProfile profile,
        Aggregate.SetUnion set,
        BsonDocument? sourceFilter = null)
    {
        var distinctIdentity = new BsonDocument();
        foreach (var column in profile.GroupByColumns)
            distinctIdentity[column] = new BsonString("$" + column);
        distinctIdentity[SetProbeValueField] = new BsonString("$" + set.Column);

        var groupByDistinct = new BsonDocument();
        foreach (var column in profile.GroupByColumns)
            groupByDistinct[column] = new BsonString("$_id." + column);
        var stages = new List<BsonDocument>();
        if (sourceFilter is not null)
            stages.Add(new BsonDocument("$match", sourceFilter));
        stages.Add(new BsonDocument("$limit", (long)profile.MaxInputRows + 1L));
        stages.Add(new BsonDocument
        {
            ["$match"] = new BsonDocument
            {
                [set.Column] = new BsonDocument("$ne", BsonNull.Value)
            }
        });
        stages.Add(new BsonDocument("$group", new BsonDocument { ["_id"] = distinctIdentity }));
        stages.Add(new BsonDocument("$group", new BsonDocument
        {
            ["_id"] = groupByDistinct,
            [SetProbeCountField] = new BsonDocument("$sum", 1)
        }));
        stages.Add(new BsonDocument("$match", new BsonDocument(SetProbeCountField,
            new BsonDocument("$gt", set.MaxValues))));
        stages.Add(new BsonDocument("$limit", 1L));
        return stages;
    }

    private List<BsonDocument> RunAggregationPipeline(IEnumerable<BsonDocument> stages)
    {
        var pipeline = PipelineDefinition<BsonDocument, BsonDocument>.Create(stages);
        var options = new AggregateOptions { Collation = new Collation("simple") };
        return (transactionSession is null
            ? collection.Aggregate(pipeline, options)
            : collection.Aggregate(transactionSession, pipeline, options)).ToList();
    }

    private static BsonDocument RenderPredicate(
        AggregationPredicate predicate,
        StorageUnit unit,
        AggregationProfile profile) => predicate switch
    {
        AggregationPredicate.All all => new BsonDocument("$and",
            new BsonArray(all.Predicates.Select(child => RenderPredicate(child, unit, profile)))),
        AggregationPredicate.Any any => new BsonDocument("$or",
            new BsonArray(any.Predicates.Select(child => RenderPredicate(child, unit, profile)))),
        AggregationPredicate.Comparison comparison => RenderComparison(comparison, unit, profile),
        _ => throw new AggregationValidationException([new("GW-AGG-PRED-006", "The aggregation predicate is not renderable.", "postPredicate")])
    };

    private static BsonDocument RenderComparison(
        AggregationPredicate.Comparison comparison,
        StorageUnit unit,
        AggregationProfile profile)
    {
        var values = comparison.Values!;
        var encoded = values.Select(value => EncodePredicateValue(unit, profile, comparison.Alias, value)).ToArray();
        var condition = comparison.Operator switch
        {
            AggregationPredicateOperator.Equal => encoded[0],
            AggregationPredicateOperator.In => new BsonDocument("$in", new BsonArray(encoded)),
            AggregationPredicateOperator.RangeInclusive => new BsonDocument
            {
                ["$gte"] = encoded[0],
                ["$lte"] = encoded[1]
            },
            AggregationPredicateOperator.Contains => new BsonDocument("$elemMatch", new BsonDocument("$eq", encoded[0])),
            _ => throw new AggregationValidationException([new("GW-AGG-PRED-009", "The aggregation predicate is not renderable.", "postPredicate")])
        };
        return new BsonDocument(comparison.Alias, condition);
    }

    private static BsonValue EncodePredicateValue(
        StorageUnit unit,
        AggregationProfile profile,
        string alias,
        object? value)
    {
        var aggregate = profile.Aggregates.Single(item => item.Alias == alias);
        var source = aggregate switch
        {
            Aggregate.Min min => min.Column,
            Aggregate.Max max => max.Column,
            Aggregate.Sum sum => sum.Column,
            Aggregate.SetUnion set => set.Column,
            Aggregate.FirstBy first => first.Column,
            Groundwork.Kernel.Aggregate.Count => null,
            _ => throw new InvalidOperationException("Unknown aggregate declaration.")
        };
        if (aggregate is Groundwork.Kernel.Aggregate.Count)
            return MongoValueCodec.Encode(value, new ColumnDefinition
            {
                Name = alias,
                Type = PortableType.Int64,
                IsNullable = true
            });
        var sourceColumn = unit.Columns.Single(column => column.Name == source);
        var outputType = aggregate is Aggregate.Sum && sourceColumn.Type is (PortableType.Int32 or PortableType.Int64)
            ? PortableType.Int64
            : sourceColumn.Type;
        return MongoValueCodec.Encode(value, sourceColumn with
        {
            Name = alias,
            Type = outputType,
            IsNullable = true
        });
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

    private static string OrderKeyField(string alias) => "__groundwork_aggregation_order_key_" + alias;

    private static PortableType OutputType(StorageUnit unit, AggregationProfile profile, string alias)
    {
        if (profile.GroupByColumns.Contains(alias, StringComparer.Ordinal))
            return unit.Columns.Single(column => column.Name == alias).Type;
        var aggregate = profile.Aggregates.Single(item => item.Alias == alias);
        return aggregate switch
        {
            Groundwork.Kernel.Aggregate.Count => PortableType.Int64,
            Groundwork.Kernel.Aggregate.SetUnion => PortableType.String,
            Groundwork.Kernel.Aggregate.Sum sum when unit.Columns.Single(column => column.Name == sum.Column).Type is PortableType.Int32 or PortableType.Int64 => PortableType.Int64,
            Groundwork.Kernel.Aggregate.Min min => unit.Columns.Single(column => column.Name == min.Column).Type,
            Groundwork.Kernel.Aggregate.Max max => unit.Columns.Single(column => column.Name == max.Column).Type,
            Groundwork.Kernel.Aggregate.Sum sum => unit.Columns.Single(column => column.Name == sum.Column).Type,
            Groundwork.Kernel.Aggregate.FirstBy first => unit.Columns.Single(column => column.Name == first.Column).Type,
            _ => throw new InvalidOperationException("Unknown aggregate declaration.")
        };
    }

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

    private static AggregationBudgetExceededException SumOverflow(
        AggregationProfile profile,
        Exception exception) => new(
            "GW-AGG-SUM-001",
            $"Sum in aggregation profile '{profile.Name}' overflowed the declared portable result type.")
        {
            Source = exception.Source
        };
}
