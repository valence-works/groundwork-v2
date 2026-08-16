using Groundwork.Kernel;
using Groundwork.MongoDb;
using Groundwork.PostgreSql;
using Groundwork.Query.Model;
using Groundwork.Sqlite;
using Groundwork.SqlServer;
using Groundwork.Testing;
using Groundwork.Store;
using Xunit;

namespace Groundwork.StreamCapabilities.Tests;

/// <summary>Cross-provider, native grouped-reduction conformance for issue #262.</summary>
public sealed class AggregationConformanceTests
{
    [Fact]
    public void Public_sessions_expose_the_named_aggregation_contract()
    {
        AssertMethod(typeof(IStorageSession));
        AssertMethod(typeof(IMongoStorageSession));

        static void AssertMethod(Type contract)
        {
            var method = Assert.Single(contract.GetMethods(), candidate => candidate.Name == "Aggregate");
            Assert.Equal(typeof(AggregationResult), method.ReturnType);
            Assert.Equal(typeof(AggregationQuery), Assert.Single(method.GetParameters()).ParameterType);
        }
    }

    [Fact]
    public void SQLite_native_aggregation_is_bit_identical_to_the_portable_oracle()
    {
        var directory = Path.Combine(Path.GetTempPath(), "groundwork-aggregation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            AssertProvider(
                new SqliteProviderFactory(),
                $"Data Source={Path.Combine(directory, "aggregation.db")}",
                "SQLite");
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    public void PostgreSQL_native_aggregation_is_bit_identical_to_the_portable_oracle()
    {
        var connection = Environment.GetEnvironmentVariable("GROUNDWORK_POSTGRES_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connection),
            "Set GROUNDWORK_POSTGRES_CONNECTION to run PostgreSQL aggregation conformance.");
        AssertProvider(new PostgreSqlProviderFactory(), connection!, "PostgreSQL");
    }

    [SkippableFact]
    public void SQLServer_native_aggregation_is_bit_identical_to_the_portable_oracle()
    {
        var connection = Environment.GetEnvironmentVariable("GROUNDWORK_SQLSERVER_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connection),
            "Set GROUNDWORK_SQLSERVER_CONNECTION to run SQL Server aggregation conformance.");
        AssertProvider(new SqlServerProviderFactory(), connection!, "SQLServer");
    }

    [SkippableFact]
    public void MongoDB_native_aggregation_is_bit_identical_through_the_testing_adapter()
    {
        var connection = Environment.GetEnvironmentVariable("GROUNDWORK_MONGO_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connection),
            "Set GROUNDWORK_MONGO_CONNECTION to run MongoDB aggregation conformance.");
        AssertProvider(new MongoProviderFactory(), connection!, "MongoDB");
    }

    [Fact]
    public void Post_reduction_predicates_must_be_declared()
    {
        var unit = FixtureUnit("aggregation_predicates");
        var query = new AggregationQuery("summary")
        {
            PostPredicate = new AggregationPredicate.Comparison(
                "integerTotal", AggregationPredicateOperator.Contains, ["not-declared"])
        };

        var exception = Assert.Throws<AggregationValidationException>(() => AggregationExecutor.Execute(
            unit, unit.AggregationProfiles.Single(), FixtureRows(), query));

        Assert.Contains(exception.Errors, error => error.Code == "GW-AGG-PRED-007");
    }

    [Fact]
    public void Source_predicate_admission_precedes_the_provider_scan()
    {
        using var connection = new InMemoryProviderFactory().Create("aggregation-source-admission-" + Guid.NewGuid().ToString("N"));
        var unit = FixtureUnit("aggregation_source_admission");
        Assert.True(connection.Schema.Apply(unit).Applied);
        var label = new ColumnRef(new TableId(unit.Name), "label", QueryType.String);

        var exception = Assert.Throws<AggregationValidationException>(() => connection.OpenSession(unit, StorageAccess.Global).Aggregate(
            new AggregationQuery("summary")
            {
                SourcePredicate = new Predicate.StartsWith(label, "plain")
            }));

        Assert.Contains(exception.Errors, error => error.Code == "GW-AGG-SOURCE-007");
    }

    [Fact]
    public void Source_predicate_is_applied_before_reduction_and_differs_from_post_predicate()
    {
        using var connection = new InMemoryProviderFactory().Create("aggregation-source-predicate-" + Guid.NewGuid().ToString("N"));
        var unit = FixtureUnit("aggregation_source_predicate");
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        foreach (var values in FixtureRows())
            Assert.Equal(WriteOutcomeStatus.Inserted, session.Insert(new StorageValues(values)).Status);

        var lowOrder = new ColumnRef(new TableId(unit.Name), "lowOrder", QueryType.Int64, isNullable: false);
        var sourceQuery = new AggregationQuery("summary")
        {
            SourcePredicate = new Predicate.Equal(lowOrder, QueryConstant.Of(lowOrder, 2L))
        };
        var source = session.Aggregate(sourceQuery);

        var sourceRow = Assert.Single(source.Rows);
        Assert.Equal("a", sourceRow["group"]);
        Assert.Equal(2_000_000_000L, sourceRow["integerTotal"]);
        Assert.Equal(2_000_000_000, sourceRow["minimum"]);
        Assert.Equal(2_000_000_000, sourceRow["maximum"]);
        Assert.Equal("a\u001fb", Assert.Single((IEnumerable<string>)sourceRow["labels"]!));
        Assert.Equal("a\u001fb", sourceRow["firstLow"]);
        Assert.Equal(
            AggregationQueryFingerprint.Create(unit, unit.AggregationProfiles.Single(), sourceQuery),
            source.ValueFingerprint);
        Assert.Equal(
            AggregationQueryFingerprint.CreateShapeFingerprint(unit, unit.AggregationProfiles.Single(), sourceQuery),
            source.ShapeFingerprint);

        var combined = session.Aggregate(new AggregationQuery("summary")
        {
            SourcePredicate = new Predicate.Equal(lowOrder, QueryConstant.Of(lowOrder, 2L)),
            PostPredicate = new AggregationPredicate.Comparison(
                "integerTotal", AggregationPredicateOperator.Equal, [2_000_000_000L])
        });
        Assert.Equal(2_000_000_000L, Assert.Single(combined.Rows)["integerTotal"]);

        var label = new ColumnRef(new TableId(unit.Name), "label", QueryType.String);
        var substring = session.Aggregate(new AggregationQuery("summary")
        {
            SourcePredicate = new Predicate.Substring(label, "plain", Anchor.Contains)
        });
        var substringRow = Assert.Single(substring.Rows);
        Assert.Equal("a", substringRow["group"]);
        Assert.Equal(2_000_000_000L, substringRow["integerTotal"]);
        Assert.Equal("plain", substringRow["firstLow"]);

        var suffix = session.Aggregate(new AggregationQuery("summary")
        {
            SourcePredicate = new Predicate.Substring(label, "plain", Anchor.EndsWith)
        });
        Assert.Equal("a", Assert.Single(suffix.Rows)["group"]);

        var exactLabel = session.Aggregate(new AggregationQuery("summary")
        {
            SourcePredicate = new Predicate.Equal(label, QueryConstant.Of(label, "plain"))
        });
        Assert.Equal(2_000_000_000L, Assert.Single(exactLabel.Rows)["integerTotal"]);

        var identity = new ColumnRef(new TableId(unit.Name), "identity", QueryType.Guid, isNullable: false);
        var exactIdentity = session.Aggregate(new AggregationQuery("summary")
        {
            SourcePredicate = new Predicate.Equal(
                identity,
                QueryConstant.Of(identity, Guid.Parse("00000000-0000-0000-0000-000000000001")))
        });
        Assert.Equal(2_000_000_000L, Assert.Single(exactIdentity.Rows)["integerTotal"]);

        var postQuery = new AggregationQuery("summary")
        {
            PostPredicate = new AggregationPredicate.Comparison(
                "integerTotal", AggregationPredicateOperator.Equal, [4_000_000_000L])
        };
        var post = session.Aggregate(postQuery);
        Assert.Equal(4_000_000_000L, Assert.Single(post.Rows)["integerTotal"]);
    }

    private static void AssertProvider(
        IStorageProviderFactory factory,
        string connectionString,
        string provider)
    {
        using var connection = factory.Create(connectionString);
        var identity = "aggregation_" + provider.ToLowerInvariant() + "_" + Guid.NewGuid().ToString("N");
        var unit = FixtureUnit(identity);
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        foreach (var values in FixtureRows())
            Assert.Equal(WriteOutcomeStatus.Inserted,
                session.Insert(new StorageValues(values)).Status);

        var expected = Canonical(AggregationExecutor.Execute(
            unit,
            unit.AggregationProfiles.Single(),
            FixtureRows()));
        var actual = Canonical(session.Aggregate(new AggregationQuery("summary")));

        Assert.Equal(expected, actual);

        var containsQuery = new AggregationQuery("summary")
        {
            PostPredicate = new AggregationPredicate.Comparison(
                "labels", AggregationPredicateOperator.Contains, ["plain"])
        };
        var expectedContains = Canonical(AggregationExecutor.Execute(
            unit,
            unit.AggregationProfiles.Single(),
            FixtureRows(),
            containsQuery));
        var actualContains = Canonical(session.Aggregate(containsQuery));

        Assert.Equal(expectedContains, actualContains);

        var lowOrder = new ColumnRef(new TableId(unit.Name), "lowOrder", QueryType.Int64, isNullable: false);
        var sourceQuery = new AggregationQuery("summary")
        {
            SourcePredicate = new Predicate.Equal(lowOrder, QueryConstant.Of(lowOrder, 2L))
        };
        var source = session.Aggregate(sourceQuery);
        var sourceRow = Assert.Single(source.Rows);
        Assert.Equal("a", sourceRow["group"]);
        Assert.Equal(2_000_000_000L, sourceRow["integerTotal"]);
        Assert.Equal("a\u001fb", sourceRow["firstLow"]);

        var combined = session.Aggregate(new AggregationQuery("summary")
        {
            SourcePredicate = new Predicate.Equal(lowOrder, QueryConstant.Of(lowOrder, 2L)),
            PostPredicate = new AggregationPredicate.Comparison(
                "integerTotal", AggregationPredicateOperator.Equal, [2_000_000_000L])
        });
        Assert.Equal(2_000_000_000L, Assert.Single(combined.Rows)["integerTotal"]);

        var label = new ColumnRef(new TableId(unit.Name), "label", QueryType.String);
        var substring = session.Aggregate(new AggregationQuery("summary")
        {
            SourcePredicate = new Predicate.Substring(label, "plain", Anchor.Contains)
        });
        var substringRow = Assert.Single(substring.Rows);
        Assert.Equal("a", substringRow["group"]);
        Assert.Equal(2_000_000_000L, substringRow["integerTotal"]);
        Assert.Equal("plain", substringRow["firstLow"]);

        var suffix = session.Aggregate(new AggregationQuery("summary")
        {
            SourcePredicate = new Predicate.Substring(label, "plain", Anchor.EndsWith)
        });
        Assert.Equal("a", Assert.Single(suffix.Rows)["group"]);

        var postFourBillion = new AggregationQuery("summary")
        {
            PostPredicate = new AggregationPredicate.Comparison(
                "integerTotal", AggregationPredicateOperator.Equal, [4_000_000_000L])
        };
        Assert.Equal(4_000_000_000L, Assert.Single(session.Aggregate(postFourBillion).Rows)["integerTotal"]);
    }

    private static StorageUnit FixtureUnit(string identity) => new()
    {
        Id = new StorageUnitId(identity),
        Name = identity,
        Columns =
        [
            new() { Name = "id", Type = PortableType.String, MaxLength = 128, IsNullable = false },
            new() { Name = "group", Type = PortableType.String, MaxLength = 128, IsNullable = false },
            new() { Name = "integerAmount", Type = PortableType.Int32 },
            new() { Name = "decimalAmount", Type = PortableType.Decimal, Precision = 28, Scale = 4 },
            new() { Name = "label", Type = PortableType.String, MaxLength = 256 },
            new() { Name = "identity", Type = PortableType.Guid, IsNullable = false },
            new() { Name = "lowOrder", Type = PortableType.Int64, IsNullable = false },
            new() { Name = "highOrder", Type = PortableType.Int64, IsNullable = false }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        AggregationProfiles =
        [
            new AggregationProfile
            {
                Name = "summary",
                GroupByColumns = ["group"],
                Aggregates =
                [
                    new Aggregate.Min("minimum", "integerAmount"),
                    new Aggregate.Max("maximum", "integerAmount"),
                    new Aggregate.Sum("integerTotal", "integerAmount"),
                    new Aggregate.Sum("decimalTotal", "decimalAmount"),
                    new Aggregate.SetUnion("labels", "label", 8),
                    new Aggregate.FirstBy("firstLow", "label", "lowOrder"),
                    new Aggregate.FirstBy("firstHigh", "label", "highOrder", SortDirection.Descending)
                ],
                AllowedPredicates =
                [
                    new AggregationPredicateAllowance
                    {
                        Alias = "integerTotal",
                        SupportedPredicates = new HashSet<AggregationPredicateOperator>
                        {
                            AggregationPredicateOperator.Equal,
                            AggregationPredicateOperator.RangeInclusive
                        }
                    },
                    new AggregationPredicateAllowance
                    {
                        Alias = "labels",
                        SupportedPredicates = new HashSet<AggregationPredicateOperator>
                        {
                            AggregationPredicateOperator.Contains
                        }
                    }
                ],
                MaxGroups = 16,
                MaxInputRows = 64
            }
        ]
    };

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> FixtureRows() =>
    [
        new Dictionary<string, object?>
        {
            ["id"] = "1", ["group"] = "a", ["integerAmount"] = 2_000_000_000,
            ["decimalAmount"] = 12_345_678_901_234_567_890.1234m,
            ["label"] = "a\u001fb", ["identity"] = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            ["lowOrder"] = 2L, ["highOrder"] = 1L
        },
        new Dictionary<string, object?>
        {
            ["id"] = "2", ["group"] = "a", ["integerAmount"] = 2_000_000_000,
            ["decimalAmount"] = 0.0001m,
            ["label"] = "plain", ["identity"] = Guid.Parse("00000000-0000-0000-0000-000000000002"),
            ["lowOrder"] = 1L, ["highOrder"] = 3L
        },
        new Dictionary<string, object?>
        {
            ["id"] = "3", ["group"] = "b", ["integerAmount"] = null,
            ["decimalAmount"] = null, ["label"] = null,
            ["identity"] = Guid.Parse("00000000-0000-0000-0000-000000000003"),
            ["lowOrder"] = 3L, ["highOrder"] = 2L
        }
    ];

    private static string Canonical(AggregationResult result) => string.Join("\n", result.Rows.Select(row =>
        string.Join("|", row.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
            pair.Value is IEnumerable<string> strings
                ? pair.Key + "=[" + string.Join(",", strings) + "]"
                : pair.Key + "=" + (pair.Value?.ToString() ?? "<null>")))));
}
