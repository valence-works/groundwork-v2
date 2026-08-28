using Groundwork.Kernel;
using Xunit;

namespace Groundwork.Kernel.Tests;

public sealed class Q9SearchKeyTests
{
    [Fact]
    public void Locale_sort_key_expansion_reuses_the_provider_owned_projection_and_index_retarget()
    {
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("people"),
            Name = "people",
            Columns =
            [
                new ColumnDefinition { Name = "id", Type = PortableType.Int32, IsNullable = false },
                new ColumnDefinition
                {
                    Name = "name",
                    Type = PortableType.String,
                    MaxLength = 32,
                    LocaleSortKey = new LocaleSortKeyDefinition
                    {
                        CultureName = "sv-SE",
                        MaximumExpansionFactor = 12
                    }
                }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Indexes = [new IndexDefinition { Name = "ix_name", Columns = [new IndexColumn("name")] }]
        };

        var physical = SearchKeyProjection.Expand(unit);

        var sortKey = Assert.Single(physical.Columns, column => column.Name == "__groundwork_search_name");
        Assert.Equal(384, sortKey.MaxLength);
        Assert.Equal(PortableCollation.Ordinal, sortKey.Collation);
        Assert.Equal("__groundwork_search_name", physical.Indexes.Single().Columns.Single().Column);
        var derived = Assert.Single(physical.DerivedColumns);
        Assert.Equal(PortableProjection.LocaleSortKey, derived.Projection);
        Assert.Contains("sv-SE", derived.AlgorithmId, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sv-SE", new[] { "Ake", "Zebra", "Åke", "Äke", "Öke" })]
    [InlineData("de-DE-u-co-phonebk", new[] { "Äke", "Ake", "Åke", "Öke", "Zebra" })]
    public void Persisted_locale_sort_keys_have_literal_non_ordinal_locale_order(
        string cultureName,
        string[] expected)
    {
        var unit = SearchKeyProjection.Expand(LocaleUnit(cultureName));

        var actual = new[] { "Ake", "Åke", "Äke", "Öke", "Zebra" }
            .Select(value => new
            {
                Value = value,
                Key = Assert.IsType<string>(SearchKeyProjection.Populate(
                    unit,
                    new Dictionary<string, object?> { ["id"] = 1, ["name"] = value })["__groundwork_search_name"])
            })
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => item.Value)
            .ToArray();

        Assert.Equal(expected, actual);
        Assert.All(new[] { "Ake", "Åke", "Äke", "Öke", "Zebra" }, value =>
            Assert.IsType<string>(SearchKeyProjection.Populate(
                unit,
                new Dictionary<string, object?> { ["id"] = 1, ["name"] = value })["__groundwork_search_name"]));
    }

    [Theory]
    [InlineData(true, false, false, "InvariantGlobalization=true")]
    [InlineData(false, true, true, "System.Globalization.UseNls=true")]
    public void Locale_sort_key_declarations_refuse_non_ICU_runtime_configuration(
        bool invariant,
        bool windows,
        bool nls,
        string expectedReason)
    {
        var refusal = PortableLocaleOrdering.ValidateRuntimeConfiguration(
            invariant,
            windows,
            nls,
            "sv-SE",
            "columns.name.localeSortKey");

        Assert.NotNull(refusal);
        Assert.Equal("GW-PORT-014", refusal.Code);
        Assert.Contains(expectedReason, refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Fluent_locale_order_requires_an_enforceable_bound()
    {
        var failure = Assert.Throws<DeclarationBuildException>(() => StorageUnit
            .Declare("people", "people")
            .Int32("id", column => column.Required())
            .String("name", 32, column => column.LocaleOrder("sv-SE", 0))
            .Key("id")
            .Build());

        Assert.Contains(failure.Findings, finding => finding.Code == "GW-PORT-014" &&
            finding.Path == "columns.name.localeSortKey");
    }

    [Fact]
    public void Writes_refuse_a_locale_key_that_exceeds_the_declared_expansion_bound()
    {
        var unit = SearchKeyProjection.Expand(new StorageUnit
        {
            Id = new StorageUnitId("bounded-locale"),
            Name = "bounded_locale",
            Columns =
            [
                new ColumnDefinition { Name = "id", Type = PortableType.Int32, IsNullable = false },
                new ColumnDefinition
                {
                    Name = "name",
                    Type = PortableType.String,
                    MaxLength = 1,
                    LocaleSortKey = new LocaleSortKeyDefinition
                    {
                        CultureName = "sv-SE",
                        MaximumExpansionFactor = 1
                    }
                }
            ],
            Key = new KeyDefinition { Columns = ["id"] }
        });

        var failure = Assert.Throws<InvalidOperationException>(() => SearchKeyProjection.Populate(
            unit,
            new Dictionary<string, object?> { ["id"] = 1, ["name"] = "ä" }));

        Assert.Contains("MaximumExpansionFactor", failure.Message, StringComparison.Ordinal);
        Assert.Contains("rebuild", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Locale_sort_keys_refuse_malformed_UTF16_before_ICU()
    {
        var failure = Assert.Throws<ArgumentException>(() =>
            PortableLocaleOrdering.CreateSortKey("\uD800", "sv-SE"));

        Assert.Contains("well-formed UTF-16", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Folded_physical_expansion_adds_one_key_and_retargets_logical_indexes()
    {
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("tickets"),
            Name = "tickets",
            Columns =
            [
                new ColumnDefinition { Name = "id", Type = PortableType.Int32, IsNullable = false },
                new ColumnDefinition { Name = "status", Type = PortableType.String, MaxLength = 32, Collation = PortableCollation.OrdinalIgnoreCase },
                new ColumnDefinition { Name = "name", Type = PortableType.String, MaxLength = 32, Collation = PortableCollation.Ordinal }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Indexes = [new IndexDefinition { Name = "ix_status", Columns = [new IndexColumn("status")] }]
        };

        var physical = SearchKeyProjection.Expand(unit);

        Assert.Equal(["id", "status", "name", "__groundwork_search_status"], physical.Columns.Select(column => column.Name));
        Assert.DoesNotContain(physical.Columns, column => column.Name == "__groundwork_search_name");
        Assert.Equal("__groundwork_search_status", physical.Indexes.Single().Columns.Single().Column);
        var derived = Assert.Single(physical.DerivedColumns);
        Assert.Equal("status", derived.SourceColumn);
        Assert.Equal(PortableStringComparison.GetSearchKeyAlgorithmId(PortableStringComparisonPolicy.AsciiIgnoreCase), derived.AlgorithmId);
        Assert.Equal(PortableCollation.Ordinal, physical.Columns.Single(column => column.Name == "status").Collation);
        Assert.Equal(PortableCollation.OrdinalIgnoreCase, physical.Columns.Single(column => column.Name == "status").LogicalCollation);
    }

    [Theory]
    [InlineData("A", "|0061")]
    [InlineData("Turkish I", "|0074|0075|0072|006B|0069|0073|0068|0020|0069")]
    public void Ascii_fold_keys_are_boundary_delimited(string value, string expected)
    {
        Assert.Equal(expected, PortableStringComparison.CreateSearchKey(value, PortableStringComparisonPolicy.AsciiIgnoreCase));
    }

    [Fact]
    public void Search_key_successor_carries_hex_units_and_refuses_maximum_boundary()
    {
        Assert.Equal("|0042", PortableStringComparison.CreateSearchKeySuccessor("|0041"));
        Assert.Equal("|0050", PortableStringComparison.CreateSearchKeySuccessor("|004F"));
        Assert.Null(PortableStringComparison.CreateSearchKeySuccessor("|FFFF"));
    }

    [Fact]
    public void Populate_computes_hidden_key_and_rejects_provider_owned_input()
    {
        var unit = SearchKeyProjection.Expand(new StorageUnit
        {
            Id = new StorageUnitId("tickets"),
            Name = "tickets",
            Columns = [new ColumnDefinition { Name = "id", Type = PortableType.Int32, IsNullable = false },
                new ColumnDefinition { Name = "status", Type = PortableType.String, MaxLength = 32, Collation = PortableCollation.OrdinalIgnoreCase }],
            Key = new KeyDefinition { Columns = ["id"] }
        });

        var values = SearchKeyProjection.Populate(unit, new Dictionary<string, object?>
        {
            ["id"] = 1,
            ["status"] = "OPEN"
        });

        Assert.Equal("|006F|0070|0065|006E", values["__groundwork_search_status"]);
        Assert.DoesNotContain("__groundwork_search_status", SearchKeyProjection.Populate(unit, new Dictionary<string, object?>
        {
            ["id"] = 1
        }).Keys);
        Assert.Throws<ArgumentException>(() => SearchKeyProjection.Populate(unit, new Dictionary<string, object?>
        {
            ["id"] = 1,
            ["status"] = "OPEN",
            ["__groundwork_search_status"] = "spoofed"
        }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("stale-search-key-v0")]
    [InlineData("prefix-groundwork-ascii-lower-v1-suffix")]
    public void Populate_refuses_unknown_or_malformed_search_key_algorithm_ids(string? algorithmId)
    {
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("malformed-search-key"),
            Name = "malformed_search_key",
            Columns =
            [
                new ColumnDefinition { Name = "id", Type = PortableType.Int32, IsNullable = false },
                new ColumnDefinition { Name = "status", Type = PortableType.String },
                new ColumnDefinition { Name = "__groundwork_search_status", Type = PortableType.String }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            DerivedColumns =
            [
                new DerivedColumnDefinition
                {
                    Name = "__groundwork_search_status",
                    SourceColumn = "status",
                    Projection = PortableProjection.BoundarySearchKey,
                    AlgorithmId = algorithmId
                }
            ]
        };

        var failure = Assert.Throws<InvalidOperationException>(() => SearchKeyProjection.Populate(
            unit,
            new Dictionary<string, object?> { ["id"] = 1, ["status"] = "Open" }));

        Assert.Contains("algorithm", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rebuild", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static StorageUnit LocaleUnit(string cultureName) => new()
    {
        Id = new StorageUnitId("people"),
        Name = "people",
        Columns =
        [
            new ColumnDefinition { Name = "id", Type = PortableType.Int32, IsNullable = false },
            new ColumnDefinition
            {
                Name = "name",
                Type = PortableType.String,
                MaxLength = 32,
                LocaleSortKey = new LocaleSortKeyDefinition
                {
                    CultureName = cultureName,
                    MaximumExpansionFactor = 12
                }
            }
        ],
        Key = new KeyDefinition { Columns = ["id"] }
    };
}
