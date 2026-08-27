using Groundwork.Kernel.Schema;
using Xunit;

namespace Groundwork.Kernel.Tests;

/// <summary>
/// Groundwork ships one set of runtime assemblies compiled for more than one target framework.
/// Anything a declaration is fingerprinted or hashed into is persisted and later compared — an
/// applied-state history written by a net10.0 process is read back by a net8.0 process and must
/// admit — so those values may not depend on which target produced them.
/// </summary>
/// <remarks>
/// <para>
/// Every assertion here pins a literal. The suite is built and run once per shipped target
/// framework, so a value that differed between targets would fail on one of the runs; an
/// assertion phrased as <c>Assert.Equal(Compute(x), Compute(x))</c> would pass on both runs even
/// if the two targets disagreed with each other, and would prove nothing.
/// </para>
/// </remarks>
public sealed class TargetFrameworkParityTests
{
    private static StorageUnit DeclareTicket() =>
        StorageUnit.Declare("tickets", "tickets")
            .String("id", 64, column => column.Required())
            .String("subject", 256, column => column.Required())
            .Int32("priority")
            .Key("id")
            .Index("by_priority", "priority")
            .Build();

    [Fact]
    public void Schema_subject_fingerprints_are_identical_on_every_shipped_target_framework()
    {
        var subject = new SchemaSubject(DeclareTicket());

        Assert.Equal(
            "4df15fc23c72df7792b399aeb92da5368b3676e7494f90108e0c451e58b1ae34",
            subject.Fingerprint);
    }

    [Fact]
    public void Schema_fingerprints_of_canonical_parts_are_identical_on_every_shipped_target_framework()
    {
        Assert.Equal(
            "91c82cf7b34b0566c5a174344b27db5aac9a22c54dad5549a76b96da1252cd52",
            SchemaFingerprint.Create(["tickets", "Tickets", null, "scope:Global", "é中😀"]));
    }

    [Fact]
    public void Portable_comparison_key_hashes_are_identical_on_every_shipped_target_framework()
    {
        // A supplementary-plane scalar and a Turkish dotless i: the two inputs most likely to move
        // if the folding or the hex encoding were resolved differently per target framework.
        var comparisonKey = PortableStringComparison.Create(
            "Straße-😀-ı",
            PortableStringComparisonPolicy.UnicodeOrdinalIgnoreCase);

        Assert.Equal("0000530000540000520000410000DF00004500002D01F60000002D000131", comparisonKey);
        Assert.Equal(
            "aa6060ccff6ca6c541b88e7295540f9946df0d824f5134f8c99af47fc49aaa97",
            PortableStringComparison.CreateHash(comparisonKey));
    }

    [Fact]
    public void The_unicode_folding_algorithm_id_is_identical_on_every_shipped_target_framework() =>
        Assert.Equal(
            "groundwork-unicode-ordinal-ignore-case-v1-" +
            "3206f759667cb9cc764ec243dfb3d322a39970184efab619e80163c36d86818f",
            PortableStringComparison.UnicodeOrdinalIgnoreCaseAlgorithmId);

    [Fact]
    public void Uuid_v7_identities_carry_the_rfc_9562_version_and_variant_on_every_target_framework()
    {
        var moment = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var generator = new UuidV7IdentityGenerator(new FixedTimeProvider(moment));

        var id = generator.Generate();
        var bytes = Guid.Parse(id).ToByteArray(bigEndian: true);

        Assert.Equal("018cc251f400", id[..12]);
        Assert.Equal(
            moment.ToUnixTimeMilliseconds(),
            ((long)bytes[0] << 40) | ((long)bytes[1] << 32) | ((long)bytes[2] << 24) |
            ((long)bytes[3] << 16) | ((long)bytes[4] << 8) | bytes[5]);
        Assert.Equal(0x70, bytes[6] & 0xF0);
        Assert.Equal(0x80, bytes[8] & 0xC0);
    }

    [Fact]
    public void Uuid_v7_refuses_a_timestamp_below_the_unix_epoch_on_every_target_framework() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new UuidV7IdentityGenerator(new FixedTimeProvider(DateTimeOffset.UnixEpoch.AddTicks(-1)))
                .Generate());

    [Fact]
    public void Snowflake_identities_are_serialized_by_the_gate_on_every_target_framework()
    {
        var generator = new SnowflakeIdentityGenerator(
            new FixedTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            new SnowflakeIdentityGeneratorOptions { WorkerId = 11 });
        var identities = new System.Collections.Concurrent.ConcurrentBag<string>();

        Parallel.For(0, 2_048, _ => identities.Add(generator.Generate()));

        Assert.Equal(2_048, identities.Distinct(StringComparer.Ordinal).Count());
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
