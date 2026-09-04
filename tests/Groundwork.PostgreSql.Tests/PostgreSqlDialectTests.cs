using System.Security.Cryptography;
using System.Text.Json;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.PostgreSql;
using Groundwork.Query.Model;
using Groundwork.Substrate.Relational;
using Groundwork.Testing;
using Groundwork.Store;
using Npgsql;
using Xunit;

namespace Groundwork.PostgreSql.Tests;

public sealed class PostgreSqlDialectTests
{
    private readonly PostgreSqlDialect dialect = new();

    [SkippableFact]
    public void Physical_foreign_keys_and_checks_apply_as_native_postgresql_constraints()
    {
        using var database = PostgreSqlFixture.OpenOrSkip();
        using var connection = new PostgreSqlProviderFactory().Create(database.ConnectionString);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var customer = StorageUnit.Declare("pg-customer-" + suffix, "pg_customer_" + suffix)
            .Guid("id", column => column.Required())
            .Key("id")
            .Build();
        var order = StorageUnit.Declare("pg-order-" + suffix, "pg_order_" + suffix)
            .Guid("id", column => column.Required())
            .Guid("customer_id", column => column.Required())
            .Int32("quantity", column => column.Required())
            .Key("id")
            .Index("by_customer", "customer_id")
            .PhysicalReference("fk_order_customer", customer, "customer_id")
            .Check("ck_order_quantity", "quantity", CheckConstraintOperator.GreaterThan, 0)
            .Build();

        Assert.True(connection.Schema.Apply(customer).Applied);
        Assert.True(connection.Schema.Apply(order).Applied);
        Assert.True(connection.Schema.Diff(order).IsEmpty);

        using var raw = new NpgsqlConnection(database.ConnectionString);
        raw.Open();
        using var command = raw.CreateCommand();
        command.CommandText = "SELECT count(*) FROM pg_catalog.pg_constraint c JOIN pg_catalog.pg_class t ON t.oid=c.conrelid WHERE t.relname=@table AND c.conname IN ('fk_order_customer','ck_order_quantity');";
        command.Parameters.AddWithValue("table", order.Name);
        Assert.Equal(2L, command.ExecuteScalar());
    }

    [SkippableFact]
    public void Owned_session_marker_matches_the_opening_path()
    {
        using var database = PostgreSqlFixture.OpenOrSkip();
        using var connection = new PostgreSqlProviderFactory().Create(database.ConnectionString);
        var name = "pg_session_ownership_" + Guid.NewGuid().ToString("N");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId(name),
            Name = name,
            Columns = [new ColumnDefinition { Name = "id", Type = PortableType.String, MaxLength = 64, IsNullable = false }],
            Key = new KeyDefinition { Columns = ["id"] }
        };
        Assert.True(connection.Schema.Apply(unit).Applied);

        Assert.False(connection.OpenSession(unit, StorageAccess.Global) is IOwnedStorageSession);
        using (var work = connection.BeginUnitOfWork(StorageAccess.Global, unit))
            Assert.False(work.OpenSession(unit) is IOwnedStorageSession);

