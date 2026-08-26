using Groundwork.Kernel;
using Groundwork.Query.Model;
using Groundwork.Store;
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
        var effectiveSource = AggregationGrouping.EffectiveSourcePredicate(Unit, profile, query);
        var sourceFilter = effectiveSource is Predicate.AlwaysTrue
            ? null
            : new MongoQueryRenderer().RenderAggregationSourcePredicate(effectiveSource, Unit.Name);
        var hasTimeBucket = AggregationGrouping.TimeBucket(profile) is not null;
        if (!hasTimeBucket)
            VerifyNativeAggregationBudgets(profile, query, sourceFilter);
        var stages = RenderNativeAggregationPipeline(Unit, profile, query, sourceFilter);

        var documents = RunAggregationPipeline(stages, isProbe: false);

        var rows = new List<AggregationRow>(documents.Count);
        foreach (var document in documents)
        {
            var inputCount = document[InputCountField].ToInt64();
            if (inputCount > profile.MaxInputRows)
                throw new AggregationBudgetExceededException(
                    "GW-AGG-BOUND-004",
                    $"Aggregation profile '{profile.Name}' refused more than MaxInputRows={profile.MaxInputRows}; input was not truncated.");

            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var group in AggregationGrouping.EffectiveGroups(profile))
            {
                var column = Unit.Columns.Single(item => item.Name == AggregationGrouping.SourceColumn(group));
                values[group.Alias] = Decode(document.GetValue(group.Alias, BsonNull.Value), column);
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
        if (hasTimeBucket)
            rows = AggregationExecutor.ApplyResultQuery(Unit, profile, query, rows).ToList();
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
        if (sourceFilter is null)
        {
            var effectiveSource = AggregationGrouping.EffectiveSourcePredicate(unit, profile, query);
            if (effectiveSource is not Predicate.AlwaysTrue)
                sourceFilter = new MongoQueryRenderer().RenderAggregationSourcePredicate(effectiveSource, unit.Name);
        }
        var stages = new List<BsonDocument>();
        var hasTimeBucket = AggregationGrouping.TimeBucket(profile) is not null;
        if (sourceFilter is not null)
            stages.Add(new BsonDocument("$match", sourceFilter));
        stages.Add(new BsonDocument("$limit", (long)profile.MaxInputRows + 1L));
        if (hasTimeBucket)
            stages.Add(new BsonDocument("$setWindowFields", new BsonDocument
            {
                ["output"] = new BsonDocument
                {
                    [InputCountField] = new BsonDocument
                    {
                        ["$sum"] = 1,
                        ["window"] = new BsonDocument("documents", new BsonArray { "unbounded", "unbounded" })
                    }
                }
            }));

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
            [InputCountField] = hasTimeBucket
                ? new BsonDocument("$max", "$" + InputCountField)
                : new BsonDocument("$sum", 1)
        };
        var identity = new BsonDocument();
        foreach (var groupDescriptor in AggregationGrouping.EffectiveGroups(profile))
            identity[groupDescriptor.Alias] = RenderGroupExpression(profile, query, groupDescriptor);
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
        foreach (var groupDescriptor in AggregationGrouping.EffectiveGroups(profile))
            projection[groupDescriptor.Alias] = "$_id." + groupDescriptor.Alias;
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
        if (query.PostPredicate is not null && !hasTimeBucket)
            stages.Add(new BsonDocument("$match", RenderPredicate(query.PostPredicate, unit, profile)));

        var sortOutput = new BsonDocument();
        foreach (var term in hasTimeBucket ? [] : AggregationQueryFingerprint.EffectiveOrderTerms(query, profile))
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
        if (query.Take is int pageLimit && AggregationGrouping.TimeBucket(profile) is null)
            stages.Add(new BsonDocument("$limit", pageLimit));
        return stages;
    }

    private void VerifyNativeAggregationBudgets(
        AggregationProfile profile,
        AggregationQuery query,
        BsonDocument? sourceFilter)
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
        foreach (var group in AggregationGrouping.EffectiveGroups(profile))
            groupIdentity[group.Alias] = RenderGroupExpression(profile, query, group);
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
            var evidence = RunAggregationPipeline(RenderSetBudgetProbe(profile, set, sourceFilter, query));
            if (evidence.Count != 0)
                throw new AggregationBudgetExceededException(
                    "GW-AGG-BOUND-007",
                    $"SetUnion '{set.Alias}' refused more than MaxValues={set.MaxValues}; values were not truncated.");
        }
    }

    internal static IReadOnlyList<BsonDocument> RenderSetBudgetProbe(
        AggregationProfile profile,
        Aggregate.SetUnion set,
        BsonDocument? sourceFilter = null,
        AggregationQuery? query = null)
    {
        query ??= AggregationQuery.For(profile.Name);
        var distinctIdentity = new BsonDocument();
        foreach (var group in AggregationGrouping.EffectiveGroups(profile))
            distinctIdentity[group.Alias] = RenderGroupExpression(profile, query, group);
        distinctIdentity[SetProbeValueField] = new BsonString("$" + set.Column);

        var groupByDistinct = new BsonDocument();
        foreach (var group in AggregationGrouping.EffectiveGroups(profile))
            groupByDistinct[group.Alias] = new BsonString("$_id." + group.Alias);
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

    private List<BsonDocument> RunAggregationPipeline(IEnumerable<BsonDocument> stages, bool isProbe = true)
    {
        var pipeline = PipelineDefinition<BsonDocument, BsonDocument>.Create(stages);
        var options = new AggregateOptions { Collation = new Collation("simple") };
        AggregationExecutionDiagnostics.Observe("aggregate");
        // The single funnel for native aggregation commands: every call is exactly one provider round
        // trip, observed here so a budget probe and the main pipeline are both counted at issue.
        commandObserver?.Observe(new ProviderCommandEvent(
            "mongodb.aggregate", "MongoDB.Aggregate(pipeline)", ProviderCommandKind.Read, IsProbe: isProbe));
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

    private static BsonValue RenderGroupExpression(
        AggregationProfile profile,
        AggregationQuery query,
        AggregationGroup group)
    {
        if (group is AggregationGroup.Column column)
            return Field(column.Alias);

        var bucket = (AggregationGroup.TimeBucket)group;
        var field = Field(bucket.SourceColumn);
        const long unixTicks = 621355968000000000L;
        if (bucket.Kind == AggregationTimeBucketKind.FixedUtc)
        {
            var origin = AggregationGrouping.FixedUtcOrigin(profile, query)?.UtcTicks ?? unixTicks;
            // Decimal128 keeps all .NET ticks (including a one-tick boundary adversary) exact;
            // BSON double arithmetic would round these ~6e17 values before flooring.
            var offset = new BsonDocument("$subtract", new BsonArray
            {
                new BsonDocument("$toDecimal", field),
                new BsonDecimal128(origin)
            });
            var bucketOffset = new BsonDocument("$multiply", new BsonArray
            {
                new BsonDocument("$floor", new BsonDocument("$divide", new BsonArray
                {
                    offset,
                    new BsonDecimal128(bucket.Width.Ticks)
                })),
                new BsonDecimal128(bucket.Width.Ticks)
            });
            return new BsonDocument("$toLong", new BsonDocument("$add", new BsonArray
            {
                new BsonDecimal128(origin),
                bucketOffset
            }));
        }

        var milliseconds = new BsonDocument("$toDate", new BsonDocument("$floor", new BsonDocument("$divide", new BsonArray
        {
            new BsonDocument("$subtract", new BsonArray
            {
                new BsonDocument("$toDecimal", field),
                new BsonDecimal128(unixTicks)
            }),
            new BsonDecimal128(10_000L)
        })));
        var timeZoneId = AggregationGrouping.LocalTimeZoneId(profile, query);
        var localMidnightText = new BsonDocument("$dateToString", new BsonDocument
        {
            ["date"] = milliseconds,
            ["format"] = "%Y-%m-%dT00:00:00.000Z",
            ["timezone"] = timeZoneId
        });
        var localMidnightWallClock = new BsonDocument("$dateFromString", new BsonDocument
        {
            ["dateString"] = localMidnightText
        });
        // MongoDB also selects the post-transition occurrence of ambiguous local midnight.
        // The prior local noon supplies the pre-transition offset without relying on an
        // ambiguous 23:xx wall time; the date check below rejects unrelated earlier changes.
        var previousLocalNoonWallClock = new BsonDocument("$dateSubtract", new BsonDocument
        {
            ["startDate"] = localMidnightWallClock,
            ["unit"] = "hour",
            ["amount"] = 12
        });
        var previousLocalNoonText = new BsonDocument("$dateToString", new BsonDocument
        {
            ["date"] = previousLocalNoonWallClock,
            ["format"] = "%Y-%m-%dT%H:%M:%S.%L"
        });
        var previousLocalNoonInstant = new BsonDocument("$dateFromString", new BsonDocument
        {
            ["dateString"] = previousLocalNoonText,
            ["timezone"] = timeZoneId
        });
        var priorOffsetCandidate = new BsonDocument("$dateAdd", new BsonDocument
        {
            ["startDate"] = previousLocalNoonInstant,
            ["unit"] = "hour",
            ["amount"] = 12
        });
        var defaultMidnight = new BsonDocument("$dateTrunc", new BsonDocument
        {
            ["date"] = milliseconds,
            ["unit"] = "day",
            ["timezone"] = timeZoneId
        });
        var sourceLocalDate = new BsonDocument("$dateToString", new BsonDocument
        {
            ["date"] = milliseconds,
            ["format"] = "%Y-%m-%d",
            ["timezone"] = timeZoneId
        });
        var candidateLocalDate = new BsonDocument("$dateToString", new BsonDocument
        {
            ["date"] = priorOffsetCandidate,
            ["format"] = "%Y-%m-%d",
            ["timezone"] = timeZoneId
        });
        var earliestValidInstant = new BsonDocument("$cond", new BsonArray
        {
            new BsonDocument("$eq", new BsonArray { sourceLocalDate, candidateLocalDate }),
            new BsonDocument("$min", new BsonArray { defaultMidnight, priorOffsetCandidate }),
            defaultMidnight
        });
        return new BsonDocument("$add", new BsonArray
        {
            unixTicks,
            new BsonDocument("$multiply", new BsonArray { new BsonDocument("$toLong", earliestValidInstant), 10_000L })
        });
    }

    private static string MinValueField(string alias) => MinValuePrefix + alias;

    private static string MaxValueField(string alias) => MaxValuePrefix + alias;

    private static string SumCountField(string alias) => SumCountPrefix + alias;

    private static string SetValuesField(string alias) => SetValuesPrefix + alias;

    private static string OrderKeyField(string alias) => "__groundwork_aggregation_order_key_" + alias;

    private static PortableType OutputType(StorageUnit unit, AggregationProfile profile, string alias)
    {
        if (AggregationGrouping.EffectiveGroups(profile).Any(group => group.Alias == alias))
        {
            var group = AggregationGrouping.EffectiveGroups(profile).Single(group => group.Alias == alias);
            return unit.Columns.Single(column => column.Name == AggregationGrouping.SourceColumn(group)).Type;
        }
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
