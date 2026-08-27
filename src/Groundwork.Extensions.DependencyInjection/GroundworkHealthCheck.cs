using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Groundwork.Extensions.DependencyInjection;

/// <summary>
/// Reports what startup admission found and what the deployed databases actually advertise.
/// </summary>
/// <remarks>
/// There is no synthetic ping here. The two facts that decide whether this application can serve
/// its declarations are the admission verdict — does the deployed catalog match the compiled
/// target — and the live capability advertisement, and those are exactly what this reports. Index
/// drift is <see cref="HealthStatus.Degraded"/> because only dependent query shapes refuse; column
/// drift and an unadvertised required capability are <see cref="HealthStatus.Unhealthy"/>.
/// </remarks>
public sealed class GroundworkHealthCheck : IHealthCheck
{
    /// <summary>The name this check is registered under by <c>AddGroundwork()</c>.</summary>
    public const string Name = "groundwork";

    private readonly GroundworkAdmissionRunner runner;

    public GroundworkHealthCheck(GroundworkAdmissionRunner runner) =>
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var report = runner.Latest ?? runner.Run();
        var data = report.Connections.ToDictionary(
            connection => connection.Name,
            Describe,
            StringComparer.Ordinal);
        var description = report.Connections.Count == 0
            ? "No Groundwork connections are registered."
            : string.Join("; ", report.Connections.Select(connection =>
                $"{connection.Name}: {connection.Status}"));

        return Task.FromResult(report.Status switch
        {
            GroundworkAdmissionStatus.Ready => HealthCheckResult.Healthy(description, data),
            GroundworkAdmissionStatus.Degraded => HealthCheckResult.Degraded(description, data: data),
            _ => HealthCheckResult.Unhealthy(description, data: data)
        });
    }

    private static object Describe(GroundworkConnectionAdmission connection) => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["status"] = connection.Status.ToString(),
        ["capabilities"] = connection.AdvertisedCapabilities,
        ["missingCapabilities"] = connection.MissingCapabilities,
        ["units"] = connection.Units.Select(unit => unit.Describe()).ToArray(),
        ["failure"] = connection.Failure
    };
}
