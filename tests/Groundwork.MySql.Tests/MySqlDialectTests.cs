using System.Collections.Immutable;
using System.Text.Json;
using Groundwork.Kernel;
using Groundwork.MySql;
using Groundwork.Query.Model;
using Groundwork.Substrate.Relational;
using Xunit;

namespace Groundwork.MySql.Tests;

public sealed class MySqlDialectTests
{
    private readonly MySqlDialect dialect = new();

    [Fact]
    public void Quotes_backticks_and_escapes_embedded_backticks()
    {
        Assert.Equal("`order``items`", dialect.QuoteIdentifier("order`items"));
        Assert.Equal("MySQL/MariaDB", dialect.ProviderName);
    }

    [Fact]
    public void Maps_every_portable_type_to_a_stable_mysql_type()
    {
        Assert.Equal("varchar(120)", dialect.MapType(Column(PortableType.String, maxLength: 120)));
        Assert.Equal("int", dialect.MapType(Column(PortableType.Int32)));
        Assert.Equal("bigint", dialect.MapType(Column(PortableType.Int64)));
        Assert.Equal("decimal(18,4)", dialect.MapType(Column(PortableType.Decimal, precision: 18, scale: 4)));
        Assert.Equal("tinyint(1)", dialect.MapType(Column(PortableType.Boolean)));
        Assert.Equal("bigint", dialect.MapType(Column(PortableType.DateTimeOffset)));
        Assert.Equal("char(36)", dialect.MapType(Column(PortableType.Guid)));
        Assert.Equal("varbinary(32)", dialect.MapType(Column(PortableType.Binary, maxLength: 32)));
        Assert.Equal("json", dialect.MapType(Column(PortableType.Json)));
        Assert.Equal("double", dialect.MapType(Column(PortableType.Double)));
    }

    [Fact]
    public void Uses_utf8mb4_binary_for_ordinal_and_refuses_folded_collations()
    {
        Assert.Equal(MySqlDialect.OrdinalCollation, dialect.MapCollation(Column(PortableType.String)));
        Assert.Equal(MySqlDialect.OrdinalCollation, dialect.MapCollation(Column(PortableType.String, collation: PortableCollation.Ordinal)));
        Assert.Throws<NotSupportedException>(() => dialect.MapCollation(Column(PortableType.String, collation: PortableCollation.OrdinalIgnoreCase)));
        Assert.Throws<NotSupportedException>(() => dialect.MapCollation(Column(PortableType.String, collation: PortableCollation.UnicodeOrdinalIgnoreCase)));
        Assert.Throws<ArgumentException>(() => dialect.MapCollation(Column(PortableType.Int32, collation: PortableCollation.Ordinal)));
    }

    [Fact]
    public void Emits_generated_auto_increment_and_refuses_non_integer_generation()
    {
        var generated = Column(PortableType.Int64, name: "id", generation: ColumnGeneration.ProviderSequence, nullable: false);
        Assert.Equal("AUTO_INCREMENT", dialect.MapGeneration(generated));
        var create = RelationalSql.CreateTable(
            dialect,
            new StorageUnit
            {
                Id = new StorageUnitId("events"),
                Name = "events",
                Columns = [generated],
                Key = new KeyDefinition { Columns = ["id"] }
            });
        Assert.Contains("AUTO_INCREMENT", create, StringComparison.Ordinal);
        Assert.EndsWith("ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4;", create, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => dialect.Validate(Column(PortableType.String, generation: ColumnGeneration.ProviderSequence)));
    }

    [Fact]
    public void Emits_index_upsert_and_batch_sql()
    {
        var index = new IndexDefinition { Name = "by_name", Columns = [new IndexColumn("name", SortDirection.Descending)], IsUnique = true };
        Assert.Equal(
            "CREATE UNIQUE INDEX `__groundwork_ix_6_events_7_by_name` ON `events` (`name` DESC);",
            dialect.CreateIndexSql("events", index, null));
        Assert.Equal("DROP INDEX `__groundwork_ix_6_events_7_by_name` ON `events`;", dialect.DropIndexSql("events", "by_name"));

        var shape = new RelationalWriteShape(
            "events",
            [new("id", "id"), new("value", "value")],
            ["id"],
            ["value"]);
        Assert.Contains("ON DUPLICATE KEY UPDATE `value`=VALUES(`value`)", dialect.ConditionalUpsertSql(shape), StringComparison.Ordinal);
        var noUpdate = new RelationalWriteShape("events", [new("id")], ["id"], []);
        Assert.Contains("ON DUPLICATE KEY UPDATE `id`=`id`", dialect.ConditionalUpsertSql(noUpdate), StringComparison.Ordinal);
        Assert.Contains("(@id_0, @value_0), (@id_1, @value_1)", dialect.BatchInsertSql(shape, 2), StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() => dialect.CreateIndexSql("events", index, "`name` IS NOT NULL"));

        var excluded = index with { MissingValues = MissingValueBehavior.Excluded };
        Assert.Null(dialect.IndexFilter(excluded));
        Assert.Equal(
            "CREATE UNIQUE INDEX `__groundwork_ix_6_events_7_by_name` ON `events` (`name` DESC);",
            RelationalSql.CreateIndex(dialect, "events", excluded));
    }

