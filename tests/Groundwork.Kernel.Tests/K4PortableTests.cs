using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Groundwork.Kernel;
using Groundwork.Kernel.ExternalModule;
using Xunit;

namespace Groundwork.Kernel.Tests;

public sealed class K4CapabilitiesScopeIdentityStringComparisonTests
{
    private static readonly Dictionary<int, int> GeneratedUnicodeMappings = CreateGeneratedUnicodeMappings();

    private static readonly MethodInfo Base62Encode = typeof(ShortIdentityGenerator).Assembly
        .GetType("Groundwork.Kernel.Base62")!
        .GetMethod("Encode", BindingFlags.Public | BindingFlags.Static)!;

    [Fact]
    public void Capability_ids_are_namespaced_and_case_sensitive()
    {
        var id = new CapabilityId("sample.module.feature");

        Assert.Equal("sample", id.Namespace);
        Assert.Equal("sample.module.feature", id.Value);
        Assert.Throws<ArgumentException>(() => new CapabilityId("feature"));
        Assert.Throws<ArgumentException>(() => new CapabilityId("Sample.Module.Feature"));
    }

    [Fact]
    public void Registry_composes_modules_and_derives_evidence_policy()
    {
        var gated = new CapabilityDescriptor(
            new CapabilityId("sample.module.gated"),
            "Gated",
            "Requires evidence.",
            EvidenceGatedByDefault: true);
        var (registry, policy) = new GroundworkModuleCatalog()
            .Add(new TestModule(gated))
            .Build();

        Assert.True(registry.IsRegistered(gated.Id));
        Assert.Contains(gated.Id, policy.EvidenceGatedCapabilities);
    }

    [Fact]
    public void External_module_assembly_can_register_a_capability()
    {
        var module = new ExternalCapabilityModule();
        var registry = new GroundworkModuleCatalog().Add(module).BuildRegistry();

        Assert.Equal("external.test", module.Name);
        Assert.True(registry.IsRegistered(ExternalCapabilityModule.Capability));
    }

    [Fact]
    public void Default_registry_contains_the_built_in_atomic_commit_capability()
    {
        Assert.True(CapabilityRegistry.Default.IsRegistered(WellKnownCapabilities.AtomicCommit));
    }

    [Fact]
    public void Registry_rejects_conflicting_redefinitions_but_accepts_equivalent_redefinitions()
    {
        var id = new CapabilityId("sample.module.conflict");
        var builder = CapabilityRegistry.CreateBuilder();
        var descriptor = new CapabilityDescriptor(id, "One", "First.");

        builder.Add(descriptor);
        builder.Add(descriptor);

        Assert.Throws<InvalidOperationException>(() =>
            builder.Add(new CapabilityDescriptor(id, "Two", "Different.", EvidenceGatedByDefault: true)));
    }

    [Fact]
    public void Bare_capability_evaluation_has_supported_missing_and_evidence_states()
    {
        var required = new CapabilityId("sample.module.gated");
        var builder = CapabilityRegistry.CreateBuilder();
        builder.Add(new CapabilityDescriptor(required, "Gated", "Requires evidence.", EvidenceGatedByDefault: true));
        var registry = builder.Build();
        var validator = new ProviderCapabilityValidator(registry);
        var provider = new ProviderCapabilityReport(
            new ProviderIdentity("test", "1"),
            new HashSet<CapabilityId> { required },
            new HashSet<CapabilityId>(),
            Array.Empty<string>());

        Assert.IsType<ProviderFit.RequiresEvidence>(validator.Evaluate([required], provider));
        Assert.IsType<ProviderFit.Unsupported>(validator.Evaluate([new CapabilityId("sample.module.other")], provider));
        Assert.IsType<ProviderFit.Supported>(validator.Evaluate(
            [required], provider, new WorkloadEvidencePolicy(new HashSet<CapabilityId>())));
    }

    [Fact]
    public void Bare_capability_validation_reports_unknown_and_unevidenced_requirements()
    {
        var required = new CapabilityId("sample.module.gated");
        var builder = CapabilityRegistry.CreateBuilder();
        builder.Add(new CapabilityDescriptor(required, "Gated", "Requires evidence.", EvidenceGatedByDefault: true));
        var registry = builder.Build();
        var validator = new ProviderCapabilityValidator(registry);
        var provider = new ProviderCapabilityReport(
            new("test", "1"),
            new HashSet<CapabilityId> { required },
            new HashSet<CapabilityId>(),
            Array.Empty<string>());

        var result = validator.Validate([required, new CapabilityId("sample.module.unknown")], provider);

        Assert.False(result.IsCompatible);
        Assert.Contains(result.Errors, diagnostic => diagnostic.Code == "GW-CAP-014");
        Assert.Contains(result.Errors, diagnostic => diagnostic.Code == "GW-CAP-013");
    }

