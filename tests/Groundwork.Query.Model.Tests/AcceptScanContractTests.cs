using Groundwork.Query.Model;
using Xunit;

namespace Groundwork.Query.Model.Tests;

public sealed class AcceptScanContractTests
{
    [Fact]
    public void Acceptance_requires_stable_identity_reason_owner_and_date()
    {
        var acceptance = ScanAcceptance.Allow(
            "GW-SCAN-0007",
            "admin-only free-text search",
            "billing",
            new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.True(acceptance.Allowed);
        Assert.Equal("GW-SCAN-0007", acceptance.Id);
        Assert.Equal("admin-only free-text search", acceptance.Reason);
        Assert.Equal("billing", acceptance.Owner);
        Assert.Equal(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), acceptance.ExpiresOn);
        Assert.Throws<ArgumentException>(() => ScanAcceptance.Allow("scan", "reason", "owner", acceptance.ExpiresOn!.Value));
        Assert.Throws<ArgumentException>(() => ScanAcceptance.Allow("GW-SCAN-0001", "", "owner", acceptance.ExpiresOn!.Value));
        Assert.Throws<ArgumentException>(() => ScanAcceptance.Allow("GW-SCAN-0001", "reason", "", acceptance.ExpiresOn!.Value));
    }

    [Fact]
    public void Acceptance_expiry_is_date_based_and_deterministic()
    {
        var acceptance = ScanAcceptance.Allow(
            "GW-SCAN-0007",
            "reason",
            "owner",
            new DateTimeOffset(2027, 1, 1, 18, 45, 0, TimeSpan.FromHours(8)));

        Assert.False(acceptance.IsExpiredAt(new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.Zero)));
        Assert.True(acceptance.IsExpiredAt(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Assembly_opt_in_attribute_is_public_and_assembly_scoped()
    {
        var usage = typeof(GwAllowAcceptedScansAttribute).GetCustomAttributes(typeof(AttributeUsageAttribute), false)
            .OfType<AttributeUsageAttribute>()
            .Single();

        Assert.Equal(AttributeTargets.Assembly, usage.ValidOn);
        Assert.False(usage.AllowMultiple);
    }
}
