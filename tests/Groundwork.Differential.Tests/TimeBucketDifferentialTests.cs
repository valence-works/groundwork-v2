using Groundwork.Kernel;
using Groundwork.MongoDb;
using Groundwork.PostgreSql;
using Groundwork.SqlServer;
using Groundwork.Sqlite;
using Groundwork.Store;
using Groundwork.Substrate.Relational;
using MongoDB.Bson;
using Xunit;

namespace Groundwork.Differential.Tests;

[Collection(NativeProviderDifferentialCollection.Name)]
public sealed class TimeBucketDifferentialTests
{
    [Fact]
    public void SQLite_live_time_bucket_matches_the_portable_oracle()
    {
        AssertProvider("SQLite", () => new SqliteProviderFactory().Create(
            "Data Source=file:groundwork_s10_live_" + Guid.NewGuid().ToString("N") + "?mode=memory&cache=shared"));
    }

    [SkippableFact]
    public void PostgreSQL_live_time_bucket_matches_the_portable_oracle() =>
        AssertProvider("PostgreSQL", () => new PostgreSqlProviderFactory().Create(Required("GROUNDWORK_POSTGRES_CONNECTION")));

    [SkippableFact]
    public void SQLServer_live_time_bucket_matches_the_portable_oracle() =>
        AssertProvider("SQL Server", () => new SqlServerProviderFactory().Create(Required("GROUNDWORK_SQLSERVER_CONNECTION")));

    [SkippableFact]
    public void MongoDB_live_time_bucket_matches_the_portable_oracle() =>
        AssertProvider("MongoDB", () => new MongoProviderFactory().Create(Required("GROUNDWORK_MONGO_CONNECTION")));

    [Fact]
    public void SQLite_live_fixed_bucket_preserves_a_fractional_invocation_origin() =>
        AssertFixedProvider("SQLite", () => new SqliteProviderFactory().Create(
            "Data Source=file:groundwork_s10_fixed_live_" + Guid.NewGuid().ToString("N") + "?mode=memory&cache=shared"));

    [SkippableFact]
    public void PostgreSQL_live_fixed_bucket_preserves_a_fractional_invocation_origin() =>
        AssertFixedProvider("PostgreSQL", () => new PostgreSqlProviderFactory().Create(Required("GROUNDWORK_POSTGRES_CONNECTION")));

    [SkippableFact]
    public void SQLServer_live_fixed_bucket_preserves_a_fractional_invocation_origin() =>
        AssertFixedProvider("SQL Server", () => new SqlServerProviderFactory().Create(Required("GROUNDWORK_SQLSERVER_CONNECTION")));

    [SkippableFact]
    public void MongoDB_live_fixed_bucket_preserves_a_fractional_invocation_origin() =>
        AssertFixedProvider("MongoDB", () => new MongoProviderFactory().Create(Required("GROUNDWORK_MONGO_CONNECTION")));

    [SkippableFact]
    public void MongoDB_time_bucket_global_input_budget_covers_distinct_buckets_in_one_operation()
    {
        using var connection = new MongoProviderFactory().Create(Required("GROUNDWORK_MONGO_CONNECTION"));
        var source = FixedUnit();
        var unit = source with
        {
            Id = new StorageUnitId("mongo_time_bucket_budget_" + Guid.NewGuid().ToString("N")),
            Name = "mongo_time_bucket_budget_" + Guid.NewGuid().ToString("N"),
            AggregationProfiles = [source.AggregationProfiles.Single() with { MaxInputRows = 2 }]
        };
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        var from = new DateTimeOffset(2026, 8, 16, 10, 15, 0, 500, TimeSpan.Zero);
        foreach (var index in Enumerable.Range(0, 3))
            Assert.Equal(WriteOutcomeStatus.Inserted, session.Insert(new StorageValues(new Dictionary<string, object?>
            {
                ["id"] = "bucket-" + index,
                ["createdAt"] = from.AddHours(index)
            })).Status);

        var calls = new List<string>();
        Groundwork.Kernel.AggregationExecutionDiagnostics.Observer = calls.Add;
        try
        {
            var exception = Assert.Throws<AggregationBudgetExceededException>(() => session.Aggregate(new AggregationQuery("hourly")
            {
                TimeRange = new AggregationTimeRange(from, from.AddHours(3))
            }));
            Assert.Equal("GW-AGG-BOUND-004", exception.Code);
            Assert.Single(calls);
            Assert.Equal("aggregate", calls[0]);
        }
        finally
        {
            Groundwork.Kernel.AggregationExecutionDiagnostics.Observer = null;
        }
    }