    [Fact]
    public void Capability_validation_preserves_non_blocking_warnings_and_reports_unsupported_fit()
    {
        var supported = new ProviderCapabilityReport(
            new("test", "1"),
            new HashSet<CapabilityId> { WellKnownCapabilities.AtomicCommit },
            new HashSet<CapabilityId> { WellKnownCapabilities.AtomicCommit },
            new[] { "Provider will materialize indexes lazily." });
        var validator = new ProviderCapabilityValidator();

        var compatible = validator.Validate([WellKnownCapabilities.AtomicCommit], supported);
        var unsupported = validator.Validate([WellKnownCapabilities.AtomicCommit], supported with
        {
            SupportedCapabilities = new HashSet<CapabilityId>(),
            EvidencedCapabilities = new HashSet<CapabilityId>()
        });

        Assert.True(compatible.IsCompatible);
        Assert.Contains(compatible.Issues, issue => issue.Code == "GW-CAP-002");
        Assert.Empty(compatible.Errors);
        Assert.False(unsupported.IsCompatible);
        Assert.Contains(unsupported.Errors, issue => issue.Code == "GW-CAP-004");
    }

    [Fact]
    public void Capability_collection_boundaries_snapshot_inputs_and_expose_immutable_views()
    {
        var capability = WellKnownCapabilities.AtomicCommit;
        var supported = new HashSet<CapabilityId> { capability };
        var evidenced = new HashSet<CapabilityId> { capability };
        var warnings = new List<string> { "before" };
        var report = new ProviderCapabilityReport(new("test", "1"), supported, evidenced, warnings);
        var replacement = new HashSet<CapabilityId> { capability };
        var reboundReport = report with { SupportedCapabilities = replacement };
        var policyInput = new HashSet<CapabilityId> { capability };
        var policy = new WorkloadEvidencePolicy(policyInput);
        var issueInput = new List<CapabilityValidationIssue>
        {
            CapabilityValidationIssue.Warning("GW-CAP-002", "warning", "provider.warnings")
        };
        var result = new CapabilityCompatibilityResult(issueInput);
        var validator = new ProviderCapabilityValidator();
        var unsupported = Assert.IsType<ProviderFit.Unsupported>(validator.Evaluate(
            [capability],
            new ProviderCapabilityReport(new("test", "1"), new HashSet<CapabilityId>(), new HashSet<CapabilityId>(), Array.Empty<string>())));
        var requiresEvidence = Assert.IsType<ProviderFit.RequiresEvidence>(validator.Evaluate(
            [capability],
            new ProviderCapabilityReport(new("test", "1"), new HashSet<CapabilityId> { capability }, new HashSet<CapabilityId>(), Array.Empty<string>())));
        var catalog = new GroundworkModuleCatalog().Add(new TestModule(
            new CapabilityDescriptor(new CapabilityId("sample.module.snapshot"), "Snapshot", "Snapshot test.")));
        var moduleSnapshot = catalog.Modules;

        supported.Clear();
        evidenced.Clear();
        warnings.Add("after");
        replacement.Clear();
        policyInput.Clear();
        issueInput.Clear();

        Assert.Contains(capability, report.SupportedCapabilities);
        Assert.Contains(capability, reboundReport.SupportedCapabilities);
        Assert.Contains(capability, report.EvidencedCapabilities);
        Assert.Equal(new[] { "before" }, report.Warnings);
        Assert.Contains(capability, policy.EvidenceGatedCapabilities);
        Assert.Single(result.Issues);
        Assert.Single(moduleSnapshot);
        var supportedView = Assert.IsAssignableFrom<ISet<CapabilityId>>(report.SupportedCapabilities);
        Assert.Throws<NotSupportedException>(() => supportedView.Add(new CapabilityId("sample.module.mutation")));
        var warningView = Assert.IsAssignableFrom<IList<string>>(report.Warnings);
        Assert.Throws<NotSupportedException>(() => warningView.Add("mutation"));
        var policyView = Assert.IsAssignableFrom<ISet<CapabilityId>>(policy.EvidenceGatedCapabilities);
        Assert.Throws<NotSupportedException>(() => policyView.Add(new CapabilityId("sample.module.mutation")));
        var issueView = Assert.IsAssignableFrom<IList<CapabilityValidationIssue>>(result.Issues);
        Assert.Throws<NotSupportedException>(() => issueView.Add(
            CapabilityValidationIssue.Warning("GW-CAP-002", "mutation", "provider.warnings")));
        var errorsView = Assert.IsAssignableFrom<IList<CapabilityValidationIssue>>(result.Errors);
        Assert.Throws<NotSupportedException>(() => errorsView.Add(
            CapabilityValidationIssue.Error("GW-CAP-004", "mutation", "requirements")));
        var missingView = Assert.IsAssignableFrom<IList<CapabilityId>>(unsupported.MissingRequirements);
        Assert.Throws<NotSupportedException>(() => missingView.Add(new CapabilityId("sample.module.mutation")));
        var evidenceView = Assert.IsAssignableFrom<IList<string>>(requiresEvidence.Reasons);
        Assert.Throws<NotSupportedException>(() => evidenceView.Add("mutation"));
        var moduleView = Assert.IsAssignableFrom<IList<IGroundworkModule>>(catalog.Modules);
        Assert.Throws<NotSupportedException>(() => moduleView.Add(new TestModule(
            new CapabilityDescriptor(new CapabilityId("sample.module.mutation"), "Mutation", "Mutation test."))));
        var descriptorView = Assert.IsAssignableFrom<IList<CapabilityDescriptor>>(WellKnownCapabilities.All);
        Assert.Throws<NotSupportedException>(() => descriptorView.Add(
            new CapabilityDescriptor(new CapabilityId("sample.module.mutation"), "Mutation", "Mutation test.")));
        var registryDescriptorView = Assert.IsAssignableFrom<IList<CapabilityDescriptor>>(
            CapabilityRegistry.Default.Descriptors);
        Assert.Throws<NotSupportedException>(() => registryDescriptorView.Add(
            new CapabilityDescriptor(new CapabilityId("sample.module.mutation"), "Mutation", "Mutation test.")));
    }

