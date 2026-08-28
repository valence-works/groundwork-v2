using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Sqlite;
using Groundwork.Store;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace Groundwork.Extensions.DependencyInjection.Tests;

/// <summary>
/// Startup admission and the health check. Runtime admission is inspect-only: the host follows the
/// kernel result, including degraded physical index drift and blocked target-fingerprint changes.
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
    public async Task Hosting_and_store_admission_agree_on_a_fatal_primary_schema_gap()
    {
        using var connection = fixture.Provider.Create(fixture.ConnectionString);
        var runtime = connection.Schema.InspectRuntimeAdmission(HostingFixture.Orders);

        var services = fixture.Services();
        services.AddGroundwork().AddConnection(fixture.Connect(HostingFixture.Orders));
        using var provider = services.BuildServiceProvider(validateScopes: true);

        await Assert.ThrowsAsync<GroundworkHostingException>(() => HostingFixture.StartAsync(provider));

        var hosted = Assert.Single(Assert.Single(
            provider.GetRequiredService<GroundworkAdmissionRunner>().Latest!.Connections).Units);
        Assert.Equal(GroundworkRuntimeSchemaAdmissionStatus.Blocked, runtime.Status);
        Assert.Equal(GroundworkAdmissionStatus.Blocked, hosted.Status);
    }

    [Fact]
    public async Task An_index_added_after_deployment_is_blocked_until_the_target_is_applied()
    {
        fixture.Deploy(HostingFixture.OrdersWithoutIndex);
        var services = fixture.Services();
        services.AddGroundwork().AddConnection(fixture.Connect(HostingFixture.Orders));
        using var provider = services.BuildServiceProvider(validateScopes: true);

        var refusal = await Assert.ThrowsAsync<GroundworkHostingException>(
            () => HostingFixture.StartAsync(provider));

        var report = provider.GetRequiredService<GroundworkAdmissionRunner>().Latest!;
        Assert.Equal(GroundworkAdmissionStatus.Blocked, report.Status);
        var unit = Assert.Single(Assert.Single(report.Connections).Units);
        Assert.Equal(GroundworkAdmissionStatus.Blocked, unit.Status);
        Assert.Contains("by_customer", unit.Describe(), StringComparison.Ordinal);

        Assert.Contains("by_customer", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(HealthStatus.Unhealthy, (await Check(provider)).Status);
    }

    [Fact]
    public async Task Hosting_and_store_admission_agree_on_a_degrading_physical_index_gap()
    {
        var database = Path.Combine(Path.GetTempPath(), $"groundwork-hosting-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={database}";
        try
        {
            var factory = new SqliteProviderFactory();
            using (var deployed = factory.Create(connectionString))
                Assert.True(deployed.Schema.Apply(HostingFixture.Orders).Applied);

            using (var mutation = new SqliteConnection(connectionString))
            {
                mutation.Open();
                using var command = mutation.CreateCommand();
                command.CommandText = "DROP INDEX \"__groundwork_ix_6_orders_11_by_customer\";";
                command.ExecuteNonQuery();
            }

            GroundworkRuntimeSchemaAdmissionResult runtime;
            using (var connection = factory.Create(connectionString))
                runtime = connection.Schema.InspectRuntimeAdmission(HostingFixture.Orders);

            var services = fixture.Services();
            services.AddGroundwork().AddConnection(options =>
                options.UseProvider(factory, connectionString).AddUnits(HostingFixture.Orders));
            using var provider = services.BuildServiceProvider(validateScopes: true);

            await HostingFixture.StartAsync(provider);

            var hosted = Assert.Single(Assert.Single(
                provider.GetRequiredService<GroundworkAdmissionRunner>().Latest!.Connections).Units);
            Assert.True(runtime.IsReady);
            Assert.True(runtime.Inspection.HasIndexDrift);
            Assert.Equal(GroundworkRuntimeSchemaAdmissionStatus.Degraded, runtime.Status);
            Assert.Equal(GroundworkAdmissionStatus.Degraded, hosted.Status);
        }
        finally
        {
            if (File.Exists(database))
                File.Delete(database);
        }
    }

    [Fact]
    public void Store_admission_exposes_safe_plan_authorization_to_consumers()
    {
        fixture.Deploy(HostingFixture.OrdersWithProfile);
        using var connection = fixture.Provider.Create(fixture.ConnectionString);

        var result = connection.Schema.InspectRuntimeAdmission(
            HostingFixture.OrdersWithChangedProfile,
            new GroundworkRuntimeSchemaAdmissionOptions { AutoApplyOnStartup = true });

        Assert.Equal(GroundworkRuntimeSchemaAdmissionStatus.Blocked, result.Status);
        Assert.False(result.IsReady);
        Assert.Equal(PhysicalSchemaApplicationOutcome.AuthorizationRequired, result.Application!.Outcome);
        Assert.Contains(result.Refusals, refusal => refusal.Code == "GW-SCHEMA-008");
        Assert.False(connection.Schema.Diff(HostingFixture.OrdersWithChangedProfile).IsEmpty);
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

    // A changed profile is a semantic migration. The kernel requires explicit authorization, so the
    // hosting layer must not start on a result the first runtime session would refuse.
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

        await Assert.ThrowsAsync<GroundworkHostingException>(
            () => HostingFixture.StartAsync(provider));

        var unit = Assert.Single(Assert.Single(
            provider.GetRequiredService<GroundworkAdmissionRunner>().Latest!.Connections).Units);
        Assert.Equal(
            SchemaChangeKind.UpdateAggregationProfile,
            Assert.Single(unit.PendingChanges).Kind);
        Assert.Equal(GroundworkAdmissionStatus.Blocked, unit.Status);
        Assert.False(unit.Applied);

        // The deployed profile is still the one that was deployed: nothing was applied behind the host.
        Assert.False(provider.GetRequiredService<IStorageProviderConnection>()
            .Schema.Diff(HostingFixture.OrdersWithChangedProfile).IsEmpty);
    }

    [Fact]
    public async Task A_changed_aggregation_profile_blocks_startup_without_authorization()
    {
        fixture.Deploy(HostingFixture.OrdersWithProfile);
        var services = fixture.Services();
        services.AddGroundwork().AddConnection(fixture.Connect(HostingFixture.OrdersWithChangedProfile));
        using var provider = services.BuildServiceProvider(validateScopes: true);

        var refusal = await Assert.ThrowsAsync<GroundworkHostingException>(
            () => HostingFixture.StartAsync(provider));

        Assert.Equal(GroundworkHostingDiagnostics.StartupAdmissionBlocked, refusal.Code);
        Assert.Equal(GroundworkAdmissionStatus.Blocked,
            provider.GetRequiredService<GroundworkAdmissionRunner>().Latest!.Status);
        Assert.Equal(HealthStatus.Unhealthy, (await Check(provider)).Status);
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
