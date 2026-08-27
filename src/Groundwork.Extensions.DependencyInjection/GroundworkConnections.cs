using Groundwork.Store;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Groundwork.Extensions.DependencyInjection;

/// <summary>
/// The process-wide set of named Groundwork connections. Every connection it hands out is a
/// container-owned singleton: opened once, shared by every request, and disposed with the root
/// service provider.
/// </summary>
public interface IGroundworkConnections
{
    /// <summary>Registered connection names, in registration order.</summary>
    IReadOnlyList<string> Names { get; }

    /// <summary>The connection registered without an explicit name.</summary>
    IStorageProviderConnection Default { get; }

    /// <summary>Opens (once) and returns the named connection.</summary>
    IStorageProviderConnection Get(string name);
}

internal sealed class GroundworkConnections : IGroundworkConnections
{
    private readonly IServiceProvider provider;
    private readonly GroundworkRegistrationGuard guard;

    public GroundworkConnections(IServiceProvider provider, GroundworkRegistrationGuard guard)
    {
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
        this.guard = guard ?? throw new ArgumentNullException(nameof(guard));
        guard.EnsureLifetimesAreHosting();
    }

    public IReadOnlyList<string> Names => guard.Names;

    public IStorageProviderConnection Default => Get(GroundworkConnectionOptions.DefaultName);

    public IStorageProviderConnection Get(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!guard.IsRegistered(name))
        {
            throw new GroundworkHostingException(
                GroundworkHostingDiagnostics.UnknownConnectionName,
                $"No Groundwork connection named '{name}' is registered. " +
                $"Register it with AddGroundwork().AddConnection(\"{name}\", …). " +
                (Names.Count == 0
                    ? "No connections are registered at all."
                    : $"Registered connections: {string.Join(", ", Names)}."));
        }

        return provider.GetRequiredKeyedService<IStorageProviderConnection>(name);
    }

    /// <summary>Materializes one connection from its named options. Called once per name.</summary>
    internal static IStorageProviderConnection Open(IServiceProvider provider, string name)
    {
        var configured = provider
            .GetRequiredService<IOptionsMonitor<GroundworkConnectionOptions>>()
            .Get(name);
        if (configured.ProviderFactory is null || string.IsNullOrWhiteSpace(configured.ConnectionString))
        {
            throw new GroundworkHostingException(
                GroundworkHostingDiagnostics.IncompleteConnection,
                $"Groundwork connection '{name}' is missing its " +
                (configured.ProviderFactory is null ? "provider factory" : "connection string") +
                ". Call options.UseProvider(new <Provider>ProviderFactory(), connectionString) when registering it.");
        }

        return configured.ProviderFactory.Create(configured.ConnectionString);
    }
}