    [Fact]
    public void Provider_capability_dimensions_snapshot_all_collection_inputs()
    {
        var valueKinds = new HashSet<IndexValueKind> { IndexValueKind.String };
        var missingValues = new HashSet<MissingValueBehavior> { MissingValueBehavior.Excluded };
        var queryOperations = new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal };
        var concurrencyModes = new HashSet<ConcurrencyDeclaration> { ConcurrencyDeclaration.None };
        var requiredCapabilities = new List<CapabilityId> { WellKnownCapabilities.AtomicCommit };
        var indexes = new IndexCapabilities(valueKinds, false, true, missingValues);
        var unitRequirements = new StorageUnitCapabilityRequirements(
            new StorageUnitId("orders"),
            requiredCapabilities,
            ConcurrencyDeclaration.None);
        var replacementQueryOperations = new HashSet<PortableQueryOperation> { PortableQueryOperation.Contains };
        var replacementConcurrencyModes = new HashSet<ConcurrencyDeclaration> { ConcurrencyDeclaration.Optimistic };
        var report = new ProviderCapabilityReport(
            new("test", "1"),
            new HashSet<CapabilityId>(),
            new HashSet<CapabilityId>(),
            indexes,
            queryOperations,
            concurrencyModes,
            Array.Empty<string>());
        var reboundReport = report with
        {
            SupportedQueryOperations = replacementQueryOperations,
            SupportedConcurrencyModes = replacementConcurrencyModes
        };

        valueKinds.Clear();
        missingValues.Clear();
        queryOperations.Clear();
        concurrencyModes.Clear();
        requiredCapabilities.Clear();
        replacementQueryOperations.Clear();
        replacementConcurrencyModes.Clear();

        Assert.Contains(IndexValueKind.String, report.Indexes.SupportedValueKinds);
        Assert.Contains(MissingValueBehavior.Excluded, report.Indexes.SupportedMissingValueBehaviors);
        Assert.Contains(PortableQueryOperation.Equal, report.SupportedQueryOperations);
        Assert.Contains(ConcurrencyDeclaration.None, report.SupportedConcurrencyModes);
        Assert.Contains(PortableQueryOperation.Contains, reboundReport.SupportedQueryOperations);
        Assert.Contains(ConcurrencyDeclaration.Optimistic, reboundReport.SupportedConcurrencyModes);
        Assert.False(report.Indexes.SupportsUniqueIndexes);
        Assert.Contains(WellKnownCapabilities.AtomicCommit, unitRequirements.RequiredCapabilities);

