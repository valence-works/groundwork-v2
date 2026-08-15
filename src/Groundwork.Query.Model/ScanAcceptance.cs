using System;

namespace Groundwork.Query.Model;

/// <summary>Opt-in marker permitting one otherwise uncovered query shape to scan.</summary>
public sealed record ScanAcceptance
{
    private const int WarningWindowDays = 30;

    private ScanAcceptance(
        bool allowed,
        string? id,
        string? reason,
        string? owner,
        DateTimeOffset? expiresOn)
    {
        Allowed = allowed;
        Id = id;
        Reason = reason;
        Owner = owner;
        ExpiresOn = expiresOn;
    }

    public bool Allowed { get; }

    public string? Id { get; }

    public string? Reason { get; }

    public string? Owner { get; }

    /// <summary>The UTC calendar date on which this acceptance stops being valid.</summary>
    public DateTimeOffset? ExpiresOn { get; }

    public static ScanAcceptance Refuse { get; } = new(false, null, null, null, null);

    public static ScanAcceptance Allow(
        string id,
        string reason,
        string owner,
        DateTimeOffset expiresOn)
    {
        if (string.IsNullOrWhiteSpace(id) ||
            !id.StartsWith("GW-SCAN-", StringComparison.Ordinal) ||
            id.Length == "GW-SCAN-".Length)
            throw new ArgumentException("A scan acceptance id must start with 'GW-SCAN-'.", nameof(id));
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A scan acceptance reason is required.", nameof(reason));
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("A scan acceptance owner is required.", nameof(owner));

        return new(
            true,
            id,
            reason,
            owner,
            NormalizeDate(expiresOn));
    }

    public bool IsExpiredAt(DateTimeOffset now)
    {
        return Allowed && ExpiresOn is DateTimeOffset expiry && NormalizeDate(now) >= expiry;
    }

    public bool IsExpiringAt(DateTimeOffset now)
    {
        return Allowed && ExpiresOn is DateTimeOffset expiry &&
               !IsExpiredAt(now) &&
               expiry - NormalizeDate(now) <= TimeSpan.FromDays(WarningWindowDays);
    }

    private static DateTimeOffset NormalizeDate(DateTimeOffset value) =>
        new(value.Year, value.Month, value.Day, 0, 0, 0, TimeSpan.Zero);
}

/// <summary>Opts an assembly into explicit, attributed accepted-scan diagnostics.</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
public sealed class GwAllowAcceptedScansAttribute : Attribute
{
}
