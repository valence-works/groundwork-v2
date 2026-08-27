using Groundwork.Kernel;
using Groundwork.Store;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace Groundwork.Extensions.DependencyInjection.Tests;

/// <summary>
/// Startup admission and the health check. Runtime is inspect-only: the host refuses to start on
/// column-level drift and starts, degraded, on index-level drift.
/// </summary>
public sealed class StartupAdmissionTests
{
    private readonly HostingFixture fixture = new();

    [Fact]
    public async Task A_deployed_declaration_admits_and_reports_healthy_with_its_capabilities()
    {
        fixture.Deploy(HostingFixture.Orders);
        var services = fixture.Services();
        services.AddGroundwork().AddConnection(fixture.Connect(HostingFixture.Orders));
        using var provider = services.BuildServiceProvider(validateScopes: true);

        await HostingFixture.StartAsync(provider);

        var report = provider.GetRequiredService<GroundworkAdmissionRunner>().Latest;
        Assert.NotNull(report);
        Assert.Equal(GroundworkAdmissionStatus.Ready, report.Status);
        var connection = Assert.Single(report.Connections);
        Assert.Contains(BatchWriteCapabilities.CompareAndDelete.Value, connection.AdvertisedCapabilities);

        var health = await Check(provider);
        Assert.Equal(HealthStatus.Healthy, health.Status);
        Assert.Contains(GroundworkConnectionOptions.DefaultName, health.Data.Keys);
    }

