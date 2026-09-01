using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Groundwork.Extensions.DependencyInjection;

/// <summary>
/// Runs startup admission at a well-defined point in the host lifecycle: before the host starts
/// serving, and once.
/// </summary>
/// <remarks>
/// A blocked verdict refuses startup with <c>GW-HOST-005</c> rather than letting the first request
/// discover it. A degraded verdict is logged and the host starts — physical index drift makes
/// dependent query shapes refuse, not the whole application. Auto-apply is admitted only for a
/// Development host; a non-Development host refuses it with <c>GW-HOST-007</c> before admission can
/// ask a provider to apply anything.
/// </remarks>
internal sealed class GroundworkStartupAdmissionService : IHostedService
{
    private readonly GroundworkAdmissionRunner runner;
    private readonly ILogger<GroundworkStartupAdmissionService> logger;
    private readonly IGroundworkConnections connections;
    private readonly IOptionsMonitor<GroundworkConnectionOptions> options;
    private readonly bool isDevelopment;
    private readonly string environmentName;

    public GroundworkStartupAdmissionService(
        GroundworkAdmissionRunner runner,
        ILogger<GroundworkStartupAdmissionService> logger,
        IGroundworkConnections connections,
        IOptionsMonitor<GroundworkConnectionOptions> options,
        bool isDevelopment,
        string environmentName)
    {
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.connections = connections ?? throw new ArgumentNullException(nameof(connections));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.isDevelopment = isDevelopment;
        this.environmentName = environmentName ?? throw new ArgumentNullException(nameof(environmentName));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var autoApplyConnections = connections.Names
            .Where(name => options.Get(name).AutoApplyOnStartup)
            .ToArray();
        if (autoApplyConnections.Length != 0 && !isDevelopment)
            throw RefuseAutoApply(autoApplyConnections, environmentName);

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

    private static GroundworkHostingException RefuseAutoApply(
        IReadOnlyList<string> connectionNames,
        string environmentName) =>
        new(
            GroundworkHostingDiagnostics.AutoApplyOnStartupNotAllowed,
            $"Groundwork connection(s) {string.Join(", ", connectionNames.Select(name => $"'{name}'"))} " +
            $"enable AutoApplyOnStartup in the non-Development host environment '{environmentName}'. " +
            "Runtime schema auto-apply is allowed only when the host environment is Development. " +
            "Use `groundwork plan` to review the deployment plan, then `groundwork apply --safe` " +
            "as the authorized deployment path.");

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
