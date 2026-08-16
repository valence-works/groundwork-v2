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
