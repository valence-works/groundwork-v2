using Groundwork.Extensions.DependencyInjection;
using Groundwork.Kernel;
using Groundwork.Store;
using Groundwork.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Groundwork.Extensions.DependencyInjection.Tests;

/// <summary>
/// Shared scaffolding for the hosting tests: one declaration, one provider factory whose stores are
/// shared by connection string, and the two moves every test makes — build a host and start it.
/// </summary>
internal sealed class HostingFixture
{
    private static int stores;

    internal HostingFixture() => ConnectionString = $"hosting-{Interlocked.Increment(ref stores)}";

    /// <summary>One factory instance, so two connection strings mean two stores and one means one.</summary>
    internal InMemoryProviderFactory Provider { get; } = new();

    internal string ConnectionString { get; }

    /// <summary>A global unit with one declared index.</summary>
    internal static StorageUnit Orders { get; } = StorageUnit.Declare("orders", "orders")
        .String("id", 64, column => column.Required())
        .String("customer", 64, column => column.Required())
        .Decimal("total", 18, 4)
        .Key("id")
        .Index("by_customer", "customer")
        .Build();

    /// <summary>The same unit before its index was declared.</summary>
    internal static StorageUnit OrdersWithoutIndex { get; } = StorageUnit.Declare("orders", "orders")
        .String("id", 64, column => column.Required())
        .String("customer", 64, column => column.Required())
        .Decimal("total", 18, 4)
        .Key("id")
        .Build();

    /// <summary>A unit carrying one declared aggregation profile.</summary>
    internal static StorageUnit OrdersWithProfile { get; } = AggregatedOrders(
        profile => profile.GroupBy("customer").Count("orders"));

    /// <summary>The same unit with the same profile name redefined — a change, not an addition.</summary>
    internal static StorageUnit OrdersWithChangedProfile { get; } = AggregatedOrders(
        profile => profile.GroupBy("customer").Count("orders").Sum("spend", "total"));

    private static StorageUnit AggregatedOrders(Action<AggregationBuilder> profile) =>
        StorageUnit.Declare("orders_aggregated", "orders_aggregated")
            .String("id", 64, column => column.Required())
            .String("customer", 64, column => column.Required())
            .Decimal("total", 18, 4)
            .Key("id")
            .Aggregate("per_customer", profile)
            .Build();

    /// <summary>Applies a declaration out of band, standing in for a completed deployment step.</summary>
    internal void Deploy(StorageUnit unit)
    {
        using var connection = Provider.Create(ConnectionString);
        connection.Schema.Apply(unit);
    }

    internal ServiceCollection Services(string? environmentName = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(
            environmentName ?? Environments.Development));
        return services;
    }

    /// <summary>Configures the default connection against this fixture's store.</summary>
    internal Action<GroundworkConnectionOptions> Connect(params StorageUnit[] units) =>
        options => options.UseProvider(Provider, ConnectionString).AddUnits(units);

    /// <summary>Runs every hosted service exactly as a host would, so startup admission really runs.</summary>
    internal static async Task StartAsync(IServiceProvider provider)
    {
        foreach (var service in provider.GetServices<IHostedService>())
            await service.StartAsync(CancellationToken.None);
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = typeof(HostingFixture).Assembly.GetName().Name!;

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
