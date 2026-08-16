using System.Collections.Immutable;
using Groundwork.Kernel;
using Groundwork.Query.Model;
using Xunit;

namespace Groundwork.Kernel.Tests;

public sealed class AggregationProfileTests
{
    [Fact]
    public void Declaration_refuses_incompatible_types_and_duplicate_names()
    {
        var unit = Unit(
            new ColumnDefinition { Name = "group", Type = PortableType.String, IsNullable = false },
            new ColumnDefinition { Name = "payload", Type = PortableType.Boolean },
            new ColumnDefinition { Name = "order", Type = PortableType.Int64, IsNullable = false });
        var invalid = Profile(
            new Aggregate.Sum("group", "payload"),
            new Aggregate.Min("group", "order"));

        var exception = Assert.Throws<AggregationValidationException>(() => AggregationProfileValidator.Validate(unit, invalid));

        Assert.Contains(exception.Errors, error => error.Code == "GW-AGG-TYPE-001");
        Assert.Contains(exception.Errors, error => error.Code == "GW-AGG-DECL-007");
    }

    [Fact]
    public void Executor_uses_portable_empty_null_sum_and_set_union_semantics()
    {
        var unit = Unit(
            new ColumnDefinition { Name = "group", Type = PortableType.String, IsNullable = true },
            new ColumnDefinition { Name = "amount", Type = PortableType.Int32 },
            new ColumnDefinition { Name = "label", Type = PortableType.String },
            new ColumnDefinition { Name = "order", Type = PortableType.Int64, IsNullable = false });
        var profile = Profile(
            new Aggregate.Sum("total", "amount"),
            new Aggregate.Min("minimum", "amount"),
            new Aggregate.SetUnion("labels", "label", 3),
            new Aggregate.FirstBy("first", "label", "order"));
        var rows = new IReadOnlyDictionary<string, object?>[]
        {
            new Dictionary<string, object?> { ["group"] = "a", ["amount"] = null, ["label"] = "z", ["order"] = 2L },
            new Dictionary<string, object?> { ["group"] = "a", ["amount"] = 4, ["label"] = null, ["order"] = 1L },
            new Dictionary<string, object?> { ["group"] = null, ["amount"] = null, ["label"] = null, ["order"] = 3L }
        };

        var result = AggregationExecutor.Execute(unit, profile, rows);

        var a = Assert.Single(result.Rows, row => Equals(row["group"], "a"));
        Assert.Equal(4L, Assert.IsType<long>(a["total"]));
        Assert.Equal(4, Assert.IsType<int>(a["minimum"]));
        Assert.Equal(new[] { "z" }, Assert.IsAssignableFrom<IEnumerable<string>>(a["labels"]));
        Assert.Null(a["first"]);
        var empty = Assert.Single(result.Rows, row => row["group"] is null);
        Assert.Null(empty["total"]);
        Assert.Null(empty["minimum"]);
        Assert.Empty(Assert.IsAssignableFrom<IEnumerable<string>>(empty["labels"]));
    }

    [Fact]
    public void Executor_refuses_input_group_and_set_union_overflow_instead_of_truncating()
    {
        var unit = Unit(
            new ColumnDefinition { Name = "group", Type = PortableType.String },
            new ColumnDefinition { Name = "value", Type = PortableType.String },
            new ColumnDefinition { Name = "order", Type = PortableType.Int64, IsNullable = false });
        var profile = Profile(new Aggregate.SetUnion("values", "value", 1)) with
        {
            MaxInputRows = 1,
            MaxGroups = 1
        };
        var rows = new IReadOnlyDictionary<string, object?>[]
        {
            new Dictionary<string, object?> { ["group"] = "a", ["value"] = "a", ["order"] = 1L },
            new Dictionary<string, object?> { ["group"] = "a", ["value"] = "b", ["order"] = 2L }
        };

        var exception = Assert.Throws<AggregationBudgetExceededException>(() => AggregationExecutor.Execute(unit, profile, rows));

        Assert.Equal("GW-AGG-BOUND-004", exception.Code);
    }

