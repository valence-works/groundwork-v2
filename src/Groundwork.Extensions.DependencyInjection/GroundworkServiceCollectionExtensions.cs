using Groundwork.Store;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace Groundwork.Extensions.DependencyInjection;

/// <summary>Registers Groundwork with a dependency injection container.</summary>
public static class GroundworkServiceCollectionExtensions
{
    /// <summary>
    /// Adds the Groundwork hosting integration and returns the builder used to declare connections.
    /// </summary>
    /// <remarks>
    /// This package references <c>Groundwork.Store</c> and nothing else from the product. Providers
    /// arrive through <see cref="IStorageProviderFactory"/> — the seam the Store contract already
    /// calls the sole provider discovery point — so adding a fifth provider needs no change here,
    /// and referencing this package never drags four database drivers into an application.
    /// </remarks>
    public static IGroundworkBuilder AddGroundwork(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions();

        var guard = services
            .Select(descriptor => descriptor.ImplementationInstance)
            .OfType<GroundworkRegistrationGuard>()
            .FirstOrDefault();
        if (guard is not null)
            return new GroundworkBuilder(services, guard);

        guard = new GroundworkRegistrationGuard(services);
        services.AddSingleton(guard);
        services.AddSingleton<IGroundworkConnections, GroundworkConnections>();
        services.AddSingleton<GroundworkAdmissionRunner>();
        services.AddSingleton<GroundworkHealthCheck>();
        services.AddSingleton<IHostedService, GroundworkStartupAdmissionService>();
        return new GroundworkBuilder(services, guard);
    }
}

/// <summary>Adds the Groundwork health check to a health-checks builder.</summary>
public static class GroundworkHealthChecksBuilderExtensions
{
    /// <summary>
    /// Registers the check that reports startup admission and live capability advertisement.
    /// </summary>
    public static IHealthChecksBuilder AddGroundwork(
        this IHealthChecksBuilder builder,
        string name = GroundworkHealthCheck.Name,
        HealthStatus? failureStatus = null,
        params string[] tags)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return builder.Add(new HealthCheckRegistration(
            name,
            provider => provider.GetRequiredService<GroundworkHealthCheck>(),
            failureStatus,
            tags.Length == 0 ? ["groundwork", "storage"] : tags));
    }
}

/// <summary>Declares the named connections an application uses.</summary>
public interface IGroundworkBuilder
{
    /// <summary>The underlying service collection.</summary>
    IServiceCollection Services { get; }

    /// <summary>Registers the default connection.</summary>
    IGroundworkBuilder AddConnection(Action<GroundworkConnectionOptions> configure);

    /// <summary>Registers a named connection, additionally resolvable as a keyed service.</summary>
    IGroundworkBuilder AddConnection(string name, Action<GroundworkConnectionOptions> configure);
}

internal sealed class GroundworkBuilder : IGroundworkBuilder
{
    private readonly GroundworkRegistrationGuard guard;

    internal GroundworkBuilder(IServiceCollection services, GroundworkRegistrationGuard guard)
    {
        Services = services;
        this.guard = guard;
    }

    public IServiceCollection Services { get; }

    public IGroundworkBuilder AddConnection(Action<GroundworkConnectionOptions> configure) =>
        AddConnection(GroundworkConnectionOptions.DefaultName, configure);

    public IGroundworkBuilder AddConnection(string name, Action<GroundworkConnectionOptions> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);
        guard.Register(name);
        Services.Configure(name, configure);

        // Connections are process singletons; sessions and units of work are scoped. Neither
        // lifetime is configurable, because only one of the two combinations is correct.
        Services.AddKeyedSingleton<IStorageProviderConnection>(name, (provider, key) =>
            GroundworkConnections.Open(provider, (string)key!));
        Services.AddKeyedScoped<IGroundworkStorage>(name, (provider, key) =>
            new GroundworkStorage(
                (string)key!,
                provider.GetRequiredService<IGroundworkConnections>().Get((string)key!)));

        if (string.Equals(name, GroundworkConnectionOptions.DefaultName, StringComparison.Ordinal))
        {
            Services.AddSingleton(provider =>
                provider.GetRequiredService<IGroundworkConnections>().Default);
            Services.AddScoped(provider =>
                provider.GetRequiredKeyedService<IGroundworkStorage>(name));
        }

        return this;
    }
}