        var owned = connection.OpenOwnedSession(unit, StorageAccess.Global);
        Assert.IsAssignableFrom<IOwnedStorageSession>(owned);
        owned.Dispose();
        Assert.Throws<ObjectDisposedException>(() => owned.Read(new StorageKey(
            new Dictionary<string, object?> { ["id"] = "after-release" })));
    }

    [SkippableFact]
    public void Owned_sessions_return_each_connection_to_a_bounded_pool()
    {
        using var database = PostgreSqlFixture.OpenOrSkip();
        var pool = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            MaxPoolSize = 1,
            Timeout = 2
        };
        using var connection = new PostgreSqlProviderFactory().Create(pool.ConnectionString);
        var name = "pg_session_pool_" + Guid.NewGuid().ToString("N");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId(name),
            Name = name,
            Columns = [new ColumnDefinition { Name = "id", Type = PortableType.String, MaxLength = 64, IsNullable = false }],
            Key = new KeyDefinition { Columns = ["id"] }
        };
        Assert.True(connection.Schema.Apply(unit).Applied);

        for (var index = 0; index < 8; index++)
        {
            using var owned = connection.OpenOwnedSession(unit, StorageAccess.Global);
            Assert.IsAssignableFrom<IOwnedStorageSession>(owned);
        }
    }

    [SkippableFact]
    public async Task Provider_disposal_cannot_miss_a_registering_legacy_session()
    {
        using var database = PostgreSqlFixture.OpenOrSkip();
        var pool = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            MaxPoolSize = 1,
            Timeout = 2
        };
        using var connection = new PostgreSqlProviderFactory().Create(pool.ConnectionString);
        var name = "pg_session_registration_" + Guid.NewGuid().ToString("N");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId(name),
            Name = name,
            Columns = [new ColumnDefinition { Name = "id", Type = PortableType.String, MaxLength = 64, IsNullable = false }],
            Key = new KeyDefinition { Columns = ["id"] }
        };
        Assert.True(connection.Schema.Apply(unit).Applied);
        using var observer = new RegistrationBarrierObserver();

        var opening = Task.Run(() => connection.OpenSession(unit, StorageAccess.Global, observer));
        Assert.True(observer.EligibilityChecked.Wait(TimeSpan.FromSeconds(5)), "Session registration did not reach the eligibility check.");
        var disposing = Task.Run(connection.Dispose);
        Assert.True(observer.DisposalAttempted.Wait(TimeSpan.FromSeconds(5)), "Provider disposal did not reach the registration boundary.");

        observer.Release.Set();
        var session = await opening;
        await disposing;

        Assert.Throws<ObjectDisposedException>(() => session.Read(new StorageKey(
            new Dictionary<string, object?> { ["id"] = "after-provider" })));
        using var returned = new NpgsqlConnection(pool.ConnectionString);
        returned.Open();
    }

    [Fact]
    public void Physicalization_refuses_an_invalid_raw_json_string_default_before_provider_work()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            PostgreSqlSchemaCoordinator.Physicalize(RawJsonStringDefaultUnit()));

        Assert.Contains("GW-PORT-013", exception.Message, StringComparison.Ordinal);
        Assert.Contains("payload", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Json", exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(String), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Aggregation_contains_uses_array_membership_not_string_substring_search()
    {
        var unit = AggregationUnit();
        var profile = new AggregationProfile
        {
            Name = "summary",
            GroupByColumns = ["group"],
            Aggregates = [new Aggregate.SetUnion("labels", "label", 2)],
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

        var sql = dialect.RenderAggregation(unit, profile, new AggregationQuery("summary")
        {
            PostPredicate = new AggregationPredicate.Comparison(
                "labels", AggregationPredicateOperator.Contains, ["plain"])
        }).CommandText;

        Assert.Contains("= ANY(\"labels\")", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("INSTR(", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Aggregation_string_order_wraps_aliases_in_a_result_relation()
    {
        var unit = AggregationUnit();
        var profile = new AggregationProfile
        {
            Name = "summary",
            GroupByColumns = ["group"],
            Aggregates = [new Aggregate.Count("count"), new Aggregate.Min("minimum", "label")]
        };

        var sql = dialect.RenderAggregation(unit, profile, new AggregationQuery("summary")
        {
            OrderByTerms = [
                new AggregationOrderTerm("minimum", SortDirection.Ascending),
                new AggregationOrderTerm("group", SortDirection.Ascending)]
        }).CommandText;

        Assert.Contains("SELECT * FROM \"__groundwork_aggregation_result\"", sql, StringComparison.Ordinal);
        Assert.Contains("string_to_array(\"minimum\", NULL)", sql, StringComparison.Ordinal);
        Assert.Contains("CASE WHEN \"minimum\" IS NULL", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Renderer_uses_native_utc_and_iana_calendar_bucket_expressions()
    {
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("aggregation-time-bucket-pg"),
            Name = "aggregation_time_bucket_pg",
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, IsNullable = false },
                new() { Name = "createdAt", Type = PortableType.DateTimeOffset }
            ],
            Key = new KeyDefinition { Columns = ["id"] }
        };
        var profile = new AggregationProfile
        {
            Name = "daily",
            GroupByExpressions = [AggregationGroup.TimeBucket.LocalCalendarDay("day", "createdAt")],
            Aggregates = [new Aggregate.Count("count")]
        };
        var from = new DateTimeOffset(2026, 3, 29, 0, 0, 0, TimeSpan.Zero);
        var sql = dialect.RenderAggregation(unit, profile, new AggregationQuery("daily")
        {
            TimeRange = new AggregationTimeRange(from, from.AddDays(2)),
            TimeZoneId = "Europe/Amsterdam"
        }).CommandText;

        Assert.Contains("date_trunc('day'", sql, StringComparison.Ordinal);
        Assert.Contains("FLOOR", sql, StringComparison.Ordinal);
        Assert.Contains("Europe/Amsterdam", sql, StringComparison.Ordinal);
        Assert.Contains("createdAt", sql, StringComparison.Ordinal);
        Assert.Contains("GROUP BY", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Scoped_native_sql_artifacts_inject_scope_before_grouping_and_budget_probe()
    {
        var unit = AggregationUnit();
        var profile = new AggregationProfile
        {
            Name = "summary",
            GroupByColumns = ["group"],
            Aggregates = [new Aggregate.Count("count")]
        };
        var scope = new ColumnRef(new TableId(unit.Name), PostgreSqlSchemaCoordinator.ScopeColumn, QueryType.String, isNullable: false);
        var providerPredicate = new Predicate.Equal(scope, QueryConstant.Of(scope, "tenant-a"));
        var query = new AggregationQuery("summary")
        {
            OrderByTerms = [
                new AggregationOrderTerm("count", SortDirection.Descending),
                new AggregationOrderTerm("group", SortDirection.Ascending)],
            Take = 5
        };

        var command = RelationalAggregationRenderer.RenderWithProviderPredicate(dialect, unit, profile, query, providerPredicate).CommandText;
        var probe = RelationalAggregationRenderer.RenderBudgetProbeWithProviderPredicate(dialect, unit, profile, query, providerPredicate).CommandText;

        Assert.StartsWith("WITH ", command, StringComparison.Ordinal);
        Assert.Contains(PostgreSqlSchemaCoordinator.ScopeColumn, command, StringComparison.Ordinal);
        Assert.Contains("COUNT(*) AS \"count\"", command, StringComparison.Ordinal);
        Assert.Contains("GROUP BY", command, StringComparison.Ordinal);
        Assert.Contains("LIMIT 5", command, StringComparison.Ordinal);
        Assert.Contains(PostgreSqlSchemaCoordinator.ScopeColumn, probe, StringComparison.Ordinal);
        Assert.Contains("GROUP BY", probe, StringComparison.Ordinal);
    }

    private static StorageUnit AggregationUnit() => new()
    {
        Id = new StorageUnitId("aggregation-predicate-render"),
        Name = "aggregation_predicate_render",
        Columns =
        [
            new() { Name = "id", Type = PortableType.String, IsNullable = false },
            new() { Name = "group", Type = PortableType.String },
            new() { Name = "label", Type = PortableType.String }
        ],
        Key = new KeyDefinition { Columns = ["id"] }
    };

    private static StorageUnit RawJsonStringDefaultUnit() => new()
    {
        Id = new StorageUnitId("pg-invalid-raw-json-default"),
        Name = "pg_invalid_raw_json_default",
        Columns =
        [
            new() { Name = "id", Type = PortableType.Guid, IsNullable = false },
            new() { Name = "payload", Type = PortableType.Json, Default = new PortableDefault("pending") }
        ],
        Key = new KeyDefinition { Columns = ["id"] }
    };

    [Fact]
    public void Aggregation_FirstBy_supports_boolean_values_with_null_preservation()
    {
        var unit = AggregationUnit() with
        {
            Columns =
            [
                ..AggregationUnit().Columns,
                new ColumnDefinition { Name = "flag", Type = PortableType.Boolean },
                new ColumnDefinition { Name = "order", Type = PortableType.Int64, IsNullable = false }
            ]
        };
        var profile = new AggregationProfile
        {
            Name = "summary",
            GroupByColumns = ["group"],
            Aggregates = [new Aggregate.FirstBy("firstFlag", "flag", "order")]
        };

        var sql = dialect.RenderAggregation(unit, profile).CommandText;

        Assert.Contains("SELECT first_input.\"flag\"", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT 1", sql, StringComparison.Ordinal);
        Assert.Contains("firstFlag", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Aggregation_predicates_use_postgres_typed_literals_and_null_membership()
    {
        var unit = AggregationUnit() with
        {
            Columns =
            [
                ..AggregationUnit().Columns,
                new ColumnDefinition { Name = "flag", Type = PortableType.Boolean },
                new ColumnDefinition { Name = "moment", Type = PortableType.DateTimeOffset },
                new ColumnDefinition { Name = "identifier", Type = PortableType.Guid },
                new ColumnDefinition { Name = "payload", Type = PortableType.Binary },
                new ColumnDefinition { Name = "order", Type = PortableType.Int64, IsNullable = false }
            ]
        };
        var profile = new AggregationProfile
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
            AllowedPredicates = new[] { "firstFlag", "firstMoment", "firstIdentifier", "firstPayload" }.Select(alias => new AggregationPredicateAllowance
            {
                Alias = alias,
                SupportedPredicates = new HashSet<AggregationPredicateOperator>
                {
                    AggregationPredicateOperator.Equal,
                    AggregationPredicateOperator.In
                }
            }).ToArray()
        };
        var instant = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var identifier = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");

        var nullSql = dialect.RenderAggregation(unit, profile, new AggregationQuery("summary")
        {
            PostPredicate = new AggregationPredicate.Comparison("firstFlag", AggregationPredicateOperator.Equal, [(object?)null])
        }).CommandText;
        var inSql = dialect.RenderAggregation(unit, profile, new AggregationQuery("summary")
        {
            PostPredicate = new AggregationPredicate.Comparison("firstFlag", AggregationPredicateOperator.In, [(object?)null, true])
        }).CommandText;
        var momentSql = dialect.RenderAggregation(unit, profile, new AggregationQuery("summary")
        {
            PostPredicate = new AggregationPredicate.Comparison("firstMoment", AggregationPredicateOperator.Equal, [instant])
        }).CommandText;
        var guidSql = dialect.RenderAggregation(unit, profile, new AggregationQuery("summary")
        {
            PostPredicate = new AggregationPredicate.Comparison("firstIdentifier", AggregationPredicateOperator.Equal, [identifier])
        }).CommandText;
        var binarySql = dialect.RenderAggregation(unit, profile, new AggregationQuery("summary")
        {
            PostPredicate = new AggregationPredicate.Comparison("firstPayload", AggregationPredicateOperator.Equal, [new byte[] { 1, 2 }])
        }).CommandText;

        Assert.Contains("\"firstFlag\" IS NULL", nullSql, StringComparison.Ordinal);
        Assert.Contains("(\"firstFlag\" IN (TRUE) OR \"firstFlag\" IS NULL)", inSql, StringComparison.Ordinal);
        Assert.Contains(instant.UtcTicks.ToString(), momentSql, StringComparison.Ordinal);
        Assert.Contains("CAST('00112233-4455-6677-8899-aabbccddeeff' AS uuid)", guidSql, StringComparison.Ordinal);
        Assert.Contains("decode('0102', 'hex')", binarySql, StringComparison.Ordinal);
    }

    [Fact]
    public void Index_ddl_spells_out_normalized_null_ordering()
    {
        var index = new IndexDefinition
        {
            Name = "by_name",
            Columns =
            [
                new IndexColumn("name", SortDirection.Ascending),
                new IndexColumn("createdAt", SortDirection.Descending)
            ],
            IncludedColumns = ["payload"]
        };

        var sql = dialect.CreateIndexSql("customers", index, null);

        Assert.Contains("\"name\" ASC NULLS FIRST", sql, StringComparison.Ordinal);
        Assert.Contains("\"createdAt\" DESC NULLS LAST", sql, StringComparison.Ordinal);
        Assert.Contains("INCLUDE (\"payload\")", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("INDEXED BY", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Composed_index_names_are_hashed_to_the_63_byte_postgreSQL_budget()
    {
        var table = new string('t', PortabilityValidator.MaximumPortableIdentifierLength);
        var index = new IndexDefinition
        {
            Name = "i",
            Columns = [new IndexColumn("value")]
        };

        var logical = $"__groundwork_ix_{table.Length}_{table}_{index.Name.Length}_{index.Name}";
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(logical)))[..10].ToLowerInvariant();
        var expected = logical[..(PortabilityValidator.MaximumPortableIdentifierLength - hash.Length - 1)] + "_" + hash;
        var sql = dialect.CreateIndexSql(table, index, null);

        Assert.Contains($"\"{expected}\"", sql, StringComparison.Ordinal);
        Assert.Equal(PortabilityValidator.MaximumPortableIdentifierLength, expected.Length);
    }

    /// <summary>
    /// A JSON column has to reach PostgreSQL as <c>jsonb</c> however it is written. The parameters of a
    /// batched statement cannot be named after their columns — one row per staged write, so the names are
    /// prefixed to stay unique — and typing them from the placeholder rather than the column silently sent
    /// every batched JSON value as text, which PostgreSQL rejects with 42804.
    /// <para>
    /// Single and batched writes are asserted together and in that order: the single write is the control,
    /// and the defect this pins was invisible precisely because that control passed.
    /// </para>
    /// </summary>
    [SkippableTheory]
    [InlineData(RowWriteMode.Insert)]
    [InlineData(RowWriteMode.Upsert)]
    public void Json_columns_reach_the_database_as_jsonb_however_they_are_written(RowWriteMode mode)
    {
        using var database = PostgreSqlFixture.OpenOrSkip();
        using var connection = new PostgreSqlProviderFactory().Create(database.ConnectionString);
        var unit = JsonDocumentUnit();
        Assert.True(connection.Schema.Apply(unit).Applied);
        var access = StorageAccess.Global;

        // Control: one row at a time already worked, and is what made the batch defect hard to see.
        var single = connection.OpenSession(unit, access)
            .Upsert(JsonRow("single", "{\"kind\":\"single\"}"), WriteOptions.Unconditional);
        Assert.True(single.Succeeded, $"Single write did not succeed: {single.Status}.");

        // The path that failed: both batch shapes go through the same parameter binding.
        using (var unitOfWork = connection.BeginUnitOfWork(access, BatchWriteOptions.Exact, [unit]))
        {
            unitOfWork.Stage(mode == RowWriteMode.Insert
                ? RowWrite.Insert(unit, JsonRow("batch-a", "{\"kind\":\"batch\"}"), WriteOptions.Unconditional)
                : RowWrite.Upsert(unit, JsonRow("batch-a", "{\"kind\":\"batch\"}"), WriteOptions.Unconditional));
            unitOfWork.Stage(mode == RowWriteMode.Insert
                ? RowWrite.Insert(unit, JsonRow("batch-b", "{\"kind\":\"batch\"}"), WriteOptions.Unconditional)
                : RowWrite.Upsert(unit, JsonRow("batch-b", "{\"kind\":\"batch\"}"), WriteOptions.Unconditional));
            unitOfWork.CommitWithOutcomes();
        }

        // Committed, not merely accepted: read the rows back through a fresh session.
        var session = connection.OpenSession(unit, access);
        foreach (var id in new[] { "single", "batch-a", "batch-b" })
        {
            Assert.NotNull(session.Read(new StorageKey(new Dictionary<string, object?>
            {
                ["id"] = id
            })));
        }
    }

    private static StorageUnit JsonDocumentUnit() => new()
    {
        Id = new StorageUnitId("logical.json.document"),
        Name = "json_documents",
        Columns =
        [
            new ColumnDefinition { Name = "id", Type = PortableType.String, IsNullable = false, MaxLength = 64 },
            new ColumnDefinition { Name = "content", Type = PortableType.Json, IsNullable = false }
        ],
        Key = new KeyDefinition { Columns = ["id"] }
    };

    private static StorageValues JsonRow(string id, string content) =>
        new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = id,
            ["content"] = content
        });

    [SkippableTheory]
    [InlineData(RowWriteMode.Insert)]
    [InlineData(RowWriteMode.Upsert)]
    public void Mixed_shape_public_batch_fallback_projects_ordinal_values_only_once(RowWriteMode mode)
    {
        using var database = PostgreSqlFixture.OpenOrSkip();
        using var connection = new PostgreSqlProviderFactory().Create(database.ConnectionString);
        var unit = OrdinalIdentityBatchUnit();
        Assert.True(connection.Schema.Apply(unit).Applied);
        using var session = connection.OpenOwnedSession(unit, StorageAccess.Global);
        var batched = Assert.IsAssignableFrom<IBatchedStorageSession>(session);
        var values = new[]
        {
            // Keep the logical dictionary order different from the declared physical order. The
            // PostgreSQL batch shape check must compare provider-ordered physical columns.
            new StorageValues(new Dictionary<string, object?>
            {
                ["name"] = "Ada", ["id"] = 1, ["note"] = "first"
            }),
            new StorageValues(new Dictionary<string, object?>
            {
                ["id"] = 2, ["name"] = "Grace"
            })
        };
        var writes = values.Select(value => mode == RowWriteMode.Insert
            ? RowWrite.Insert(unit, value)
            : RowWrite.Upsert(unit, value)).ToArray();

        var outcomes = batched.ApplyBatch(writes);

        Assert.Equal(2, outcomes.Count);
        Assert.All(outcomes, outcome => Assert.True(outcome.Outcome.Succeeded));
        using var raw = new NpgsqlConnection(database.ConnectionString);
        raw.Open();
        using var command = raw.CreateCommand();
        command.CommandText = "SELECT \"id\", \"name\", \"__groundwork_ordinal_name\" FROM \"ordinal_identity_batch\" ORDER BY \"id\";";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal("Ada", reader.GetString(1));
        Assert.Equal(PortableStringComparison.CreateOrdinal("Ada"), reader.GetString(2));
        Assert.True(reader.Read());
        Assert.Equal(2, reader.GetInt32(0));
        Assert.Equal("Grace", reader.GetString(1));
        Assert.Equal(PortableStringComparison.CreateOrdinal("Grace"), reader.GetString(2));
        Assert.False(reader.Read());
    }

    private static StorageUnit OrdinalIdentityBatchUnit() =>
        StorageUnit.Declare("ordinal-identity-batch", "ordinal_identity_batch")
            .Int32("id", column => column.Required())
            .String("name", 32, column => column.Required().OrdinalIdentity("__groundwork_ordinal_name"))
            .String("note", 32)
            .Key("id")
            .Index("by_name", index => index.UseOrdinalIdentities().Column("name"))
            .Build();

    [SkippableFact]
    public void Provider_applies_a_63_byte_storage_unit_name_without_rewriting()
    {
        using var database = PostgreSqlFixture.OpenOrSkip();
        using var connection = new PostgreSqlProviderFactory().Create(database.ConnectionString);
        var name = new string('a', PortabilityValidator.MaximumPortableIdentifierLength);
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("logical.boundary.id"),
            Name = name,
            Columns = [new ColumnDefinition { Name = "id", Type = PortableType.Int32, IsNullable = false }],
            Key = new KeyDefinition { Columns = ["id"] }
        };

        Assert.True(connection.Schema.Apply(unit).Applied);
        Assert.True(connection.Schema.Diff(unit).IsEmpty);
    }

    [Fact]
    public void Partial_unique_conflict_target_contains_its_inference_predicate()
    {
        var shape = new RelationalWriteShape(
            "customers",
            [new RelationalWriteColumn("email")],
            ["email"],
            []);

        var sql = dialect.ConditionalUpsertSql(shape, "\"email\" IS NOT NULL");

        Assert.Contains("ON CONFLICT (\"email\") WHERE \"email\" IS NOT NULL", sql,
            StringComparison.Ordinal);
        Assert.DoesNotContain("INDEX", sql, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    [Trait("Category", "Concurrency")]
    public async Task Live_concurrent_schema_admission_serializes_infrastructure_creation()
    {
        var baseConnection = Environment.GetEnvironmentVariable("GROUNDWORK_POSTGRES_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(baseConnection),
            "Set GROUNDWORK_POSTGRES_CONNECTION to run PostgreSQL integration tests.");

        using var database = PostgreSqlFixture.OpenOrSkip();
        await ApplyDifferentTargetsConcurrently(database.ConnectionString, 24, "pg_infrastructure_race");
    }

    [SkippableFact]
    [Trait("Category", "Concurrency")]
    public async Task Live_concurrent_schema_applies_for_different_targets_after_bootstrap()
    {
        var baseConnection = Environment.GetEnvironmentVariable("GROUNDWORK_POSTGRES_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(baseConnection),
            "Set GROUNDWORK_POSTGRES_CONNECTION to run PostgreSQL integration tests.");

        using var database = PostgreSqlFixture.OpenOrSkip();
        using (var bootstrap = new NpgsqlConnection(database.ConnectionString))
        {
            bootstrap.Open();
            new PostgreSqlDialect().EnsureInfrastructure(bootstrap);
        }

        await ApplyDifferentTargetsConcurrently(database.ConnectionString, 2, "pg_catalog_race");
    }

    private static async Task ApplyDifferentTargetsConcurrently(
        string connectionString,
        int workerCount,
        string prefix)
    {
        using var ready = new Barrier(workerCount);
        var tasks = Enumerable.Range(0, workerCount).Select(index => Task.Run(() =>
        {
            ready.SignalAndWait(TimeSpan.FromSeconds(30));
            var name = $"{prefix}_{index}_{Guid.NewGuid():N}";
            var unit = new StorageUnit
            {
                Id = new StorageUnitId(name),
                Name = name,
                Columns = [new ColumnDefinition { Name = "id", Type = PortableType.String, IsNullable = false }],
                Key = new KeyDefinition { Columns = ["id"] }
            };

            using var connection = new PostgreSqlProviderFactory().Create(connectionString);
            Assert.True(connection.Schema.Apply(unit).Applied);
        })).ToArray();

        await Task.WhenAll(tasks);
    }

    [SkippableFact]
    public void Live_catalog_records_the_explicit_null_ordering_bits()
    {
        using var database = PostgreSqlFixture.OpenOrSkip();
        var unit = NullOrderingUnit("pg-null-ordering");
        using var connection = new PostgreSqlProviderFactory().Create(database.ConnectionString);
        connection.Schema.Apply(unit);

        using var raw = new NpgsqlConnection(database.ConnectionString);
        raw.Open();
        using var command = raw.CreateCommand();
        command.CommandText = """
            SELECT i.indoption[0], i.indoption[1]
            FROM pg_catalog.pg_class table_class
            JOIN pg_catalog.pg_namespace namespace ON namespace.oid=table_class.relnamespace
            JOIN pg_catalog.pg_index i ON i.indrelid=table_class.oid
            JOIN pg_catalog.pg_class index_class ON index_class.oid=i.indexrelid
            WHERE namespace.nspname=current_schema()
              AND table_class.relname=@table
              AND index_class.relname=@index;
            """;
        command.Parameters.AddWithValue("table", unit.Name);
        command.Parameters.AddWithValue("index", PhysicalIndexName(unit.Name, "by_name"));
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal((short)2, reader.GetInt16(0)); // ASC NULLS FIRST
        Assert.Equal((short)1, reader.GetInt16(1)); // DESC NULLS LAST
    }

    [SkippableFact]
    public void Live_schema_validation_tolerates_default_null_ordering_as_index_only_drift()
    {
        using var database = PostgreSqlFixture.OpenOrSkip();
        var unit = NullOrderingUnit("pg-null-drift");
        using var connection = new PostgreSqlProviderFactory().Create(database.ConnectionString);
        connection.Schema.Apply(unit);

        using (var raw = new NpgsqlConnection(database.ConnectionString))
        {
            raw.Open();
            using var command = raw.CreateCommand();
            var physicalIndexName = PhysicalIndexName(unit.Name, "by_name");
            command.CommandText = $"DROP INDEX \"{physicalIndexName}\"; CREATE INDEX \"{physicalIndexName}\" ON \"{unit.Name}\" (\"value\");";
            command.ExecuteNonQuery();
        }

        // Index-only drift is observable and enforced at query admission, but a no-change schema
        // apply intentionally remains non-fatal so operators can inspect and repair it.
        var result = connection.Schema.Apply(unit);
        Assert.True(result.Applied);
        Assert.True(result.IsNoOp);
        Assert.True(connection.Schema.Diff(unit).IsEmpty);
    }

    [SkippableFact]
    public void Live_partial_unique_conflict_target_supports_conditional_upsert()
    {
        using var database = PostgreSqlFixture.OpenOrSkip();

        // The provider-facing declaration necessarily has a primary key, which could hide a
        // broken partial-index inference clause. Prove the generated statement against a raw
        // table that has only the partial unique index as its conflict arbiter.
        var rawTable = "pg_partial_raw_" + Guid.NewGuid().ToString("N");
        var rawIndex = rawTable + "__email";
        using (var raw = new NpgsqlConnection(database.ConnectionString))
        {
            raw.Open();
            try
            {
                using (var create = raw.CreateCommand())
                {
                    create.CommandText = $"CREATE TABLE \"{rawTable}\" (\"email\" text, \"value\" text);" +
                        $" CREATE UNIQUE INDEX \"{rawIndex}\" ON \"{rawTable}\" (\"email\") WHERE \"email\" IS NOT NULL;";
                    create.ExecuteNonQuery();
                }

                var shape = new RelationalWriteShape(
                    rawTable,
                    [new RelationalWriteColumn("email"), new RelationalWriteColumn("value")],
                    ["email"],
                    ["value"]);
                using var upsert = raw.CreateCommand();
                upsert.CommandText = dialect.ConditionalUpsertSql(shape, "\"email\" IS NOT NULL");
                upsert.Parameters.AddWithValue("email", "raw@example.test");
                upsert.Parameters.AddWithValue("value", "first");
                Assert.Equal(1, upsert.ExecuteNonQuery());
                upsert.Parameters["value"].Value = "second";
                Assert.Equal(1, upsert.ExecuteNonQuery());

                using var read = raw.CreateCommand();
                read.CommandText = $"SELECT \"value\" FROM \"{rawTable}\" WHERE \"email\"='raw@example.test';";
                Assert.Equal("second", read.ExecuteScalar());
            }
            finally
            {
                using var drop = raw.CreateCommand();
                drop.CommandText = $"DROP TABLE IF EXISTS \"{rawTable}\";";
                drop.ExecuteNonQuery();
            }
        }

        var unit = new StorageUnit
        {
            Id = new StorageUnitId("pg-partial-upsert"),
            Name = "pg_partial_upsert",
            Columns =
            [
                new ColumnDefinition { Name = "email", Type = PortableType.String, IsNullable = false },
                new ColumnDefinition { Name = "value", Type = PortableType.String }
            ],
            Key = new KeyDefinition { Columns = ["email"] },
            Indexes =
            [
                new IndexDefinition
                {
                    Name = "unique_email_present",
                    Columns = [new IndexColumn("email")],
                    IsUnique = true,
                    MissingValues = MissingValueBehavior.Excluded
                }
            ]
        };
        using var connection = new PostgreSqlProviderFactory().Create(database.ConnectionString);
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        var concurrency = Assert.IsAssignableFrom<IConcurrencyStorageSession>(session);

        Assert.Equal(WriteOutcomeStatus.Inserted,
            concurrency.ConditionalUpsert(new StorageValues(new Dictionary<string, object?>
            {
                ["email"] = "person@example.test", ["value"] = "first"
            })).Status);
        var updated = concurrency.ConditionalUpsert(new StorageValues(new Dictionary<string, object?>
        {
            ["email"] = "person@example.test", ["value"] = "second"
        }));
        Assert.Equal(WriteOutcomeStatus.Updated, updated.Status);
        Assert.Equal("second", session.Read(new StorageKey(new Dictionary<string, object?>
        {
            ["email"] = "person@example.test"
        }))!.Values.Values["value"]);
    }

    [SkippableFact]
    public void Live_round_trip_covers_the_portable_postgresql_type_mapping()
    {
        using var database = PostgreSqlFixture.OpenOrSkip();
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("pg-types"),
            Name = "pg_types",
            Columns =
            [
                new ColumnDefinition { Name = "id", Type = PortableType.String, IsNullable = false },
                new ColumnDefinition { Name = "int32", Type = PortableType.Int32 },
                new ColumnDefinition { Name = "int64", Type = PortableType.Int64 },
                new ColumnDefinition { Name = "decimal", Type = PortableType.Decimal, Precision = 38, Scale = 4 },
                new ColumnDefinition { Name = "boolean", Type = PortableType.Boolean },
                new ColumnDefinition { Name = "timestamp", Type = PortableType.DateTimeOffset },
                new ColumnDefinition { Name = "guid", Type = PortableType.Guid },
                new ColumnDefinition { Name = "binary", Type = PortableType.Binary },
                new ColumnDefinition { Name = "json", Type = PortableType.Json }
            ],
            Key = new KeyDefinition { Columns = ["id"] }
        };
        using var connection = new PostgreSqlProviderFactory().Create(database.ConnectionString);
        connection.Schema.Apply(unit);
        var timestamp = new DateTimeOffset(2026, 8, 15, 12, 34, 56, TimeSpan.Zero).AddTicks(7890);
        var identifier = Guid.NewGuid();
        using var document = JsonDocument.Parse("{\"active\":true,\"count\":3}");
        var values = new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "portable",
            ["int32"] = 32,
            ["int64"] = 64L,
            ["decimal"] = 1234.5678m,
            ["boolean"] = true,
            ["timestamp"] = timestamp,
            ["guid"] = identifier,
            ["binary"] = new byte[] { 1, 2, 3 },
            ["json"] = document.RootElement.Clone()
        });
        var session = connection.OpenSession(unit, StorageAccess.Global);
        Assert.Equal(WriteOutcomeStatus.Inserted, session.Insert(values).Status);
        var read = session.Read(new StorageKey(new Dictionary<string, object?> { ["id"] = "portable" }));
        Assert.NotNull(read);
        Assert.Equal(32, read!.Values.Values["int32"]);
        Assert.Equal(64L, read.Values.Values["int64"]);
        Assert.Equal(1234.5678m, read.Values.Values["decimal"]);
        Assert.Equal(true, read.Values.Values["boolean"]);
        Assert.Equal(timestamp, read.Values.Values["timestamp"]);
        Assert.Equal(identifier, read.Values.Values["guid"]);
        Assert.Equal(new byte[] { 1, 2, 3 }, (byte[])read.Values.Values["binary"]!);
        var json = (JsonElement)read.Values.Values["json"]!;
        Assert.True(json.GetProperty("active").GetBoolean());
        Assert.Equal(3, json.GetProperty("count").GetInt32());
    }

    [SkippableFact]
    public void Live_additive_schema_evolution_backfills_then_finalizes_required_columns()
    {
        using var database = PostgreSqlFixture.OpenOrSkip();
        var initial = new StorageUnit
        {
            Id = new StorageUnitId("pg-evolution"),
            Name = "pg_evolution",
            Columns = [new ColumnDefinition { Name = "id", Type = PortableType.String, IsNullable = false }],
            Key = new KeyDefinition { Columns = ["id"] }
        };
        var evolved = initial with
        {
            Columns =
            [
                .. initial.Columns,
                new ColumnDefinition
                {
                    Name = "priority",
                    Type = PortableType.Int32,
                    IsNullable = false,
                    Default = new PortableDefault(0)
                }
            ]
        };
        using var connection = new PostgreSqlProviderFactory().Create(database.ConnectionString);
        connection.Schema.Apply(initial);
        var session = connection.OpenSession(initial, StorageAccess.Global);
        Assert.Equal(WriteOutcomeStatus.Inserted, session.Insert(new StorageValues(
            new Dictionary<string, object?> { ["id"] = "existing" })).Status);

        var result = connection.Schema.Apply(evolved);
        Assert.True(result.Applied);
        var read = connection.OpenSession(evolved, StorageAccess.Global).Read(
            new StorageKey(new Dictionary<string, object?> { ["id"] = "existing" }));
        Assert.NotNull(read);
        Assert.Equal(0, read!.Values.Values["priority"]);
    }

    [SkippableFact]
    public async Task Batch_fallback_completes_without_reentering_the_connection_gate()
    {
        using var database = PostgreSqlFixture.OpenOrSkip();
        using var connection = new PostgreSqlProviderFactory().Create(database.ConnectionString);
        var name = "pg_nested_write_" + Guid.NewGuid().ToString("N");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId(name),
            Name = name,
            Columns =
            [
                new ColumnDefinition { Name = "id", Type = PortableType.String, MaxLength = 64, IsNullable = false },
                new ColumnDefinition { Name = "value", Type = PortableType.String, MaxLength = 64, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Concurrency = ConcurrencyDeclaration.Optimistic()
        };
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        var batched = Assert.IsAssignableFrom<IBatchedStorageSession>(session);

        // A non-unconditional precondition takes the batch fallback. The fallback must reuse the
        // batch transaction without waiting for the non-reentrant connection gate it already holds.
        var write = RowWrite.Upsert(
            unit,
            new StorageValues(new Dictionary<string, object?> { ["id"] = "a", ["value"] = "nested" }),
            WriteOptions.CreateOnly);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var outcomes = await batched.ApplyBatchAsync([write], exactOutcomes: true, timeout.Token);
        Assert.Equal(WriteOutcomeStatus.Upserted, outcomes.Single().Outcome.Status);
    }

    [SkippableFact]
    public async Task Shared_session_serializes_a_second_caller_during_batch_fallback()
    {
        using var database = PostgreSqlFixture.OpenOrSkip();
        using var connection = new PostgreSqlProviderFactory().Create(database.ConnectionString);
        var name = "pg_batch_gate_" + Guid.NewGuid().ToString("N");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId(name),
            Name = name,
            Columns =
            [
                new ColumnDefinition { Name = "id", Type = PortableType.String, MaxLength = 64, IsNullable = false },
                new ColumnDefinition { Name = "value", Type = PortableType.String, MaxLength = 64, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Concurrency = ConcurrencyDeclaration.Optimistic()
        };
        Assert.True(connection.Schema.Apply(unit).Applied);

        var observer = new BlockingFallbackObserver();
        var session = connection.OpenSession(unit, StorageAccess.Global, observer);
        var batched = Assert.IsAssignableFrom<IBatchedStorageSession>(session);
        // Run the producer away from xUnit's synchronization context. It deliberately blocks in the
        // observer after reaching the provider command; invoking it inline can park its continuation
        // behind this test's synchronous signal wait before the signal has been raised.
        var first = Task.Run(async () => await batched.ApplyBatchAsync(
            [RowWrite.Upsert(
                unit,
                new StorageValues(new Dictionary<string, object?> { ["id"] = "a", ["value"] = "first" }),
                WriteOptions.CreateOnly)],
            exactOutcomes: true));

        Assert.True(observer.FallbackEntered.Wait(TimeSpan.FromSeconds(5)),
            "The fallback did not reach its provider command in time.");
        observer.CheckForOverlap.Set();
        using var secondStarted = new ManualResetEventSlim();
        var second = Task.Run(() =>
        {
            secondStarted.Set();
            return session.Read(new StorageKey(
                new Dictionary<string, object?> { ["id"] = "missing" }));
        });
        Assert.True(secondStarted.Wait(TimeSpan.FromSeconds(5)), "The second caller did not start in time.");
        await Task.Delay(TimeSpan.FromMilliseconds(150));
        Assert.False(observer.Overlapped,
            "A concurrent caller reached the shared connection while the batch fallback held its gate.");

        observer.Release.Set();
        var outcomes = await first;
        Assert.Equal(WriteOutcomeStatus.Upserted, outcomes.Single().Outcome.Status);
        Assert.Null(await second);
    }

    [SkippableFact]
    public async Task Queued_async_read_honors_cancellation_while_shared_session_gate_is_held()
    {
        using var database = PostgreSqlFixture.OpenOrSkip();
        using var connection = new PostgreSqlProviderFactory().Create(database.ConnectionString);
        var name = "pg_read_cancel_" + Guid.NewGuid().ToString("N");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId(name),
            Name = name,
            Columns = [new ColumnDefinition { Name = "id", Type = PortableType.String, MaxLength = 64, IsNullable = false }],
            Key = new KeyDefinition { Columns = ["id"] }
        };
        Assert.True(connection.Schema.Apply(unit).Applied);

        using var observer = new BlockingReadObserver();
        var session = connection.OpenSession(unit, StorageAccess.Global, observer);
        var first = Task.Run(async () => await session.ReadAsync(new StorageKey(
            new Dictionary<string, object?> { ["id"] = "first" })));
        Assert.True(observer.ReadEntered.Wait(TimeSpan.FromSeconds(5)),
            "The first read did not reach the provider command in time.");

        using var cancellation = new CancellationTokenSource();
        var queued = session.ReadAsync(
            new StorageKey(new Dictionary<string, object?> { ["id"] = "queued" }), cancellation.Token).AsTask();
        cancellation.Cancel();
        var completed = await Task.WhenAny(queued, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(queued, completed);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await queued);

        observer.Release.Set();
        Assert.Null(await first);
    }

    private sealed class BlockingFallbackObserver : IProviderCommandObserver
    {
        internal ManualResetEventSlim FallbackEntered { get; } = new();
        internal ManualResetEventSlim CheckForOverlap { get; } = new();
        internal ManualResetEventSlim Release { get; } = new();
        private int overlapped;
        internal bool Overlapped => Volatile.Read(ref overlapped) != 0;

        public void Observe(ProviderCommandEvent command)
        {
            // CreateOnly selects the row-wise batch fallback, but it remains an Upsert operation;
            // the provider therefore reports `postgresql.upsert`, not `conditional-upsert`.
            // Block the first write command rather than coupling this serialization proof to that
            // internal operation label.
            if (command.Kind == ProviderCommandKind.Write)
            {
                FallbackEntered.Set();
                Release.Wait(TimeSpan.FromSeconds(5));
                return;
            }

            // The fallback may issue its own write probe before the blocked command. Only a
            // non-probe read can be the independent caller this test is measuring.
            if (CheckForOverlap.IsSet && !Release.IsSet &&
                command.Kind == ProviderCommandKind.Read && !command.IsProbe)
                Interlocked.Exchange(ref overlapped, 1);
        }
    }

    private sealed class BlockingReadObserver : IProviderCommandObserver, IDisposable
    {
        internal ManualResetEventSlim ReadEntered { get; } = new();
        internal ManualResetEventSlim Release { get; } = new();

        public void Observe(ProviderCommandEvent command)
        {
            if (command.Operation == "postgresql.read")
            {
                ReadEntered.Set();
                Release.Wait(TimeSpan.FromSeconds(5));
            }
        }

        public void Dispose()
        {
            Release.Set();
            ReadEntered.Dispose();
            Release.Dispose();
        }
    }

    [SkippableFact]
    public async Task Provider_passes_the_shipped_conformance_suite_on_both_surfaces()
    {
        using var database = PostgreSqlFixture.OpenOrSkip();

        // One database, both surfaces: each run proves the whole contract on its own storage units.
        var synchronous = ConformanceSuite.Run(new PostgreSqlProviderFactory(), database.ConnectionString);
        Assert.True(synchronous.Passed, string.Join(Environment.NewLine,
            synchronous.Failures.Select(failure => $"{failure.Name}: {failure.Failure}")));

        var asynchronous = await ConformanceSuite.RunAsync(new PostgreSqlProviderFactory(), database.ConnectionString);
        Assert.True(asynchronous.Passed, string.Join(Environment.NewLine,
            asynchronous.Failures.Select(failure => $"{failure.Name}: {failure.Failure}")));
    }

    [SkippableFact]
    [Trait("Category", "Concurrency")]
    public void Async_writes_hold_every_named_concurrency_invariant_under_contention()
    {
        using var database = PostgreSqlFixture.OpenOrSkip();
        var report = ConcurrencyHarness.Run(
            new StorageProviderConcurrencyFactory("postgresql", new PostgreSqlProviderFactory()),
            database.ConnectionString,
            new ConcurrencyProbeOptions
            {
                WriterCount = 16,
                KeyCount = 1,
                RepeatCount = 1,
                Seed = 8245,
                Concurrency = ConcurrencyKind.Optimistic,
                Surface = ConcurrencySurface.Asynchronous
            });

        Assert.True(report.Passed, report.ToString());
        Assert.All(report.Scenarios.SelectMany(scenario => scenario.Invariants), invariant =>
            Assert.True(invariant.Passed, $"{invariant.Name}: {invariant.Detail}"));
    }

    [SkippableFact]
    [Trait("Category", "Concurrency")]
    public void Async_unit_of_work_commits_hold_every_named_concurrency_invariant_under_contention()
    {
        using var database = PostgreSqlFixture.OpenOrSkip();
        var report = ConcurrencyHarness.Run(
            new StorageProviderConcurrencyFactory(
                "postgresql", new PostgreSqlProviderFactory(), commitThroughUnitOfWork: true),
            database.ConnectionString,
            new ConcurrencyProbeOptions
            {
                WriterCount = 8,
                KeyCount = 1,
                RepeatCount = 1,
                Seed = 9245,
                Concurrency = ConcurrencyKind.Optimistic,
                Surface = ConcurrencySurface.Asynchronous
            });

        Assert.True(report.Passed, report.ToString());
    }

    [SkippableFact]
    public void Live_compare_and_delete_preserves_revision_cas_and_exact_rollback()
    {
        using var database = PostgreSqlFixture.OpenOrSkip();
        var name = "pg_compare_delete_" + Guid.NewGuid().ToString("N");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId(name),
            Name = name,
            Columns =
            [
                new ColumnDefinition { Name = "id", Type = PortableType.String, IsNullable = false },
                new ColumnDefinition { Name = "owner", Type = PortableType.String },
                new ColumnDefinition { Name = "fence", Type = PortableType.Int64, IsNullable = false },
                new ColumnDefinition { Name = "amount", Type = PortableType.Decimal, Precision = 12, Scale = 2 }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Concurrency = ConcurrencyDeclaration.Optimistic()
        };
        using var connection = new PostgreSqlProviderFactory().Create(database.ConnectionString);
        connection.Schema.Apply(unit);
        var marker = new StorageUnit
        {
            Id = new StorageUnitId(name + "_marker"),
            Name = name + "_marker",
            Columns =
            [
                new ColumnDefinition { Name = "id", Type = PortableType.String, IsNullable = false },
                new ColumnDefinition { Name = "value", Type = PortableType.String, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] }
        };
        connection.Schema.Apply(marker);
        Assert.Contains(connection.Capabilities, capability => capability.Id == BatchWriteCapabilities.CompareAndDelete);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        Assert.Equal(1L, session.Insert(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "claim-1", ["owner"] = "worker-a", ["fence"] = 7L
        })).Version);
        session.Insert(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "claim-decimal", ["owner"] = "worker-a", ["fence"] = 7L, ["amount"] = 7m
        }));
        Assert.Equal(WriteOutcomeStatus.Deleted,
            session.CompareAndDelete(new StorageKey(new Dictionary<string, object?> { ["id"] = "claim-decimal" }),
                new Dictionary<string, object?> { ["amount"] = 7 }).Status);

        var mismatchObserver = new ProviderCommandObserver();
        var mismatchSession = (ICompareAndDeleteStorageSession)connection.OpenSession(unit, StorageAccess.Global, mismatchObserver);
        Assert.Equal(WriteOutcomeStatus.ComparisonMismatch,
            mismatchSession.CompareAndDelete(new StorageKey(new Dictionary<string, object?> { ["id"] = "claim-1" }),
                new Dictionary<string, object?> { ["owner"] = "worker-b", ["fence"] = 7L }).Status);
        Assert.Equal(1, mismatchObserver.RoundTrips);
        Assert.Contains(mismatchObserver.Commands, command => command.Operation == "postgresql.compare-and-delete-read");
        Assert.Equal(2L, session.Update(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "claim-1", ["owner"] = "worker-a", ["fence"] = 7L
        }), WriteOptions.IfVersion(1)).Version);
        var deleteObserver = new ProviderCommandObserver();
        var deleteSession = (ICompareAndDeleteStorageSession)connection.OpenSession(unit, StorageAccess.Global, deleteObserver);
        var deleted = deleteSession.CompareAndDelete(new StorageKey(new Dictionary<string, object?> { ["id"] = "claim-1" }),
            new Dictionary<string, object?> { ["owner"] = "worker-a", ["fence"] = 7L });
        Assert.Equal(WriteOutcomeStatus.Deleted, deleted.Status);
        Assert.Equal(2L, deleted.Version);
        Assert.Equal(2, deleteObserver.RoundTrips);
        Assert.Contains(deleteObserver.Commands, command => command.Operation == "postgresql.compare-and-delete");
        Assert.Equal(WriteOutcomeStatus.NotFound,
            session.CompareAndDelete(new StorageKey(new Dictionary<string, object?> { ["id"] = "claim-1" }),
                new Dictionary<string, object?> { ["owner"] = "worker-a" }).Status);

        session.Insert(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "claim-reclaimed", ["owner"] = "worker-a", ["fence"] = 7L
        }));
        var claimed = session.Read(new StorageKey(new Dictionary<string, object?> { ["id"] = "claim-reclaimed" }))!;
        using (var compareWork = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, unit, marker))
        {
            compareWork.Stage(RowWrite.Insert(marker, new StorageValues(new Dictionary<string, object?>
            {
                ["id"] = "marker", ["value"] = "must-rollback"
            })));
            var compare = RowWrite.CompareAndDelete(unit,
                new StorageKey(new Dictionary<string, object?> { ["id"] = "claim-reclaimed" }),
                new Dictionary<string, object?> { ["owner"] = claimed.Values.Values["owner"], ["fence"] = claimed.Values.Values["fence"] });
            compareWork.Stage(compare);

            var reclaimer = connection.OpenSession(unit, StorageAccess.Global);
            Assert.Equal(2L, reclaimer.Update(new StorageValues(new Dictionary<string, object?>
            {
                ["id"] = "claim-reclaimed", ["owner"] = "worker-b", ["fence"] = 8L
            }), WriteOptions.IfVersion(claimed.Version!.Value)).Version);

            var exception = Assert.Throws<BatchWriteException>(() => compareWork.CommitWithOutcomes());
            var outcome = Assert.Single(exception.Outcomes);
            Assert.Same(compare, outcome.Write);
            Assert.Equal(WriteOutcomeStatus.ComparisonMismatch, outcome.Outcome.Status);
            var reclaimed = session.Read(new StorageKey(new Dictionary<string, object?> { ["id"] = "claim-reclaimed" }))!;
            Assert.Equal("worker-b", reclaimed.Values.Values["owner"]);
            Assert.Equal(8L, reclaimed.Values.Values["fence"]);
            Assert.Equal(2L, reclaimed.Version);
            Assert.Null(connection.OpenSession(marker, StorageAccess.Global).Read(
                new StorageKey(new Dictionary<string, object?> { ["id"] = "marker" })));
        }

        session.Insert(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "claim-null", ["owner"] = null, ["fence"] = 7L
        }));
        Assert.Equal(WriteOutcomeStatus.Deleted,
            session.CompareAndDelete(new StorageKey(new Dictionary<string, object?> { ["id"] = "claim-null" }),
                new Dictionary<string, object?> { ["owner"] = null }).Status);

        session.Insert(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "claim-omitted", ["fence"] = 7L
        }));
        Assert.Equal(WriteOutcomeStatus.Deleted,
            session.CompareAndDelete(new StorageKey(new Dictionary<string, object?> { ["id"] = "claim-omitted" }),
                new Dictionary<string, object?> { ["owner"] = null }).Status);

    }

    private static StorageUnit NullOrderingUnit(string id) => new()
    {
        Id = new StorageUnitId(id),
        Name = id.Replace('-', '_'),
        Columns =
        [
            new ColumnDefinition { Name = "id", Type = PortableType.String, IsNullable = false },
            new ColumnDefinition { Name = "value", Type = PortableType.String }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        Indexes =
        [
            new IndexDefinition
            {
                Name = "by_name",
                Columns =
                [
                    new IndexColumn("value", SortDirection.Ascending),
                    new IndexColumn("id", SortDirection.Descending)
                ]
            }
        ]
    };

    private static string PhysicalIndexName(string table, string index) =>
        $"__groundwork_ix_{table.Length}_{table}_{index.Length}_{index}";

    [SkippableFact]
    public void Live_dropped_column_on_a_plain_unit_is_fatal_at_session_open()
    {
        using var database = PostgreSqlFixture.OpenOrSkip();
        var unit = AdmissionUnit("pg_admission_drop");
        using (var connection = new PostgreSqlProviderFactory().Create(database.ConnectionString))
        {
            Assert.True(connection.Schema.Apply(unit).Applied);
        }

        using (var raw = new NpgsqlConnection(database.ConnectionString))
        {
            raw.Open();
            using var command = raw.CreateCommand();
            command.CommandText = $"ALTER TABLE \"{unit.Name}\" DROP COLUMN \"payload\";";
            command.ExecuteNonQuery();
        }

        using var reopened = new PostgreSqlProviderFactory().Create(database.ConnectionString);
        var failure = Assert.Throws<GroundworkRuntimeSchemaAdmissionException>(() => reopened.OpenSession(unit, StorageAccess.Global));
        Assert.Contains("GW-RUNTIME-001", failure.Message, StringComparison.Ordinal);
        Assert.Contains("payload", failure.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Live_dropped_index_degrades_instead_of_blocking_session_open()
    {
        using var database = PostgreSqlFixture.OpenOrSkip();
        var unit = AdmissionUnit("pg_admission_index") with
        {
            Indexes = [new IndexDefinition { Name = "by_payload", Columns = [new IndexColumn("payload")] }]
        };
        using (var connection = new PostgreSqlProviderFactory().Create(database.ConnectionString))
        {
            Assert.True(connection.Schema.Apply(unit).Applied);
        }

        using (var raw = new NpgsqlConnection(database.ConnectionString))
        {
            raw.Open();
            using var command = raw.CreateCommand();
            command.CommandText = $"DROP INDEX \"{PhysicalIndexName(unit.Name, "by_payload")}\";";
            command.ExecuteNonQuery();
        }

        using var reopened = new PostgreSqlProviderFactory().Create(database.ConnectionString);
        var session = reopened.OpenSession(unit, StorageAccess.Global);
        Assert.Equal(WriteOutcomeStatus.Inserted, session.Insert(new StorageValues(
            new Dictionary<string, object?> { ["id"] = "one", ["payload"] = "first" })).Status);
    }

    [SkippableFact]
    public void Live_admission_inspects_once_per_unit_per_connection()
    {
        using var database = PostgreSqlFixture.OpenOrSkip();
        var unit = AdmissionUnit("pg_admission_cache");
        using (var connection = new PostgreSqlProviderFactory().Create(database.ConnectionString))
        {
            Assert.True(connection.Schema.Apply(unit).Applied);
        }

        using var reopened = new PostgreSqlProviderFactory().Create(database.ConnectionString);
        var firstObserver = new ProviderCommandObserver();
        _ = reopened.OpenSession(unit, StorageAccess.Global, firstObserver);
        var admissionEvent = Assert.Single(firstObserver.Commands);
        Assert.Equal("postgresql.schema-admission", admissionEvent.Operation);
        Assert.Equal(ProviderCommandKind.Read, admissionEvent.Kind);

        var secondObserver = new ProviderCommandObserver();
        _ = reopened.OpenSession(unit, StorageAccess.Global, secondObserver);
        Assert.Equal(0, secondObserver.RoundTrips);
    }

    private static StorageUnit AdmissionUnit(string id) => new()
    {
        Id = new StorageUnitId(id),
        Name = id,
        Columns =
        [
            new ColumnDefinition { Name = "id", Type = PortableType.String, IsNullable = false },
            new ColumnDefinition { Name = "payload", Type = PortableType.String }
        ],
        Key = new KeyDefinition { Columns = ["id"] }
    };

    private sealed class RegistrationBarrierObserver : ISessionRegistrationObserver, IDisposable
    {
        internal ManualResetEventSlim EligibilityChecked { get; } = new();
        internal ManualResetEventSlim DisposalAttempted { get; } = new();
        internal ManualResetEventSlim Release { get; } = new();

        public void Observe(ProviderCommandEvent command) { }

        public void OnSessionRegistrationEligibilityChecked()
        {
            EligibilityChecked.Set();
            Release.Wait(TimeSpan.FromSeconds(5));
        }

        public void OnProviderDisposalAttempted() => DisposalAttempted.Set();

        public void Dispose()
        {
            Release.Set();
            EligibilityChecked.Dispose();
            DisposalAttempted.Dispose();
            Release.Dispose();
        }
    }
}
