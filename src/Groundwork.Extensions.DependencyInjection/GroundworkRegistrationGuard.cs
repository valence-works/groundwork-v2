using Groundwork.Store;
using Microsoft.Extensions.DependencyInjection;

namespace Groundwork.Extensions.DependencyInjection;

/// <summary>
/// Keeps the hosting lifetime model honest.
/// </summary>
/// <remarks>
/// <para>
/// A storage connection owns provider resources for its whole life — pools, transactions, and for
/// SQLite the single <c>${database}.schema.lock</c> file handle. Registering one per request is the
/// reflex an ASP.NET Core developer brings from other data-access libraries, and it is wrong here:
/// the second connection to the same SQLite database blocks on a lock the first one will not
/// release until the process exits.
/// </para>
/// <para>
/// So the registration surface offers no lifetime knob at all, and this guard refuses the mistake
/// when it arrives by another route. It holds the live <see cref="IServiceCollection"/> — which
/// keeps accepting descriptors until the provider is built — so a hand-written scoped registration
/// added after <c>AddGroundwork()</c> is still caught, at startup, with a named code.
/// </para>
/// </remarks>
internal sealed class GroundworkRegistrationGuard
{
    private readonly IServiceCollection services;
    private readonly List<string> names = [];
    private readonly HashSet<string> registered = new(StringComparer.Ordinal);

    internal GroundworkRegistrationGuard(IServiceCollection services) =>
        this.services = services ?? throw new ArgumentNullException(nameof(services));

    internal IReadOnlyList<string> Names => names;

    internal bool IsRegistered(string name) => registered.Contains(name);

    internal void Register(string name)
    {
        if (!registered.Add(name))
        {
            throw new GroundworkHostingException(
                GroundworkHostingDiagnostics.DuplicateConnectionName,
                $"A Groundwork connection named '{name}' is already registered. " +
                "Give the second connection a different name, or configure the existing one instead " +
                $"with services.Configure<GroundworkConnectionOptions>(\"{name}\", …).");
        }

        names.Add(name);
    }

    /// <summary>
    /// Refuses any storage connection registered with a lifetime other than singleton.
    /// </summary>
    internal void EnsureLifetimesAreHosting()
    {
        var offenders = services
            .Where(descriptor =>
                descriptor.Lifetime != ServiceLifetime.Singleton &&
                IsStorageConnection(descriptor.ServiceType))
            .Select(descriptor => $"{Describe(descriptor.ServiceType)} ({descriptor.Lifetime})")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(description => description, StringComparer.Ordinal)
            .ToArray();
        if (offenders.Length == 0)
            return;

        throw new GroundworkHostingException(
            GroundworkHostingDiagnostics.ConnectionLifetime,
            $"A Groundwork storage connection is registered with a non-singleton lifetime: " +
            $"{string.Join(", ", offenders)}. " +
            "A connection owns provider resources and process-wide schema locks for its whole life, so a " +
            "second connection to the same database blocks rather than opening. " +
            "Register connections with AddGroundwork().AddConnection(…), which registers them as process " +
            "singletons, and inject the scoped IGroundworkStorage for per-request sessions and units of work.");
    }

    private static bool IsStorageConnection(Type serviceType) =>
        typeof(IStorageProviderConnection).IsAssignableFrom(serviceType);

    private static string Describe(Type serviceType) => serviceType.FullName ?? serviceType.Name;
}
