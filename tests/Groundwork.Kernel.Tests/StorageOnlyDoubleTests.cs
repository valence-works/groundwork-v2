using Xunit;

namespace Groundwork.Kernel.Tests;

/// <summary>
/// <see cref="PortableType.Double"/> is storable and never comparable. These tests pin both halves
/// of that: a Double column is a first-class part of a declaration, and every structural position
/// that compares one value with another refuses it.
/// </summary>
public sealed class StorageOnlyDoubleTests
{
    [Fact]
    public void A_double_column_is_declarable_and_keeps_its_declared_type()
    {
        var unit = Groundwork.Kernel.StorageUnit
            .Declare("telemetry", "telemetry")
            .Guid("id", column => column.Required())
            .Double("reading")
            .Key("id")
            .Build();

        var reading = Assert.Single(unit.Columns, column => column.Name == "reading");
        Assert.Equal(PortableType.Double, reading.Type);
        Assert.True(reading.IsNullable);
    }

    [Fact]
    public void Appending_double_leaves_the_names_already_written_into_schema_documents_alone()
    {
        // The canonical schema document and every fingerprint derived from it spell a column's
        // type with Enum.ToString, so the meaning of an existing document depends on the names,
        // not on the numbering. Pinning both keeps a later insertion from being a silent break.
        Assert.Equal(
            "String,Int32,Int64,Decimal,Boolean,DateTimeOffset,Guid,Binary,Json,Double",
            string.Join(",", Enum.GetNames<PortableType>()));
        Assert.Equal(9, (int)PortableType.Double);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(0.1d)]
    [InlineData(double.Epsilon)]
    [InlineData(double.MaxValue)]
    [InlineData(double.MinValue)]
    [InlineData(1e-320d)]
    public void The_storable_domain_admits_every_finite_value(double value) =>
        Assert.True(PortableDouble.IsStorable(value));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-0d)]
    public void The_storable_domain_excludes_what_the_stores_do_not_return_unchanged(double value) =>
        Assert.False(PortableDouble.IsStorable(value));

    [Fact]
    public void Positive_and_negative_zero_are_distinguished_by_the_domain_test() =>
        // The two compare equal, so a naive check would admit both.
        Assert.True(PortableDouble.IsStorable(0d) && !PortableDouble.IsStorable(-0d));

    [Fact]
    public void A_double_key_column_is_refused()
    {
        var refusals = BuildRefusals(builder => builder
            .Double("reading")
            .Key("reading"));

        var refusal = Assert.Single(refusals, finding => finding.Code == "GW-PORT-012");
        Assert.Contains("a key column", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Declare Decimal or Int64", refusal.Message, StringComparison.Ordinal);
        Assert.Equal("key.columns[0]", refusal.Path);
    }

    [Fact]
    public void A_double_index_column_is_refused()
    {
        var refusals = BuildRefusals(builder => builder
            .Guid("id", column => column.Required())
            .Double("reading")
            .Key("id")
            .Index("by_reading", index => index.Ascending("reading")));

        var refusal = Assert.Single(refusals, finding => finding.Code == "GW-PORT-012");
        Assert.Contains("an index column of 'by_reading'", refusal.Message, StringComparison.Ordinal);
        Assert.Equal("indexes.by_reading.columns[0]", refusal.Path);
    }

    [Fact]
    public void A_double_group_by_column_is_refused()
    {
        var unit = Unit(
            [
                Column("id", PortableType.Guid, nullable: false),
                Column("reading", PortableType.Double)
            ],
            key: ["id"]) with
        {
            AggregationProfiles =
            [
                new AggregationProfile
                {
                    Name = "by_reading",
                    GroupByColumns = ["reading"],
                    Aggregates = [new Aggregate.Count("rows")],
                    MaxGroups = 10,
                    MaxInputRows = 10
                }
            ]
        };

        var refusal = Assert.Single(
            PortabilityValidator.Validate(unit).Refusals,
            finding => finding.Code == "GW-PORT-012");
        Assert.Contains("a group-by column of aggregation profile 'by_reading'", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-0d)]
    public void A_declared_default_outside_the_storable_domain_is_refused(double value)
    {
        var unit = Unit(
            [
                Column("id", PortableType.Guid, nullable: false),
                Column("reading", PortableType.Double) with { Default = new PortableDefault(value) }
            ],
            key: ["id"]);

        var refusal = Assert.Single(
            PortabilityValidator.Validate(unit).Refusals,
            finding => finding.Code == "GW-PORT-013");
        Assert.Equal("columns.reading.default", refusal.Path);
        Assert.Contains("Use a finite value, or declare Decimal or Int64.", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_finite_declared_default_is_accepted()
    {
        var unit = Unit(
            [
                Column("id", PortableType.Guid, nullable: false),
                Column("reading", PortableType.Double) with { Default = new PortableDefault(0.1d) }
            ],
            key: ["id"]);

        Assert.Empty(PortabilityValidator.Validate(unit).Refusals);
    }

    [Fact]
    public void A_double_retention_order_column_is_refused()
    {
        var unit = Unit(
            [
                Column("id", PortableType.Guid, nullable: false),
                Column("reading", PortableType.Double, nullable: false)
            ],
            key: ["id"]) with
        {
            Retention = new RetentionDeclaration { KeepNewest = 10, OrderColumn = "reading" }
        };

        Assert.Single(PortabilityValidator.Validate(unit).Refusals, finding => finding.Code == "GW-PORT-007");
    }

    [Fact]
    public void Summing_or_ordering_a_double_column_is_refused_by_the_aggregation_contract()
    {
        var unit = Unit(
            [
                Column("id", PortableType.Guid, nullable: false),
                Column("bucket", PortableType.String, nullable: false, maxLength: 32),
                Column("reading", PortableType.Double, nullable: false)
            ],
            key: ["id"]);

        Assert.Equal("GW-AGG-TYPE-001", Refuse(unit, new Aggregate.Sum("total", "reading")));
        Assert.Equal("GW-AGG-TYPE-003", Refuse(unit, new Aggregate.Min("lowest", "reading")));
        Assert.Equal("GW-AGG-TYPE-003", Refuse(unit, new Aggregate.Max("highest", "reading")));
    }

    /// <summary>
    /// A folded String column grows a derived search-key column; a Double column is not a text
    /// value and so is never a search-key source. Proven by declaring both side by side.
    /// </summary>
    [Fact]
    public void A_double_column_is_never_a_search_key_source()
    {
        var unit = SearchKeyProjection.Expand(Unit(
            [
                Column("id", PortableType.Guid, nullable: false),
                Column("code", PortableType.String, nullable: false, maxLength: 32,
                    collation: PortableCollation.OrdinalIgnoreCase),
                Column("reading", PortableType.Double, nullable: false)
            ],
            key: ["id"]));

        Assert.Equal("code", string.Join(",", unit.DerivedColumns.Select(derived => derived.SourceColumn)));
    }

    private static string Refuse(StorageUnit unit, Aggregate aggregate)
    {
        var profile = new AggregationProfile
        {
            Name = "profile",
            GroupByColumns = ["bucket"],
            Aggregates = [aggregate],
            MaxGroups = 10,
            MaxInputRows = 10
        };
        var exception = Assert.Throws<AggregationValidationException>(() =>
            AggregationProfileValidator.Validate(unit, profile));
        return Assert.Single(exception.Errors).Code;
    }

    private static IReadOnlyList<DeclarationFinding> BuildRefusals(
        Func<StorageDeclarationBuilder, StorageDeclarationBuilder> declare)
    {
        var exception = Assert.Throws<DeclarationBuildException>(() =>
            declare(Groundwork.Kernel.StorageUnit.Declare("telemetry", "telemetry")).Build());
        return exception.Findings;
    }

    private static StorageUnit Unit(ColumnDefinition[] columns, string[] key) => new()
    {
        Id = new StorageUnitId("telemetry"),
        Name = "telemetry",
        Columns = columns,
        Key = new KeyDefinition { Columns = key }
    };

    private static ColumnDefinition Column(
        string name,
        PortableType type,
        bool nullable = true,
        int? maxLength = null,
        PortableCollation? collation = null) => new()
        {
            Name = name,
            Type = type,
            IsNullable = nullable,
            MaxLength = maxLength,
            Collation = collation
        };
}
