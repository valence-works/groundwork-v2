using System.Text.Json;
using Groundwork.Kernel;
using Groundwork.PostgreSql;
using Groundwork.Substrate.Relational;
using Groundwork.Testing;
using Npgsql;
using Xunit;

namespace Groundwork.PostgreSql.Tests;

public sealed class PostgreSqlDialectTests
{
    private readonly PostgreSqlDialect dialect = new();

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

    [Fact]
    public void Index_ddl_spells_out_normalized_null_ordering()
    {
        var index = new IndexDefinition
        {
            Name = "by-name",
            Columns =
            [
                new IndexColumn("name", SortDirection.Ascending),
                new IndexColumn("createdAt", SortDirection.Descending)
            ]
        };

        var sql = dialect.CreateIndexSql("customers", index, null);

        Assert.Contains("\"name\" ASC NULLS FIRST", sql, StringComparison.Ordinal);
        Assert.Contains("\"createdAt\" DESC NULLS LAST", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("INDEXED BY", sql, StringComparison.OrdinalIgnoreCase);
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
        command.Parameters.AddWithValue("index", unit.Name + "__by-name");
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal((short)2, reader.GetInt16(0)); // ASC NULLS FIRST
        Assert.Equal((short)1, reader.GetInt16(1)); // DESC NULLS LAST
    }

    [SkippableFact]
    public void Live_schema_validation_refuses_a_default_null_ordering_index()
    {
        using var database = PostgreSqlFixture.OpenOrSkip();
        var unit = NullOrderingUnit("pg-null-drift");
        using var connection = new PostgreSqlProviderFactory().Create(database.ConnectionString);
        connection.Schema.Apply(unit);

        using (var raw = new NpgsqlConnection(database.ConnectionString))
        {
            raw.Open();
            using var command = raw.CreateCommand();
            command.CommandText = $"DROP INDEX \"{unit.Name}__by-name\"; CREATE INDEX \"{unit.Name}__by-name\" ON \"{unit.Name}\" (\"value\");";
            command.ExecuteNonQuery();
        }

        var exception = Assert.Throws<InvalidOperationException>(() => connection.Schema.Apply(unit));
        Assert.Contains("by-name", exception.Message, StringComparison.Ordinal);
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
                    Name = "unique-email-present",
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
    public void Provider_passes_the_shipped_conformance_suite()
    {
        using var database = PostgreSqlFixture.OpenOrSkip();
        var report = ConformanceSuite.Run(new PostgreSqlProviderFactory(), database.ConnectionString);
        Assert.True(report.Passed, string.Join(Environment.NewLine,
            report.Failures.Select(failure => $"{failure.Name}: {failure.Failure}")));
    }

    private sealed class PostgreSqlFixture : IDisposable
    {
        private readonly string adminConnectionString;
        private readonly string schema;

        private PostgreSqlFixture(string adminConnectionString, string schema, string connectionString)
        {
            this.adminConnectionString = adminConnectionString;
            this.schema = schema;
            ConnectionString = connectionString;
        }

        public string ConnectionString { get; }

        public static PostgreSqlFixture OpenOrSkip()
        {
            var baseConnection = Environment.GetEnvironmentVariable("GROUNDWORK_POSTGRES_CONNECTION");
            Skip.If(string.IsNullOrWhiteSpace(baseConnection),
                "Set GROUNDWORK_POSTGRES_CONNECTION to run PostgreSQL integration tests.");
            var schema = "w2_" + Guid.NewGuid().ToString("N");
            using var admin = new NpgsqlConnection(baseConnection);
            try
            {
                admin.Open();
            }
            catch (Exception exception)
            {
                Skip.If(true, $"PostgreSQL is unavailable: {exception.Message}");
                throw;
            }
            using (var command = admin.CreateCommand())
            {
                command.CommandText = $"CREATE SCHEMA \"{schema}\";";
                command.ExecuteNonQuery();
            }
            var builder = new NpgsqlConnectionStringBuilder(baseConnection) { SearchPath = schema };
            return new PostgreSqlFixture(baseConnection, schema, builder.ConnectionString);
        }

        public void Dispose()
        {
            using var admin = new NpgsqlConnection(adminConnectionString);
            admin.Open();
            using var command = admin.CreateCommand();
            command.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;";
            command.ExecuteNonQuery();
        }
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
                Name = "by-name",
                Columns =
                [
                    new IndexColumn("value", SortDirection.Ascending),
                    new IndexColumn("id", SortDirection.Descending)
                ]
            }
        ]
    };
}