    [Fact]
    public void Every_provider_renders_the_bucket_inside_one_native_group_operation()
    {
        var unit = Unit();
        var profile = unit.AggregationProfiles.Single();
        var from = new DateTimeOffset(2026, 3, 29, 0, 0, 0, TimeSpan.Zero);
        var query = new AggregationQuery(profile.Name)
        {
            TimeRange = new AggregationTimeRange(from, from.AddDays(2)),
            TimeZoneId = "Europe/Amsterdam",
            OrderByTerms = [new AggregationOrderTerm("bucket", SortDirection.Ascending)]
        };

        foreach (var dialect in new RelationalDialect[] { new SqliteDialect(), new PostgreSqlDialect(), new SqlServerDialect() })
        {
            var command = RelationalAggregationRenderer.Render(dialect, unit, profile, query).CommandText;
            Assert.Contains("GROUP BY", command, StringComparison.Ordinal);
            Assert.Contains("createdAt", command, StringComparison.Ordinal);
            Assert.Contains("__groundwork_aggregation_result", command, StringComparison.Ordinal);
            Assert.Contains("1001", command, StringComparison.Ordinal);
            Assert.DoesNotContain("QueryRequest", command, StringComparison.Ordinal);
        }

        var pipeline = MongoStorageSession.RenderNativeAggregationPipeline(unit, profile, query);
        var json = string.Join("\n", pipeline.Select(stage => stage.ToJson()));
        Assert.Contains("$group", json, StringComparison.Ordinal);
        Assert.Contains("$setWindowFields", json, StringComparison.Ordinal);
        Assert.Contains("$dateTrunc", json, StringComparison.Ordinal);
        Assert.Contains("$dateFromString", json, StringComparison.Ordinal);
        Assert.Contains("$dateAdd", json, StringComparison.Ordinal);
        Assert.Contains("1001", json, StringComparison.Ordinal);
        Assert.Contains("Europe/Amsterdam", json, StringComparison.Ordinal);
        Assert.DoesNotContain("$lookup", json, StringComparison.Ordinal);

        var ordinaryUnit = unit with
        {
            AggregationProfiles =
            [
                new AggregationProfile
                {
                    Name = "ordinary",
                    GroupByColumns = ["id"],
                    Aggregates = [new Aggregate.Count("count")]
                }
            ]
        };
        var ordinaryPipeline = MongoStorageSession.RenderNativeAggregationPipeline(
            ordinaryUnit,
            ordinaryUnit.AggregationProfiles.Single(),
            new AggregationQuery("ordinary"));
        Assert.DoesNotContain("$setWindowFields", string.Join("\n", ordinaryPipeline.Select(stage => stage.ToJson())), StringComparison.Ordinal);
    }

    [Fact]
    public void Reference_and_sqlite_paths_have_identical_dst_bucket_identity()
    {
        var unit = Unit();
        var profile = unit.AggregationProfiles.Single();
        var from = new DateTimeOffset(2026, 3, 29, 0, 30, 0, TimeSpan.Zero);
        var rows = new IReadOnlyDictionary<string, object?>[]
        {
            new Dictionary<string, object?> { ["id"] = "before", ["createdAt"] = from },
            new Dictionary<string, object?> { ["id"] = "after", ["createdAt"] = from.AddHours(20) },
            new Dictionary<string, object?> { ["id"] = "null", ["createdAt"] = null }
        };

        var expected = AggregationExecutor.Execute(unit, profile, rows, new AggregationQuery(profile.Name)
        {
            TimeRange = new AggregationTimeRange(from, from.AddDays(1)),
            TimeZoneId = "Europe/Amsterdam"
        });
        Assert.Equal(new DateTimeOffset(2026, 3, 28, 23, 0, 0, TimeSpan.Zero), expected.Rows.Single()["bucket"]);
        Assert.Equal(2L, expected.Rows.Single()["count"]);

        using var connection = new SqliteProviderFactory().Create(
            "Data Source=file:groundwork_s10_" + Guid.NewGuid().ToString("N") + "?mode=memory&cache=shared");
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        foreach (var row in rows)
            session.Insert(new StorageValues(row));
        var actual = session.Aggregate(new AggregationQuery(profile.Name)
        {
            TimeRange = new AggregationTimeRange(from, from.AddDays(1)),
            TimeZoneId = "Europe/Amsterdam"
        });
        Assert.Equal(expected.Rows.Select(row => row["bucket"]), actual.Rows.Select(row => row["bucket"]));
        Assert.Equal(expected.Rows.Select(row => row["count"]), actual.Rows.Select(row => row["count"]));
    }

