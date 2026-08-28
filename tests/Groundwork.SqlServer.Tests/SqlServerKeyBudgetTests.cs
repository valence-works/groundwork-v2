using Groundwork.Kernel;
using Groundwork.SqlServer;
using Xunit;

namespace Groundwork.SqlServer.Tests;

public sealed class SqlServerKeyBudgetTests
{
    [Fact]
    public void Physicalization_refuses_an_invalid_raw_json_string_default_before_provider_work()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            SqlServerSchemaCoordinator.Physicalize(RawJsonStringDefaultUnit()));

        Assert.Contains("GW-PORT-013", exception.Message, StringComparison.Ordinal);
        Assert.Contains("payload", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Json", exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(String), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Batch_type_names_use_the_validated_physical_name_not_the_logical_id()
    {
        var physical = new StorageUnit
        {
            Id = new StorageUnitId("logical.id/with spaces/" + new string('x', 80)),
            Name = "batch_type_boundary",
            Columns = [new ColumnDefinition { Name = "id", Type = PortableType.Int32, IsNullable = false }],
            Key = new KeyDefinition { Columns = ["id"] }
        };

        var batchTypeName = SqlServerSchemaCoordinator.BatchTypeName(physical);

        Assert.Equal($"__groundwork_batch_type_{physical.Name.Length}_{physical.Name}", batchTypeName);
        Assert.DoesNotContain("logical", batchTypeName, StringComparison.Ordinal);
        Assert.NotEqual(batchTypeName, SqlServerSchemaCoordinator.BatchTypeName(
            physical with { Name = "batch_type_boundary_variant" }));
        Assert.True(PortabilityValidator.ValidatePhysicalIdentifier(
            batchTypeName,
            "sqlserver.batchType.name",
            maximumByteLength: 128,
            allowProviderOwnedPrefix: true).IsPortable);
    }

    [Fact]
    public void A_string_key_at_1700_bytes_is_admitted()
    {
        SqlServerIndexKeyBudgetValidator.Validate(Unit(
            Column("value", PortableType.String, maxLength: 850),
            Index("by_value", "value")));
    }

    [Fact]
    public void A_string_key_over_1700_bytes_reports_arithmetic_and_column()
    {
        var exception = Assert.Throws<SqlServerKeyBudgetException>(() =>
            SqlServerIndexKeyBudgetValidator.Validate(Unit(
                Column("id", PortableType.Int32, nullable: false),
                Column("email", PortableType.String, maxLength: 851),
                Index("ux_email", "email"))));

        Assert.Equal("ux_email", exception.IndexName);
        Assert.Equal(1702, exception.RequiredBytes);
        Assert.Contains("email=851*2", exception.Message, StringComparison.Ordinal);
        Assert.Contains("1702", exception.Message, StringComparison.Ordinal);
        Assert.Contains("1700", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_thirty_two_column_key_is_admitted_and_thirty_three_is_refused()
    {
        var columns = Enumerable.Range(0, 33)
            .Select(index => Column("c" + index, PortableType.Int32, nullable: false))
            .ToArray();

        SqlServerIndexKeyBudgetValidator.Validate(Unit(columns, Index("by-all", columns.Select(column => column.Name).Take(32).ToArray())));

        var exception = Assert.Throws<SqlServerKeyBudgetException>(() =>
            SqlServerIndexKeyBudgetValidator.Validate(Unit(columns, Index("by-all", columns.Select(column => column.Name).ToArray()))));

        Assert.Equal(33, exception.RequiredColumns);
        Assert.Contains("33 key columns", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(9, 5)]
    [InlineData(10, 9)]
    [InlineData(19, 9)]
    [InlineData(20, 13)]
    [InlineData(28, 13)]
    [InlineData(29, 17)]
    [InlineData(38, 17)]
    public void Decimal_key_width_uses_SQL_Server_storage_tiers(int precision, int expectedBytes)
    {
        var column = Column("amount", PortableType.Decimal, nullable: false, precision: precision, scale: 0);
        var paddingLength = (1700 - expectedBytes) / 2;
        SqlServerIndexKeyBudgetValidator.Validate(Unit(
            column,
            Column("padding", PortableType.String, nullable: false, maxLength: paddingLength),
            Index("by_amount", "amount", "padding")));
    }

    [Theory]
    [InlineData(0, 340, 1700)]
    [InlineData(1, 242, 1694)]
    public void Future_search_key_factors_are_measured_as_ASCII_bytes(
        int policyValue,
        int sourceLength,
        int expectedBytes)
    {
        var policy = (SqlServerSearchKeyExpansionPolicy)policyValue;
        var unit = Unit(Column("name", PortableType.String, nullable: false, maxLength: sourceLength), Index("by_name", "name"));

        SqlServerIndexKeyBudgetValidator.Validate(unit, new Dictionary<string, SqlServerSearchKeyExpansionPolicy>
        {
            ["name"] = policy
        });

        Assert.Equal(expectedBytes, SqlServerIndexKeyBudgetValidator.EstimateSearchKeyBytes(sourceLength, policy));
    }

    [Theory]
    [InlineData(0, 341, 1705, "*5")]
    [InlineData(1, 243, 1701, "*7")]
    public void Future_search_key_factors_refuse_at_the_first_over_budget_length(
        int policyValue,
        int sourceLength,
        int expectedBytes,
        string expectedFactor)
    {
        var policy = (SqlServerSearchKeyExpansionPolicy)policyValue;
        var exception = Assert.Throws<SqlServerKeyBudgetException>(() =>
            SqlServerIndexKeyBudgetValidator.Validate(
                Unit(Column("id", PortableType.Int32, nullable: false), Column("name", PortableType.String, nullable: false, maxLength: sourceLength), Index("by_name", "name")),
                new Dictionary<string, SqlServerSearchKeyExpansionPolicy> { ["name"] = policy }));

        Assert.Contains("name=", exception.Message, StringComparison.Ordinal);
        Assert.Contains("name=" + sourceLength + expectedFactor, exception.Message, StringComparison.Ordinal);
        Assert.Contains(expectedBytes.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.Contains("1700", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Folded_physical_index_budget_reports_logical_source_length_and_policy()
    {
        var logical = new StorageUnit
        {
            Id = new StorageUnitId("sqlserver-folded-budget"),
            Name = "SqlServerFoldedBudget",
            Columns =
            [
                new() { Name = "id", Type = PortableType.Int32, IsNullable = false },
                new() { Name = "name", Type = PortableType.String, IsNullable = false, MaxLength = 243, Collation = PortableCollation.UnicodeOrdinalIgnoreCase }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Indexes = [new IndexDefinition { Name = "by_name", Columns = [new IndexColumn("name")] }]
        };

        var physical = SqlServerSchemaCoordinator.Physicalize(logical);
        var exception = Assert.Throws<SqlServerKeyBudgetException>(() =>
            SqlServerIndexKeyBudgetValidator.Validate(physical));

        Assert.Equal("by_name", exception.IndexName);
        Assert.Equal(1_701, exception.RequiredBytes);
        Assert.Contains("name=243*7", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("__groundwork_search_name=1701*1", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Locale_physical_index_budget_uses_its_declared_expansion_factor()
    {
        var logical = new StorageUnit
        {
            Id = new StorageUnitId("sqlserver-locale-budget"),
            Name = "SqlServerLocaleBudget",
            Columns =
            [
                new() { Name = "id", Type = PortableType.Int32, IsNullable = false },
                new()
                {
                    Name = "name",
                    Type = PortableType.String,
                    IsNullable = false,
                    MaxLength = 142,
                    LocaleSortKey = new LocaleSortKeyDefinition
                    {
                        CultureName = "sv-SE",
                        MaximumExpansionFactor = 12
                    }
                }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Indexes = [new IndexDefinition { Name = "by_name", Columns = [new IndexColumn("name")] }]
        };

        var physical = SqlServerSchemaCoordinator.Physicalize(logical);
        var exception = Assert.Throws<SqlServerKeyBudgetException>(() =>
            SqlServerIndexKeyBudgetValidator.Validate(physical));

        Assert.Equal(1_704, exception.RequiredBytes);
        Assert.Contains("name=142*12", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("__groundwork_search_name=1704*1", exception.Message, StringComparison.Ordinal);
    }

    private static StorageUnit Unit(params object[] values)
    {
        var columns = values.OfType<ColumnDefinition>().ToArray();
        var index = values.OfType<IndexDefinition>().Single();
        return Unit(columns, index);
    }

    private static StorageUnit Unit(ColumnDefinition column, params object[] values) =>
        Unit(new[] { column }.Concat(values.OfType<ColumnDefinition>()).ToArray(), values.OfType<IndexDefinition>().Single());

    private static StorageUnit Unit(ColumnDefinition[] columns, IndexDefinition index) => new()
    {
        Id = new StorageUnitId("sqlserver-budget"),
        Name = "SqlServerBudget",
        Columns = columns,
        Key = new KeyDefinition { Columns = [columns[0].Name] },
        Indexes = [index]
    };

    private static StorageUnit RawJsonStringDefaultUnit() => new()
    {
        Id = new StorageUnitId("sqlserver-invalid-raw-json-default"),
        Name = "sqlserver_invalid_raw_json_default",
        Columns =
        [
            new() { Name = "id", Type = PortableType.Guid, IsNullable = false },
            new() { Name = "payload", Type = PortableType.Json, Default = new PortableDefault("pending") }
        ],
        Key = new KeyDefinition { Columns = ["id"] }
    };

    private static ColumnDefinition Column(
        string name,
        PortableType type,
        bool nullable = true,
        int? maxLength = null,
        int? precision = null,
        int? scale = null) => new()
    {
        Name = name,
        Type = type,
        IsNullable = nullable,
        MaxLength = maxLength,
        Precision = precision,
        Scale = scale
    };

    private static IndexDefinition Index(string name, params string[] columns) => new()
    {
        Name = name,
        Columns = columns.Select(column => new IndexColumn(column)).ToArray()
    };
}
