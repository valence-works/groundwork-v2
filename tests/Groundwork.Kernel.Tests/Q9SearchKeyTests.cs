using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using System.Text.Json.Nodes;
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

    [Fact]
    public void Ordinal_identity_expansion_declares_a_provider_owned_persisted_projection()
    {
        var logical = StorageUnit.Declare("people", "people")
            .Int32("id", column => column.Required())
            .String("name", 32, column => column.Required().OrdinalIdentity("__groundwork_ordinal_name"))
            .Key("id")
            .Build();

        var physical = SearchKeyProjection.Expand(logical);
        var identity = Assert.Single(physical.Columns, column => column.Name == "__groundwork_ordinal_name");
        Assert.Equal(PortableType.String, identity.Type);
        Assert.False(identity.IsNullable);
        Assert.Equal(128, identity.MaxLength);
        Assert.Equal(PortableCollation.Ordinal, identity.Collation);
        var derived = Assert.Single(physical.DerivedColumns);
        Assert.Equal("name", derived.SourceColumn);
        Assert.Equal(PortableProjection.OrdinalIdentity, derived.Projection);
        Assert.Equal(PortableStringComparison.OrdinalAlgorithmId, derived.AlgorithmId);
    }

    [Fact]
    public void Ordinal_identity_singleton_indexes_retain_the_logical_source_as_a_cover()
    {
        var logical = StorageUnit.Declare("people", "people")
            .String("name", 512, column => column.Required().OrdinalIdentity("__groundwork_ordinal_name"))
            .Key("name")
            .Index("by_name", index => index.UseOrdinalIdentities().Column("name"))
            .Build();

        var physical = SearchKeyProjection.Expand(logical);
        var index = Assert.Single(physical.Indexes);

        Assert.Equal(["__groundwork_ordinal_name"], index.Columns.Select(column => column.Column));
        Assert.Equal(["name"], index.IncludedColumns);
    }

    [Fact]
    public void Ordinal_identity_composite_indexes_do_not_duplicate_the_bounded_logical_source()
    {
        var logical = StorageUnit.Declare("events", "events")
            .String("scope", 64, column => column.Required())
            .String("executionId", 512, column => column.Required().OrdinalIdentity("__groundwork_ordinal_executionId"))
            .String("order", 64, column => column.Required())
            .String("tie", 64, column => column.Required())
            .Key("scope", "executionId")
            .Index("by_execution", "scope", "executionId", "order", "tie")
            .Build();

        var physical = SearchKeyProjection.Expand(logical);
        var index = Assert.Single(physical.Indexes);

        Assert.Equal(["scope", "executionId", "order", "tie"], index.Columns.Select(column => column.Column));
    }

    [Fact]
    public void Ordinal_identity_requires_its_dedicated_provider_owned_namespace()
    {
        var logical = StorageUnit.Declare("people", "people")
            .Int32("id", column => column.Required())
            .String("name", 32, column => column.Required().OrdinalIdentity("__groundwork_search_name"))
            .Key("id")
            .Build();

        var failure = Assert.Throws<InvalidOperationException>(() => SearchKeyProjection.Expand(logical));

        Assert.Contains(SearchKeyProjection.OrdinalIdentityPrefix, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ordinal_identity_writes_are_derived_and_provider_owned()
    {
        var logical = StorageUnit.Declare("people", "people")
            .Int32("id", column => column.Required())
            .String("name", 32, column => column.Required().OrdinalIdentity("__groundwork_ordinal_name"))
            .Key("id")
            .Build();
        var physical = SearchKeyProjection.Expand(logical);

        var values = SearchKeyProjection.Populate(physical, new Dictionary<string, object?>
        {
            ["id"] = 1,
            ["name"] = "Ada"
        });
        Assert.Equal(PortableStringComparison.CreateOrdinal("Ada"), values["__groundwork_ordinal_name"]);

        Assert.Throws<ArgumentException>(() => SearchKeyProjection.Populate(physical, new Dictionary<string, object?>
        {
            ["id"] = 1,
            ["name"] = "Ada",
            ["__groundwork_ordinal_name"] = "spoofed"
        }));
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

    [Fact]
    public void Element_search_key_expansion_preserves_positions_and_unicode_ordinal_keys()
    {
        var physical = SearchKeyProjection.Expand(ElementUnit());

        var key = Assert.Single(physical.Columns, column => column.Name == "__groundwork_search_workflowIds");
        Assert.Equal(PortableType.Json, key.Type);
        Assert.True(key.IsNullable);
        var derived = Assert.Single(physical.DerivedColumns);
        Assert.Equal(PortableProjection.ElementBoundarySearchKey, derived.Projection);
        Assert.Equal(
            "groundwork-element-search-key-array-v1+" +
            "max-450+" +
            PortableStringComparison.GetSearchKeyAlgorithmId(PortableStringComparisonPolicy.UnicodeOrdinalIgnoreCase),
            derived.AlgorithmId);

        var values = SearchKeyProjection.Populate(
            physical,
            new Dictionary<string, object?>
            {
                ["id"] = 1,
                ["workflowIds"] = new object?[] { "Örn", 42, null, "WORK" }
            });

        var document = Assert.IsAssignableFrom<IReadOnlyList<string?>>(values["__groundwork_search_workflowIds"]);
        Assert.Equal(
            [
                PortableStringComparison.CreateSearchKey("Örn", PortableStringComparisonPolicy.UnicodeOrdinalIgnoreCase),
                null,
                null,
                PortableStringComparison.CreateSearchKey("WORK", PortableStringComparisonPolicy.UnicodeOrdinalIgnoreCase)
            ],
            document);
    }

    [Fact]
    public void Element_search_key_population_supports_an_unexpanded_logical_declaration()
    {
        var values = SearchKeyProjection.Populate(
            ElementUnit(),
            new Dictionary<string, object?>
            {
                ["id"] = 1,
                ["workflowIds"] = new[] { "Örn" }
            });

        var document = Assert.IsAssignableFrom<IReadOnlyList<string?>>(values["__groundwork_search_workflowIds"]);
        Assert.Equal(
            PortableStringComparison.CreateSearchKey("Örn", PortableStringComparisonPolicy.UnicodeOrdinalIgnoreCase),
            document[0]);
    }

    [Fact]
    public void Element_search_key_refuses_ill_formed_string_values_instead_of_silently_dropping_them()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => SearchKeyProjection.Populate(
            SearchKeyProjection.Expand(ElementUnit()),
            new Dictionary<string, object?>
            {
                ["id"] = 1,
                ["workflowIds"] = new[] { "\uD800" }
            }));

        Assert.Contains("ill-formed UTF-16", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Element_search_key_refuses_values_over_the_declared_element_bound()
    {
        var unit = SearchKeyProjection.Expand(new StorageUnit
        {
            Id = new StorageUnitId("bounded-elements"),
            Name = "bounded_elements",
            Columns =
            [
                new ColumnDefinition { Name = "id", Type = PortableType.Int32, IsNullable = false },
                new ColumnDefinition
                {
                    Name = "workflowIds",
                    Type = PortableType.Json,
                    ElementSearchKey = new ElementSearchKeyDefinition
                    {
                        Collation = PortableCollation.UnicodeOrdinalIgnoreCase,
                        MaximumElementCodeUnits = 2
                    }
                }
            ],
            Key = new KeyDefinition { Columns = ["id"] }
        });

        var failure = Assert.Throws<InvalidOperationException>(() => SearchKeyProjection.Populate(
            unit,
            new Dictionary<string, object?> { ["id"] = 1, ["workflowIds"] = new[] { "long" } }));

        Assert.Contains("MaximumElementCodeUnits", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Element_search_key_reads_json_nodes_without_losing_string_elements()
    {
        var values = SearchKeyProjection.Populate(
            SearchKeyProjection.Expand(ElementUnit()),
            new Dictionary<string, object?>
            {
                ["id"] = 1,
                ["workflowIds"] = new JsonArray("Örn", 42, null)
            });

        var key = Assert.IsAssignableFrom<IReadOnlyList<string?>>(values["__groundwork_search_workflowIds"]);
        Assert.Equal(
            PortableStringComparison.CreateSearchKey("Örn", PortableStringComparisonPolicy.UnicodeOrdinalIgnoreCase),
            key[0]);
        Assert.Null(key[1]);
        Assert.Null(key[2]);
    }

    [Fact]
    public void Element_search_key_treats_a_dictionary_as_malformed_json_instead_of_an_array()
    {
        var values = SearchKeyProjection.Populate(
            SearchKeyProjection.Expand(ElementUnit()),
            new Dictionary<string, object?>
            {
                ["id"] = 1,
                ["workflowIds"] = new Dictionary<string, object?> { ["value"] = "WORK" }
            });

        Assert.Null(values["__groundwork_search_workflowIds"]);
    }

    [Fact]
    public void Element_search_key_column_is_nullable_when_a_non_nullable_owner_is_malformed()
    {
        var logical = ElementUnit() with
        {
            Columns = [.. ElementUnit().Columns.Select(column => column.Name == "workflowIds"
                ? column with { IsNullable = false }
                : column)]
        };
        var physical = SearchKeyProjection.Expand(logical);

        Assert.True(physical.Columns.Single(column => column.Name == "__groundwork_search_workflowIds").IsNullable);
        var values = SearchKeyProjection.Populate(
            physical,
            new Dictionary<string, object?>
            {
                ["id"] = 1,
                ["workflowIds"] = new Dictionary<string, object?> { ["value"] = "WORK" }
            });

        Assert.Null(values["__groundwork_search_workflowIds"]);
    }

    [Fact]
    public void Adding_an_element_search_key_plans_add_then_authorized_derived_backfill()
    {
        var logical = ElementUnit() with
        {
            Columns = [.. ElementUnit().Columns.Select(column => column with { ElementSearchKey = null })]
        };
        var desired = SearchKeyProjection.Expand(ElementUnit());
        var initial = AppliedState(logical);
        var plan = PhysicalSchemaDiffPlanner.Plan(
            new PhysicalSchemaTarget(new SchemaSubject(desired), new ProviderIdentity("test", "1.0")),
            PhysicalSchemaHistoryState.FromApplied(initial),
            DateTimeOffset.UnixEpoch);

        var operations = plan.Operations.ToArray();
        var add = Assert.Single(operations.OfType<AddColumnOperation>(), operation =>
            operation.Column.Name == "__groundwork_search_workflowIds");
        var backfill = Assert.Single(operations.OfType<BackfillColumnOperation>(), operation =>
            operation.Derived?.Projection == PortableProjection.ElementBoundarySearchKey);
        Assert.True(backfill.RequiresAuthorization);
        Assert.True(Array.IndexOf(operations, add) < Array.IndexOf(operations, backfill));
    }

    [Fact]
    public void Changing_an_element_search_key_algorithm_plans_an_authorized_derived_backfill()
    {
        var initialUnit = SearchKeyProjection.Expand(ElementUnit());
        var initial = AppliedState(initialUnit);
        var changedLogical = ElementUnit() with
        {
            Columns = [.. ElementUnit().Columns.Select(column => column.Name == "workflowIds"
                ? column with
                {
                    ElementSearchKey = new ElementSearchKeyDefinition
                    {
                        Collation = PortableCollation.OrdinalIgnoreCase,
                        MaximumElementCodeUnits = 450
                    }
                }
                : column)]
        };
        var plan = PhysicalSchemaDiffPlanner.Plan(
            new PhysicalSchemaTarget(
                new SchemaSubject(SearchKeyProjection.Expand(changedLogical)),
                new ProviderIdentity("test", "1.0")),
            PhysicalSchemaHistoryState.FromApplied(initial),
            DateTimeOffset.UnixEpoch);

        var backfill = Assert.Single(plan.Operations.OfType<BackfillColumnOperation>(), operation =>
            operation.Derived?.Projection == PortableProjection.ElementBoundarySearchKey);
        Assert.True(plan.IsApplicable, string.Join("; ", plan.Refusals.Select(refusal => refusal.Message)));
        Assert.True(backfill.RequiresAuthorization);
        Assert.NotEqual(
            initialUnit.DerivedColumns.Single().AlgorithmId,
            backfill.Derived!.AlgorithmId);
    }

    [Fact]
    public void Changing_an_element_bound_plans_an_authorized_validation_backfill()
    {
        var initial = AppliedState(SearchKeyProjection.Expand(ElementUnit()));
        var changedLogical = ElementUnit() with
        {
            Columns = [.. ElementUnit().Columns.Select(column => column.Name == "workflowIds"
                ? column with
                {
                    ElementSearchKey = column.ElementSearchKey! with { MaximumElementCodeUnits = 225 }
                }
                : column)]
        };
        var plan = PhysicalSchemaDiffPlanner.Plan(
            new PhysicalSchemaTarget(
                new SchemaSubject(SearchKeyProjection.Expand(changedLogical)),
                new ProviderIdentity("test", "1.0")),
            PhysicalSchemaHistoryState.FromApplied(initial),
            DateTimeOffset.UnixEpoch);

        var backfill = Assert.Single(plan.Operations.OfType<BackfillColumnOperation>(), operation =>
            operation.Derived?.Projection == PortableProjection.ElementBoundarySearchKey);
        Assert.True(backfill.RequiresAuthorization);
        Assert.Contains("max-225", backfill.Derived!.AlgorithmId, StringComparison.Ordinal);
    }

    [Fact]
    public void Applied_state_omits_legacy_null_element_keys_and_round_trips_declared_keys()
    {
        var legacyUnit = ElementUnit() with
        {
            Columns = [.. ElementUnit().Columns.Select(column => column with { ElementSearchKey = null })]
        };
        var legacyJson = PhysicalSchemaAppliedStateSerializer.Serialize(AppliedState(legacyUnit));

        Assert.DoesNotContain("elementSearchKey", legacyJson, StringComparison.Ordinal);
        Assert.Equal(legacyJson, PhysicalSchemaAppliedStateSerializer.Serialize(
            PhysicalSchemaAppliedStateSerializer.Deserialize(legacyJson)));

        var declaredJson = PhysicalSchemaAppliedStateSerializer.Serialize(AppliedState(ElementUnit()));
        Assert.Contains("elementSearchKey", declaredJson, StringComparison.Ordinal);
        var restored = PhysicalSchemaAppliedStateSerializer.Deserialize(declaredJson);
        Assert.Equal(
            PortableCollation.UnicodeOrdinalIgnoreCase,
            restored.Snapshot.Subject.Columns.Single(column => column.Name == "workflowIds").ElementSearchKey!.Collation);
    }

    private static PhysicalSchemaAppliedState AppliedState(StorageUnit unit)
    {
        var target = new PhysicalSchemaTarget(new SchemaSubject(unit), new ProviderIdentity("test", "1.0"));
        var plan = PhysicalSchemaDiffPlanner.Plan(target, PhysicalSchemaHistoryState.Empty, DateTimeOffset.UnixEpoch);
        return plan.Complete(
            plan.Operations
                .Where(operation => operation.Kind != PhysicalSchemaOperationKind.PublishAppliedState)
                .Select(operation => new PhysicalSchemaOperationAcknowledgement(
                    operation.Identity,
                    operation.Fingerprint,
                    DateTimeOffset.UnixEpoch))
                .ToArray(),
            DateTimeOffset.UnixEpoch);
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

    private static StorageUnit ElementUnit() => new()
    {
        Id = new StorageUnitId("workflows"),
        Name = "workflows",
        Columns =
        [
            new ColumnDefinition { Name = "id", Type = PortableType.Int32, IsNullable = false },
            new ColumnDefinition
            {
                Name = "workflowIds",
                Type = PortableType.Json,
                ElementSearchKey = new ElementSearchKeyDefinition
                {
                    Collation = PortableCollation.UnicodeOrdinalIgnoreCase,
                    MaximumElementCodeUnits = 450
                }
            }
        ],
        Key = new KeyDefinition { Columns = ["id"] }
    };
}
