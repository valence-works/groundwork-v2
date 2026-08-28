using Groundwork.Kernel;
using Groundwork.Store;

namespace Groundwork.Extensions.DependencyInjection;

/// <summary>
/// Everything one named Groundwork connection needs. Bind it from configuration, configure it in
/// code, or both — it is an ordinary named options class.
/// </summary>
/// <remarks>
/// There is deliberately no lifetime setting here. A storage connection owns provider resources and
/// process-wide locks — the SQLite <c>${database}.schema.lock</c> among them — so it is always a
/// process singleton. Per-request work belongs to <see cref="IGroundworkStorage"/>.
/// </remarks>
public sealed class GroundworkConnectionOptions
{
    /// <summary>The name used when a connection is registered without one.</summary>
    public const string DefaultName = "Default";

    /// <summary>
    /// The provider seam. Supply the factory from whichever provider package the application
    /// references; this package deliberately knows about none of them.
    /// </summary>
    public IStorageProviderFactory? ProviderFactory { get; set; }

    /// <summary>The provider connection string, safe to bind from configuration.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// The declarations this connection serves. Startup admission compares each of them against the
    /// deployed catalog, and the health check reports the verdict.
    /// </summary>
    public IList<StorageUnit> Units { get; } = [];

    /// <summary>
    /// Capability ids the application requires. They are verified against what the *deployed*
    /// database advertises at startup, because capabilities are advertised, not assumed.
    /// </summary>
    public IList<string> RequiredCapabilities { get; } = [];

    /// <summary>
    /// Development-only. Asks the provider's kernel runtime-admission seam to apply a plan during
    /// startup when the kernel's plan protection authorizes it. Off by default; destructive and
    /// semantic work still requires explicit authorization through the <c>groundwork</c> CLI — see
    /// the Schema Management wiki page.
    /// </summary>
    public bool AutoApplyOnStartup { get; set; }

    /// <summary>Sets the provider factory and connection string in one call.</summary>
    public GroundworkConnectionOptions UseProvider(IStorageProviderFactory factory, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ProviderFactory = factory;
        ConnectionString = connectionString;
        return this;
    }

    /// <summary>Declares the storage units this connection serves.</summary>
    public GroundworkConnectionOptions AddUnits(params StorageUnit[] units)
    {
        ArgumentNullException.ThrowIfNull(units);
        foreach (var unit in units)
            Units.Add(unit ?? throw new ArgumentException("A storage unit cannot be null.", nameof(units)));
        return this;
    }

    /// <summary>Requires the deployed database to advertise every listed capability id.</summary>
    public GroundworkConnectionOptions RequireCapabilities(params string[] capabilityIds)
    {
        ArgumentNullException.ThrowIfNull(capabilityIds);
        foreach (var id in capabilityIds)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            RequiredCapabilities.Add(id);
        }

        return this;
    }
}
