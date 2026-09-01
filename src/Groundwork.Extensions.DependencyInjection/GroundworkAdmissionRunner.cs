using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Store;
using Microsoft.Extensions.Options;

namespace Groundwork.Extensions.DependencyInjection;

/// <summary>
/// Runs startup admission and remembers the verdict so the health check reports the same facts the
/// host started on.
/// </summary>
/// <remarks>
/// Admission asks each connection for the kernel's runtime admission result. It does not write, and
/// it does not apply physical schema unless a connection explicitly opted into development
/// auto-apply; the provider seam delegates that choice to the kernel's plan-protection rule.
/// </remarks>
public sealed class GroundworkAdmissionRunner
{
    private readonly IGroundworkConnections connections;
    private readonly IOptionsMonitor<GroundworkConnectionOptions> options;
    private readonly bool autoApplyAllowed;

    public GroundworkAdmissionRunner(
        IGroundworkConnections connections,
        IOptionsMonitor<GroundworkConnectionOptions> options)
        : this(connections, options, autoApplyAllowed: false)
    {
    }

    internal GroundworkAdmissionRunner(
        IGroundworkConnections connections,
        IOptionsMonitor<GroundworkConnectionOptions> options,
        bool autoApplyAllowed)
    {
        this.connections = connections ?? throw new ArgumentNullException(nameof(connections));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.autoApplyAllowed = autoApplyAllowed;
    }

    /// <summary>The verdict from the last pass, or null before the host has started.</summary>
    public GroundworkAdmissionReport? Latest { get; private set; }

    /// <summary>Runs admission across every registered connection and stores the verdict.</summary>
    public GroundworkAdmissionReport Run()
    {
        var report = new GroundworkAdmissionReport(
            connections.Names.Select(Admit).ToArray());
        Latest = report;
        return report;
    }

    private GroundworkConnectionAdmission Admit(string name)
    {
        var configured = options.Get(name);
        IStorageProviderConnection connection;
        try
        {
            connection = connections.Get(name);
        }
        catch (Exception exception) when (exception is not GroundworkHostingException)
        {
            return new GroundworkConnectionAdmission(
                name, GroundworkAdmissionStatus.Failed, [], [], [], exception.Message);
        }

        var advertised = connection.Capabilities
            .Select(capability => capability.Id.Value)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var missing = configured.RequiredCapabilities
            .Where(required => !advertised.Contains(required, StringComparer.Ordinal))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        try
        {
            var units = configured.Units
                .Select(unit => Admit(connection, unit, autoApplyAllowed && configured.AutoApplyOnStartup))
                .ToArray();
            var status = new[]
                {
                    units.Length == 0 ? GroundworkAdmissionStatus.Ready : units.Max(unit => unit.Status),
                    missing.Length == 0 ? GroundworkAdmissionStatus.Ready : GroundworkAdmissionStatus.Blocked
                }.Max();
            return new GroundworkConnectionAdmission(name, status, units, advertised, missing);
        }
        catch (Exception exception) when (exception is not GroundworkHostingException)
        {
            return new GroundworkConnectionAdmission(
                name, GroundworkAdmissionStatus.Failed, [], advertised, missing, exception.Message);
        }
    }

    private static GroundworkUnitAdmission Admit(
        IStorageProviderConnection connection,
        StorageUnit unit,
        bool autoApply)
    {
        // Establish the kernel verdict before asking the reporting-only public diff surface. The
        // first inspect is intentionally read-only; when auto-apply is enabled the second inspect
        // executes the same provider seam after the pending display has been captured.
        var initial = connection.Schema.InspectRuntimeAdmission(
            unit,
            new GroundworkRuntimeSchemaAdmissionOptions { AutoApplyOnStartup = false });

        // Keep the established public description for logs and health data. It is deliberately
        // reporting only: the admission verdict and any auto-apply decision come from the provider
        // seam below, not from the legacy display vocabulary.
        IReadOnlyList<SchemaChange> pending;
        try
        {
            pending = connection.Schema.Diff(unit).Changes;
        }
        catch
        {
            // Reporting must not replace a kernel admission verdict. A provider may reject its
            // public display diff for a declaration that the seam has already classified as
            // blocked, while the unit status remains actionable.
            pending = [];
        }
        var result = autoApply
            ? connection.Schema.InspectRuntimeAdmission(
                unit,
                new GroundworkRuntimeSchemaAdmissionOptions { AutoApplyOnStartup = true })
            : initial;
        return new GroundworkUnitAdmission(
            unit.Name,
            Status(result),
            pending,
            Applied: result.AppliedOperationCount != 0);
    }

    private static GroundworkAdmissionStatus Status(GroundworkRuntimeSchemaAdmissionResult result)
        => result.Status switch
        {
            GroundworkRuntimeSchemaAdmissionStatus.Ready => GroundworkAdmissionStatus.Ready,
            GroundworkRuntimeSchemaAdmissionStatus.Degraded => GroundworkAdmissionStatus.Degraded,
            GroundworkRuntimeSchemaAdmissionStatus.Blocked => GroundworkAdmissionStatus.Blocked,
            _ => throw new ArgumentOutOfRangeException(nameof(result))
        };
}