    [Fact]
    public void Maps_logical_indexes_to_their_collision_safe_physical_names()
    {
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("index-map"),
            Name = "events",
            Columns =
            [
                Column(PortableType.Int64, name: "id", nullable: false),
                Column(PortableType.String, name: "name")
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Indexes = [new IndexDefinition { Name = "by_name", Columns = [new IndexColumn("name")] }]
        };

        var physical = MySqlDialect.PhysicalIndexName(unit.Name, "by_name");
        Assert.Equal("__groundwork_ix_6_events_7_by_name", physical);
    }

    [Fact]
    public void Converts_and_reads_back_portable_values_without_aliasing_buffers()
    {
        var timestamp = new DateTimeOffset(2026, 8, 30, 10, 11, 12, TimeSpan.FromHours(2)).AddTicks(1234);
        var bytes = new byte[] { 1, 2, 3 };
        var convertedBytes = Assert.IsType<byte[]>(dialect.ConvertValue(bytes, Column(PortableType.Binary)));
        Assert.NotSame(bytes, convertedBytes);
        Assert.Equal(timestamp.UtcTicks, dialect.ConvertValue(timestamp, Column(PortableType.DateTimeOffset)));
        Assert.Equal(1, dialect.ConvertValue(true, Column(PortableType.Boolean)));
        Assert.Equal("bde4c9e4-65cc-4d6e-bf50-6d9c4f27c22a", dialect.ConvertValue(Guid.Parse("bde4c9e4-65cc-4d6e-bf50-6d9c4f27c22a"), Column(PortableType.Guid)));
        Assert.Equal(123, dialect.ReadValue(123L, Column(PortableType.Int32)));
        Assert.Equal(timestamp.UtcTicks, Assert.IsType<DateTimeOffset>(dialect.ReadValue(timestamp.UtcTicks, Column(PortableType.DateTimeOffset))).UtcTicks);
        var json = Assert.IsType<JsonElement>(dialect.ReadValue("{\"ok\":true}", Column(PortableType.Json)));
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Null(dialect.ReadValue(DBNull.Value, Column(PortableType.String)));
    }

    [Fact]
    public void Emits_sql_mode_independent_expression_defaults_for_text_json_and_binary()
    {
        Assert.Equal(
            "(convert(0x736c6173685c6e65776c696e650a71756f746527636f6e74726f6c1a using utf8mb4))",
            dialect.MapDefault(Column(PortableType.String) with
            {
                Default = new PortableDefault("slash\\newline\nquote'control\u001a")
            }));
        Assert.Equal(
            "(convert(0x7b226f6b223a747275657d using utf8mb4))",
            dialect.MapDefault(Column(PortableType.Json) with
            {
                Default = new PortableDefault(JsonDocument.Parse("{\"ok\":true}"))
            }));
        Assert.Equal(
            "(0x001aff)",
            dialect.MapDefault(Column(PortableType.Binary) with
            {
                Default = new PortableDefault(new byte[] { 0, 26, 255 })
            }));
        Assert.Equal(
            "(convert(x'' using utf8mb4))",
            dialect.MapDefault(Column(PortableType.String) with { Default = new PortableDefault(string.Empty) }));
        Assert.Equal(
            "(x'')",
            dialect.MapDefault(Column(PortableType.Binary) with { Default = new PortableDefault(Array.Empty<byte>()) }));
    }

    [Fact]
    public void Renders_binary_ordinal_fragments_and_mysql_parameterized_paging()
    {
        var table = new TableId("events");
        var name = new ColumnRef(table, "name", QueryType.String, isNullable: true, maxLength: 120);
        var request = new QueryRequest(
            table,
            new Predicate.Equal(name, QueryConstant.Of(name, "Ada")),
            ImmutableArray.Create(new OrderTerm(name, OrderDirection.Ascending, NullOrder.Last)),
            Projection.ColumnsOnly(name),
            Paging.OffsetLimit(4, 12));

        var command = new MySqlQueryRenderer().Render(request);
        Assert.Contains($"`name` COLLATE {MySqlDialect.OrdinalCollation}", command.CommandText, StringComparison.Ordinal);
        Assert.Contains($"HEX(CONVERT(`name` COLLATE {MySqlDialect.OrdinalCollation} USING utf16)) = HEX(CONVERT(@p0 USING utf16))", command.CommandText, StringComparison.Ordinal);
        Assert.Contains($"ORDER BY CASE WHEN `name` COLLATE {MySqlDialect.OrdinalCollation} IS NULL", command.CommandText, StringComparison.Ordinal);
        Assert.Contains($"HEX(CONVERT(`name` COLLATE {MySqlDialect.OrdinalCollation} USING utf16)) ASC", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("LIMIT @p1 OFFSET @p2", command.CommandText, StringComparison.Ordinal);
        Assert.Equal(3, command.Parameters.Length);
        Assert.Equal(MySqlQueryRenderer.ParameterBudget, dialect.ParameterBudget);
    }

    private static ColumnDefinition Column(
        PortableType type,
        string name = "value",
        int? maxLength = null,
        int? precision = null,
        int? scale = null,
        PortableCollation? collation = null,
        ColumnGeneration generation = ColumnGeneration.Supplied,
        bool nullable = true) => new()
        {
            Name = name,
            Type = type,
            MaxLength = maxLength,
            Precision = precision,
            Scale = scale,
            Collation = collation,
            Generation = generation,
            IsNullable = nullable
        };

}
