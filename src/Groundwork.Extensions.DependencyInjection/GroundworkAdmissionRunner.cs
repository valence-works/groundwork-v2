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
/// <seealso href="https://github.com/valence-works/groundwork-v2/issues/201">
/// The drift classification below duplicates a rule the kernel owns, because that rule is not
/// reachable through the public Store contract yet. Read the comment above the mapping methods.
/// </seealso>
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

        // "Additive" here approximates PhysicalSchemaPlanProtection.IsSafe from the public SchemaDiff.
        // It is the weaker of the two tests — see the note above IsAdditive, and #201.
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

    // ---------------------------------------------------------------------------------------------
    // TEMPORARY DUPLICATE — this is not the authority. See
    // https://github.com/valence-works/groundwork-v2/issues/201.
    //
    // Provider schema executors decide what is column drift (GW-RUNTIME-001) versus index drift
    // (GW-RUNTIME-002), and PhysicalSchemaPlanProtection decides what a startup auto-apply may
    // execute without authorization. Neither is reachable from IStorageProviderConnection —
    // GroundworkRuntimeSchemaAdmission.InspectRuntimeAdmission needs an IPhysicalSchemaExecutor and
    // a PhysicalSchemaTarget, which are provider-internal — so this maps the public SchemaDiff onto
    // the same intent by hand.
    //
    // Two implementations of one rule, with nothing keeping them in step, is the shape #74 was about
    // and #196 removed by making one physicalization the only implementation. It can disagree in both
    // directions: a Ready verdict where the first session open would refuse with GW-RUNTIME-001, or a
    // Blocked verdict where the runtime would have admitted. Do not extend these methods to cover new
    // cases — close #201 by exposing runtime admission on the Store contract and delete them.
    //
    // This has already drifted once, before it shipped: the additive list below included
    // UpdateAggregationProfile, which providers emit for a *changed* deployed profile as well as a new
    // one. The kernel's rule is that anything semantic needs explicit authorization. Read that as
    // evidence for #201 rather than as a list that is now correct.
    // ---------------------------------------------------------------------------------------------

    private static bool IsColumnLevel(SchemaChange change) => change.Kind is
        SchemaChangeKind.CreateStorageUnit or
        SchemaChangeKind.AddColumn or
        SchemaChangeKind.AddDerivedColumn;

    /// <summary>
    /// Only what genuinely adds without altering anything already deployed.
    /// </summary>
    /// <remarks>
    /// <see cref="SchemaChangeKind.UpdateAggregationProfile"/> is deliberately absent. Providers emit
    /// that kind both for a profile that is new and for one that already exists deployed, so applying
    /// it can redefine how an aggregation behaves against stored data. That is a semantic migration,
    /// and the kernel requires explicit authorization for those rather than a startup switch.
    /// </remarks>
    private static bool IsAdditive(SchemaChange change) => change.Kind is
        SchemaChangeKind.CreateStorageUnit or
        SchemaChangeKind.AddColumn or
        SchemaChangeKind.AddDerivedColumn or
        SchemaChangeKind.CreateIndex;
}
