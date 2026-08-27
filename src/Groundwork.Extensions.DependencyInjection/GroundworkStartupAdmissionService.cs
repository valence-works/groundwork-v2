using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Groundwork.Extensions.DependencyInjection;

/// <summary>
/// Runs startup admission at a well-defined point in the host lifecycle: before the host starts
/// serving, and once.
/// </summary>
/// <remarks>
/// A blocked verdict refuses startup with <c>GW-HOST-005</c> rather than letting the first request
/// discover it. A degraded verdict is logged and the host starts — a missing index makes dependent
/// query shapes refuse, not the whole application.
/// </remarks>
internal sealed class GroundworkStartupAdmissionService : IHostedService
{
    private readonly GroundworkAdmissionRunner runner;
    private readonly ILogger<GroundworkStartupAdmissionService> logger;

    public GroundworkStartupAdmissionService(
        GroundworkAdmissionRunner runner,
        ILogger<GroundworkStartupAdmissionService> logger)
    {
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var report = runner.Run();
        foreach (var connection in report.Connections)
        {
            foreach (var unit in connection.Units.Where(unit => unit.Applied))
            {
                logger.LogWarning(
                    "Groundwork connection '{Connection}' applied physical schema at startup for {Unit}. " +
                    "AutoApplyOnStartup is a development convenience; production schema belongs to the groundwork CLI.",
                    connection.Name, unit.Describe());
            }

            switch (connection.Status)
            {
                case GroundworkAdmissionStatus.Ready:
                    logger.LogInformation(
                        "Groundwork connection '{Connection}' admitted {UnitCount} storage unit(s); advertised capabilities: {Capabilities}.",
                        connection.Name, connection.Units.Count, string.Join(", ", connection.AdvertisedCapabilities));
                    break;
                case GroundworkAdmissionStatus.Degraded:
                    logger.LogWarning(
                        "Groundwork connection '{Connection}' has index drift (GW-RUNTIME-002); dependent query shapes will refuse. {Units}",
                        connection.Name, Describe(connection));
                    break;
                default:
                    throw Refuse(connection);
            }
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static GroundworkHostingException Refuse(GroundworkConnectionAdmission connection)
    {
        if (connection.MissingCapabilities.Count != 0)
        {
            return new GroundworkHostingException(
                GroundworkHostingDiagnostics.CapabilityNotAdvertised,
                $"The database behind Groundwork connection '{connection.Name}' does not advertise " +
                $"{string.Join(", ", connection.MissingCapabilities)}. " +
                "Capabilities describe what the deployed database can actually do, so deploy a topology that " +
                "provides them, or drop the requirement and degrade gracefully. Advertised: " +
                (connection.AdvertisedCapabilities.Count == 0
                    ? "none"
                    : string.Join(", ", connection.AdvertisedCapabilities)) + ".");
        }

        if (connection.Failure is not null)
        {
            return new GroundworkHostingException(
                GroundworkHostingDiagnostics.StartupAdmissionBlocked,
                $"Groundwork connection '{connection.Name}' could not complete startup admission: {connection.Failure}");
        }

        return new GroundworkHostingException(
            GroundworkHostingDiagnostics.StartupAdmissionBlocked,
            $"Groundwork connection '{connection.Name}' has physical schema work pending, so the deployed " +
            $"catalog cannot serve the compiled declaration. {Describe(connection)} " +
            "Apply it from the deployment step with `groundwork apply --schema groundwork.schema.json " +
            "--provider <alias> --safe`; runtime is inspect-only by default.");
    }

    private static string Describe(GroundworkConnectionAdmission connection) =>
        string.Join("; ", connection.Units
            .Where(unit => unit.Status != GroundworkAdmissionStatus.Ready)
            .Select(unit => unit.Describe()));
}