    [Fact]
    public void Executor_uses_structural_group_keys_when_values_contain_the_internal_separator()
    {
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("aggregation-structural-groups"),
            Name = "aggregation_structural_groups",
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, IsNullable = false },
                new() { Name = "left", Type = PortableType.String, IsNullable = false },
                new() { Name = "right", Type = PortableType.String, IsNullable = false },
                new() { Name = "amount", Type = PortableType.Int64, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] }
        };
        var profile = new AggregationProfile
        {
            Name = "summary",
            GroupByColumns = ["left", "right"],
            Aggregates = [new Aggregate.Sum("total", "amount")]
        };
        var rows = new IReadOnlyDictionary<string, object?>[]
        {
            new Dictionary<string, object?> { ["id"] = "1", ["left"] = "a", ["right"] = "b\u001fs:c", ["amount"] = 1L },
            new Dictionary<string, object?> { ["id"] = "2", ["left"] = "a\u001fs:b", ["right"] = "c", ["amount"] = 2L }
        };

        var result = AggregationExecutor.Execute(unit, profile, rows);

        Assert.Equal(2, result.Rows.Count);
        Assert.Contains(result.Rows, row => Equals(row["right"], "b\u001fs:c") && Equals(row["total"], 1L));
        Assert.Contains(result.Rows, row => Equals(row["left"], "a\u001fs:b") && Equals(row["total"], 2L));
    }

    [Fact]
    public void FirstBy_breaks_equal_order_values_by_the_declared_key()
    {
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("aggregation-first-tie"),
            Name = "aggregation_first_tie",
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, IsNullable = false },
                new() { Name = "group", Type = PortableType.String, IsNullable = false },
                new() { Name = "label", Type = PortableType.String },
                new() { Name = "order", Type = PortableType.Int64, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] }
        };
        var profile = new AggregationProfile
        {
            Name = "summary",
            GroupByColumns = ["group"],
            Aggregates = [new Aggregate.FirstBy("first", "label", "order")]
        };
        var rows = new IReadOnlyDictionary<string, object?>[]
        {
            new Dictionary<string, object?> { ["id"] = "b", ["group"] = "g", ["label"] = "wrong", ["order"] = 1L },
            new Dictionary<string, object?> { ["id"] = "a", ["group"] = "g", ["label"] = "right", ["order"] = 1L }
        };

        var row = Assert.Single(AggregationExecutor.Execute(unit, profile, rows).Rows);

        Assert.Equal("right", row["first"]);
    }

    [Fact]
    public void SetUnion_aliases_are_not_orderable_outputs()
    {
        var unit = Unit(
            new ColumnDefinition { Name = "group", Type = PortableType.String },
            new ColumnDefinition { Name = "label", Type = PortableType.String });
        var profile = Profile(new Aggregate.SetUnion("labels", "label", 2));

        var exception = Assert.Throws<AggregationValidationException>(() => AggregationExecutor.Execute(
            unit,
            profile,
            [new Dictionary<string, object?> { ["group"] = "g", ["label"] = "a" }],
            new AggregationQuery("summary") { OrderBy = "labels" }));

        Assert.Contains(exception.Errors, error => error.Code == "GW-AGG-QUERY-005");
    }

    [Fact]
    public void Source_predicate_admission_binds_to_declared_columns_and_portability()
    {
        var unit = Unit(
            new ColumnDefinition { Name = "group", Type = PortableType.String },
            new ColumnDefinition { Name = "amount", Type = PortableType.Int64 });
        var profile = Profile(new Aggregate.Sum("total", "amount"));
        var amount = new ColumnRef(new TableId(unit.Name), "amount", QueryType.String);

        var exception = Assert.Throws<AggregationValidationException>(() => AggregationExecutor.ValidateQuery(
            unit,
            profile,
            new AggregationQuery("summary")
            {
                SourcePredicate = new Predicate.Equal(amount, QueryConstant.Of(amount, "wrong"))
            }));

        Assert.Contains(exception.Errors, error => error.Code == "GW-AGG-SOURCE-003");

        var wrongTable = new ColumnRef(new TableId("other_table"), "amount", QueryType.Int64);
        var tableException = Assert.Throws<AggregationValidationException>(() => AggregationExecutor.ValidateQuery(
            unit,
            profile,
            new AggregationQuery("summary")
            {
                SourcePredicate = new Predicate.Equal(wrongTable, QueryConstant.Of(wrongTable, 7L))
            }));

        Assert.Contains(tableException.Errors, error => error.Code == "GW-AGG-SOURCE-002");
    }

    [Fact]
    public void Source_predicate_admission_refuses_element_sets_as_a_closed_surface()
    {
        var unit = Unit(
            new ColumnDefinition { Name = "group", Type = PortableType.String },
            new ColumnDefinition { Name = "amount", Type = PortableType.Int64 });
        var profile = Profile(new Aggregate.Sum("total", "amount"));

        var exception = Assert.Throws<AggregationValidationException>(() => AggregationExecutor.ValidateQuery(
            unit,
            profile,
            new AggregationQuery("summary")
            {
                SourcePredicate = new Predicate.ElementOf(
                    new ElementSetRef("labels", QueryType.String),
                    [QueryConstant.Of("plain")],
                    SetQuantifier.Any)
            }));

        Assert.Contains(exception.Errors, error => error.Code == "GW-AGG-SOURCE-005");
    }

    [Fact]
    public void Aggregation_query_fingerprints_bind_source_values_without_changing_shape()
    {
        var unit = Unit(
            new ColumnDefinition { Name = "group", Type = PortableType.String },
            new ColumnDefinition { Name = "amount", Type = PortableType.Int64 });
        var profile = Profile(new Aggregate.Sum("total", "amount"));
        var amount = new ColumnRef(new TableId(unit.Name), "amount", QueryType.Int64);
        var first = new AggregationQuery("summary")
        {
            SourcePredicate = new Predicate.Equal(amount, QueryConstant.Of(amount, 7L))
        };
        var second = new AggregationQuery("summary")
        {
            SourcePredicate = new Predicate.Equal(amount, QueryConstant.Of(amount, 11L))
        };

        Assert.Equal(
            AggregationQueryFingerprint.CreateShapeFingerprint(unit, profile, first),
            AggregationQueryFingerprint.CreateShapeFingerprint(unit, profile, second));
        Assert.NotEqual(
            AggregationQueryFingerprint.Create(unit, profile, first),
            AggregationQueryFingerprint.Create(unit, profile, second));
        Assert.Contains("int64", PredicateCanonicalizer.ToCanonicalString(first.SourcePredicate!), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Source_predicate_literal_budget_is_refused_before_execution()
    {
        var unit = Unit(
            new ColumnDefinition { Name = "group", Type = PortableType.String },
            new ColumnDefinition { Name = "amount", Type = PortableType.Int64 });
        var profile = Profile(new Aggregate.Sum("total", "amount"));
        var amount = new ColumnRef(new TableId(unit.Name), "amount", QueryType.Int64);
        var values = Enumerable.Range(0, 1_001).Select(value => QueryConstant.Of(amount, (long)value));
        var query = new AggregationQuery("summary")
        {
            SourcePredicate = new Predicate.In(amount, values)
        };

        var exception = Assert.Throws<AggregationBudgetExceededException>(() => AggregationExecutor.ValidateQuery(unit, profile, query));

        Assert.Equal("GW-AGG-BOUND-008", exception.Code);
    }

    [Fact]
    public void StartsWith_source_predicates_are_refused_until_a_persisted_search_projection_exists()
    {
        var unit = Unit(
            new ColumnDefinition { Name = "group", Type = PortableType.String },
            new ColumnDefinition { Name = "label", Type = PortableType.String });
        var profile = Profile(new Aggregate.SetUnion("labels", "label", 2));
        var label = new ColumnRef(new TableId(unit.Name), "label", QueryType.String);

        var exception = Assert.Throws<AggregationValidationException>(() => AggregationExecutor.ValidateQuery(
            unit,
            profile,
            new AggregationQuery("summary")
            {
                SourcePredicate = new Predicate.StartsWith(label, "pre")
            }));

        Assert.Contains(exception.Errors, error => error.Code == "GW-AGG-SOURCE-007");
    }

    [Fact]
    public void Count_and_order_terms_produce_deterministic_top_n_results()
    {
        var unit = Unit(
            new ColumnDefinition { Name = "group", Type = PortableType.String },
            new ColumnDefinition { Name = "amount", Type = PortableType.Int64 });
        var profile = Profile(new Aggregate.Count("count"), new Aggregate.Sum("total", "amount"));
        var rows = new IReadOnlyDictionary<string, object?>[]
        {
            new Dictionary<string, object?> { ["group"] = "b", ["amount"] = 1L },
            new Dictionary<string, object?> { ["group"] = "a", ["amount"] = 1L },
            new Dictionary<string, object?> { ["group"] = "b", ["amount"] = 1L }
        };

        var result = AggregationExecutor.Execute(unit, profile, rows, new AggregationQuery("summary")
        {
            OrderByTerms = [
                new AggregationOrderTerm("count", SortDirection.Descending),
                new AggregationOrderTerm("group", SortDirection.Ascending)
            ],
            Take = 5
        });

        Assert.Equal(["b", "a"], result.Rows.Select(row => row["group"]));
        Assert.Equal([2L, 1L], result.Rows.Select(row => row["count"]));
    }

    [Fact]
    public void Aggregation_order_terms_refuse_duplicate_aliases_and_invalid_directions()
    {
        var unit = Unit(
            new ColumnDefinition { Name = "group", Type = PortableType.String },
            new ColumnDefinition { Name = "amount", Type = PortableType.Int64 });
        var profile = Profile(new Aggregate.Count("count"));

        var duplicate = Assert.Throws<AggregationValidationException>(() => AggregationExecutor.ValidateQuery(
            unit,
            profile,
            new AggregationQuery("summary")
            {
                OrderByTerms = [new AggregationOrderTerm("count"), new AggregationOrderTerm("count")]
            }));
        Assert.Contains(duplicate.Errors, error => error.Code == "GW-AGG-QUERY-007" && error.Path == "orderByTerms");

        var invalidDirection = Assert.Throws<AggregationValidationException>(() => AggregationExecutor.ValidateQuery(
            unit,
            profile,
            new AggregationQuery("summary")
            {
                OrderByTerms = [new AggregationOrderTerm("count", (SortDirection)99)]
            }));
        Assert.Contains(invalidDirection.Errors, error => error.Code == "GW-AGG-QUERY-008");
    }

    [Fact]
    public void Aggregation_fingerprints_bind_order_terms_and_scope_values()
    {
        var unit = Unit(
            new ColumnDefinition { Name = "group", Type = PortableType.String },
            new ColumnDefinition { Name = "amount", Type = PortableType.Int64 });
        var profile = Profile(new Aggregate.Count("count"), new Aggregate.Sum("total", "amount"));
        var first = new AggregationQuery("summary")
        {
            OrderByTerms = [
                new AggregationOrderTerm("count", SortDirection.Descending),
                new AggregationOrderTerm("group", SortDirection.Ascending)]
        };
        var second = first with
        {
            OrderByTerms = [
                new AggregationOrderTerm("group", SortDirection.Ascending),
                new AggregationOrderTerm("count", SortDirection.Descending)]
        };
        var directionChanged = first with
        {
            OrderByTerms = [
                new AggregationOrderTerm("count", SortDirection.Ascending),
                new AggregationOrderTerm("group", SortDirection.Ascending)]
        };
        var scopedA = new StorageScope("tenant-a");
        var scopedB = new StorageScope("tenant-b");

        Assert.NotEqual(
            AggregationQueryFingerprint.CreateShapeFingerprint(unit, profile, first),
            AggregationQueryFingerprint.CreateShapeFingerprint(unit, profile, second));
        Assert.NotEqual(
            AggregationQueryFingerprint.CreateShapeFingerprint(unit, profile, first),
            AggregationQueryFingerprint.CreateShapeFingerprint(unit, profile, directionChanged));
        Assert.NotEqual(
            AggregationQueryFingerprint.Create(unit, profile, first, scopedA),
            AggregationQueryFingerprint.Create(unit, profile, first, scopedB));
    }

    [Fact]
    public void Captured_profiles_cannot_be_mutated_through_read_only_interfaces()
    {
        var source = Profile(new Aggregate.SetUnion("labels", "label", 2)) with
        {
            AllowedPredicates =
            [
                new AggregationPredicateAllowance
                {
                    Alias = "labels",
                    SupportedPredicates = new HashSet<AggregationPredicateOperator>
                    {
                        AggregationPredicateOperator.Contains
                    }
                }
            ]
        };
        var snapshot = AggregationProfileSnapshot.Capture(source);

        var groups = Assert.IsAssignableFrom<IList<string>>(snapshot.GroupByColumns);
        Assert.Throws<NotSupportedException>(() => groups[0] = "changed");
        var aggregates = Assert.IsAssignableFrom<IList<Aggregate>>(snapshot.Aggregates);
        Assert.Throws<NotSupportedException>(() => aggregates[0] = new Aggregate.Min("other", "label"));
        var allowances = Assert.IsAssignableFrom<IList<AggregationPredicateAllowance>>(snapshot.AllowedPredicates);
        Assert.Throws<NotSupportedException>(() => allowances[0] = new AggregationPredicateAllowance
        {
            Alias = "other",
            SupportedPredicates = ImmutableHashSet<AggregationPredicateOperator>.Empty
        });
        var supported = Assert.IsAssignableFrom<ISet<AggregationPredicateOperator>>(
            snapshot.AllowedPredicates[0].SupportedPredicates);
        Assert.Throws<NotSupportedException>(() => supported.Add(AggregationPredicateOperator.Equal));
    }

    [Fact]
    public void Fixed_utc_time_buckets_are_exact_and_exclude_nulls_with_an_exclusive_upper_bound()
    {
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("time-bucket-fixed"),
            Name = "time_bucket_fixed",
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, IsNullable = false },
                new() { Name = "createdAt", Type = PortableType.DateTimeOffset },
                new() { Name = "value", Type = PortableType.Int64 }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            AggregationProfiles =
            [
                new AggregationProfile
                {
                    Name = "hourly",
                    GroupByExpressions = [AggregationGroup.TimeBucket.FixedUtc("bucket", "createdAt", TimeSpan.FromHours(1))],
                    Aggregates = [new Aggregate.Count("count")]
                }
            ]
        };
        var from = new DateTimeOffset(2026, 3, 1, 10, 30, 0, TimeSpan.Zero);
        var to = from.AddHours(2);
        var rows = new IReadOnlyDictionary<string, object?>[]
        {
            new Dictionary<string, object?> { ["id"] = "first", ["createdAt"] = from, ["value"] = 1L },
            new Dictionary<string, object?> { ["id"] = "second", ["createdAt"] = from.AddMinutes(29), ["value"] = 2L },
            new Dictionary<string, object?> { ["id"] = "upper", ["createdAt"] = to, ["value"] = 3L },
            new Dictionary<string, object?> { ["id"] = "null", ["createdAt"] = null, ["value"] = 4L }
        };

        var result = AggregationExecutor.Execute(unit, unit.AggregationProfiles.Single(), rows, new AggregationQuery("hourly")
        {
            TimeRange = new AggregationTimeRange(from, to)
        });

        var bucket = from;
        var output = Assert.Single(result.Rows);
        Assert.Equal(bucket, output["bucket"]);
        Assert.Equal(2L, output["count"]);
    }

    [Fact]
    public void Declaration_refuses_multiple_time_bucket_groups_before_execution()
    {
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("multiple-time-buckets"),
            Name = "multiple_time_buckets",
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, IsNullable = false },
                new() { Name = "createdAt", Type = PortableType.DateTimeOffset },
                new() { Name = "updatedAt", Type = PortableType.DateTimeOffset }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            AggregationProfiles =
            [
                new AggregationProfile
                {
                    Name = "invalid",
                    GroupByExpressions =
                    [
                        AggregationGroup.TimeBucket.FixedUtc("created", "createdAt", TimeSpan.FromHours(1)),
                        AggregationGroup.TimeBucket.FixedUtc("updated", "updatedAt", TimeSpan.FromHours(1))
                    ],
                    Aggregates = [new Aggregate.Count("count")]
                }
            ]
        };

        var exception = Assert.Throws<AggregationValidationException>(() => AggregationProfileValidator.ValidateUnit(unit));

        Assert.Contains(exception.Errors, error => error.Code == "GW-AGG-GROUP-006");
    }

    [Fact]
    public void Local_calendar_day_buckets_follow_iana_dst_transitions()
    {
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("time-bucket-local"),
            Name = "time_bucket_local",
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, IsNullable = false },
                new() { Name = "createdAt", Type = PortableType.DateTimeOffset },
                new() { Name = "value", Type = PortableType.Int64 }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            AggregationProfiles =
            [
                new AggregationProfile
                {
                    Name = "daily",
                    GroupByExpressions = [AggregationGroup.TimeBucket.LocalCalendarDay("day", "createdAt")],
                    Aggregates = [new Aggregate.Count("count")]
                }
            ]
        };
        // Amsterdam's spring-forward day is 23 hours; both instants still belong to the same
        // local midnight bucket, whose UTC start is 23:00 on the preceding UTC day.
        var spring = new DateTimeOffset(2026, 3, 29, 0, 30, 0, TimeSpan.Zero);
        var rows = new IReadOnlyDictionary<string, object?>[]
        {
            new Dictionary<string, object?> { ["id"] = "before", ["createdAt"] = spring, ["value"] = 1L },
            new Dictionary<string, object?> { ["id"] = "after", ["createdAt"] = spring.AddHours(20), ["value"] = 2L }
        };

        var result = AggregationExecutor.Execute(unit, unit.AggregationProfiles.Single(), rows, new AggregationQuery("daily")
        {
            TimeRange = new AggregationTimeRange(spring, spring.AddDays(1)),
            TimeZoneId = "Europe/Amsterdam"
        });

        var output = Assert.Single(result.Rows);
        Assert.Equal(new DateTimeOffset(2026, 3, 28, 23, 0, 0, TimeSpan.Zero), output["day"]);
        Assert.Equal(2L, output["count"]);
    }

    [Fact]
    public void Fixed_utc_time_buckets_support_non_integral_second_widths()
    {
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("time-bucket-subsecond"),
            Name = "time_bucket_subsecond",
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, IsNullable = false },
                new() { Name = "createdAt", Type = PortableType.DateTimeOffset }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            AggregationProfiles =
            [
                new AggregationProfile
                {
                    Name = "subsecond",
                    GroupByExpressions = [AggregationGroup.TimeBucket.FixedUtc("bucket", "createdAt", TimeSpan.FromMilliseconds(1500))],
                    Aggregates = [new Aggregate.Count("count")]
                }
            ]
        };
        var from = new DateTimeOffset(2026, 8, 16, 10, 15, 0, 500, TimeSpan.Zero);
        var boundary = from.AddMilliseconds(1500);
        var result = AggregationExecutor.Execute(unit, unit.AggregationProfiles.Single(),
        [
            new Dictionary<string, object?> { ["id"] = "first", ["createdAt"] = from },
            new Dictionary<string, object?> { ["id"] = "last", ["createdAt"] = boundary.AddTicks(-1) },
            new Dictionary<string, object?> { ["id"] = "second", ["createdAt"] = boundary }
        ], new AggregationQuery("subsecond")
        {
            TimeRange = new AggregationTimeRange(from, boundary.AddMilliseconds(1500))
        });

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(2L, Assert.Single(result.Rows, row => Equals(row["bucket"], from))["count"]);
        Assert.Equal(1L, Assert.Single(result.Rows, row => Equals(row["bucket"], boundary))["count"]);
    }

    private static StorageUnit Unit(params ColumnDefinition[] columns) => new()
    {
        Id = new StorageUnitId("aggregation-tests"),
        Name = "aggregation_tests",
        Columns = columns,
        Key = new KeyDefinition { Columns = ["group"] }
    };

    private static AggregationProfile Profile(params Aggregate[] aggregates) => new()
    {
        Name = "summary",
        GroupByColumns = ["group"],
        Aggregates = aggregates,
        AllowedPredicates = []
    };
}
