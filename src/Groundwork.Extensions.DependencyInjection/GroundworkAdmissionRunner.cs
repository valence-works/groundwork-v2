using Groundwork.Kernel;
using Groundwork.Store;
using Microsoft.Extensions.Options;

namespace Groundwork.Extensions.DependencyInjection;

/// <summary>
/// Runs startup admission and remembers the verdict so the health check reports the same facts the
/// host started on.
/// </summary>
/// <remarks>
/// Admission is inspect-only: it asks each connection for the difference between the deployed
/// catalog and the compiled declaration, and it verifies that the deployed database really
/// advertises the capabilities the application said it needs. It does not write, and it does not
/// apply physical schema unless a connection explicitly opted into development auto-apply.
/// </remarks>
public sealed class GroundworkAdmissionRunner
{
    private readonly IGroundworkConnections connections;
    private readonly IOptionsMonitor<GroundworkConnectionOptions> options;

    public GroundworkAdmissionRunner(
        IGroundworkConnections connections,
        IOptionsMonitor<GroundworkConnectionOptions> options)
    {
        this.connections = connections ?? throw new ArgumentNullException(nameof(connections));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
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
                .Select(unit => Admit(connection, unit, configured.AutoApplyOnStartup))
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
        var pending = connection.Schema.Diff(unit).Changes;
        if (pending.Count == 0)
            return new GroundworkUnitAdmission(unit.Name, GroundworkAdmissionStatus.Ready, []);

        if (autoApply && pending.All(IsAdditive))
        {
            connection.Schema.Apply(unit);
            return new GroundworkUnitAdmission(unit.Name, GroundworkAdmissionStatus.Ready, pending, Applied: true);
        }

        var status = pending.Any(IsColumnLevel)
            ? GroundworkAdmissionStatus.Blocked
            : GroundworkAdmissionStatus.Degraded;
        return new GroundworkUnitAdmission(unit.Name, status, pending);
    }

    private static bool IsColumnLevel(SchemaChange change) => change.Kind is
        SchemaChangeKind.CreateStorageUnit or
        SchemaChangeKind.AddColumn or
        SchemaChangeKind.AddDerivedColumn;

    private static bool IsAdditive(SchemaChange change) => change.Kind is
        SchemaChangeKind.CreateStorageUnit or
        SchemaChangeKind.AddColumn or
        SchemaChangeKind.AddDerivedColumn or
        SchemaChangeKind.CreateIndex or
        SchemaChangeKind.UpdateAggregationProfile;
}
