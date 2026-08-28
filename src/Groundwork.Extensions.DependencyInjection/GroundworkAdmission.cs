using Groundwork.Store;

namespace Groundwork.Extensions.DependencyInjection;

/// <summary>
/// How a connection or a storage unit came out of startup admission.
/// </summary>
/// <remarks>
/// The split mirrors the runtime admission contract: a missing unit, column, or derived column
/// means data cannot be read or written correctly and is <see cref="Blocked"/> (GW-RUNTIME-001).
/// Physical index drift against an otherwise matching applied target only makes dependent query
/// shapes refuse and is <see cref="Degraded"/> (GW-RUNTIME-002); a changed declaration is blocked
/// until that target is applied.
/// </remarks>
public enum GroundworkAdmissionStatus
{
    /// <summary>The deployed catalog matches the compiled target.</summary>
    Ready,

    /// <summary>Index-level work is pending. Dependent query shapes refuse; the application serves.</summary>
    Degraded,

    /// <summary>Unit- or column-level work is pending. The application must not serve.</summary>
    Blocked,

    /// <summary>Admission itself could not run — the connection or the catalog read failed.</summary>
    Failed
}

/// <summary>Startup admission for one declared storage unit.</summary>
public sealed record GroundworkUnitAdmission(
    string Unit,
    GroundworkAdmissionStatus Status,
    IReadOnlyList<SchemaChange> PendingChanges,
    bool Applied = false)
{
    /// <summary>A single-line summary suitable for a log entry or a health-check data value.</summary>
    public string Describe() =>
        PendingChanges.Count == 0
            ? $"{Unit}: {Status}"
            : $"{Unit}: {Status}{(Applied ? " (applied)" : string.Empty)} — " +
              string.Join(", ", PendingChanges.Select(change => $"{change.Kind} {change.Identity}"));
}

/// <summary>Startup admission for one named connection.</summary>
public sealed record GroundworkConnectionAdmission(
    string Name,
    GroundworkAdmissionStatus Status,
    IReadOnlyList<GroundworkUnitAdmission> Units,
    IReadOnlyList<string> AdvertisedCapabilities,
    IReadOnlyList<string> MissingCapabilities,
    string? Failure = null);

/// <summary>The result of one startup admission pass across every registered connection.</summary>
public sealed record GroundworkAdmissionReport(IReadOnlyList<GroundworkConnectionAdmission> Connections)
{
    /// <summary>The worst status across every connection.</summary>
    public GroundworkAdmissionStatus Status => Connections.Count == 0
        ? GroundworkAdmissionStatus.Ready
        : Connections.Max(connection => connection.Status);
}
