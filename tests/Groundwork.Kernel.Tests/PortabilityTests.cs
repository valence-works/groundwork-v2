using Groundwork.Kernel;
using Xunit;

namespace Groundwork.Kernel.Tests;

public sealed class PortabilityTests
{
    [Fact]
    public void Decimal_without_precision_and_scale_is_refused()
    {
        var result = Validate(Unit(Column("amount", PortableType.Decimal)));

        AssertCode(result, "GW-PORT-002", "amount");
    }

    [Fact]
    public void Bounded_string_and_binary_keys_are_required()
    {
        var result = Validate(Unit([
            Column("name", PortableType.String),
            Column("payload", PortableType.Binary)
        ],
            indexes: [Index("by-values", "name", "payload")]));

        Assert.Contains(result.Refusals, diagnostic =>
            diagnostic.Code == "GW-PORT-003" && diagnostic.Message.Contains("name", StringComparison.Ordinal));
        Assert.Contains(result.Refusals, diagnostic =>
            diagnostic.Code == "GW-PORT-003" && diagnostic.Message.Contains("payload", StringComparison.Ordinal));
    }

    [Fact]
    public void Index_key_budget_is_exactly_1700_bytes()
    {
        var atLimit = Validate(Unit([
            Column("value", PortableType.String, maxLength: 850)
        ],
            indexes: [Index("by-value", "value")]));
        var overLimit = Validate(Unit([
            Column("value", PortableType.String, maxLength: 851)
        ],
            indexes: [Index("by-value", "value")]));

        Assert.DoesNotContain(atLimit.Refusals, diagnostic => diagnostic.Code == "GW-PORT-004");
        var diagnostic = Assert.Single(overLimit.Refusals, item => item.Code == "GW-PORT-004");
        Assert.Contains("1702", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("1700", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("by-value", diagnostic.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(9, 5)]
    [InlineData(10, 9)]
    [InlineData(19, 9)]
    [InlineData(20, 13)]
    [InlineData(28, 13)]
    [InlineData(29, 17)]
    [InlineData(38, 17)]
    public void Decimal_precision_ranges_use_pinned_sql_server_key_widths(int precision, int expectedBytes)
    {
        var maxLengthAtLimit = (1699 - expectedBytes) / 2;
        var atLimit = Validate(Unit([
            Column("amount", PortableType.Decimal, nullable: false, precision: precision, scale: 0),
            Column("padding", PortableType.String, nullable: false, maxLength: maxLengthAtLimit)
        ], indexes: [Index("by-amount", "amount", "padding")]));
        var overLimit = Validate(Unit([
            Column("amount", PortableType.Decimal, nullable: false, precision: precision, scale: 0),
            Column("padding", PortableType.String, nullable: false, maxLength: maxLengthAtLimit + 1)
        ], indexes: [Index("by-amount", "amount", "padding")]));

        Assert.DoesNotContain(atLimit.Refusals, refusal => refusal.Code == "GW-PORT-004");
        Assert.Contains(overLimit.Refusals, refusal => refusal.Code == "GW-PORT-004" &&
            refusal.Message.Contains((1701).ToString(), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(39)]
    public void Decimal_precision_outside_calculable_range_fails_closed(int precision)
    {
        var result = Validate(Unit([
            Column("amount", PortableType.Decimal, nullable: false, precision: precision, scale: 0)
        ], indexes: [Index("by-amount", "amount")]));

        var diagnostic = Assert.Single(result.Refusals, refusal => refusal.Code == "GW-PORT-004");
        Assert.Contains("amount", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains($"precision {precision}", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("supported range 1-38", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_decimal_precision_is_reported_after_an_unbounded_key_term()
    {
        var result = Validate(Unit([
            Column("name", PortableType.String, nullable: false),
            Column("amount", PortableType.Decimal, nullable: false, precision: 39, scale: 0)
        ], indexes: [Index("ix-name-amount", "name", "amount")]));

        Assert.Contains(result.Refusals, refusal => refusal.Code == "GW-PORT-003" &&
            refusal.Message.Contains("name", StringComparison.Ordinal));
        var diagnostic = Assert.Single(result.Refusals, refusal => refusal.Code == "GW-PORT-004");
        Assert.Contains("amount", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("precision 39", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unique_nullable_included_index_is_refused()
    {
        var result = Validate(Unit([
            Column("email", PortableType.String, maxLength: 320)
        ],
            indexes: [Index("ux-email", "email", unique: true)]));

        AssertCode(result, "GW-PORT-001", "ux-email");
        Assert.Contains(result.Refusals, refusal => refusal.Code == "GW-PORT-001" && refusal.Message.Contains("email", StringComparison.Ordinal));
    }

    [Fact]
    public void Unique_nullable_compound_index_is_admitted_when_uniqueness_is_implied()
    {
        var result = Validate(Unit([
            Column("id", PortableType.Guid, nullable: false),
            Column("email", PortableType.String, maxLength: 320)
        ],
            indexes:
            [
                Index("ux-id", "id", unique: true),
                Index("ux-id-email", "id", "email", unique: true)
            ]));

        Assert.DoesNotContain(result.Refusals, diagnostic => diagnostic.Code == "GW-PORT-001");
    }

    [Fact]
    public void Nullable_included_unique_subset_exempts_the_larger_compound_index()
    {
        var result = Validate(Unit([
            Column("tenant", PortableType.String, maxLength: 64),
            Column("email", PortableType.String, maxLength: 320)
        ],
            indexes:
            [
                Index("ux-tenant", "tenant", unique: true),
                Index("ux-tenant-email", "tenant", "email", unique: true)
            ]));

        var diagnostic = Assert.Single(result.Refusals, refusal => refusal.Code == "GW-PORT-001");
        Assert.Equal("indexes.ux-tenant", diagnostic.Path);
    }

    [Fact]
    public void Duplicate_physical_index_signatures_are_refused_with_both_logical_names()
    {
        var result = Validate(Unit([
            Column("id", PortableType.Guid, nullable: false),
            Column("tenant", PortableType.String, nullable: false, maxLength: 64)
        ],
            indexes:
            [
                Index("by-tenant-primary", "tenant", unique: true),
                Index("by-tenant-alias", "tenant", unique: true)
            ],
            key: ["id"]));

        var refusal = Assert.Single(result.Refusals, item => item.Code == "GW-PORT-009");
        Assert.Equal("indexes.by-tenant-primary|by-tenant-alias", refusal.Path);
        Assert.Contains("by-tenant-primary", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("by-tenant-alias", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("consolidate", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Physical_index_signature_distinguishes_order_direction_uniqueness_and_missing_values()
    {
        var unit = Unit([
            Column("id", PortableType.Guid, nullable: false),
            Column("tenant", PortableType.String, nullable: true, maxLength: 64)
        ],
            indexes:
            [
                Index("by-tenant-ascending", "tenant", unique: false),
                new IndexDefinition
                {
                    Name = "by-tenant-descending",
                    Columns = [new IndexColumn("tenant", SortDirection.Descending)]
                },
                new IndexDefinition
                {
                    Name = "by-tenant-sparse",
                    Columns = [new IndexColumn("tenant")],
                    MissingValues = MissingValueBehavior.Excluded
                },
                Index("by-tenant-unique", "tenant", unique: true)
            ],
            key: ["id"]);

        Assert.DoesNotContain(Validate(unit).Refusals, item => item.Code == "GW-PORT-009");
    }

    [Fact]
    public void Fluent_declaration_refuses_duplicate_physical_index_signatures()
    {
        var exception = Assert.Throws<DeclarationBuildException>(() => Groundwork.Kernel.StorageUnit
            .Declare("duplicates", "duplicates")
            .Guid("id", column => column.Required())
            .String("tenant", 64, column => column.Required())
            .Key("id")
            .UniqueIndex("by-tenant-primary", index => index.Column("tenant"))
            .UniqueIndex("by-tenant-alias", index => index.Column("tenant"))
            .Build());

        Assert.Contains(exception.Findings, item => item.Code == "GW-PORT-009" &&
            item.Path == "indexes.by-tenant-primary|by-tenant-alias");
    }

    [Fact]
    public void Schema_subject_refuses_duplicate_physical_index_signatures_before_fingerprinting()
    {
        var unit = Unit([
            Column("id", PortableType.Guid, nullable: false),
            Column("tenant", PortableType.String, nullable: false, maxLength: 64)
        ],
            indexes:
            [
                Index("by-tenant-primary", "tenant", unique: true),
                Index("by-tenant-alias", "tenant", unique: true)
            ],
            key: ["id"]);

        var exception = Assert.Throws<ArgumentException>(() => new Groundwork.Kernel.Schema.SchemaSubject(unit));

        Assert.Contains("GW-PORT-009", exception.Message, StringComparison.Ordinal);
        Assert.Contains("by-tenant-primary", exception.Message, StringComparison.Ordinal);
        Assert.Contains("by-tenant-alias", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Dotted_physical_column_name_is_refused_at_the_fluent_boundary()
    {
        var exception = Assert.Throws<DeclarationBuildException>(() => Groundwork.Kernel.StorageUnit
            .Declare("invalid", "invalid")
            .String("state.interruptedExecution.status", 200)
            .Key("state.interruptedExecution.status")
            .Build());

        Assert.Contains(exception.Findings, finding => finding.Code == "GW-PORT-010" &&
            finding.Path == "columns.state.interruptedExecution.status" &&
            finding.Message.Contains("ASCII letters", StringComparison.Ordinal));
    }

    [Fact]
    public void Physical_identifiers_are_capped_at_63_ascii_bytes()
    {
        var atLimit = Validate(Unit([
            Column("id", PortableType.Guid, nullable: false),
            Column(new string('a', 63), PortableType.String)
        ], key: ["id"]));
        var overLimit = Validate(Unit([
            Column("id", PortableType.Guid, nullable: false),
            Column(new string('b', 64), PortableType.String)
        ], key: ["id"]));

        Assert.DoesNotContain(atLimit.Refusals, refusal => refusal.Code == "GW-PORT-010");
        var diagnostic = Assert.Single(overLimit.Refusals, refusal => refusal.Code == "GW-PORT-010");
        Assert.Contains("at most 63 ASCII bytes", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("shorter", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Overlong_identifiers_are_refused_at_builder_and_schema_subject_boundaries()
    {
        var name = new string('x', 64);
        var builderException = Assert.Throws<DeclarationBuildException>(() => Groundwork.Kernel.StorageUnit
            .Declare("overlong", "overlong")
            .String(name, 64)
            .Key(name)
            .Build());
        var schemaException = Assert.Throws<ArgumentException>(() => new Groundwork.Kernel.Schema.SchemaSubject(
            Unit([Column(name, PortableType.String, maxLength: 64)], key: [name])));

        Assert.Contains(builderException.Findings, finding => finding.Code == "GW-PORT-010" &&
            finding.Message.Contains("at most 63 ASCII bytes", StringComparison.Ordinal));
        Assert.Contains("GW-PORT-010", schemaException.Message, StringComparison.Ordinal);
        Assert.Contains("at most 63 ASCII bytes", schemaException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Search_key_expansion_refuses_a_hidden_name_over_63_bytes_before_provider_io()
    {
        var sourceName = new string('s', 63 - SearchKeyProjection.Prefix.Length + 1);
        var unit = Unit([
            Column(sourceName, PortableType.String, maxLength: 64, collation: PortableCollation.OrdinalIgnoreCase)
        ]);
        var expanded = SearchKeyProjection.Expand(unit);
        var result = Validate(unit);
        var builderException = Assert.Throws<DeclarationBuildException>(() => Groundwork.Kernel.StorageUnit
            .Declare("overlong-search-key", "overlong-search-key")
            .String(sourceName, 64, column => column.Collation(PortableCollation.OrdinalIgnoreCase))
            .Key(sourceName)
            .Build());
        var schemaException = Assert.Throws<ArgumentException>(() => new Groundwork.Kernel.Schema.SchemaSubject(unit));

        var hiddenName = SearchKeyProjection.ColumnName(sourceName);
        Assert.True(expanded.Columns.Single(column => column.Name == hiddenName).Name.Length > 63);
        var diagnostic = Assert.Single(result.Refusals, refusal => refusal.Code == "GW-PORT-010");
        Assert.Equal($"derivedColumns.{hiddenName}.name", diagnostic.Path);
        Assert.Contains(hiddenName, diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("at most 63 ASCII bytes", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(builderException.Findings, finding => finding.Code == "GW-PORT-010" &&
            finding.Path == $"derivedColumns.{hiddenName}.name");
        Assert.Contains("GW-PORT-010", schemaException.Message, StringComparison.Ordinal);
        Assert.Contains(hiddenName, schemaException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Forged_physical_references_are_refused_independently_of_the_builder()
    {
        var cases = new Dictionary<string, StorageUnit>(StringComparer.Ordinal)
        {
            ["columns"] = Unit(
                [Column("state.status", PortableType.String, nullable: false, maxLength: 64)],
                key: ["state.status"]),
            ["key"] = Unit(
                [Column("id", PortableType.Guid, nullable: false)],
                key: ["id.dot"]),
            ["indexes"] = Unit(
                [Column("id", PortableType.Guid, nullable: false)],
                indexes: [new IndexDefinition { Name = "by-id", Columns = [new IndexColumn("id.dot")] }]),
            ["derived"] = Unit(
                [Column("id", PortableType.Guid, nullable: false)],
                key: ["id"]) with
            {
                DerivedColumns = [new DerivedColumnDefinition
                {
                    Name = "state.folded",
                    SourceColumn = "id",
                    Projection = PortableProjection.UnicodeFold
                }]
            },
            ["concurrency"] = Unit(
                [Column("id", PortableType.Guid, nullable: false)],
                key: ["id"]) with
            {
                Concurrency = ConcurrencyDeclaration.Optimistic("version.dot")
            },
            ["aggregation"] = Unit(
                [
                    Column("id", PortableType.Guid, nullable: false),
                    Column("value", PortableType.Int32, nullable: false)
                ],
                key: ["id"]) with
            {
                AggregationProfiles = [new AggregationProfile
                {
                    Name = "totals",
                    GroupByColumns = ["group.dot"],
                    Aggregates = [new Aggregate.Sum("total", "value")]
                }]
            }
        };

        Assert.Equal(
            cases.Keys,
            cases.Where(item => PortabilityValidator.Validate(item.Value).Refusals.Any(refusal => refusal.Code == "GW-PORT-010"))
                .Select(item => item.Key));
    }

    [Fact]
    public void Provider_owned_hidden_columns_remain_valid_physical_identifiers()
    {
        var result = Validate(new StorageUnit
        {
            Id = new StorageUnitId("hidden"),
            Name = "hidden",
            Columns =
            [
                new() { Name = ProviderOwnedColumns.Scope, Type = PortableType.String, IsNullable = false, MaxLength = 128 },
                new() { Name = "__groundwork_action", Type = PortableType.String, IsNullable = false, MaxLength = 1 },
                new() { Name = "__groundwork_version", Type = PortableType.Int64, IsNullable = false },
                new() { Name = SearchKeyProjection.Prefix + "name", Type = PortableType.String, MaxLength = 320 }
            ],
            Key = new KeyDefinition { Columns = [ProviderOwnedColumns.Scope] }
        });

        Assert.DoesNotContain(result.Refusals, refusal => refusal.Code == "GW-PORT-010");
    }

    [Fact]
    public void Overlong_provider_owned_hidden_columns_are_refused()
    {
        var hiddenName = SearchKeyProjection.Prefix + new string('h', 63);
        var result = Validate(new StorageUnit
        {
            Id = new StorageUnitId("overlong-hidden"),
            Name = "overlong-hidden",
            Columns =
            [
                new() { Name = ProviderOwnedColumns.Scope, Type = PortableType.String, IsNullable = false, MaxLength = 128 },
                new() { Name = hiddenName, Type = PortableType.String, MaxLength = 320 }
            ],
            Key = new KeyDefinition { Columns = [ProviderOwnedColumns.Scope] }
        });

        var diagnostic = Assert.Single(result.Refusals, refusal => refusal.Code == "GW-PORT-010");
        Assert.Equal($"columns.{hiddenName}", diagnostic.Path);
        Assert.Contains("at most 63 ASCII bytes", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Provider_sequence_requires_non_nullable_int64()
    {
        var result = Validate(Unit([
            Column("sequence", PortableType.Int32, generation: ColumnGeneration.ProviderSequence)
        ]));

        AssertCode(result, "GW-PORT-005", "sequence");
    }

    [Fact]
    public void Provider_sequence_must_be_the_sole_primary_key_column()
    {
        var result = Validate(Unit([
            Column("sequence", PortableType.Int64, nullable: false, generation: ColumnGeneration.ProviderSequence),
            Column("payload", PortableType.String)
        ], key: ["payload"]));

        AssertCode(result, "GW-PORT-005", "sequence");
    }

    [Fact]
    public void Provider_sequence_is_valid_when_it_is_the_non_nullable_int64_primary_key()
    {
        var result = Validate(Unit([
            Column("sequence", PortableType.Int64, nullable: false, generation: ColumnGeneration.ProviderSequence),
            Column("payload", PortableType.String)
        ], key: ["sequence"]));

        Assert.DoesNotContain(result.Refusals, refusal => refusal.Code == "GW-PORT-005");
    }

    [Fact]
    public void Collation_must_be_in_the_portable_set()
    {
        var result = Validate(Unit([Column("name", PortableType.String, collation: (PortableCollation)99)]));

        AssertCode(result, "GW-PORT-006", "name");
    }

    [Fact]
    public void Retention_order_column_must_be_non_nullable_and_orderable()
    {
        var result = Validate(
            Unit([Column("payload", PortableType.Json)]),
            new PortabilityValidationContext(retention: new RetentionDeclaration("payload")));

        AssertCode(result, "GW-PORT-007", "payload");
    }

    [Fact]
    public void Retention_negative_keep_newest_is_refused_but_zero_is_portable()
    {
        var negative = Validate(
            Unit([Column("sequence", PortableType.Int64, nullable: false)]),
            new PortabilityValidationContext(retention: new RetentionDeclaration
            {
                KeepNewest = -1,
                OrderColumn = "sequence"
            }));
        var zero = Validate(
            Unit([Column("sequence", PortableType.Int64, nullable: false)]),
            new PortabilityValidationContext(retention: new RetentionDeclaration
            {
                KeepNewest = 0,
                OrderColumn = "sequence"
            }));

        AssertCode(negative, "GW-PORT-007", "sequence");
        Assert.DoesNotContain(zero.Refusals, refusal => refusal.Code == "GW-PORT-007");
    }

    [Theory]
    [InlineData(PortableType.String, true)]
    [InlineData(PortableType.Int32, true)]
    [InlineData(PortableType.Int64, true)]
    [InlineData(PortableType.Decimal, true)]
    [InlineData(PortableType.Boolean, false)]
    [InlineData(PortableType.DateTimeOffset, true)]
    [InlineData(PortableType.Guid, false)]
    [InlineData(PortableType.Binary, false)]
    [InlineData(PortableType.Json, false)]
    public void Retention_orderability_is_explicit_for_each_portable_type(PortableType type, bool isOrderable)
    {
        var result = Validate(
            Unit([Column("value", type, nullable: false, maxLength: 320, precision: 18, scale: 2)]),
            new PortabilityValidationContext(retention: new RetentionDeclaration("value")));

        Assert.Equal(isOrderable, result.Refusals.All(refusal => refusal.Code != "GW-PORT-007"));
    }

    [Fact]
    public void Mongo_composite_key_order_must_match_applied_state()
    {
        var result = Validate(
            Unit([
                Column("tenant", PortableType.String, nullable: false, maxLength: 64),
                Column("id", PortableType.Guid, nullable: false)
            ],
                key: ["tenant", "id"]),
            new PortabilityValidationContext(
                ["mongodb"],
                priorAppliedMongoCompositeKeyOrder: ["id", "tenant"]));

        AssertCode(result, "GW-PORT-008", "tenant");
    }

    [Fact]
    public void All_three_invocation_seams_match_for_each_portability_rule()
    {
        foreach (var fixture in RuleFixtures())
        {
            var direct = PortabilityValidator.Validate(fixture.Unit, fixture.Context);
            var expected = Wire(direct);

            Assert.Equal(new[] { fixture.Code }, direct.Refusals.Select(refusal => refusal.Code));
            Assert.Equal(expected, Wire(BuilderPortabilityValidation.Validate(fixture.Unit, fixture.Context)));
            Assert.Equal(expected, Wire(ManifestPortabilityValidation.Validate(fixture.Unit, fixture.Context)));
            Assert.Equal(expected, Wire(SchemaTargetPortabilityValidation.Validate(fixture.Unit, fixture.Context)));
        }
    }

    private static PortabilityValidationResult Validate(StorageUnit unit, PortabilityValidationContext? context = null) =>
        PortabilityValidator.Validate(unit, context);

    private static string ToWire(PortabilityRefusal diagnostic) =>
        diagnostic.Code + "|" + diagnostic.Path + "|" + diagnostic.Message;

    private static string[] Wire(PortabilityValidationResult result) =>
        result.Refusals.Select(ToWire).ToArray();

    private static IEnumerable<PortabilityRuleFixture> RuleFixtures() =>
    [
        new(
            "GW-PORT-001",
            Unit([
                Column("id", PortableType.Guid, nullable: false),
                Column("email", PortableType.String, maxLength: 320)
            ], indexes: [Index("ux-email", "email", unique: true)])),
        new(
            "GW-PORT-002",
            Unit([Column("amount", PortableType.Decimal)])),
        new(
            "GW-PORT-003",
            Unit([Column("name", PortableType.String)], indexes: [Index("ix-name", "name")])),
        new(
            "GW-PORT-004",
            Unit(
                [Column("name", PortableType.String, nullable: false, maxLength: 851)],
                indexes: [Index("ix-name", "name")])),
        new(
            "GW-PORT-005",
            Unit([Column("sequence", PortableType.Int32, generation: ColumnGeneration.ProviderSequence)])),
        new(
            "GW-PORT-006",
            Unit([Column("name", PortableType.String, collation: (PortableCollation)99)])),
        new(
            "GW-PORT-007",
            Unit([Column("active", PortableType.Boolean, nullable: false)]),
            new PortabilityValidationContext(retention: new RetentionDeclaration("active"))),
        new(
            "GW-PORT-008",
            Unit(
                [
                    Column("tenant", PortableType.String, nullable: false, maxLength: 64),
                    Column("id", PortableType.Guid, nullable: false)
                ],
                key: ["tenant", "id"]),
            new PortabilityValidationContext(
                ["mongodb"],
                priorAppliedMongoCompositeKeyOrder: ["id", "tenant"])),
        new(
            "GW-PORT-009",
            Unit(
                [
                    Column("id", PortableType.Guid, nullable: false),
                    Column("tenant", PortableType.String, nullable: false, maxLength: 64)
                ],
                indexes:
                [
                    Index("by-tenant-primary", "tenant", unique: true),
                    Index("by-tenant-alias", "tenant", unique: true)
                ],
                key: ["id"])),
        new(
            "GW-PORT-010",
            Unit([
                Column("id", PortableType.Guid, nullable: false),
                Column(new string('x', 64), PortableType.String)
            ], key: ["id"]))
    ];

    private sealed record PortabilityRuleFixture(
        string Code,
        StorageUnit Unit,
        PortabilityValidationContext? Context = null);

    private static void AssertCode(PortabilityValidationResult result, string code, string detail)
    {
        var diagnostic = Assert.Single(result.Refusals, item => item.Code == code);
        Assert.Contains(detail, diagnostic.Message, StringComparison.Ordinal);
    }

    private static StorageUnit Unit(
        params ColumnDefinition[] columns) => Unit(columns, indexes: [], key: [columns[0].Name]);

    private static StorageUnit Unit(
        ColumnDefinition[] columns,
        IndexDefinition[]? indexes = null,
        string[]? key = null) => new()
        {
            Id = new StorageUnitId("portability"),
            Name = "Portability",
            Columns = columns,
            Key = new KeyDefinition { Columns = key ?? [columns[0].Name] },
            Indexes = indexes ?? []
        };

    private static ColumnDefinition Column(
        string name,
        PortableType type,
        bool nullable = true,
        int? maxLength = null,
        int? precision = null,
        int? scale = null,
        PortableCollation? collation = null,
        ColumnGeneration generation = ColumnGeneration.Supplied) => new()
        {
            Name = name,
            Type = type,
            IsNullable = nullable,
            MaxLength = maxLength,
            Precision = precision,
            Scale = scale,
            Collation = collation,
            Generation = generation
        };

    private static IndexDefinition Index(string name, params string[] columns) =>
        Index(name, columns, unique: false);

    private static IndexDefinition Index(string name, string column, bool unique) =>
        Index(name, [column], unique);

    private static IndexDefinition Index(string name, string column, string secondColumn, bool unique = false) =>
        Index(name, [column, secondColumn], unique);

    private static IndexDefinition Index(string name, string[] columns, bool unique) => new()
    {
        Name = name,
        Columns = columns.Select(column => new IndexColumn(column)).ToArray(),
        IsUnique = unique
    };
}