    [Fact]
    public async Task An_undeployed_declaration_refuses_startup_and_names_the_cli()
    {
        var services = fixture.Services();
        services.AddGroundwork().AddConnection(fixture.Connect(HostingFixture.Orders));
        using var provider = services.BuildServiceProvider(validateScopes: true);

        var refusal = await Assert.ThrowsAsync<GroundworkHostingException>(
            () => HostingFixture.StartAsync(provider));
        Assert.Equal(GroundworkHostingDiagnostics.StartupAdmissionBlocked, refusal.Code);
        Assert.Contains("orders", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("groundwork apply", refusal.Message, StringComparison.Ordinal);

        var health = await Check(provider);
        Assert.Equal(HealthStatus.Unhealthy, health.Status);
    }

    [Fact]
    public async Task Index_drift_degrades_instead_of_blocking_startup()
    {
        fixture.Deploy(HostingFixture.OrdersWithoutIndex);
        var services = fixture.Services();
        services.AddGroundwork().AddConnection(fixture.Connect(HostingFixture.Orders));
        using var provider = services.BuildServiceProvider(validateScopes: true);

        await HostingFixture.StartAsync(provider);

        var report = provider.GetRequiredService<GroundworkAdmissionRunner>().Latest!;
        Assert.Equal(GroundworkAdmissionStatus.Degraded, report.Status);
        var unit = Assert.Single(Assert.Single(report.Connections).Units);
        Assert.Equal(GroundworkAdmissionStatus.Degraded, unit.Status);
        Assert.Contains("by_customer", unit.Describe(), StringComparison.Ordinal);

        Assert.Equal(HealthStatus.Degraded, (await Check(provider)).Status);
    }

    [Fact]
    public async Task A_capability_the_database_does_not_advertise_refuses_startup()
    {
        fixture.Deploy(HostingFixture.Orders);
        var services = fixture.Services();
        services.AddGroundwork().AddConnection(options => fixture
            .Connect(HostingFixture.Orders)(options.RequireCapabilities("groundwork.storage.time-travel")));
        using var provider = services.BuildServiceProvider(validateScopes: true);

        var refusal = await Assert.ThrowsAsync<GroundworkHostingException>(
            () => HostingFixture.StartAsync(provider));
        Assert.Equal(GroundworkHostingDiagnostics.CapabilityNotAdvertised, refusal.Code);
        Assert.Contains("groundwork.storage.time-travel", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_capability_the_database_does_advertise_admits()
    {
        fixture.Deploy(HostingFixture.Orders);
        var services = fixture.Services();
        services.AddGroundwork().AddConnection(options => fixture
            .Connect(HostingFixture.Orders)(options.RequireCapabilities(BatchWriteCapabilities.ExactRetention.Value)));
        using var provider = services.BuildServiceProvider(validateScopes: true);

        await HostingFixture.StartAsync(provider);

        Assert.Equal(GroundworkAdmissionStatus.Ready,
            provider.GetRequiredService<GroundworkAdmissionRunner>().Latest!.Status);
    }

    [Fact]
    public async Task Auto_apply_is_off_by_default_and_applies_only_when_asked()
    {
        Assert.False(new GroundworkConnectionOptions().AutoApplyOnStartup);

        var services = fixture.Services();
        services.AddGroundwork().AddConnection(options =>
        {
            fixture.Connect(HostingFixture.Orders)(options);
            options.AutoApplyOnStartup = true;
        });
        using var provider = services.BuildServiceProvider(validateScopes: true);

        await HostingFixture.StartAsync(provider);

        var unit = Assert.Single(Assert.Single(
            provider.GetRequiredService<GroundworkAdmissionRunner>().Latest!.Connections).Units);
        Assert.True(unit.Applied);
        Assert.Equal(GroundworkAdmissionStatus.Ready, unit.Status);
        Assert.True(provider.GetRequiredService<IStorageProviderConnection>()
            .Schema.Diff(HostingFixture.Orders).IsEmpty);

        // Pins the positive side of the list: these kinds add without altering anything deployed, so
        // auto-apply may execute them. The negative side is pinned below.
        Assert.Equal(
            [SchemaChangeKind.CreateStorageUnit, SchemaChangeKind.AddColumn, SchemaChangeKind.CreateIndex],
            unit.PendingChanges.Select(change => change.Kind).Distinct());
    }

    // Providers emit UpdateAggregationProfile for a profile that already exists deployed as well as
    // for a new one, so applying it can redefine how an aggregation behaves against stored data. The
    // kernel calls that a semantic migration and requires explicit authorization; a startup switch is
    // not that. This is the regression guard for a list that already drifted once.
    [Fact]
    public async Task A_changed_aggregation_profile_is_never_auto_applied()
    {
        fixture.Deploy(HostingFixture.OrdersWithProfile);
        var services = fixture.Services();
        services.AddGroundwork().AddConnection(options =>
        {
            fixture.Connect(HostingFixture.OrdersWithChangedProfile)(options);
            options.AutoApplyOnStartup = true;
        });
        using var provider = services.BuildServiceProvider(validateScopes: true);

        await HostingFixture.StartAsync(provider);

        var unit = Assert.Single(Assert.Single(
            provider.GetRequiredService<GroundworkAdmissionRunner>().Latest!.Connections).Units);
        Assert.Equal(
            SchemaChangeKind.UpdateAggregationProfile,
            Assert.Single(unit.PendingChanges).Kind);
        Assert.False(unit.Applied);

        // The deployed profile is still the one that was deployed: nothing was applied behind the host.
        Assert.False(provider.GetRequiredService<IStorageProviderConnection>()
            .Schema.Diff(HostingFixture.OrdersWithChangedProfile).IsEmpty);
    }

    [Fact]
    public async Task A_changed_aggregation_profile_degrades_rather_than_blocking_startup()
    {
        fixture.Deploy(HostingFixture.OrdersWithProfile);
        var services = fixture.Services();
        services.AddGroundwork().AddConnection(fixture.Connect(HostingFixture.OrdersWithChangedProfile));
        using var provider = services.BuildServiceProvider(validateScopes: true);

        // Reads and writes are unaffected; only the aggregation runs against the deployed profile.
        await HostingFixture.StartAsync(provider);

        Assert.Equal(GroundworkAdmissionStatus.Degraded,
            provider.GetRequiredService<GroundworkAdmissionRunner>().Latest!.Status);
        Assert.Equal(HealthStatus.Degraded, (await Check(provider)).Status);
    }

    [Fact]
    public async Task The_health_check_reports_every_named_connection()
    {
        var reporting = new HostingFixture();
        fixture.Deploy(HostingFixture.Orders);
        reporting.Deploy(HostingFixture.Orders);
        var services = fixture.Services();
        services.AddGroundwork()
            .AddConnection("primary", fixture.Connect(HostingFixture.Orders))
            .AddConnection("reporting", reporting.Connect(HostingFixture.Orders));
        using var provider = services.BuildServiceProvider(validateScopes: true);

        await HostingFixture.StartAsync(provider);

        var health = await Check(provider);
        Assert.Equal(HealthStatus.Healthy, health.Status);
        Assert.Equal(["primary", "reporting"], health.Data.Keys.Order(StringComparer.Ordinal));
    }

    private static Task<HealthCheckResult> Check(IServiceProvider provider) =>
        provider.GetRequiredService<GroundworkHealthCheck>().CheckHealthAsync(
            new HealthCheckContext
            {
                Registration = new HealthCheckRegistration(
                    GroundworkHealthCheck.Name,
                    _ => provider.GetRequiredService<GroundworkHealthCheck>(),
                    failureStatus: null,
                    tags: null)
            });
}