        var valueKindView = Assert.IsAssignableFrom<ISet<IndexValueKind>>(report.Indexes.SupportedValueKinds);
        Assert.Throws<NotSupportedException>(() => valueKindView.Add(IndexValueKind.Number));
        var missingValueView = Assert.IsAssignableFrom<ISet<MissingValueBehavior>>(
            report.Indexes.SupportedMissingValueBehaviors);
        Assert.Throws<NotSupportedException>(() => missingValueView.Add(MissingValueBehavior.Included));
        var queryOperationView = Assert.IsAssignableFrom<ISet<PortableQueryOperation>>(
            report.SupportedQueryOperations);
        Assert.Throws<NotSupportedException>(() => queryOperationView.Add(PortableQueryOperation.Contains));
        var concurrencyView = Assert.IsAssignableFrom<ISet<ConcurrencyDeclaration>>(
            report.SupportedConcurrencyModes);
        Assert.Throws<NotSupportedException>(() => concurrencyView.Add(ConcurrencyDeclaration.Optimistic));
        var requiredCapabilityView = Assert.IsAssignableFrom<IList<CapabilityId>>(
            unitRequirements.RequiredCapabilities);
        Assert.Throws<NotSupportedException>(() => requiredCapabilityView.Add(
            new CapabilityId("sample.module.mutation")));
        var allIndexKindsView = Assert.IsAssignableFrom<ISet<IndexValueKind>>(
            IndexCapabilities.All.SupportedValueKinds);
        Assert.Throws<NotSupportedException>(() => allIndexKindsView.Add(IndexValueKind.Number));
    }

    [Fact]
    public void Structured_unit_validation_preserves_v1_capability_and_concurrency_diagnostics()
    {
        var gated = new CapabilityId("sample.module.gated");
        var missing = new CapabilityId("sample.module.missing");
        var unknown = new CapabilityId("sample.module.unknown");
        var builder = CapabilityRegistry.CreateBuilder();
        builder.Add(new CapabilityDescriptor(gated, "Gated", "Requires evidence.", EvidenceGatedByDefault: true));
        builder.Add(new CapabilityDescriptor(missing, "Missing", "Not advertised."));
        var validator = new ProviderCapabilityValidator(builder.Build());
        var unit = new StorageUnitCapabilityRequirements(
            new StorageUnitId("orders"),
            [gated, missing, unknown],
            ConcurrencyDeclaration.Optimistic);
        var provider = new ProviderCapabilityReport(
            new("test", "1"),
            new HashSet<CapabilityId> { gated },
            new HashSet<CapabilityId>(),
            IndexCapabilities.All,
            Enum.GetValues<PortableQueryOperation>().ToHashSet(),
            new HashSet<ConcurrencyDeclaration> { ConcurrencyDeclaration.None },
            ["Provider will materialize indexes lazily."]);

        Assert.IsType<ProviderFit.Unsupported>(validator.Evaluate([unit], provider));
        var result = validator.Validate([unit], provider);

        Assert.False(result.IsCompatible);
        Assert.Contains(result.Issues, issue => issue.Code == "GW-CAP-002");
        Assert.Contains(result.Errors, issue => issue.Code == "GW-CAP-004");
        Assert.Contains(result.Errors, issue => issue.Code == "GW-CAP-005");
        Assert.Contains(result.Errors, issue => issue.Code == "GW-CAP-013");
        Assert.Contains(result.Errors, issue => issue.Code == "GW-CAP-014");

        var compatibleUnit = new StorageUnitCapabilityRequirements(
            new StorageUnitId("orders"),
            [gated, missing],
            ConcurrencyDeclaration.Optimistic);
        var compatibleProvider = provider with
        {
            SupportedCapabilities = new HashSet<CapabilityId> { gated, missing },
            EvidencedCapabilities = new HashSet<CapabilityId> { gated, missing },
            SupportedConcurrencyModes = new HashSet<ConcurrencyDeclaration> { ConcurrencyDeclaration.Optimistic }
        };

        Assert.True(validator.Validate([compatibleUnit], compatibleProvider).IsCompatible);
    }

    [Fact]
    public void Scope_rejects_reserved_missing_malformed_and_overlong_values()
    {
        foreach (var value in new[]
        {
            "", " ", "tenant ", " tenant-a", " __groundwork_internal", "__groundwork_global__", "tenant\0a", "tenant\uD800a", "tenant\uD800",
            "tenant\uDC00"
        })
            Assert.Throws<ArgumentException>(() => new StorageScope(value));

        Assert.Throws<ArgumentException>(() => new StorageScope(new string('a', StorageScope.MaxValueLength + 1)));
    }

    [Fact]
    public void Scope_accepts_well_formed_unicode_at_the_portable_limit()
    {
        var value = string.Concat(new string('a', StorageScope.MaxValueLength - 2), char.ConvertFromUtf32(0x1F680));

        Assert.Equal(value, new StorageScope(value).Value);
    }

    [Fact]
    public void Identity_generators_preserve_short_and_uuid_shapes()
    {
        var time = new TestTimeProvider();

        Assert.Equal(11, new ShortIdentityGenerator(time).Generate().Length);
        Assert.Equal(32, new UuidV7IdentityGenerator(time).Generate().Length);
        Assert.Equal(32, new GuidIdentityGenerator().Generate().Length);
        Assert.Equal(11, new SnowflakeIdentityGenerator(time, new() { WorkerId = 1 }).Generate().Length);
    }

    [Theory]
    [InlineData(0UL, "00000000000")]
    [InlineData(1UL, "00000000001")]
    [InlineData(61UL, "0000000000z")]
    [InlineData(62UL, "00000000010")]
    [InlineData(1000UL, "000000000G8")]
    [InlineData(1UL << 22, "0000000Hb84")]
    [InlineData(1UL << 41, "0000ciKbTd2")]
    [InlineData(ulong.MaxValue, "LygHa16AHYF")]
    public void Base62_identity_encoding_matches_v1_golden_vectors(ulong value, string expected)
    {
        Assert.Equal(expected, EncodeBase62(value));
    }

    [Fact]
    public void Base62_identity_encoding_preserves_numeric_ordinal_order()
    {
        ulong[] values =
        [
            0, 1, 61, 62, 1_000, 1UL << 22, 1UL << 41, long.MaxValue, ulong.MaxValue - 1, ulong.MaxValue
        ];

        for (var left = 0; left < values.Length; left++)
        for (var right = 0; right < values.Length; right++)
        {
            var numeric = values[left].CompareTo(values[right]);
            var ordinal = string.CompareOrdinal(EncodeBase62(values[left]), EncodeBase62(values[right]));
            Assert.Equal(Math.Sign(numeric), Math.Sign(ordinal));
        }
    }

    [Fact]
    public void Snowflake_identity_matches_the_v1_fixed_golden_vector()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(
            "0d6salhsREG",
            new SnowflakeIdentityGenerator(time, new SnowflakeIdentityGeneratorOptions { WorkerId = 1 }).Generate());
    }

    [Fact]
    public void Uuid_v7_identity_preserves_the_v1_timestamp_prefix()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var id = new UuidV7IdentityGenerator(time).Generate();

        Assert.Equal("018cc251f400", id[..12]);
    }

    [Fact]
    public void Snowflake_rejects_backwards_clock_and_invalid_worker_ids()
    {
        var time = new TestTimeProvider();
        var generator = new SnowflakeIdentityGenerator(time, new SnowflakeIdentityGeneratorOptions { WorkerId = 3 });
        generator.Generate();
        time.Advance(TimeSpan.FromMilliseconds(-10));

        Assert.Throws<InvalidOperationException>(() => generator.Generate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnowflakeIdentityGeneratorOptions { WorkerId = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnowflakeIdentityGeneratorOptions { WorkerId = 1024 });
    }

    [Fact]
    public void Identity_factory_creates_each_generator_kind_and_requires_snowflake_options()
    {
        var time = new TestTimeProvider();

        Assert.IsType<ShortIdentityGenerator>(GroundworkIdentityGenerators.Create(IdentityGeneratorKind.Short, time));
        Assert.IsType<UuidV7IdentityGenerator>(GroundworkIdentityGenerators.Create(IdentityGeneratorKind.UuidV7, time));
        Assert.IsType<GuidIdentityGenerator>(GroundworkIdentityGenerators.Create(IdentityGeneratorKind.Guid, time));
        Assert.IsType<SnowflakeIdentityGenerator>(GroundworkIdentityGenerators.Create(
            IdentityGeneratorKind.Snowflake, time, new SnowflakeIdentityGeneratorOptions()));
        Assert.Throws<ArgumentNullException>(() => GroundworkIdentityGenerators.Create(IdentityGeneratorKind.Snowflake));
    }

    [Fact]
    public void Short_ids_sort_chronologically_and_reject_times_before_the_epoch()
    {
        var time = new TestTimeProvider();
        var generator = new ShortIdentityGenerator(time);
        var first = generator.Generate();
        time.Advance(TimeSpan.FromMilliseconds(1));
        var second = generator.Generate();
        time.Advance(TimeSpan.FromSeconds(1));
        var third = generator.Generate();

        Assert.True(string.CompareOrdinal(first, second) < 0);
        Assert.True(string.CompareOrdinal(second, third) < 0);

        var beforeEpoch = new TestTimeProvider();
        beforeEpoch.Advance(TimeSpan.FromDays(-365 * 7));
        Assert.Throws<InvalidOperationException>(() => new ShortIdentityGenerator(beforeEpoch).Generate());
    }

    [Fact]
    public void Short_ids_are_distinct_within_one_millisecond()
    {
        var ids = new HashSet<string>();
        var generator = new ShortIdentityGenerator(new TestTimeProvider());

        for (var index = 0; index < 1_000; index++)
            ids.Add(generator.Generate());

        Assert.True(ids.Count > 990);
    }

    [Fact]
    public void Uuid_v7_ids_sort_chronologically_and_are_unique_within_one_millisecond()
    {
        var time = new TestTimeProvider();
        var generator = new UuidV7IdentityGenerator(time);
        var first = generator.Generate();
        time.Advance(TimeSpan.FromMilliseconds(5));
        var second = generator.Generate();

        Assert.True(string.CompareOrdinal(first, second) < 0);

        time = new TestTimeProvider();
        generator = new UuidV7IdentityGenerator(time);
        var ids = new HashSet<string>();
        for (var index = 0; index < 1_000; index++)
            Assert.True(ids.Add(generator.Generate()));
    }

    [Fact]
    public void Snowflake_ids_are_strictly_increasing_across_a_full_sequence_and_workers_are_distinct()
    {
        var time = new TestTimeProvider();
        var generator = new SnowflakeIdentityGenerator(time, new SnowflakeIdentityGeneratorOptions { WorkerId = 7 });
        var ids = new List<string>(4_096);
        var previous = string.Empty;
        for (var index = 0; index < 4_096; index++)
        {
            var current = generator.Generate();
            if (index > 0)
                Assert.True(string.CompareOrdinal(previous, current) < 0);
            previous = current;
            ids.Add(current);
        }

        Assert.Equal(4_096, ids.Distinct().Count());
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(string.CompareOrdinal(previous, generator.Generate()) < 0);

        var workerOne = new SnowflakeIdentityGenerator(new TestTimeProvider(), new SnowflakeIdentityGeneratorOptions { WorkerId = 1 });
        var workerTwo = new SnowflakeIdentityGenerator(new TestTimeProvider(), new SnowflakeIdentityGeneratorOptions { WorkerId = 2 });
        Assert.NotEqual(workerOne.Generate(), workerTwo.Generate());
    }

    [Fact]
    public void Snowflake_rejects_times_before_the_epoch()
    {
        var time = new TestTimeProvider();
        time.Advance(TimeSpan.FromDays(-365 * 7));

        Assert.Throws<InvalidOperationException>(() => new SnowflakeIdentityGenerator(
            time,
            new SnowflakeIdentityGeneratorOptions { WorkerId = 1 }).Generate());
    }

    [Theory]
    [InlineData(PortableStringComparisonPolicy.Ordinal, "A", "|0041")]
    [InlineData(PortableStringComparisonPolicy.AsciiIgnoreCase, "A", "|0061")]
    [InlineData(PortableStringComparisonPolicy.UnicodeOrdinalIgnoreCase, "å😀", "|0000C5|01F600")]
    public void String_search_keys_match_the_v1_golden_vectors(
        PortableStringComparisonPolicy policy,
        string value,
        string expected)
    {
        Assert.Equal(expected, PortableStringComparison.CreateSearchKey(value, policy));
        Assert.Equal("groundwork-boundary-delimited-search-key-v1", PortableStringComparison.SearchKeyAlgorithmId);
    }

    [Fact]
    public void String_comparison_rejects_malformed_utf16_and_keeps_algorithm_fingerprint()
    {
        Assert.Throws<ArgumentException>(() => PortableStringComparison.CreateOrdinal("\uD800"));
        Assert.Equal(
            "groundwork-unicode-ordinal-ignore-case-v1-3206f759667cb9cc764ec243dfb3d322a39970184efab619e80163c36d86818f",
            PortableStringComparison.UnicodeOrdinalIgnoreCaseAlgorithmId);
    }

    [Fact]
    public void Unicode_generated_table_is_sorted_complete_and_hash_stable()
    {
        var pairs = UnicodeOrdinalCasingData.SimpleUppercaseMappings;
        Assert.Equal(UnicodeOrdinalCasingData.SimpleUppercaseMappingCount * 2, pairs.Length);

        var bytes = new byte[pairs.Length * sizeof(int)];
        var previous = -1;
        for (var index = 0; index < pairs.Length; index += 2)
        {
            var source = pairs[index];
            var mapped = pairs[index + 1];
            Assert.True(source > previous);
            Assert.True(Rune.IsValid(source));
            Assert.True(Rune.IsValid(mapped));
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(index * sizeof(int)), source);
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan((index + 1) * sizeof(int)), mapped);
            previous = source;
        }

        Assert.Equal(
            "3206f759667cb9cc764ec243dfb3d322a39970184efab619e80163c36d86818f",
            Convert.ToHexStringLower(SHA256.HashData(bytes)));
        Assert.DoesNotContain(0x0131, pairs.ToArray());
        Assert.DoesNotContain(0x017F, pairs.ToArray());
        Assert.Contains(0xA7CF, pairs.ToArray());
        Assert.Contains(0xA7D3, pairs.ToArray());
        Assert.Contains(0xA7D5, pairs.ToArray());
    }

    [Theory]
    [InlineData("API-Z9", "api-z9")]
    [InlineData("already lower", "already lower")]
    [InlineData("[]_@", "[]_@")]
    public void Ascii_ignore_case_uses_the_v1_versioned_comparison_key(string value, string expected)
    {
        Assert.Equal(PortableStringComparison.AsciiIgnoreCaseAlgorithmId, PortableStringComparison.GetAlgorithmId(
            PortableStringComparisonPolicy.AsciiIgnoreCase));
        Assert.Equal(expected, PortableStringComparison.CreateAsciiIgnoreCase(value));
    }

    [Theory]
    [InlineData("Å")]
    [InlineData("İ")]
    [InlineData("ß")]
    [InlineData("é")]
    [InlineData("line\nbreak")]
    public void Ascii_ignore_case_rejects_non_portable_values(string value)
    {
        Assert.False(PortableStringComparison.IsAsciiIgnoreCaseValue(value));
        Assert.Throws<ArgumentException>(() => PortableStringComparison.CreateAsciiIgnoreCase(value));
    }

    [Theory]
    [InlineData("å", "Å")]
    [InlineData("ς", "σ")]
    [InlineData("i", "I")]
    [InlineData("\U00010428", "\U00010400")]
    [InlineData("\U00010D70", "\U00010D50")]
    public void Unicode_ignore_case_matches_v1_case_pairs(string left, string right)
    {
        Assert.Equal(
            PortableStringComparison.CreateUnicodeOrdinalIgnoreCase(left),
            PortableStringComparison.CreateUnicodeOrdinalIgnoreCase(right));
    }

    [Fact]
    public void Unicode_keys_match_v1_fixed_case_fold_boundaries()
    {
        string[] values =
        [
            "i", "I", "İ", "ı", "ß", "ẞ", "ss", "Σ", "σ", "ς", "K", "K", "k",
            "ﬀ", "FF", "é", "É", "e\u0301", "ſ", "\U00010D70", "\U00010D50",
            "\U00016EBB", "\U00016EA0", "\uE000", "\U00010000",
            "\U00010400", "\U00010428", "😀", "\0"
        ];

        foreach (var left in values)
        foreach (var right in values)
        {
            var expected = StringComparer.Ordinal.Compare(
                CreateGeneratedUnicodeKey(left),
                CreateGeneratedUnicodeKey(right));
            var actual = StringComparer.Ordinal.Compare(
                PortableStringComparison.CreateUnicodeOrdinalIgnoreCase(left),
                PortableStringComparison.CreateUnicodeOrdinalIgnoreCase(right));
            Assert.True(
                Math.Sign(expected) == Math.Sign(actual),
                $"Ordinal-ignore-case order differs for U+{string.Join(" U+", left.EnumerateRunes().Select(rune => rune.Value.ToString("X4")))} and U+{string.Join(" U+", right.EnumerateRunes().Select(rune => rune.Value.ToString("X4")))}: expected {expected}, actual {actual}.");
        }
    }

    [Fact]
    public void Unicode_keys_exhaustively_match_generated_uppercase_mappings_and_order()
    {
        var pairs = UnicodeOrdinalCasingData.SimpleUppercaseMappings;
        var values = new List<string>(UnicodeOrdinalCasingData.SimpleUppercaseMappingCount);
        var expectedOrder = new List<(int Source, string Value)>(values.Capacity);
        for (var index = 0; index < pairs.Length; index += 2)
        {
            var source = new Rune(pairs[index]).ToString();
            var mapped = new Rune(pairs[index + 1]).ToString();
            values.Add(source);
            expectedOrder.Add((pairs[index + 1], source));
            Assert.Equal(CreateGeneratedUnicodeKey(source), PortableStringComparison.CreateUnicodeOrdinalIgnoreCase(source));
            Assert.Equal(CreateGeneratedUnicodeKey(mapped), PortableStringComparison.CreateUnicodeOrdinalIgnoreCase(mapped));
            Assert.Equal(
                PortableStringComparison.CreateUnicodeOrdinalIgnoreCase(mapped),
                PortableStringComparison.CreateUnicodeOrdinalIgnoreCase(source));
        }

        var expected = expectedOrder
            .OrderBy(pair => pair.Source)
            .ThenBy(pair => pair.Value, StringComparer.Ordinal)
            .Select(pair => pair.Value)
            .ToArray();
        var actual = values
            .OrderBy(PortableStringComparison.CreateUnicodeOrdinalIgnoreCase, StringComparer.Ordinal)
            .ThenBy(value => value, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected, actual);

        Assert.NotEqual(
            PortableStringComparison.CreateUnicodeOrdinalIgnoreCase("ſ"),
            PortableStringComparison.CreateUnicodeOrdinalIgnoreCase("S"));
    }

    [Fact]
    public void Unicode_keys_exhaustively_match_generated_supplementary_case_equivalence_and_order()
    {
        var pairs = UnicodeOrdinalCasingData.SimpleUppercaseMappings;
        var values = new List<string>();
        var expectedOrder = new List<(int Mapped, string Value)>();
        for (var index = 0; index < pairs.Length; index += 2)
        {
            if (pairs[index] <= 0xFFFF)
                continue;

            var value = new Rune(pairs[index]).ToString();
            var mapped = new Rune(pairs[index + 1]).ToString();
            values.Add(value);
            expectedOrder.Add((pairs[index + 1], value));
            Assert.Equal(CreateGeneratedUnicodeKey(value), PortableStringComparison.CreateUnicodeOrdinalIgnoreCase(value));
            Assert.Equal(
                PortableStringComparison.CreateUnicodeOrdinalIgnoreCase(mapped),
                PortableStringComparison.CreateUnicodeOrdinalIgnoreCase(value));
        }

        Assert.Equal(282, values.Count);
        var expected = expectedOrder
            .OrderBy(pair => pair.Mapped)
            .ThenBy(pair => pair.Value, StringComparer.Ordinal)
            .Select(pair => pair.Value)
            .ToArray();
        var actual = values
            .OrderBy(PortableStringComparison.CreateUnicodeOrdinalIgnoreCase, StringComparer.Ordinal)
            .ThenBy(value => value, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(PortableStringComparisonPolicy.Ordinal, "xÅ😀y", "Å😀")]
    [InlineData(PortableStringComparisonPolicy.AsciiIgnoreCase, "xAPI-z", "api")]
    [InlineData(PortableStringComparisonPolicy.UnicodeOrdinalIgnoreCase, "xÅ😀y", "å😀")]
    public void Search_keys_preserve_comparison_unit_boundaries_for_portable_contains(
        PortableStringComparisonPolicy policy,
        string value,
        string search)
    {
        var valueKey = PortableStringComparison.CreateSearchKey(value, policy);
        var searchKey = PortableStringComparison.CreateSearchKey(search, policy);

        Assert.Equal("groundwork-boundary-delimited-search-key-v1", PortableStringComparison.SearchKeyAlgorithmId);
        Assert.Contains(searchKey, valueKey, StringComparison.Ordinal);
    }

    [Fact]
    public void Search_keys_preserve_encoded_unit_boundaries()
    {
        const string value = "\u0001\u0002";
        const string search = "\U00010000";
        var comparisonValue = PortableStringComparison.CreateUnicodeOrdinalIgnoreCase(value);
        var comparisonSearch = PortableStringComparison.CreateUnicodeOrdinalIgnoreCase(search);

        Assert.Contains(comparisonSearch, comparisonValue, StringComparison.Ordinal);
        Assert.DoesNotContain(
            PortableStringComparison.CreateSearchKey(search, PortableStringComparisonPolicy.UnicodeOrdinalIgnoreCase),
            PortableStringComparison.CreateSearchKey(value, PortableStringComparisonPolicy.UnicodeOrdinalIgnoreCase),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Identity_projection_and_bounded_prefix_match_v1_contract()
    {
        var value = new string('Å', 2_048) + "😀";
        var comparison = PortableStringComparison.CreateUnicodeOrdinalIgnoreCase(value);
        var projection = PortableStringComparison.ProjectIdentity(value, PortableStringComparisonPolicy.UnicodeOrdinalIgnoreCase);

        Assert.Equal(value, projection.OriginalValue);
        Assert.Equal(comparison, projection.ComparisonKey);
        Assert.Equal(projection.LookupKey, projection.ComparisonKeyHash);
        Assert.Equal(comparison[..256], PortableStringComparison.CreateBoundedPrefix(comparison, 256));
        Assert.Equal(64, projection.LookupKey.Length);
        Assert.Equal(PortableStringComparison.LookupHashAlgorithmId, projection.LookupAlgorithmId);
        Assert.Equal(450, PortableStringComparison.MaximumIdentityCodeUnits);
        PortableStringComparison.ValidateIdentity(new string('x', 450));
        Assert.Throws<ArgumentException>(() => PortableStringComparison.ValidateIdentity(new string('x', 451)));
    }

    [Fact]
    public void Kernel_does_not_expose_the_v1_untyped_required_capabilities_mechanism()
    {
        Assert.DoesNotContain(
            typeof(StorageUnit).GetProperties(),
            property => property.Name == "RequiredCapabilities");
    }

    private sealed class TestModule(CapabilityDescriptor descriptor) : IGroundworkModule
    {
        public string Name => "sample.module";

        public void RegisterCapabilities(ICapabilityRegistryBuilder builder) => builder.Add(descriptor);
    }

    private sealed class TestTimeProvider(DateTimeOffset? initial = null) : TimeProvider
    {
        private DateTimeOffset now = initial ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan delta) => now += delta;
    }

    private static string EncodeBase62(ulong value) => (string)Base62Encode.Invoke(null, [value])!;

    private static Dictionary<int, int> CreateGeneratedUnicodeMappings()
    {
        var mappings = new Dictionary<int, int>(UnicodeOrdinalCasingData.SimpleUppercaseMappingCount);
        var pairs = UnicodeOrdinalCasingData.SimpleUppercaseMappings;
        for (var index = 0; index < pairs.Length; index += 2)
            mappings.Add(pairs[index], pairs[index + 1]);
        return mappings;
    }

    private static string CreateGeneratedUnicodeKey(string value)
    {
        var normalized = new StringBuilder(value.Length * 6);
        Span<char> encoded = stackalloc char[6];
        foreach (var rune in value.EnumerateRunes())
        {
            var mapped = GeneratedUnicodeMappings.GetValueOrDefault(rune.Value, rune.Value);
            mapped.TryFormat(encoded, out _, "X6", CultureInfo.InvariantCulture);
            normalized.Append(encoded);
        }
        return normalized.ToString();
    }
}
