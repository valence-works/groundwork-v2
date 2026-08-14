using System.Reflection;
using Groundwork.Kernel;
using Xunit;

namespace Groundwork.Kernel.Tests;

public sealed class K4CapabilitiesScopeIdentityStringComparisonTests
{
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
    public void Scope_rejects_reserved_missing_malformed_and_overlong_values()
    {
        foreach (var value in new[]
        {
            "", " ", "tenant ", " __groundwork_internal", "__groundwork_global__", "tenant\0a", "tenant\uD800",
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
        var type = typeof(ShortIdentityGenerator).Assembly.GetType("Groundwork.Kernel.Base62")!;
        var encode = type.GetMethod("Encode", BindingFlags.Public | BindingFlags.Static)!;

        Assert.Equal(expected, encode.Invoke(null, [value]));
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
}
