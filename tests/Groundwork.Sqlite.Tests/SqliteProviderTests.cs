using Microsoft.Data.Sqlite;
using System.Text.Json;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Testing;
using Groundwork.Store;
using Groundwork.Sqlite;
using Groundwork.Query.Linq;
using Groundwork.Query.Model;
using Groundwork.Query.Linq.Sqlite;
using Groundwork.Query.Planning;
using Groundwork.Substrate.Relational;
using Xunit;

namespace Groundwork.Sqlite.Tests;

public sealed class SqliteProviderTests
{
    [Fact]
    public void Provider_composed_index_names_are_injective_for_underscore_components()
    {
        var left = SqliteDialect.PhysicalIndexName("a_", "b");
        var right = SqliteDialect.PhysicalIndexName("a", "_b");

        Assert.NotEqual(left, right);
    }

    [Fact]
    public void A_63_byte_storage_unit_name_applies_without_provider_rewriting()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var name = new string('a', PortabilityValidator.MaximumPortableIdentifierLength);
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("sqlite-boundary"),
            Name = name,
            Columns = [new ColumnDefinition { Name = "id", Type = PortableType.Int32, IsNullable = false }],
            Key = new KeyDefinition { Columns = ["id"] }
        };

        Assert.True(connection.Schema.Apply(unit).Applied);
        Assert.True(connection.Schema.Diff(unit).IsEmpty);
    }

    [Fact]
    public void Schema_diff_and_apply_refuse_a_default_with_the_wrong_clr_type()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("sqlite-invalid-default"),
            Name = "sqlite_invalid_default",
            Columns =
            [
                new() { Name = "id", Type = PortableType.Guid, IsNullable = false },
                new() { Name = "attempts", Type = PortableType.Int64, Default = new PortableDefault(1.5d) }
            ],
            Key = new KeyDefinition { Columns = ["id"] }
        };

        var diffFailure = Assert.Throws<InvalidOperationException>(() => connection.Schema.Diff(unit));
        Assert.Contains("GW-PORT-013", diffFailure.Message, StringComparison.Ordinal);
        Assert.Contains("Int64", diffFailure.Message, StringComparison.Ordinal);
        Assert.Contains("Double", diffFailure.Message, StringComparison.Ordinal);

        var applyFailure = Assert.Throws<InvalidOperationException>(() => connection.Schema.Apply(unit));
        Assert.Contains("GW-PORT-013", applyFailure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_defaults_are_serialized_as_json_literals_in_sqlite_schema()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["state"] = "pending",
            ["items"] = new List<object?> { true, 2 }
        };
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("sqlite-json-default"),
            Name = "gw_sqlite_json_default",
            Columns =
            [
                new() { Name = "id", Type = PortableType.Guid, IsNullable = false },
                new() { Name = "payload", Type = PortableType.Json, Default = new PortableDefault(payload) }
            ],
            Key = new KeyDefinition { Columns = ["id"] }
        };

        Assert.True(connection.Schema.Apply(unit).Applied);
        var id = Guid.NewGuid();
        var row = connection.OpenSession(unit, StorageAccess.Global).Insert(new StorageValues(
            new Dictionary<string, object?> { ["id"] = id }));

        Assert.Equal(WriteOutcomeStatus.Inserted, row.Status);
        var stored = connection.OpenSession(unit, StorageAccess.Global).Read(new StorageKey(
            new Dictionary<string, object?> { ["id"] = id }));
        Assert.NotNull(stored);
        var json = Assert.IsType<JsonElement>(stored!.Values.Values["payload"]);
        Assert.Equal("pending", json.GetProperty("state").GetString());
        var items = json.GetProperty("items").EnumerateArray().ToArray();
        Assert.True(items[0].GetBoolean());
        Assert.Equal(2, items[1].GetInt32());
    }

    [Theory]
    [InlineData("pending", false)]
    [InlineData("\"pending\"", true)]
    public void Raw_json_string_defaults_are_validated_and_deployed_consistently(string value, bool expectedPortable)
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("sqlite-raw-json-default-" + expectedPortable),
            Name = "gw_sqlite_raw_json_default_" + (expectedPortable ? "valid" : "invalid"),
            Columns =
            [
                new() { Name = "id", Type = PortableType.Guid, IsNullable = false },
                new() { Name = "payload", Type = PortableType.Json, Default = new PortableDefault(value) }
            ],
            Key = new KeyDefinition { Columns = ["id"] }
        };

        if (!expectedPortable)
        {
            var diffFailure = Assert.Throws<InvalidOperationException>(() => connection.Schema.Diff(unit));
            Assert.Contains("GW-PORT-013", diffFailure.Message, StringComparison.Ordinal);
            Assert.Contains("payload", diffFailure.Message, StringComparison.Ordinal);
            Assert.Contains("Json", diffFailure.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(String), diffFailure.Message, StringComparison.Ordinal);

            var applyFailure = Assert.Throws<InvalidOperationException>(() => connection.Schema.Apply(unit));
            Assert.Contains("GW-PORT-013", applyFailure.Message, StringComparison.Ordinal);
            return;
        }

        Assert.True(connection.Schema.Apply(unit).Applied);
        var id = Guid.NewGuid();
        Assert.Equal(WriteOutcomeStatus.Inserted,
            connection.OpenSession(unit, StorageAccess.Global).Insert(new StorageValues(
                new Dictionary<string, object?> { ["id"] = id })).Status);

        var stored = connection.OpenSession(unit, StorageAccess.Global).Read(new StorageKey(
            new Dictionary<string, object?> { ["id"] = id }));
        Assert.NotNull(stored);
        Assert.Equal("pending", Assert.IsType<JsonElement>(stored!.Values.Values["payload"]).GetString());
    }

    [Fact]
    public void Json_document_and_element_root_string_defaults_are_serialized_and_deployed()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        using var document = JsonDocument.Parse("\"pending\"");

        foreach (var (kind, value) in new[]
        {
            ("document", (object)document),
            ("element", (object)document.RootElement)
        })
        {
            var unit = new StorageUnit
            {
                Id = new StorageUnitId("sqlite-json-string-default-" + kind),
                Name = "gw_sqlite_json_string_default_" + kind,
                Columns =
                [
                    new() { Name = "id", Type = PortableType.Guid, IsNullable = false },
                    new() { Name = "payload", Type = PortableType.Json, Default = new PortableDefault(value) }
                ],
                Key = new KeyDefinition { Columns = ["id"] }
            };

            Assert.True(connection.Schema.Apply(unit).Applied);
            var id = Guid.NewGuid();
            Assert.Equal(WriteOutcomeStatus.Inserted,
                connection.OpenSession(unit, StorageAccess.Global).Insert(new StorageValues(
                    new Dictionary<string, object?> { ["id"] = id })).Status);

            var stored = connection.OpenSession(unit, StorageAccess.Global).Read(new StorageKey(
                new Dictionary<string, object?> { ["id"] = id }));
            Assert.NotNull(stored);
            Assert.Equal("pending", Assert.IsType<JsonElement>(stored!.Values.Values["payload"]).GetString());
        }
    }

    /// <summary>
    /// The runtime convenience apply takes no authorization callback, so it is the one path where a
    /// declaration edit could reach a live catalog unreviewed. It performs what re-applying could
    /// put back and refuses what it could not, by name — the rows stay put either way.
    /// </summary>
    [Fact]
    public void Schema_apply_refuses_a_drop_it_cannot_undo_and_leaves_the_column_and_its_rows()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = OrdersUnit(includeLegacyTotal: true);
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        session.Upsert(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "o-1",
            ["customer"] = "ada",
            ["legacy_total"] = 7m
        }));

        var failure = Assert.Throws<InvalidOperationException>(() =>
            connection.Schema.Apply(OrdersUnit(includeLegacyTotal: false)));

        Assert.Contains("GW-SCHEMA-010", failure.Message, StringComparison.Ordinal);
        Assert.Contains("drop-column:orders.legacy_total", failure.Message, StringComparison.Ordinal);
        // Refused before any provider work: the column and the row it holds are untouched.
        var stored = connection.OpenSession(unit, StorageAccess.Global).Read(new StorageKey(new Dictionary<string, object?> { ["id"] = "o-1" }));
        Assert.NotNull(stored);
        Assert.Equal(7m, stored!.Values.Values["legacy_total"]);
        Assert.Contains(
            connection.Schema.Diff(OrdersUnit(includeLegacyTotal: false)).Changes,
            change => change.Kind == SchemaChangeKind.DropColumn && change.Identity == "legacy_total");
    }

    /// <summary>The same apply still performs a rename, because renaming carries the rows.</summary>
    [Fact]
    public void Schema_apply_performs_a_rename_and_carries_the_rows()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = OrdersUnit(includeLegacyTotal: false);
        connection.Schema.Apply(unit);
        connection.OpenSession(unit, StorageAccess.Global).Upsert(new StorageValues(
            new Dictionary<string, object?> { ["id"] = "o-1", ["customer"] = "ada" }));

        var renamed = OrdersUnit(includeLegacyTotal: false) with
        {
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, MaxLength = 64, IsNullable = false },
                new() { Name = "buyer", Id = "customer", Type = PortableType.String, MaxLength = 64 }
            ]
        };
        Assert.True(connection.Schema.Apply(renamed).Applied);

        var stored = connection.OpenSession(renamed, StorageAccess.Global).Read(new StorageKey(new Dictionary<string, object?> { ["id"] = "o-1" }));
        Assert.NotNull(stored);
        Assert.Equal("ada", stored!.Values.Values["buyer"]);
    }

    private static StorageUnit OrdersUnit(bool includeLegacyTotal) => new()
    {
        Id = new StorageUnitId("orders"),
        Name = "gw_apply_orders",
        Columns =
        [
            new() { Name = "id", Type = PortableType.String, MaxLength = 64, IsNullable = false },
            new() { Name = "customer", Type = PortableType.String, MaxLength = 64 },
            ..(includeLegacyTotal
                ? new[] { new ColumnDefinition { Name = "legacy_total", Type = PortableType.Decimal, Precision = 18, Scale = 4 } }
                : [])
        ],
        Key = new KeyDefinition { Columns = ["id"] }
    };

    [Fact]
    public void Native_aggregation_predicates_preserve_typed_null_bool_datetime_guid_and_binary_values()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var instant = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var identifier = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("aggregation-typed-sqlite"),
            Name = "aggregation_typed_sqlite",
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, IsNullable = false },
                new() { Name = "group", Type = PortableType.String, IsNullable = false },
                new() { Name = "flag", Type = PortableType.Boolean },
                new() { Name = "moment", Type = PortableType.DateTimeOffset },
                new() { Name = "identifier", Type = PortableType.Guid },
                new() { Name = "payload", Type = PortableType.Binary },
                new() { Name = "order", Type = PortableType.Int64, IsNullable = false }
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
                        new Aggregate.FirstBy("firstFlag", "flag", "order"),
                        new Aggregate.FirstBy("firstMoment", "moment", "order"),
                        new Aggregate.FirstBy("firstIdentifier", "identifier", "order"),
                        new Aggregate.FirstBy("firstPayload", "payload", "order")
                    ],
                    AllowedPredicates = new[] { "firstFlag", "firstMoment", "firstIdentifier", "firstPayload" }
                        .Select(alias => new AggregationPredicateAllowance
                        {
                            Alias = alias,
                            SupportedPredicates = new HashSet<AggregationPredicateOperator>
                            {
                                AggregationPredicateOperator.Equal,
                                AggregationPredicateOperator.In
                            }
                        }).ToArray()
                }
            ]
        };
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        session.Insert(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "one", ["group"] = "g", ["flag"] = null, ["moment"] = instant,
            ["identifier"] = identifier, ["payload"] = new byte[] { 1, 2 }, ["order"] = 1L
        }));
        session.Insert(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "two", ["group"] = "g", ["flag"] = true, ["moment"] = instant.AddDays(1),
            ["identifier"] = Guid.NewGuid(), ["payload"] = new byte[] { 3, 4 }, ["order"] = 2L
        }));

        Assert.Single(session.Aggregate(new AggregationQuery("summary")
        {
            PostPredicate = new AggregationPredicate.Comparison("firstFlag", AggregationPredicateOperator.Equal, [(object?)null])
        }).Rows);
        Assert.Single(session.Aggregate(new AggregationQuery("summary")
        {
            PostPredicate = new AggregationPredicate.Comparison("firstFlag", AggregationPredicateOperator.In, [(object?)null, true])
        }).Rows);
        Assert.Single(session.Aggregate(new AggregationQuery("summary")
        {
            PostPredicate = new AggregationPredicate.Comparison("firstMoment", AggregationPredicateOperator.Equal, [instant])
        }).Rows);
        Assert.Single(session.Aggregate(new AggregationQuery("summary")
        {
            PostPredicate = new AggregationPredicate.Comparison("firstIdentifier", AggregationPredicateOperator.Equal, [identifier])
        }).Rows);
        Assert.Single(session.Aggregate(new AggregationQuery("summary")
        {
            PostPredicate = new AggregationPredicate.Comparison("firstPayload", AggregationPredicateOperator.Equal, [new byte[] { 1, 2 }])
        }).Rows);
    }

    [Fact]
    public void Native_time_bucket_aggregation_is_one_sql_grouping_with_exact_range_semantics()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("sqlite-time-bucket"),
            Name = "s10_time_bucket",
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, IsNullable = false },
                new() { Name = "createdAt", Type = PortableType.DateTimeOffset },
                new() { Name = "amount", Type = PortableType.Int64 }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            AggregationProfiles =
            [
                new AggregationProfile
                {
                    Name = "hourly",
                    GroupByExpressions = [AggregationGroup.TimeBucket.FixedUtc("bucket", "createdAt", TimeSpan.FromHours(1))],
                    Aggregates = [new Aggregate.Count("count"), new Aggregate.Sum("total", "amount")],
                    MaxInputRows = 20,
                    MaxGroups = 10
                }
            ]
        };
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        var from = new DateTimeOffset(2026, 8, 16, 10, 30, 0, TimeSpan.Zero);
        var to = from.AddHours(2);
        foreach (var row in new[]
        {
            ("first", from, 3L),
            ("second", from.AddMinutes(15), 4L),
            ("upper", to, 99L),
            ("null", (DateTimeOffset?)null, 100L)
        })
        {
            Assert.Equal(WriteOutcomeStatus.Inserted, session.Insert(new StorageValues(new Dictionary<string, object?>
            {
                ["id"] = row.Item1, ["createdAt"] = row.Item2, ["amount"] = row.Item3
            })).Status);
        }

        var result = session.Aggregate(new AggregationQuery("hourly")
        {
            TimeRange = new AggregationTimeRange(from, to)
        });

        var output = Assert.Single(result.Rows);
        Assert.Equal(from, output["bucket"]);
        Assert.Equal(2L, output["count"]);
        Assert.Equal(7L, output["total"]);
    }

    [Fact]
    public void Native_local_calendar_day_bucket_handles_the_spring_forward_boundary()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("sqlite-local-day"),
            Name = "s10_local_day",
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
                    Name = "daily",
                    GroupByExpressions = [AggregationGroup.TimeBucket.LocalCalendarDay("day", "createdAt")],
                    Aggregates = [new Aggregate.Count("count")]
                }
            ]
        };
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        var from = new DateTimeOffset(2026, 3, 29, 0, 30, 0, TimeSpan.Zero);
        session.Insert(new StorageValues(new Dictionary<string, object?> { ["id"] = "before", ["createdAt"] = from }));
        session.Insert(new StorageValues(new Dictionary<string, object?> { ["id"] = "after", ["createdAt"] = from.AddHours(20) }));

        var output = Assert.Single(session.Aggregate(new AggregationQuery("daily")
        {
            TimeRange = new AggregationTimeRange(from, from.AddDays(1)),
            TimeZoneId = "Europe/Amsterdam"
        }).Rows);

        Assert.Equal(new DateTimeOffset(2026, 3, 28, 23, 0, 0, TimeSpan.Zero), output["day"]);
        Assert.Equal(2L, output["count"]);
    }

    [Fact]
    public void Native_aggregation_preserves_separator_values_and_independent_FirstBy_orders()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = AggregationUnit(
            "aggregation-adversarial",
            [
                new Aggregate.SetUnion("labels", "label", 8),
                new Aggregate.FirstBy("firstAscending", "label", "ascendingOrder"),
                new Aggregate.FirstBy("firstDescending", "label", "descendingOrder", SortDirection.Descending)
            ]);
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        InsertAggregationRow(session, "1", "g", 0, 0m, "a\u001fb", 1, 1);
        InsertAggregationRow(session, "2", "g", 0, 0m, "plain", 2, 3);
        InsertAggregationRow(session, "3", "g", 0, 0m, "last", 3, 2);

        var row = Assert.Single(session.Aggregate(new AggregationQuery("summary")).Rows);

        Assert.Equal(new[] { "a\u001fb", "last", "plain" }, Assert.IsType<string[]>(row["labels"]));
        Assert.Equal("a\u001fb", row["firstAscending"]);
        Assert.Equal("plain", row["firstDescending"]);
    }

    [Fact]
    public void Native_aggregation_sums_decimal_text_exactly_and_widens_Int32()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = AggregationUnit(
            "aggregation-sums",
            [new Aggregate.Sum("integerTotal", "integerAmount"), new Aggregate.Sum("decimalTotal", "decimalAmount")]);
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        InsertAggregationRow(session, "1", "g", 2_000_000_000, 12_345_678_901_234_567_890.1234m, "a", 1, 1);
        InsertAggregationRow(session, "2", "g", 2_000_000_000, 0.0001m, "b", 2, 2);

        var row = Assert.Single(session.Aggregate(new AggregationQuery("summary")).Rows);

        Assert.Equal(4_000_000_000L, Assert.IsType<long>(row["integerTotal"]));
        Assert.Equal(12_345_678_901_234_567_890.1235m, Assert.IsType<decimal>(row["decimalTotal"]));
    }

    [Fact]
    public void Native_aggregation_orders_string_group_and_aggregate_aliases_by_utf16_ordinal()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = AggregationUnit(
            "aggregation-string-order",
            [new Aggregate.Count("count"), new Aggregate.Min("minimum", "label")]);
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        var supplementary = char.ConvertFromUtf32(0x10000);
        var privateUse = "\uE000";
        InsertAggregationRow(session, "1", "z", 0, 0m, supplementary, 1, 1);
        InsertAggregationRow(session, "2", "a", 0, 0m, privateUse, 2, 2);

        var rows = session.Aggregate(new AggregationQuery("summary")
        {
            OrderByTerms = [
                new AggregationOrderTerm("minimum", SortDirection.Ascending),
                new AggregationOrderTerm("group", SortDirection.Ascending)]
        }).Rows;

        Assert.Equal(["z", "a"], rows.Select(row => row["group"]));
        Assert.Equal([supplementary, privateUse], rows.Select(row => row["minimum"]));
    }

    [Fact]
    public void Native_aggregation_count_alias_reusing_string_source_orders_as_Int64()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = AggregationUnit(
            "aggregation-count-source-alias",
            [new Aggregate.Count("label")]);
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        InsertAggregationRow(session, "a-1", "a", 0, 0m, "first", 1, 1);
        InsertAggregationRow(session, "a-2", "a", 0, 0m, "second", 2, 2);
        InsertAggregationRow(session, "b-1", "b", 0, 0m, "third", 3, 3);

        var rows = session.Aggregate(new AggregationQuery("summary")
        {
            OrderByTerms =
            [
                new AggregationOrderTerm("label", SortDirection.Descending),
                new AggregationOrderTerm("group", SortDirection.Ascending)
            ]
        }).Rows;

        Assert.Equal(["a", "b"], rows.Select(row => row["group"]));
        Assert.Equal([2L, 1L], rows.Select(row => Assert.IsType<long>(row["label"])));
    }

    [Fact]
    public void Renderer_resolves_count_alias_before_string_source_column_for_order_type()
    {
        var unit = AggregationUnit(
            "aggregation-count-source-alias-render",
            [new Aggregate.Count("label")]) with
        {
            Columns = AggregationUnit("aggregation-count-source-alias-render-group", [])
                .Columns.Select(column => column.Name == "group"
                    ? column with { Type = PortableType.Int64 }
                    : column).ToArray()
        };
        var profile = unit.AggregationProfiles.Single();
        var sql = RelationalAggregationRenderer.Render(
            new SqliteDialect(),
            unit,
            profile,
            new AggregationQuery("summary")
            {
                OrderByTerms = [new AggregationOrderTerm("label", SortDirection.Ascending)]
            }).CommandText;

        Assert.Contains("CASE WHEN \"label\" IS NULL THEN 0 ELSE 1 END, \"label\" ASC", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("GROUNDWORK_UTF16_ORDINAL", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Native_decimal_sum_reports_the_portable_overflow_refusal()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var template = AggregationUnit("aggregation-sum-overflow", [new Aggregate.Sum("decimalTotal", "decimalAmount")]);
        var unit = template with
        {
            Columns = template.Columns.Select(column => column.Name == "decimalAmount"
                ? column with { Precision = 29, Scale = 0 }
                : column).ToArray()
        };
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        InsertAggregationRow(session, "1", "g", 0, decimal.MaxValue, "a", 1, 1);
        InsertAggregationRow(session, "2", "g", 0, decimal.MaxValue, "b", 2, 2);

        var exception = Assert.Throws<AggregationBudgetExceededException>(() =>
            session.Aggregate(new AggregationQuery("summary")));

        Assert.Equal("GW-AGG-SUM-001", exception.Code);
    }

    [Fact]
    public void Native_SetUnion_uses_ordinal_identity_even_for_NOCASE_storage()
    {
        using var store = TemporaryStore.Create();
        var unit = AggregationUnit("aggregation-ordinal-set", [new Aggregate.SetUnion("labels", "label", 2)]) with
        {
            Columns = AggregationUnit("unused", []).Columns.Select(column => column.Name == "label"
                ? column with { Collation = PortableCollation.OrdinalIgnoreCase }
                : column).ToArray()
        };
        using (var native = new SqliteConnection(store.ConnectionString))
        {
            native.Open();
            using var command = native.CreateCommand();
            command.CommandText = """
                CREATE TABLE aggregation_ordinal_set (
                    id TEXT COLLATE BINARY NOT NULL PRIMARY KEY,
                    "group" TEXT COLLATE BINARY NOT NULL,
                    integerAmount INTEGER NOT NULL,
                    decimalAmount TEXT NOT NULL,
                    label TEXT COLLATE NOCASE NULL,
                    __groundwork_search_label TEXT COLLATE BINARY NULL,
                    ascendingOrder INTEGER NOT NULL,
                    descendingOrder INTEGER NOT NULL,
                    __groundwork_action TEXT NOT NULL DEFAULT 'I');
                """;
            command.ExecuteNonQuery();
        }
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        InsertAggregationRow(session, "1", "g", 0, 0m, "A", 1, 1);
        InsertAggregationRow(session, "2", "g", 0, 0m, "a", 2, 2);

        var row = Assert.Single(session.Aggregate(new AggregationQuery("summary")).Rows);

        Assert.Equal(new[] { "A", "a" }, Assert.IsType<string[]>(row["labels"]));
    }

    [Theory]
    [InlineData("input", "GW-AGG-BOUND-004")]
    [InlineData("groups", "GW-AGG-BOUND-005")]
    [InlineData("values", "GW-AGG-BOUND-007")]
    public void Native_aggregation_refuses_each_budget_without_truncating(
        string budget,
        string expectedCode)
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var template = AggregationUnit(
            "aggregation-budget-" + budget,
            [new Aggregate.SetUnion("labels", "label", budget == "values" ? 1 : 8)]);
        var unit = template with
        {
            AggregationProfiles =
            [
                template.AggregationProfiles.Single() with
                {
                    MaxInputRows = budget == "input" ? 1 : 8,
                    MaxGroups = budget == "groups" ? 1 : 8
                }
            ]
        };
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        InsertAggregationRow(session, "1", "a", 0, 0m, "x", 1, 1);
        InsertAggregationRow(session, "2", budget == "groups" ? "b" : "a", 0, 0m, "y", 2, 2);

        var exception = Assert.Throws<AggregationBudgetExceededException>(() =>
            session.Aggregate(new AggregationQuery("summary")));

        Assert.Equal(expectedCode, exception.Code);
    }

    [Fact]
    public void Declared_aggregation_profile_executes_as_a_bounded_native_reduction()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("aggregation-sqlite"),
            Name = "aggregation_sqlite",
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, IsNullable = false },
                new() { Name = "group", Type = PortableType.String, IsNullable = false },
                new() { Name = "amount", Type = PortableType.Int64 },
                new() { Name = "label", Type = PortableType.String },
                new() { Name = "order", Type = PortableType.Int64, IsNullable = false }
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
                        new Aggregate.Min("minimum", "amount"),
                        new Aggregate.Max("maximum", "amount"),
                        new Aggregate.Sum("total", "amount"),
                        new Aggregate.SetUnion("labels", "label", 4),
                        new Aggregate.FirstBy("first", "label", "order")
                    ],
                    MaxGroups = 4,
                    MaxInputRows = 8
                }
            ]
        };
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        Assert.Equal(PortableType.Int64, session.Unit.Columns.Single(column => column.Name == "amount").Type);
        Assert.Equal("amount", Assert.IsType<Aggregate.Sum>(session.Unit.AggregationProfiles.Single().Aggregates.Single(item => item.Alias == "total")).Column);
        foreach (var row in new[]
        {
            (Id: "1", Group: "a", Amount: (long?)3, Label: "x", Order: 2L),
            (Id: "2", Group: "a", Amount: (long?)null, Label: "y", Order: 1L),
            (Id: "3", Group: "b", Amount: (long?)7, Label: null, Order: 3L),
            (Id: "4", Group: "c", Amount: (long?)null, Label: "z", Order: 4L)
        })
            Assert.Equal(WriteOutcomeStatus.Inserted, session.Insert(new StorageValues(new Dictionary<string, object?>
            {
                ["id"] = row.Id, ["group"] = row.Group, ["amount"] = row.Amount, ["label"] = row.Label, ["order"] = row.Order
            })).Status);

        var result = session.Aggregate(new AggregationQuery("summary"));

        Assert.Equal(3, result.Rows.Count);
        var a = Assert.Single(result.Rows, item => Equals(item["group"], "a"));
        Assert.Equal(3L, Assert.IsType<long>(a["total"]));
        Assert.Equal(new[] { "x", "y" }, Assert.IsType<string[]>(a["labels"]));
        Assert.Equal("y", a["first"]);
        var c = Assert.Single(result.Rows, item => Equals(item["group"], "c"));
        Assert.Null(c["total"]);
    }

    [Fact]
    public void Scoped_aggregation_is_native_and_isolated_before_grouping()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = AggregationUnit("aggregation-scoped-native", [
            new Aggregate.Count("count"),
            new Aggregate.Sum("total", "integerAmount")]);
        unit = unit with
        {
            Scope = ScopePolicy.Scoped,
            AggregationProfiles = [unit.AggregationProfiles.Single() with
            {
                AllowedPredicates = [new AggregationPredicateAllowance
                {
                    Alias = "count",
                    SupportedPredicates = new HashSet<AggregationPredicateOperator>
                    {
                        AggregationPredicateOperator.Equal
                    }
                }]
            }]
        };
        Assert.True(connection.Schema.Apply(unit).Applied);
        var first = connection.OpenSession(unit, StorageAccess.Scoped(new StorageScope("tenant-a")));
        var second = connection.OpenSession(unit, StorageAccess.Scoped(new StorageScope("tenant-b")));
        InsertAggregationRow(first, "same-key", "same-group", 2, 0m, "a", 1, 1);
        InsertAggregationRow(first, "only-a", "same-group", 3, 0m, "b", 2, 2);
        InsertAggregationRow(second, "same-key", "same-group", 11, 0m, "c", 1, 1);

        var scopedQuery = new AggregationQuery("summary")
        {
            OrderByTerms = [new AggregationOrderTerm("count", SortDirection.Descending), new AggregationOrderTerm("group", SortDirection.Ascending)]
        };
        var aResult = first.Aggregate(scopedQuery);
        var bResult = second.Aggregate(scopedQuery);
        var countFiltered = first.Aggregate(scopedQuery with
        {
            PostPredicate = new AggregationPredicate.Comparison("count", AggregationPredicateOperator.Equal, [2L])
        });
        var a = Assert.Single(aResult.Rows);
        var b = Assert.Single(bResult.Rows);

        Assert.Equal("same-group", a["group"]);
        Assert.Equal(2L, a["count"]);
        Assert.Equal(5L, a["total"]);
        Assert.Equal(1L, b["count"]);
        Assert.Equal(11L, b["total"]);
        Assert.Equal(2L, Assert.Single(countFiltered.Rows)["count"]);
        var aFingerprint = first.Aggregate(new AggregationQuery("summary")).ValueFingerprint;
        var bFingerprint = second.Aggregate(new AggregationQuery("summary")).ValueFingerprint;
        Assert.NotEqual(aFingerprint, bFingerprint);
        Assert.Equal(aResult.ShapeFingerprint, bResult.ShapeFingerprint);
    }

    [Fact]
    public void Scoped_native_sql_artifacts_inject_scope_before_grouping_and_budget_probe()
    {
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("aggregation-scoped-artifact"),
            Name = "aggregation_scoped_artifact",
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, IsNullable = false },
                new() { Name = "group", Type = PortableType.String, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] }
        };
        var profile = new AggregationProfile
        {
            Name = "summary",
            GroupByColumns = ["group"],
            Aggregates = [new Aggregate.Count("count")],
            AllowedPredicates = []
        };
        var scope = new ColumnRef(new TableId(unit.Name), SqliteSchemaCoordinator.ScopeColumn, QueryType.String, isNullable: false);
        var providerPredicate = new Predicate.Equal(scope, QueryConstant.Of(scope, "tenant-a"));
        var query = new AggregationQuery("summary")
        {
            OrderByTerms = [
                new AggregationOrderTerm("count", SortDirection.Descending),
                new AggregationOrderTerm("group", SortDirection.Ascending)],
            Take = 5
        };

        var command = RelationalAggregationRenderer.RenderWithProviderPredicate(new SqliteDialect(), unit, profile, query, providerPredicate).CommandText;
        var probe = RelationalAggregationRenderer.RenderBudgetProbeWithProviderPredicate(new SqliteDialect(), unit, profile, query, providerPredicate).CommandText;

        Assert.StartsWith("WITH ", command, StringComparison.Ordinal);
        Assert.Contains(SqliteSchemaCoordinator.ScopeColumn, command, StringComparison.Ordinal);
        Assert.Contains("COUNT(*) AS \"count\"", command, StringComparison.Ordinal);
        Assert.Contains("GROUP BY", command, StringComparison.Ordinal);
        Assert.Contains("LIMIT 5", command, StringComparison.Ordinal);
        Assert.Contains(SqliteSchemaCoordinator.ScopeColumn, probe, StringComparison.Ordinal);
        Assert.Contains("GROUP BY", probe, StringComparison.Ordinal);
    }

    private static StorageUnit AggregationUnit(string identity, IReadOnlyList<Aggregate> aggregates) => new()
    {
        Id = new StorageUnitId(identity),
        Name = identity.Replace('-', '_'),
        Columns =
        [
            new() { Name = "id", Type = PortableType.String, IsNullable = false },
            new() { Name = "group", Type = PortableType.String, IsNullable = false },
            new() { Name = "integerAmount", Type = PortableType.Int32, IsNullable = false },
            new() { Name = "decimalAmount", Type = PortableType.Decimal, Precision = 28, Scale = 4, IsNullable = false },
            new() { Name = "label", Type = PortableType.String },
            new() { Name = "ascendingOrder", Type = PortableType.Int64, IsNullable = false },
            new() { Name = "descendingOrder", Type = PortableType.Int64, IsNullable = false }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        AggregationProfiles =
        [
            new AggregationProfile
            {
                Name = "summary",
                GroupByColumns = ["group"],
                Aggregates = aggregates,
                MaxGroups = 8,
                MaxInputRows = 16
            }
        ]
    };

    private static void InsertAggregationRow(
        IStorageSession session,
        string id,
        string group,
        int integerAmount,
        decimal decimalAmount,
        string? label,
        long ascendingOrder,
        long descendingOrder) =>
        Assert.Equal(WriteOutcomeStatus.Inserted, session.Insert(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = id,
            ["group"] = group,
            ["integerAmount"] = integerAmount,
            ["decimalAmount"] = decimalAmount,
            ["label"] = label,
            ["ascendingOrder"] = ascendingOrder,
            ["descendingOrder"] = descendingOrder
        })).Status);

    [Fact]
    public void Provider_sequence_uses_sqlite_autoincrement_and_returns_generated_values()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = SequenceUnit("sequence-" + Guid.NewGuid().ToString("N"));

        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        var first = session.Insert(new StorageValues(new Dictionary<string, object?> { ["payload"] = "first" }));
        var second = session.Upsert(new StorageValues(new Dictionary<string, object?> { ["payload"] = "second" }));

        Assert.Equal(WriteOutcomeStatus.Inserted, first.Status);
        Assert.Equal(1L, first.GeneratedValue<long>("sequence"));
        Assert.Equal(WriteOutcomeStatus.Upserted, second.Status);
        Assert.Equal(2L, second.GeneratedValue<long>("sequence"));
        Assert.Equal("first", session.Read(new StorageKey(new Dictionary<string, object?> { ["sequence"] = 1L }))!
            .Values.Values["payload"]);
        Assert.Throws<ArgumentException>(() => session.Insert(new StorageValues(new Dictionary<string, object?>
        {
            ["sequence"] = 99L,
            ["payload"] = "caller-supplied"
        })));
    }

    [Fact]
    public void Provider_sequence_batch_returns_one_generated_value_per_exact_row()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = SequenceUnit("sequence-batch-" + Guid.NewGuid().ToString("N"));
        connection.Schema.Apply(unit);

        using var work = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, unit);
        work.Stage(RowWrite.Insert(unit, new StorageValues(new Dictionary<string, object?> { ["payload"] = "one" })));
        work.Stage(RowWrite.Insert(unit, new StorageValues(new Dictionary<string, object?> { ["payload"] = "two" })));

        var report = work.CommitWithOutcomes();

        Assert.True(report.IsSuccessful);
        Assert.Equal([1L, 2L], report.Outcomes.Select(outcome => outcome.Outcome.GeneratedValue<long>("sequence")));
    }

    [Fact]
    public void Scoped_provider_sequence_is_unit_wide_and_scope_isolates_reads()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = SequenceUnit("scoped-sequence-" + Guid.NewGuid().ToString("N")) with
        {
            Scope = ScopePolicy.Scoped
        };
        connection.Schema.Apply(unit);

        var first = connection.OpenSession(unit, StorageAccess.Scoped(new StorageScope("first")));
        var second = connection.OpenSession(unit, StorageAccess.Scoped(new StorageScope("second")));
        var firstSequence = first.Insert(Values("first")).GeneratedValue<long>("sequence");
        var secondSequence = second.Insert(Values("second")).GeneratedValue<long>("sequence");

        Assert.Equal(1L, firstSequence);
        Assert.Equal(2L, secondSequence);
        Assert.NotNull(first.Read(Key(firstSequence)));
        Assert.Null(first.Read(Key(secondSequence)));
        Assert.NotNull(second.Read(Key(secondSequence)));
        Assert.Null(second.Read(Key(firstSequence)));
    }

    [Fact]
    public void Privileged_cross_scope_query_is_native_scope_preserving_and_query_only()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var name = "cross_scope_" + Guid.NewGuid().ToString("N");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId(name),
            Name = name.Replace('-', '_'),
            Columns =
            [
                new ColumnDefinition { Name = "id", Type = PortableType.String, IsNullable = false },
                new ColumnDefinition { Name = "value", Type = PortableType.String }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Scope = ScopePolicy.Scoped
        };
        connection.Schema.Apply(unit);
        connection.OpenSession(unit, StorageAccess.Scoped(new StorageScope("tenant-a")))
            .Insert(new StorageValues(new Dictionary<string, object?> { ["id"] = "same", ["value"] = "shared" }));
        connection.OpenSession(unit, StorageAccess.Scoped(new StorageScope("tenant-b")))
            .Insert(new StorageValues(new Dictionary<string, object?> { ["id"] = "same", ["value"] = "shared" }));

        var session = connection.OpenSession(unit, StorageAccess.PrivilegedAcrossScopes(
            new StorageAccessAudit("sqlite-proof", "recover-stalled-workflows")));
        var table = new TableId(unit.Name);
        var request = new QueryRequest(
            table,
            Predicate.AlwaysTrue.Instance,
            [],
            Projection.All,
            Paging.Keyset(1),
            ResultShape.TotalCount.Instance);

        Assert.Throws<InvalidOperationException>(() => session.Read(
            new StorageKey(new Dictionary<string, object?> { ["id"] = "same" })));
        var first = session.QueryAcrossScopes(request);

        Assert.Equal(2, first.TotalCount);
        Assert.Single(first.Rows);
        Assert.NotNull(first.NextContinuationToken);
        var second = session.QueryAcrossScopes(new QueryRequest(
            table,
            request.Where,
            request.Order,
            request.Projection,
            Paging.Continuation(first.NextContinuationToken!, 1),
            request.Result));
        Assert.Single(second.Rows);
        Assert.Equal(
            new[] { "tenant-a", "tenant-b" },
            first.Rows.Concat(second.Rows).Select(row => row.Scope.Value).Order(StringComparer.Ordinal));
        Assert.All(first.Rows.Concat(second.Rows), row => Assert.Equal("same", row.Values["id"]));
    }

    [Fact]
    public void Provider_sequence_only_insert_uses_default_values()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var name = "sequence_only_" + Guid.NewGuid().ToString("N");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId(name),
            Name = name.Replace('-', '_'),
            Columns =
            [
                new() { Name = "sequence", Type = PortableType.Int64, IsNullable = false, Generation = ColumnGeneration.ProviderSequence }
            ],
            Key = new KeyDefinition { Columns = ["sequence"] }
        };
        connection.Schema.Apply(unit);

        var inserted = connection.OpenSession(unit, StorageAccess.Global)
            .Insert(new StorageValues(new Dictionary<string, object?>()));

        Assert.Equal(1L, inserted.GeneratedValue<long>("sequence"));
    }

    private sealed class LinqTicket
    {
        public string Id { get; set; } = string.Empty;
        public string Display { get; set; } = string.Empty;
        public string Code = string.Empty;
    }

    [Fact]
    public async Task Configured_linq_database_executes_ToListAsync_against_sqlite()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("linq-tickets"), Name = "linq_tickets",
            Columns = [new() { Name = "Id", Type = PortableType.String, IsNullable = false }, new() { Name = "value_col", Type = PortableType.String }, new() { Name = "code_col", Type = PortableType.String }],
            Key = new KeyDefinition { Columns = ["Id"] },
            Indexes = [new() { Name = "ix_value", Columns = [new IndexColumn("value_col")] }]
        };
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        Assert.Equal(WriteOutcomeStatus.Inserted, session.Insert(new StorageValues(new Dictionary<string, object?> { ["Id"] = "a", ["value_col"] = "hit", ["code_col"] = "C1" })).Status);

        var query = new GwQueryDatabase(new SqliteLinqExecutor(session)).Table<LinqTicket>(
            new GwTableModel<LinqTicket>("linq_tickets", [
                new GwColumn<LinqTicket>(nameof(LinqTicket.Id), "Id", QueryType.String, false),
                new GwColumn<LinqTicket>(nameof(LinqTicket.Display), "value_col", QueryType.String),
                new GwColumn<LinqTicket>(nameof(LinqTicket.Code), "code_col", QueryType.String)
            ])).Where(ticket => ticket.Display == "hit");
        var rows = await query.ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal("a", row.Id);
        Assert.Equal("hit", row.Display);
        Assert.Equal("C1", row.Code);
        await Assert.ThrowsAsync<InvalidOperationException>(() => query.Select(ticket => new { ticket.Id }).ToListAsync());
    }

    [Fact]
    public async Task Configured_linq_executor_counts_and_probes_provider_side_with_honest_cancellation()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("linq-counts"), Name = "linq_counts",
            Columns = [new() { Name = "Id", Type = PortableType.String, IsNullable = false }, new() { Name = "value_col", Type = PortableType.String }],
            Key = new KeyDefinition { Columns = ["Id"] },
            Indexes = [new() { Name = "ix_value", Columns = [new IndexColumn("value_col")] }]
        };
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        foreach (var (id, value) in new[] { ("a", "hit"), ("b", "hit"), ("c", "miss") })
            Assert.Equal(WriteOutcomeStatus.Inserted, session.Insert(new StorageValues(new Dictionary<string, object?> { ["Id"] = id, ["value_col"] = value })).Status);

        var executor = new SqliteLinqExecutor(session);
        var table = new GwQueryDatabase(executor).Table<LinqCountTicket>(
            new GwTableModel<LinqCountTicket>("linq_counts", [
                new GwColumn<LinqCountTicket>(nameof(LinqCountTicket.Id), "Id", QueryType.String, false),
                new GwColumn<LinqCountTicket>(nameof(LinqCountTicket.Display), "value_col", QueryType.String)
            ]));

        Assert.Equal(2, await table.Query.Where(ticket => ticket.Display == "hit").CountAsync(executor));
        Assert.Equal(0, await table.Query.Where(ticket => ticket.Display == "absent").CountAsync(executor));
        Assert.True(await table.Query.Where(ticket => ticket.Display == "miss").AnyAsync(executor));
        Assert.False(await table.Query.Where(ticket => ticket.Display == "absent").AnyAsync(executor));

        var cancelled = new CancellationToken(canceled: true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => table.Query.ToListAsync(executor, cancelled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => table.Query.CountAsync(executor, cancelled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => table.Query.AnyAsync(executor, cancelled));
    }

    [Fact]
    public async Task Configured_linq_executor_reads_through_a_unit_of_work_session()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("linq-uow"), Name = "linq_uow",
            Columns = [new() { Name = "Id", Type = PortableType.String, IsNullable = false }, new() { Name = "value_col", Type = PortableType.String }],
            Key = new KeyDefinition { Columns = ["Id"] },
            Indexes = [new() { Name = "ix_value", Columns = [new IndexColumn("value_col")] }]
        };
        Assert.True(connection.Schema.Apply(unit).Applied);
        using var unitOfWork = connection.BeginUnitOfWork(StorageAccess.Global, unit);
        var session = unitOfWork.OpenSession(unit);
        unitOfWork.Stage(RowWrite.Insert(unit, new StorageValues(new Dictionary<string, object?> { ["Id"] = "a", ["value_col"] = "staged" })));

        var executor = new SqliteLinqExecutor(session);
        var table = new GwQueryDatabase(executor).Table<LinqCountTicket>(
            new GwTableModel<LinqCountTicket>("linq_uow", [
                new GwColumn<LinqCountTicket>(nameof(LinqCountTicket.Id), "Id", QueryType.String, false),
                new GwColumn<LinqCountTicket>(nameof(LinqCountTicket.Display), "value_col", QueryType.String)
            ]));

        Assert.Equal(1, await table.Query.Where(ticket => ticket.Display == "staged").CountAsync(executor));
        var row = Assert.Single(await table.Query.Where(ticket => ticket.Display == "staged").ToListAsync(executor));
        Assert.Equal("a", row.Id);
        unitOfWork.Commit();
    }

    [Fact]
    public async Task Concurrent_writes_and_async_counts_on_one_session_stay_serialized()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("linq-serial"), Name = "linq_serial",
            Columns = [new() { Name = "Id", Type = PortableType.String, IsNullable = false }, new() { Name = "value_col", Type = PortableType.String }],
            Key = new KeyDefinition { Columns = ["Id"] },
            Indexes = [new() { Name = "ix_value", Columns = [new IndexColumn("value_col")] }]
        };
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        var executor = new SqliteLinqExecutor(session);
        var table = new GwQueryDatabase(executor).Table<LinqCountTicket>(
            new GwTableModel<LinqCountTicket>("linq_serial", [
                new GwColumn<LinqCountTicket>(nameof(LinqCountTicket.Id), "Id", QueryType.String, false),
                new GwColumn<LinqCountTicket>(nameof(LinqCountTicket.Display), "value_col", QueryType.String)
            ]));

        var writes = Task.Run(() =>
        {
            for (var iteration = 0; iteration < 200; iteration++)
                session.Upsert(new StorageValues(new Dictionary<string, object?> { ["Id"] = "row-" + iteration % 10, ["value_col"] = "v" + iteration }));
        });
        while (!writes.IsCompleted)
            Assert.InRange(await Scanned(table).CountAsync(executor), 0, 10);
        await writes;
        Assert.Equal(10, await Scanned(table).CountAsync(executor));
    }

    [Fact]
    public async Task Configured_linq_executor_completes_synchronously_over_an_in_process_provider()
    {
        using var database = new InMemoryProviderFactory().Create("memory://linq-executor-" + Guid.NewGuid().ToString("N"));
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("linq-foreign"), Name = "linq_foreign",
            Columns = [new() { Name = "Id", Type = PortableType.String, IsNullable = false, MaxLength = 64 }],
            Key = new KeyDefinition { Columns = ["Id"] },
            // No declared index. The key is a coverage candidate in its own right, so the
            // `Where(t => t.Id == ...)` below is admitted on the key alone — this unit used to
            // carry a second declaration over the same column purely to get past the gate.
        };
        Assert.True(database.Schema.Apply(unit).Applied);
        var session = database.OpenSession(unit, StorageAccess.Global);
        Assert.Equal(WriteOutcomeStatus.Inserted, session.Insert(new StorageValues(new Dictionary<string, object?> { ["Id"] = "a" })).Status);

        var executor = new SqliteLinqExecutor(session);
        var table = new GwQueryDatabase(executor).Table<LinqCountTicket>(
            new GwTableModel<LinqCountTicket>("linq_foreign", [
                new GwColumn<LinqCountTicket>(nameof(LinqCountTicket.Id), "Id", QueryType.String, false)
            ]));

        var pending = table.Query.Where(ticket => ticket.Id == "a").CountAsync(executor);
        Assert.True(pending.IsCompleted);
        Assert.Equal(1, await pending);
        Assert.True(await table.Query.Where(ticket => ticket.Id == "a").AnyAsync(executor));
        Assert.Equal("a", Assert.Single(await Scanned(table).ToListAsync(executor)).Id);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            table.Query.CountAsync(executor, new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task Configured_linq_executor_reads_through_a_consumer_session_decorator()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("linq-decorated"), Name = "linq_decorated",
            Columns = [new() { Name = "Id", Type = PortableType.String, IsNullable = false, MaxLength = 64 }],
            Key = new KeyDefinition { Columns = ["Id"] },
            Indexes = [new() { Name = "ix_id", Columns = [new IndexColumn("Id")] }]
        };
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        Assert.Equal(WriteOutcomeStatus.Inserted, session.Insert(new StorageValues(new Dictionary<string, object?> { ["Id"] = "a" })).Status);

        var decorated = new PassThroughStorageSession(session);
        var executor = new SqliteLinqExecutor(decorated);
        var table = new GwQueryDatabase(executor).Table<LinqCountTicket>(
            new GwTableModel<LinqCountTicket>("linq_decorated", [
                new GwColumn<LinqCountTicket>(nameof(LinqCountTicket.Id), "Id", QueryType.String, false)
            ]));

        Assert.Equal(1, await Scanned(table).CountAsync(executor));
        Assert.Equal("a", Assert.Single(await Scanned(table).ToListAsync(executor)).Id);

        // The executor reaches storage through the session contract's asynchronous member, which is
        // the only one that carries the caller's token. Reading through the synchronous member would
        // still return these rows, so the counters are what distinguish the two routes.
        Assert.Equal(0, decorated.SynchronousQueries);
        Assert.Equal(2, decorated.AsynchronousQueries);
    }

    private sealed class PassThroughStorageSession(IStorageSession inner) : IStorageSession
    {
        internal int SynchronousQueries { get; private set; }

        internal int AsynchronousQueries { get; private set; }

        public StorageUnit Unit => inner.Unit;
        public StorageAccess Access => inner.Access;
        public StoredEntry? Read(StorageKey key) => inner.Read(key);
        public ValueTask<StoredEntry?> ReadAsync(StorageKey key, CancellationToken cancellationToken = default) => inner.ReadAsync(key, cancellationToken);
        public QueryMaterializedResult Query(QueryRequest request, QueryRenderOptions? options = null)
        {
            SynchronousQueries++;
            return inner.Query(request, options);
        }

        public ValueTask<QueryMaterializedResult> QueryAsync(QueryRequest request, QueryRenderOptions? options = null, CancellationToken cancellationToken = default)
        {
            AsynchronousQueries++;
            return inner.QueryAsync(request, options, cancellationToken);
        }
        public AggregationResult Aggregate(AggregationQuery query) => inner.Aggregate(query);
        public ValueTask<AggregationResult> AggregateAsync(AggregationQuery query, CancellationToken cancellationToken = default) => inner.AggregateAsync(query, cancellationToken);
        public WriteOutcome Insert(StorageValues values, WriteOptions? options = null) => inner.Insert(values, options);
        public ValueTask<WriteOutcome> InsertAsync(StorageValues values, WriteOptions? options = null, CancellationToken cancellationToken = default) => inner.InsertAsync(values, options, cancellationToken);
        public WriteOutcome Update(StorageValues values, WriteOptions? options = null) => inner.Update(values, options);
        public ValueTask<WriteOutcome> UpdateAsync(StorageValues values, WriteOptions? options = null, CancellationToken cancellationToken = default) => inner.UpdateAsync(values, options, cancellationToken);
        public WriteOutcome Upsert(StorageValues values, WriteOptions? options = null) => inner.Upsert(values, options);
        public ValueTask<WriteOutcome> UpsertAsync(StorageValues values, WriteOptions? options = null, CancellationToken cancellationToken = default) => inner.UpsertAsync(values, options, cancellationToken);
        public WriteOutcome Delete(StorageKey key, WriteOptions? options = null) => inner.Delete(key, options);
        public ValueTask<WriteOutcome> DeleteAsync(StorageKey key, WriteOptions? options = null, CancellationToken cancellationToken = default) => inner.DeleteAsync(key, options, cancellationToken);
        public WriteOutcome Append(OperationId operationId, IReadOnlyList<StorageValues> values) => inner.Append(operationId, values);
        public ValueTask<WriteOutcome> AppendAsync(OperationId operationId, IReadOnlyList<StorageValues> values, CancellationToken cancellationToken = default) => inner.AppendAsync(operationId, values, cancellationToken);
    }

    [Fact]
    public async Task Async_query_cancelled_while_waiting_for_the_provider_gate_is_refused()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("linq-gate"), Name = "linq_gate",
            Columns = [new() { Name = "Id", Type = PortableType.String, IsNullable = false, MaxLength = 64 }],
            Key = new KeyDefinition { Columns = ["Id"] },
            Indexes = [new() { Name = "ix_id", Columns = [new IndexColumn("Id")] }]
        };
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        var executor = new SqliteLinqExecutor(session);
        var table = new GwQueryDatabase(executor).Table<LinqCountTicket>(
            new GwTableModel<LinqCountTicket>("linq_gate", [
                new GwColumn<LinqCountTicket>(nameof(LinqCountTicket.Id), "Id", QueryType.String, false)
            ]));

        using var cancellation = new CancellationTokenSource();
        Task<long> pending;
        lock (((SqliteProviderConnection)connection).Gate)
        {
            pending = Task.Run(() => Scanned(table).CountAsync(executor, cancellation.Token));
            Thread.Sleep(250);
            cancellation.Cancel();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task Existence_probe_on_a_continuation_request_answers_the_remaining_window()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("linq-window"), Name = "linq_window",
            Columns = [new() { Name = "Id", Type = PortableType.String, IsNullable = false, MaxLength = 64 }],
            Key = new KeyDefinition { Columns = ["Id"] },
            Indexes = [new() { Name = "ix_id", Columns = [new IndexColumn("Id")] }]
        };
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        foreach (var id in new[] { "a", "b", "c" })
            Assert.Equal(WriteOutcomeStatus.Inserted, session.Insert(new StorageValues(new Dictionary<string, object?> { ["Id"] = id })).Status);

        var tableId = new TableId("linq_window");
        var idColumn = new ColumnRef(tableId, "Id", QueryType.String, isNullable: false);
        var order = System.Collections.Immutable.ImmutableArray.Create(new OrderTerm(idColumn, OrderDirection.Ascending, NullOrder.Last));
        var firstPage = session.Query(new QueryRequest(tableId, Predicate.AlwaysTrue.Instance, order, Projection.All, Paging.Keyset(2)));
        Assert.Equal(2, firstPage.Rows.Count);
        Assert.NotNull(firstPage.NextContinuationToken);

        var executor = new SqliteLinqExecutor(session);
        var remaining = new QueryRequest(tableId, Predicate.AlwaysTrue.Instance, order, Projection.All,
            Paging.Continuation(firstPage.NextContinuationToken!, 2));
        Assert.True(await executor.AnyAsync(remaining));

        var endToken = QueryContinuationToken.Encode(remaining, QueryRenderOptions.Default, [QueryConstant.Of(idColumn, "c")]);
        var exhausted = new QueryRequest(tableId, Predicate.AlwaysTrue.Instance, order, Projection.All,
            Paging.Continuation(endToken, 2));
        Assert.False(await executor.AnyAsync(exhausted));
        Assert.Equal(3, await executor.CountAsync(exhausted));
    }

    /// <summary>
    /// Marks a deliberately unfiltered read. These tests are about execution mechanics, not coverage,
    /// and an unbounded read is a scan on every provider — so it says so explicitly rather than
    /// slipping past the gate.
    /// </summary>
    private static IGwQueryable<LinqCountTicket> Scanned(GwQueryTable<LinqCountTicket> table) =>
        table.AcceptScan("GW-SCAN-0001", "provider execution mechanics", "groundwork-tests",
            DateTimeOffset.UtcNow.AddYears(10));

    [Fact]
    public async Task Session_only_executor_still_admits_under_sqlites_own_parameter_budget()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("linq-budget"), Name = "linq_budget",
            Columns = [new() { Name = "Id", Type = PortableType.String, IsNullable = false, MaxLength = 64 }],
            Key = new KeyDefinition { Columns = ["Id"] },
            Indexes = [new() { Name = "ix_id", Columns = [new IndexColumn("Id")] }]
        };
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);

        // No connection is handed over, so there is nothing to advertise a budget. The adapter is
        // bound to SQLite at compile time and supplies SQLite's own 999 anyway, so a request SQLite
        // cannot bind is refused at admission rather than reaching the renderer.
        var table = new TableId("linq_budget");
        var id = new ColumnRef(table, "Id", QueryType.String, isNullable: false, maxLength: 64);
        var request = new QueryRequest(
            table,
            new Predicate.In(id, Enumerable.Range(0, 1_000).Select(value => QueryConstant.Of(id, "id" + value))),
            [],
            Projection.All,
            Paging.OffsetLimit(0, 1));

        var refusal = await Assert.ThrowsAsync<RuntimeValueFenceException>(
            () => new SqliteLinqExecutor(session).ToListAsync<LinqCountTicket>(request));

        Assert.Equal("GW-RUNTIME-011", refusal.Code);
        Assert.Contains("999", refusal.Message, StringComparison.Ordinal);
    }

    private sealed class LinqCountTicket
    {
        public string Id { get; set; } = string.Empty;
        public string Display { get; set; } = string.Empty;
    }

    [Fact]
    public async Task Async_write_waits_for_the_provider_gate()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("async-gate"), Name = "async_gate",
            Columns = [new() { Name = "Id", Type = PortableType.String, IsNullable = false }],
            Key = new KeyDefinition { Columns = ["Id"] }
        };
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);

        Task<WriteOutcome> pending;
        lock (((SqliteProviderConnection)connection).Gate)
        {
            pending = Task.Run(() =>
                session.InsertAsync(new StorageValues(new Dictionary<string, object?> { ["Id"] = "a" })).AsTask());
            Thread.Sleep(250);
            Assert.False(pending.IsCompleted);
        }

        Assert.Equal(WriteOutcomeStatus.Inserted, (await pending).Status);
        Assert.NotNull(await session.ReadAsync(new StorageKey(new Dictionary<string, object?> { ["Id"] = "a" })));
    }

    [Fact]
    public async Task Async_unit_of_work_commit_stages_flushes_and_reads_back()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("async-uow"), Name = "async_uow",
            Columns =
            [
                new() { Name = "Id", Type = PortableType.String, IsNullable = false },
                new() { Name = "Value", Type = PortableType.String, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["Id"] }
        };
        Assert.True(connection.Schema.Apply(unit).Applied);

        using (var unitOfWork = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, unit))
        {
            var staged = unitOfWork.OpenSession(unit);
            unitOfWork.Stage(RowWrite.Insert(unit, new StorageValues(new Dictionary<string, object?>
            {
                ["Id"] = "a", ["Value"] = "staged"
            })));
            var flushed = await staged.ReadAsync(new StorageKey(new Dictionary<string, object?> { ["Id"] = "a" }));
            Assert.Equal("staged", flushed?.Values.Values["Value"]);
            var report = await unitOfWork.CommitWithOutcomesAsync();
            Assert.Equal(1, report.Succeeded);
        }

        var session = connection.OpenSession(unit, StorageAccess.Global);
        var committed = await session.ReadAsync(new StorageKey(new Dictionary<string, object?> { ["Id"] = "a" }));
        Assert.Equal("staged", committed?.Values.Values["Value"]);
    }

    [Fact]
    public async Task Nested_write_is_refused_rather_than_blocking()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("nested-write"), Name = "nested_write",
            Columns =
            [
                new() { Name = "Id", Type = PortableType.String, IsNullable = false },
                new() { Name = "Value", Type = PortableType.String, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["Id"] },
            Concurrency = ConcurrencyDeclaration.Optimistic()
        };
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        var batched = Assert.IsAssignableFrom<IBatchedStorageSession>(session);

        // A non-unconditional precondition takes the batch fallback, which re-enters the write
        // path from inside the batch's own transaction.
        var write = RowWrite.Upsert(
            unit,
            new StorageValues(new Dictionary<string, object?> { ["Id"] = "a", ["Value"] = "nested" }),
            WriteOptions.CreateOnly);

        var pending = Task.Run(() => batched.ApplyBatch([write]));
        Assert.Same(pending, await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(30))));
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        Assert.Contains("GW-WRITE-NESTED-001", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provider_passes_provider_neutral_conformance_on_both_surfaces()
    {
        using var store = TemporaryStore.Create();

        // One store, both surfaces: each run proves the whole contract on its own storage units.
        var synchronous = ConformanceSuite.Run(new SqliteProviderFactory(), store.ConnectionString);
        Assert.True(synchronous.Passed, string.Join(Environment.NewLine, synchronous.Failures.Select(failure => $"{failure.Name}: {failure.Failure}")));

        var asynchronous = await ConformanceSuite.RunAsync(new SqliteProviderFactory(), store.ConnectionString);
        Assert.True(asynchronous.Passed, string.Join(Environment.NewLine, asynchronous.Failures.Select(failure => $"{failure.Name}: {failure.Failure}")));
    }

    [Fact]
    public void Non_nullable_addition_rebuild_preserves_rows_and_unique_indexes()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var original = Model(includePriority: false);
        Assert.True(connection.Schema.Apply(original).Applied);
        var session = connection.OpenSession(original, StorageAccess.Global);
        Assert.Equal(WriteOutcomeStatus.Inserted, session.Insert(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "one", ["value"] = "keep", ["uniqueValue"] = "unique"
        })).Status);

        var evolved = Model(includePriority: true);
        var applied = connection.Schema.Apply(evolved);
        Assert.True(applied.Applied);
        var read = connection.OpenSession(evolved, StorageAccess.Global).Read(new StorageKey(
            new Dictionary<string, object?> { ["id"] = "one" }));
        Assert.NotNull(read);
        Assert.Equal("keep", read!.Values.Values["value"]);
        Assert.Equal(0, read.Values.Values["priority"]);
        Assert.Equal(["by_value", "unique_value"], connection.Catalog.ReadIndexes(evolved.Id).Select(index => index.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(WriteOutcomeStatus.UniqueViolation, connection.OpenSession(evolved, StorageAccess.Global).Insert(
            new StorageValues(new Dictionary<string, object?>
            {
                ["id"] = "two", ["value"] = "other", ["uniqueValue"] = "unique", ["priority"] = 1
            })).Status);
    }

    [Fact]
    public void Folded_schema_migration_backfills_and_partial_updates_preserve_the_key()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var original = new StorageUnit
        {
            Id = new StorageUnitId("folded-migration"),
            Name = "folded_migration",
            Columns =
            [
                new ColumnDefinition { Name = "id", Type = PortableType.Int32, IsNullable = false },
                new ColumnDefinition { Name = "status", Type = PortableType.String, MaxLength = 32, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] }
        };
        Assert.True(connection.Schema.Apply(original).Applied);
        Assert.Equal(WriteOutcomeStatus.Inserted, connection.OpenSession(original, StorageAccess.Global)
            .Insert(new StorageValues(new Dictionary<string, object?> { ["id"] = 1, ["status"] = "Open" })).Status);

        var folded = original with
        {
            Columns = [.. original.Columns.Select(column => column.Name == "status"
                ? column with { Collation = PortableCollation.OrdinalIgnoreCase }
                : column)],
            Indexes = [new IndexDefinition { Name = "by_status", Columns = [new IndexColumn("status")] }]
        };
        Assert.Contains(SearchKeyProjection.Expand(folded).Columns, column => column.Name == "__groundwork_search_status");
        var foldedDiff = connection.Schema.Diff(folded);
        using (var historyConnection = new SqliteConnection(store.ConnectionString))
        {
            historyConnection.Open();
            using var historyCommand = historyConnection.CreateCommand();
            historyCommand.CommandText = "SELECT state_json FROM __groundwork_schema_history WHERE subject_id='folded-migration'";
            var state = PhysicalSchemaAppliedStateSerializer.Deserialize((string)historyCommand.ExecuteScalar()!);
            var target = SqliteSchemaCoordinator.Target(SqliteSchemaCoordinator.Physicalize(folded));
            var plan = PhysicalSchemaDiffPlanner.Plan(
                target,
                PhysicalSchemaHistoryState.FromApplied(state),
                DateTimeOffset.UnixEpoch);
            Assert.True(plan.IsApplicable, string.Join("; ", plan.Refusals.Select(refusal => refusal.Code + ":" + refusal.Message)));
            Assert.Contains(plan.Operations, operation => operation is BackfillColumnOperation backfill &&
                backfill.Derived is not null && backfill.RequiresAuthorization);
            Assert.Contains(plan.Operations, operation => operation is FinalizeColumnOperation finalize &&
                finalize.Column.Name == SearchKeyProjection.ColumnName("status"));
        }
        var foldedApply = connection.Schema.Apply(folded);
        Assert.True(foldedApply.Applied, string.Join("; ", foldedDiff.Changes.Select(change => change.Kind + ":" + change.Identity)) + " / " + string.Join("; ", foldedApply.Diff.Changes.Select(change => change.Kind + ":" + change.Identity)));

        var status = new ColumnRef(
            new TableId(folded.Name), "status", Groundwork.Query.Model.QueryType.String, false, 32,
            stringComparison: Groundwork.Query.Model.QueryStringComparisonPolicy.AsciiIgnoreCase);
        var session = connection.OpenSession(folded, StorageAccess.Global);
        var stored = session.Read(new StorageKey(new Dictionary<string, object?> { ["id"] = 1 }));
        Assert.NotNull(stored);
        Assert.DoesNotContain(SearchKeyProjection.ColumnName("status"), stored!.Values.Values.Keys);
        var result = session.Query(new Groundwork.Query.Model.QueryRequest(
            new Groundwork.Query.Model.TableId(folded.Name),
            new Groundwork.Query.Model.Predicate.StartsWith(status, "OP"),
            [], Groundwork.Query.Model.Projection.All, Groundwork.Query.Model.Paging.None));
        Assert.Equal([1], result.Rows.Select(row => Assert.IsType<int>(row["id"])));

        var indexed = session.Query(new Groundwork.Query.Model.QueryRequest(
            new Groundwork.Query.Model.TableId(folded.Name),
            new Groundwork.Query.Model.Predicate.StartsWith(status, "OP"),
            [], Groundwork.Query.Model.Projection.All, Groundwork.Query.Model.Paging.None),
            folded.CreateQueryRenderOptions("by_status"));
        Assert.Equal("by_status", indexed.SelectedIndex);
        Assert.False(indexed.IndexHintApplied);
        Assert.Equal([1], indexed.Rows.Select(row => Assert.IsType<int>(row["id"])));

        Assert.Equal(WriteOutcomeStatus.Updated, session.Update(new StorageValues(new Dictionary<string, object?> { ["id"] = 1 })).Status);
        Assert.Single(session.Query(new Groundwork.Query.Model.QueryRequest(
            new Groundwork.Query.Model.TableId(folded.Name),
            new Groundwork.Query.Model.Predicate.StartsWith(status, "OP"),
            [], Groundwork.Query.Model.Projection.All, Groundwork.Query.Model.Paging.None)).Rows);

        using var work = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, folded);
        work.Stage(RowWrite.Update(folded, new StorageValues(new Dictionary<string, object?> { ["id"] = 1 })));
        Assert.True(work.CommitWithOutcomes().IsSuccessful);
        Assert.Single(session.Query(new Groundwork.Query.Model.QueryRequest(
            new Groundwork.Query.Model.TableId(folded.Name),
            new Groundwork.Query.Model.Predicate.StartsWith(status, "OP"),
            [], Groundwork.Query.Model.Projection.All, Groundwork.Query.Model.Paging.None)).Rows);

        connection.Dispose();
        using (var tamper = new SqliteConnection(store.ConnectionString))
        {
            tamper.Open();
            using var command = tamper.CreateCommand();
            command.CommandText = "UPDATE __groundwork_search_key_algorithms SET algorithm_id='stale-search-key-v0' WHERE table_name='folded_migration' AND column_name='__groundwork_search_status';";
            command.ExecuteNonQuery();
        }
        using var reopened = new SqliteProviderFactory().Create(store.ConnectionString);
        var admission = Assert.Throws<GroundworkRuntimeSchemaAdmissionException>(() => reopened.OpenSession(folded, StorageAccess.Global));
        Assert.Contains("GW-RUNTIME-001", admission.Message, StringComparison.Ordinal);
        Assert.Contains("search-key algorithm", admission.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Store_lock_is_held_for_connection_lifetime()
    {
        using var store = TemporaryStore.Create();
        using var first = new SqliteProviderFactory().Create(store.ConnectionString);
        var error = Assert.Throws<InvalidOperationException>(() => new SqliteProviderFactory().Create(store.ConnectionString));
        Assert.Contains("GW-SQLITE-LIFETIME-001", error.Message, StringComparison.Ordinal);
        Assert.Contains("one IStorageProviderConnection per database file", error.Message, StringComparison.Ordinal);
        first.Dispose();
        using var second = new SqliteProviderFactory().Create(store.ConnectionString);
    }

    [Fact]
    public void Batched_upserts_use_one_native_command_and_return_all_outcomes()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = Model(includePriority: false, includeUniqueIndex: false);
        connection.Schema.Apply(unit);
        _ = connection.OpenSession(unit, StorageAccess.Global);
        var observer = new ProviderCommandObserver();
        using var work = connection.BeginUnitOfWork(
            StorageAccess.Global,
            new BatchWriteOptions { MaxRowsPerFlush = 1_000, OutcomeMode = BatchOutcomeMode.Exact },
            observer,
            unit);

        for (var index = 0; index < 1_000; index++)
        {
            work.Stage(RowWrite.Upsert(unit, new StorageValues(new Dictionary<string, object?>
            {
                ["id"] = $"id-{index}",
                ["value"] = $"value-{index}",
                ["uniqueValue"] = $"unique-{index}"
            })));
        }

        var summary = work.CommitWithOutcomes();

        Assert.True(summary.IsSuccessful);
        Assert.Equal(1_000, summary.Submitted);
        Assert.Equal(1, observer.RoundTrips);
        Assert.Equal("value-999", connection.OpenSession(unit, StorageAccess.Global)
            .Read(new StorageKey(new Dictionary<string, object?> { ["id"] = "id-999" }))!
            .Values.Values["value"]);
    }

    [Fact]
    public void Batched_insert_failure_reports_the_key_and_rolls_back_the_batch()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = Model(includePriority: false);
        connection.Schema.Apply(unit);
        using var work = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, unit);
        var first = RowWrite.Insert(unit, new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "one", ["value"] = "one", ["uniqueValue"] = "duplicate"
        }));
        var second = RowWrite.Insert(unit, new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "two", ["value"] = "two", ["uniqueValue"] = "duplicate"
        }));
        work.Stage(first);
        work.Stage(second);

        var error = Assert.Throws<BatchWriteException>(() => work.CommitWithOutcomes());
        Assert.Same(second, Assert.Single(error.Outcomes).Write);
        Assert.Contains("id=two", error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(WriteOutcomeStatus.UniqueViolation), error.Message, StringComparison.Ordinal);
        Assert.Null(connection.OpenSession(unit, StorageAccess.Global)
            .Read(new StorageKey(new Dictionary<string, object?> { ["id"] = "one" })));
    }

    [Fact]
    public void Batched_upserts_accept_heterogeneous_column_shapes()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = Model(includePriority: true, includeUniqueIndex: false);
        connection.Schema.Apply(unit);
        using var work = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, unit);
        work.Stage(RowWrite.Upsert(unit, new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "full", ["value"] = "full", ["uniqueValue"] = "full", ["priority"] = 7
        })));
        work.Stage(RowWrite.Upsert(unit, new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "partial", ["value"] = "partial", ["uniqueValue"] = "partial"
        })));

        var summary = work.CommitWithOutcomes();

        Assert.True(summary.IsSuccessful);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        Assert.Equal(7, session.Read(new StorageKey(new Dictionary<string, object?>
        {
            ["id"] = "full"
        }))!.Values.Values["priority"]);
        Assert.Equal(0, session.Read(new StorageKey(new Dictionary<string, object?>
        {
            ["id"] = "partial"
        }))!.Values.Values["priority"]);
    }

    [Fact]
    public void Exact_batched_upserts_return_optimistic_versions()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = Model(includePriority: false, includeUniqueIndex: false) with
        {
            Id = new StorageUnitId("batched-versions"),
            Name = "batched_versions",
            Concurrency = ConcurrencyDeclaration.Optimistic()
        };
        connection.Schema.Apply(unit);

        using (var first = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, unit))
        {
            first.Stage(RowWrite.Upsert(unit, new StorageValues(new Dictionary<string, object?>
            {
                ["id"] = "same", ["value"] = "first", ["uniqueValue"] = "first"
            })));
            Assert.Equal(1, first.CommitWithOutcomes().Outcomes.Single().Outcome.Version);
        }

        using var second = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, unit);
        second.Stage(RowWrite.Upsert(unit, new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "same", ["value"] = "second", ["uniqueValue"] = "second"
        })));
        Assert.Equal(2, second.CommitWithOutcomes().Outcomes.Single().Outcome.Version);
    }

    [Fact]
    public void Batched_upserts_chunk_at_the_sqlite_variable_limit()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var columns = Enumerable.Range(0, 39)
            .Select(index => new ColumnDefinition { Name = $"value{index}", Type = PortableType.String })
            .Prepend(new ColumnDefinition { Name = "id", Type = PortableType.String, IsNullable = false })
            .ToArray();
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("batched-wide"),
            Name = "batched_wide",
            Columns = columns,
            Key = new KeyDefinition { Columns = ["id"] }
        };
        connection.Schema.Apply(unit);
        _ = connection.OpenSession(unit, StorageAccess.Global);
        var observer = new ProviderCommandObserver();
        using var work = connection.BeginUnitOfWork(
            StorageAccess.Global,
            new BatchWriteOptions { MaxRowsPerFlush = 1_000, OutcomeMode = BatchOutcomeMode.Exact },
            observer,
            unit);
        for (var row = 0; row < 1_000; row++)
        {
            var values = columns.ToDictionary(
                column => column.Name,
                column => (object?)$"{column.Name}-{row}",
                StringComparer.Ordinal);
            work.Stage(RowWrite.Upsert(unit, new StorageValues(values)));
        }

        var report = work.CommitWithOutcomes();

        Assert.Equal(1_000, report.Succeeded);
        Assert.Equal(2, observer.RoundTrips);
    }

    private static StorageUnit Model(bool includePriority, bool includeUniqueIndex = true) => new()
    {
        Id = new StorageUnitId("rebuild"), Name = "rebuild",
        Columns = includePriority
            ? [new() { Name = "id", Type = PortableType.String, IsNullable = false }, new() { Name = "value", Type = PortableType.String }, new() { Name = "uniqueValue", Type = PortableType.String }, new() { Name = "priority", Type = PortableType.Int32, IsNullable = false, Default = new PortableDefault(0) }]
            : [new() { Name = "id", Type = PortableType.String, IsNullable = false }, new() { Name = "value", Type = PortableType.String }, new() { Name = "uniqueValue", Type = PortableType.String }],
        Key = new KeyDefinition { Columns = ["id"] },
        Indexes = includeUniqueIndex
            ?
            [
                new IndexDefinition { Name = "by_value", Columns = [new IndexColumn("value")] },
                new IndexDefinition { Name = "unique_value", Columns = [new IndexColumn("uniqueValue")], IsUnique = true }
            ]
            : [new IndexDefinition { Name = "by_value", Columns = [new IndexColumn("value")] }]
    };

    private static StorageUnit SequenceUnit(string name) => new()
    {
        Id = new StorageUnitId(name),
        Name = name.Replace('-', '_'),
        Columns =
        [
            new() { Name = "sequence", Type = PortableType.Int64, IsNullable = false, Generation = ColumnGeneration.ProviderSequence },
            new() { Name = "payload", Type = PortableType.String }
        ],
        Key = new KeyDefinition { Columns = ["sequence"] }
    };

    private static StorageValues Values(string payload) => new(
        new Dictionary<string, object?> { ["payload"] = payload });

    private static StorageKey Key(long sequence) => new(
        new Dictionary<string, object?> { ["sequence"] = sequence });

    private sealed class TemporaryStore : IDisposable
    {
        private readonly string directory;
        private TemporaryStore(string directory) { this.directory = directory; ConnectionString = $"Data Source={Path.Combine(directory, "store.db")}"; }
        public string ConnectionString { get; }
        public static TemporaryStore Create() { var path = Path.Combine(Path.GetTempPath(), "groundwork-sqlite-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return new(path); }
        public void Dispose() { try { Directory.Delete(directory, recursive: true); } catch { } }
    }
}