    [Fact]
    public void One_stored_dataset_can_be_queried_in_multiple_invocation_time_zones()
    {
        var unit = Unit();
        using var connection = new SqliteProviderFactory().Create(
            "Data Source=file:groundwork_s10_zone_" + Guid.NewGuid().ToString("N") + "?mode=memory&cache=shared");
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        var instant = new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero);
        Assert.Equal(WriteOutcomeStatus.Inserted, session.Insert(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "same-row", ["createdAt"] = instant
        })).Status);

        var amsterdam = session.Aggregate(new AggregationQuery("daily")
        {
            TimeRange = new AggregationTimeRange(instant.AddDays(-1), instant.AddDays(1)),
            TimeZoneId = "Europe/Amsterdam"
        });
        var kathmandu = session.Aggregate(new AggregationQuery("daily")
        {
            TimeRange = new AggregationTimeRange(instant.AddDays(-1), instant.AddDays(1)),
            TimeZoneId = "Asia/Kathmandu"
        });

        Assert.Equal(new DateTimeOffset(2026, 1, 14, 23, 0, 0, TimeSpan.Zero), Assert.Single(amsterdam.Rows)["bucket"]);
        Assert.Equal(new DateTimeOffset(2026, 1, 15, 18, 15, 0, TimeSpan.Zero), Assert.Single(kathmandu.Rows)["bucket"]);
        Assert.NotEqual(amsterdam.ValueFingerprint, kathmandu.ValueFingerprint);
    }

    [Fact]
    public void Windows_zone_ids_are_refused_at_the_invocation_boundary()
    {
        var unit = Unit();
        var instant = new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero);
        var exception = Assert.Throws<AggregationValidationException>(() => AggregationExecutor.Execute(
            unit,
            unit.AggregationProfiles.Single(),
            [],
            new AggregationQuery("daily")
            {
                TimeRange = new AggregationTimeRange(instant, instant.AddDays(1)),
                TimeZoneId = "Pacific Standard Time"
            }));
        Assert.Contains(exception.Errors, error => error.Code == "GW-AGG-QUERY-017");
    }

    [Fact]
    public void Utc_iana_zone_id_is_accepted_without_a_path_separator()
    {
        var unit = Unit();
        var instant = new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero);
        var result = AggregationExecutor.Execute(
            unit,
            unit.AggregationProfiles.Single(),
            [new Dictionary<string, object?> { ["id"] = "utc-row", ["createdAt"] = instant }],
            new AggregationQuery("daily")
            {
                TimeRange = new AggregationTimeRange(instant.AddHours(-1), instant.AddHours(1)),
                TimeZoneId = "UTC"
            });

        Assert.Equal(new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero), Assert.Single(result.Rows)["bucket"]);
    }

    [Fact]
    public void Fixed_bucket_origin_is_invocation_bound_in_result_identity()
    {
        var unit = FixedUnit();
        var profile = unit.AggregationProfiles.Single();
        var firstOrigin = new DateTimeOffset(2026, 8, 16, 10, 15, 0, 500, TimeSpan.Zero);
        var secondOrigin = firstOrigin.AddMinutes(15);
        var rows = new IReadOnlyDictionary<string, object?>[]
        {
            new Dictionary<string, object?> { ["id"] = "row", ["createdAt"] = firstOrigin.AddMinutes(10) }
        };
        var first = new AggregationQuery("hourly")
        {
            TimeRange = new AggregationTimeRange(firstOrigin, firstOrigin.AddHours(1)),
            TimeBucketOrigin = firstOrigin
        };
        var second = first with { TimeBucketOrigin = secondOrigin };

        var firstResult = AggregationExecutor.Execute(unit, profile, rows, first);
        var secondResult = AggregationExecutor.Execute(unit, profile, rows, second);

        Assert.NotEqual(firstResult.Rows.Single()["bucket"], secondResult.Rows.Single()["bucket"]);
        Assert.NotEqual(firstResult.ShapeFingerprint, secondResult.ShapeFingerprint);
        Assert.NotEqual(firstResult.ValueFingerprint, secondResult.ValueFingerprint);
    }

    [Fact]
    public void SQLite_scoped_time_bucket_queries_isolate_identical_keys_and_data()
    {
        using var connection = new SqliteProviderFactory().Create(
            "Data Source=file:groundwork_s10_scoped_" + Guid.NewGuid().ToString("N") + "?mode=memory&cache=shared");
        var unit = Unit() with
        {
            Id = new StorageUnitId("scoped_time_bucket_" + Guid.NewGuid().ToString("N")),
            Name = "scoped_time_bucket_" + Guid.NewGuid().ToString("N"),
            Scope = ScopePolicy.Scoped
        };
        Assert.True(connection.Schema.Apply(unit).Applied);
        var scopeA = connection.OpenSession(unit, StorageAccess.Scoped(new StorageScope("scope-a")));
        var scopeB = connection.OpenSession(unit, StorageAccess.Scoped(new StorageScope("scope-b")));
        var timestamp = new DateTimeOffset(2026, 3, 29, 0, 30, 0, TimeSpan.Zero);
        var row = new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "same-key", ["createdAt"] = timestamp
        });
        Assert.Equal(WriteOutcomeStatus.Inserted, scopeA.Insert(row).Status);
        Assert.Equal(WriteOutcomeStatus.Inserted, scopeB.Insert(row).Status);
        var query = new AggregationQuery("daily")
        {
            TimeRange = new AggregationTimeRange(timestamp, timestamp.AddDays(1)),
            TimeZoneId = "Europe/Amsterdam"
        };

        Assert.Equal(1L, Assert.Single(scopeA.Aggregate(query).Rows)["count"]);
        Assert.Equal(1L, Assert.Single(scopeB.Aggregate(query).Rows)["count"]);
    }

    [Fact]
    public void Etc_utc_local_day_preserves_a_one_tick_before_epoch_timestamp()
    {
        var unit = Unit();
        var timestamp = DateTimeOffset.UnixEpoch.AddTicks(-1);
        var expected = AggregationExecutor.Execute(unit, unit.AggregationProfiles.Single(),
        [new Dictionary<string, object?> { ["id"] = "pre-epoch", ["createdAt"] = timestamp }],
        new AggregationQuery("daily")
        {
            TimeRange = new AggregationTimeRange(timestamp.AddDays(-1), timestamp.AddDays(1)),
            TimeZoneId = "Etc/UTC"
        });

        using var connection = new SqliteProviderFactory().Create(
            "Data Source=file:groundwork_s10_pre_epoch_" + Guid.NewGuid().ToString("N") + "?mode=memory&cache=shared");
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        Assert.Equal(WriteOutcomeStatus.Inserted, session.Insert(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "pre-epoch", ["createdAt"] = timestamp
        })).Status);
        var actual = session.Aggregate(new AggregationQuery("daily")
        {
            TimeRange = new AggregationTimeRange(timestamp.AddDays(-1), timestamp.AddDays(1)),
            TimeZoneId = "Etc/UTC"
        });

        Assert.Equal(new DateTimeOffset(1969, 12, 31, 0, 0, 0, TimeSpan.Zero), Assert.Single(expected.Rows)["bucket"]);
        Assert.Equal(expected.Rows.Select(row => row["bucket"]), actual.Rows.Select(row => row["bucket"]));
    }

    [Fact]
    public void SQLite_time_bucket_budget_evidence_survives_take_and_post_filter()
    {
        using var connection = new SqliteProviderFactory().Create(
            "Data Source=file:groundwork_s10_budget_" + Guid.NewGuid().ToString("N") + "?mode=memory&cache=shared");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("time_bucket_budget"),
            Name = "time_bucket_budget_" + Guid.NewGuid().ToString("N"),
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, MaxLength = 64, IsNullable = false },
                new() { Name = "createdAt", Type = PortableType.DateTimeOffset },
                new() { Name = "label", Type = PortableType.String, MaxLength = 16 }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            AggregationProfiles =
            [
                new AggregationProfile
                {
                    Name = "hourly",
                    GroupByExpressions = [AggregationGroup.TimeBucket.FixedUtc("bucket", "createdAt", TimeSpan.FromHours(1))],
                    Aggregates = [new Aggregate.Count("count"), new Aggregate.SetUnion("labels", "label", 1)],
                    AllowedPredicates =
                    [
                        new AggregationPredicateAllowance
                        {
                            Alias = "count",
                            SupportedPredicates = new HashSet<AggregationPredicateOperator> { AggregationPredicateOperator.Equal }
                        }
                    ]
                }
            ]
        };
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        var from = new DateTimeOffset(2026, 8, 18, 10, 0, 0, TimeSpan.Zero);
        foreach (var row in new[]
        {
            new Dictionary<string, object?> { ["id"] = "one", ["createdAt"] = from, ["label"] = "a" },
            new Dictionary<string, object?> { ["id"] = "two", ["createdAt"] = from.AddHours(1), ["label"] = "a" },
            new Dictionary<string, object?> { ["id"] = "three", ["createdAt"] = from.AddHours(1).AddMinutes(1), ["label"] = "b" }
        })
            Assert.Equal(WriteOutcomeStatus.Inserted, session.Insert(new StorageValues(row)).Status);

        var exception = Assert.Throws<AggregationBudgetExceededException>(() => session.Aggregate(new AggregationQuery("hourly")
        {
            TimeRange = new AggregationTimeRange(from, from.AddHours(2)),
            Take = 1,
            PostPredicate = new AggregationPredicate.Comparison("count", AggregationPredicateOperator.Equal, [99L])
        }));
        Assert.Equal("GW-AGG-BOUND-007", exception.Code);
    }

    private static void AssertProvider(string providerName, Func<IStorageProviderConnection> open)
    {
        using var connection = open();
        var unit = Unit() with { Id = new StorageUnitId("time_bucket_live_" + Guid.NewGuid().ToString("N")), Name = "s10_live_" + Guid.NewGuid().ToString("N") };
        Assert.True(connection.Schema.Apply(unit).Applied, providerName);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        var from = new DateTimeOffset(2026, 3, 29, 0, 30, 0, TimeSpan.Zero);
        var rows = new IReadOnlyDictionary<string, object?>[]
        {
            new Dictionary<string, object?> { ["id"] = "before", ["createdAt"] = from },
            new Dictionary<string, object?> { ["id"] = "after", ["createdAt"] = from.AddHours(20) },
            new Dictionary<string, object?> { ["id"] = "upper", ["createdAt"] = from.AddDays(1) },
            new Dictionary<string, object?> { ["id"] = "null", ["createdAt"] = null }
        };
        foreach (var row in rows)
            Assert.Equal(WriteOutcomeStatus.Inserted, session.Insert(new StorageValues(row)).Status);
        var result = AggregateWithSingleNativeCall(session, new AggregationQuery("daily")
        {
            TimeRange = new AggregationTimeRange(from, from.AddDays(1)),
            TimeZoneId = "Europe/Amsterdam"
        });
        var output = Assert.Single(result.Rows);
        Assert.Equal(new DateTimeOffset(2026, 3, 28, 23, 0, 0, TimeSpan.Zero), output["bucket"]);
        Assert.Equal(2L, output["count"]);

        // This exact local-midnight boundary exercises the one-tick floor. The
        // preceding .NET tick must remain on the preceding local day in every native renderer.
        var localMidnight = new DateTimeOffset(2026, 3, 28, 23, 0, 0, TimeSpan.Zero);
        var precedingTick = localMidnight.AddTicks(-1);
        foreach (var row in new IReadOnlyDictionary<string, object?>[]
        {
            new Dictionary<string, object?> { ["id"] = "local-before-midnight", ["createdAt"] = precedingTick },
            new Dictionary<string, object?> { ["id"] = "local-midnight", ["createdAt"] = localMidnight }
        })
            Assert.Equal(WriteOutcomeStatus.Inserted, session.Insert(new StorageValues(row)).Status);
        var boundary = AggregateWithSingleNativeCall(session, new AggregationQuery("daily")
        {
            TimeRange = new AggregationTimeRange(precedingTick, localMidnight.AddHours(1)),
            TimeZoneId = "Europe/Amsterdam"
        });
        Assert.Equal(2, boundary.Rows.Count);
        Assert.Equal(1L, Assert.Single(boundary.Rows, row => Equals(row["bucket"], new DateTimeOffset(2026, 3, 27, 23, 0, 0, TimeSpan.Zero)))["count"]);
        Assert.Equal(1L, Assert.Single(boundary.Rows, row => Equals(row["bucket"], localMidnight))["count"]);

        // America/Sao_Paulo advanced at local midnight in 2018. The local date still has a
        // deterministic bucket: its earliest valid instant is 01:00 local (03:00 UTC).
        var saoTransition = new DateTimeOffset(2018, 11, 4, 3, 0, 0, TimeSpan.Zero);
        var saoBefore = saoTransition.AddSeconds(-1);
        foreach (var row in new IReadOnlyDictionary<string, object?>[]
        {
            new Dictionary<string, object?> { ["id"] = "sao-before-midnight", ["createdAt"] = saoBefore },
            new Dictionary<string, object?> { ["id"] = "sao-after-midnight", ["createdAt"] = saoTransition }
        })
            Assert.Equal(WriteOutcomeStatus.Inserted, session.Insert(new StorageValues(row)).Status);
        var sao = AggregateWithSingleNativeCall(session, new AggregationQuery("daily")
        {
            TimeRange = new AggregationTimeRange(saoBefore, saoTransition.AddHours(1)),
            TimeZoneId = "America/Sao_Paulo"
        });
        Assert.Equal(2, sao.Rows.Count);
        Assert.Equal(1L, Assert.Single(sao.Rows, row => Equals(row["bucket"], new DateTimeOffset(2018, 11, 3, 3, 0, 0, TimeSpan.Zero)))["count"]);
        Assert.Equal(1L, Assert.Single(sao.Rows, row => Equals(row["bucket"], saoTransition))["count"]);

        // Amsterdam's 2026 fall-back repeats the 02:00 local hour. Both sides of the
        // transition remain in the same local calendar day and must share its UTC midnight.
        var fallBefore = new DateTimeOffset(2026, 10, 25, 0, 59, 59, TimeSpan.Zero).AddTicks(9_999_999);
        var fallAfter = new DateTimeOffset(2026, 10, 25, 1, 0, 0, TimeSpan.Zero);
        foreach (var row in new IReadOnlyDictionary<string, object?>[]
        {
            new Dictionary<string, object?> { ["id"] = "fall-before", ["createdAt"] = fallBefore },
            new Dictionary<string, object?> { ["id"] = "fall-after", ["createdAt"] = fallAfter }
        })
            Assert.Equal(WriteOutcomeStatus.Inserted, session.Insert(new StorageValues(row)).Status);
        var fall = AggregateWithSingleNativeCall(session, new AggregationQuery("daily")
        {
            TimeRange = new AggregationTimeRange(fallBefore, fallAfter.AddHours(1)),
            TimeZoneId = "Europe/Amsterdam"
        });
        var fallOutput = Assert.Single(fall.Rows);
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 22, 0, 0, TimeSpan.Zero), fallOutput["bucket"]);
        Assert.Equal(2L, fallOutput["count"]);

        // Goose Bay repeated local midnight in 2010. The declared contract selects the
        // earliest instant (the occurrence with the larger UTC offset), which is 03:00Z.
        var ambiguousMidnightInstant = new DateTimeOffset(2010, 11, 7, 4, 30, 0, TimeSpan.Zero);
        Assert.Equal(WriteOutcomeStatus.Inserted, session.Insert(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "goose-bay-ambiguous-midnight", ["createdAt"] = ambiguousMidnightInstant
        })).Status);
        var ambiguousMidnight = AggregateWithSingleNativeCall(session, new AggregationQuery("daily")
        {
            TimeRange = new AggregationTimeRange(ambiguousMidnightInstant.AddHours(-1), ambiguousMidnightInstant.AddHours(1)),
            TimeZoneId = "America/Goose_Bay"
        });
        var ambiguousOutput = Assert.Single(ambiguousMidnight.Rows);
        Assert.Equal(new DateTimeOffset(2010, 11, 7, 3, 0, 0, TimeSpan.Zero), ambiguousOutput["bucket"]);
        Assert.Equal(1L, ambiguousOutput["count"]);

        // Apia skipped all of 2011-12-30. Looking back into that nonexistent local date
        // must not move the following day's bucket later than its real 00:00 boundary.
        var apiaInstant = new DateTimeOffset(2011, 12, 30, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(WriteOutcomeStatus.Inserted, session.Insert(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "apia-after-skipped-date", ["createdAt"] = apiaInstant
        })).Status);
        var apia = AggregateWithSingleNativeCall(session, new AggregationQuery("daily")
        {
            TimeRange = new AggregationTimeRange(apiaInstant.AddHours(-1), apiaInstant.AddHours(1)),
            TimeZoneId = "Pacific/Apia"
        });
        var apiaOutput = Assert.Single(apia.Rows);
        Assert.Equal(new DateTimeOffset(2011, 12, 30, 10, 0, 0, TimeSpan.Zero), apiaOutput["bucket"]);
        Assert.Equal(1L, apiaOutput["count"]);

        // The same logical key/data in two scopes must not cross-contaminate a native bucket.
        var scopedUnit = Unit() with
        {
            Id = new StorageUnitId("time_bucket_scoped_" + Guid.NewGuid().ToString("N")),
            Name = "s10_scoped_" + Guid.NewGuid().ToString("N"),
            Scope = ScopePolicy.Scoped
        };
        Assert.True(connection.Schema.Apply(scopedUnit).Applied, providerName);
        var scopeA = connection.OpenSession(scopedUnit, StorageAccess.Scoped(new StorageScope("bucket-a")));
        var scopeB = connection.OpenSession(scopedUnit, StorageAccess.Scoped(new StorageScope("bucket-b")));
        var scopedInstant = new DateTimeOffset(2026, 3, 29, 0, 30, 0, TimeSpan.Zero);
        var shared = new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "same-key", ["createdAt"] = scopedInstant
        });
        Assert.Equal(WriteOutcomeStatus.Inserted, scopeA.Insert(shared).Status);
        Assert.Equal(WriteOutcomeStatus.Inserted, scopeB.Insert(shared).Status);
        Assert.Equal(WriteOutcomeStatus.Inserted, scopeA.Insert(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "scope-a-only", ["createdAt"] = scopedInstant.AddHours(1)
        })).Status);
        var scopedQuery = new AggregationQuery("daily")
        {
            TimeRange = new AggregationTimeRange(scopedInstant, scopedInstant.AddDays(1)),
            TimeZoneId = "Europe/Amsterdam"
        };
        Assert.Equal(2L, Assert.Single(AggregateWithSingleNativeCall(scopeA, scopedQuery).Rows)["count"]);
        Assert.Equal(1L, Assert.Single(AggregateWithSingleNativeCall(scopeB, scopedQuery).Rows)["count"]);
    }

    private static void AssertFixedProvider(string providerName, Func<IStorageProviderConnection> open)
    {
        using var connection = open();
        var unit = FixedUnit() with
        {
            Id = new StorageUnitId("fixed_time_bucket_live_" + Guid.NewGuid().ToString("N")),
            Name = "fixed_s10_live_" + Guid.NewGuid().ToString("N")
        };
        Assert.True(connection.Schema.Apply(unit).Applied, providerName);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        var from = new DateTimeOffset(2026, 8, 16, 10, 15, 0, 500, TimeSpan.Zero);
        var firstEnd = from.AddHours(1);
        var secondEnd = firstEnd.AddHours(1);
        var to = from.AddDays(31);
        foreach (var row in new IReadOnlyDictionary<string, object?>[]
        {
            new Dictionary<string, object?> { ["id"] = "first", ["createdAt"] = from },
            new Dictionary<string, object?> { ["id"] = "first-last-tick", ["createdAt"] = firstEnd.AddTicks(-1) },
            new Dictionary<string, object?> { ["id"] = "second-first-tick", ["createdAt"] = firstEnd },
            new Dictionary<string, object?> { ["id"] = "second-last-tick", ["createdAt"] = secondEnd.AddTicks(-1) },
            new Dictionary<string, object?> { ["id"] = "upper", ["createdAt"] = to }
        })
            Assert.Equal(WriteOutcomeStatus.Inserted, session.Insert(new StorageValues(row)).Status);

        var output = AggregateWithSingleNativeCall(session, new AggregationQuery("hourly")
        {
            TimeRange = new AggregationTimeRange(from, to)
        }).Rows;
        Assert.Equal(2, output.Count);
        Assert.Equal(2L, Assert.Single(output, row => Equals(row["bucket"], from))["count"]);
        Assert.Equal(2L, Assert.Single(output, row => Equals(row["bucket"], firstEnd))["count"]);

        var reduced = AggregateWithSingleNativeCall(session, new AggregationQuery("hourly")
        {
            TimeRange = new AggregationTimeRange(from, to),
            PostPredicate = new AggregationPredicate.Comparison(
                "count", AggregationPredicateOperator.Equal, [2L]),
            OrderByTerms = [new AggregationOrderTerm("bucket", SortDirection.Descending)],
            Take = 1
        });
        var reducedOutput = Assert.Single(reduced.Rows);
        Assert.Equal(firstEnd, reducedOutput["bucket"]);
        Assert.Equal(2L, reducedOutput["count"]);

        // Also exercise a source range before its explicit origin; negative elapsed durations
        // must use mathematical floor rather than SQL integer truncation toward zero.
        var negativeOrigin = AggregateWithSingleNativeCall(session, new AggregationQuery("hourly")
        {
            TimeRange = new AggregationTimeRange(from, from.AddHours(2)),
            TimeBucketOrigin = from.AddHours(1).AddMinutes(30)
        }).Rows;
        Assert.Equal(3, negativeOrigin.Count);
        Assert.Equal(1L, Assert.Single(negativeOrigin, row => Equals(row["bucket"], from.AddMinutes(-30)))["count"]);
        Assert.Equal(2L, Assert.Single(negativeOrigin, row => Equals(row["bucket"], from.AddMinutes(30)))["count"]);
        Assert.Equal(1L, Assert.Single(negativeOrigin, row => Equals(row["bucket"], from.AddMinutes(90)))["count"]);

        // Keep the historical epoch anchor as an explicit adversarial invocation: provider
        // arithmetic must not round .NET tick values near 6e17 before flooring.
        var epochBoundary = new DateTimeOffset(2026, 8, 17, 10, 0, 0, TimeSpan.Zero);
        foreach (var row in new IReadOnlyDictionary<string, object?>[]
        {
            new Dictionary<string, object?> { ["id"] = "epoch-before", ["createdAt"] = epochBoundary.AddTicks(-1) },
            new Dictionary<string, object?> { ["id"] = "epoch-boundary", ["createdAt"] = epochBoundary },
            new Dictionary<string, object?> { ["id"] = "epoch-last", ["createdAt"] = epochBoundary.AddHours(1).AddTicks(-1) }
        })
            Assert.Equal(WriteOutcomeStatus.Inserted, session.Insert(new StorageValues(row)).Status);
        var epochOutput = session.Aggregate(new AggregationQuery("hourly")
        {
            TimeRange = new AggregationTimeRange(epochBoundary.AddTicks(-1), epochBoundary.AddHours(1)),
            TimeBucketOrigin = DateTimeOffset.UnixEpoch
        }).Rows;
        Assert.Equal(2, epochOutput.Count);
        Assert.Equal(1L, Assert.Single(epochOutput, row => Equals(row["bucket"], epochBoundary.AddHours(-1)))["count"]);
        Assert.Equal(2L, Assert.Single(epochOutput, row => Equals(row["bucket"], epochBoundary))["count"]);

        var future = new DateTimeOffset(2040, 1, 1, 0, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(WriteOutcomeStatus.Inserted, session.Insert(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "future", ["createdAt"] = future
        })).Status);
        var futureQuery = new AggregationQuery("hourly")
        {
            TimeRange = new AggregationTimeRange(future, future.AddHours(1)),
            TimeBucketOrigin = DateTimeOffset.UnixEpoch
        };
        var futureOutput = session.Aggregate(futureQuery);
        Assert.Equal(future, Assert.Single(futureOutput.Rows)["bucket"]);
    }

    private static AggregationResult AggregateWithSingleNativeCall(
        IStorageSession session,
        AggregationQuery query)
    {
        var calls = new List<string>();
        Groundwork.Kernel.AggregationExecutionDiagnostics.Observer = calls.Add;
        try
        {
            var result = session.Aggregate(query);
            Assert.Single(calls);
            Assert.Equal("aggregate", calls[0]);
            return result;
        }
        finally
        {
            Groundwork.Kernel.AggregationExecutionDiagnostics.Observer = null;
        }
    }

    private static string Required(string name) =>
        RequiredCore(name);

    private static string RequiredCore(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        Skip.If(string.IsNullOrWhiteSpace(value), "Set " + name + " to run this live provider proof.");
        return value!;
    }

    private static StorageUnit Unit() => new()
    {
        Id = new StorageUnitId("time_bucket_differential"),
        Name = "time_bucket_differential",
        Columns =
        [
            new() { Name = "id", Type = PortableType.String, MaxLength = 128, IsNullable = false },
            new() { Name = "createdAt", Type = PortableType.DateTimeOffset }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        AggregationProfiles =
        [
            new AggregationProfile
            {
                Name = "daily",
                GroupByExpressions = [AggregationGroup.TimeBucket.LocalCalendarDay("bucket", "createdAt")],
                Aggregates = [new Aggregate.Count("count")]
            }
        ]
    };

    private static StorageUnit FixedUnit() => new()
    {
        Id = new StorageUnitId("time_bucket_fixed_differential"),
        Name = "time_bucket_fixed_differential",
        Columns =
        [
            new() { Name = "id", Type = PortableType.String, MaxLength = 128, IsNullable = false },
            new() { Name = "createdAt", Type = PortableType.DateTimeOffset }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        AggregationProfiles =
        [
            new AggregationProfile
            {
                Name = "hourly",
                GroupByExpressions = [AggregationGroup.TimeBucket.FixedUtc("bucket", "createdAt", TimeSpan.FromHours(1))],
                Aggregates = [new Aggregate.Count("count")],
                AllowedPredicates =
                [
                    new AggregationPredicateAllowance
                    {
                        Alias = "count",
                        SupportedPredicates = new HashSet<AggregationPredicateOperator>
                        {
                            AggregationPredicateOperator.Equal
                        }
                    }
                ]
            }
        ]
    };
}
