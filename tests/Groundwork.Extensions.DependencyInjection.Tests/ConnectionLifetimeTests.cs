using Groundwork.Sqlite;
using Groundwork.Store;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Groundwork.Extensions.DependencyInjection.Tests;

/// <summary>
/// The lifetime model: one connection per database per process, sessions and units of work per
/// scope. These are the behaviors that stop a host from reaching for the per-request connection.
/// </summary>
public sealed class ConnectionLifetimeTests
{
    private readonly HostingFixture fixture = new();

    [Fact]
    public void Connection_is_one_process_singleton_while_storage_is_per_scope()
    {
        fixture.Deploy(HostingFixture.Orders);
        var services = fixture.Services();
        services.AddGroundwork().AddConnection(fixture.Connect(HostingFixture.Orders));
        using var provider = services.BuildServiceProvider(validateScopes: true);

        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        var left = first.ServiceProvider.GetRequiredService<IGroundworkStorage>();
        var right = second.ServiceProvider.GetRequiredService<IGroundworkStorage>();

        Assert.NotSame(left, right);
        Assert.Same(left.Connection, right.Connection);
        Assert.Same(left.Connection, provider.GetRequiredService<IStorageProviderConnection>());
    }

    [Fact]
    public void Named_connections_resolve_as_keyed_services_and_stay_separate()
    {
        var reporting = new HostingFixture();
        fixture.Deploy(HostingFixture.Orders);
        reporting.Deploy(HostingFixture.Orders);
        var services = fixture.Services();
        services.AddGroundwork()
            .AddConnection("primary", fixture.Connect(HostingFixture.Orders))
            .AddConnection("reporting", reporting.Connect(HostingFixture.Orders));
        using var provider = services.BuildServiceProvider(validateScopes: true);

        var primary = provider.GetRequiredKeyedService<IStorageProviderConnection>("primary");
        var secondary = provider.GetRequiredKeyedService<IStorageProviderConnection>("reporting");
        Assert.NotSame(primary, secondary);

        using var scope = provider.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredKeyedService<IGroundworkStorage>("reporting");
        Assert.Equal("reporting", storage.Name);
        Assert.Same(secondary, storage.Connection);
        Assert.Equal(["primary", "reporting"], provider.GetRequiredService<IGroundworkConnections>().Names);
    }

    [Fact]
    public void A_hand_written_scoped_connection_registration_is_refused_by_code()
    {
        var services = fixture.Services();
        services.AddGroundwork().AddConnection(fixture.Connect());
        services.AddScoped<IStorageProviderConnection>(_ => fixture.Provider.Create(fixture.ConnectionString));
        using var provider = services.BuildServiceProvider();

        var refusal = Assert.Throws<GroundworkHostingException>(
            () => provider.GetRequiredService<IGroundworkConnections>());
        Assert.Equal(GroundworkHostingDiagnostics.ConnectionLifetime, refusal.Code);
        Assert.Contains("IGroundworkStorage", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_transient_connection_registration_added_after_AddGroundwork_is_still_refused()
    {
        var services = fixture.Services();
        services.AddGroundwork().AddConnection(fixture.Connect());
        services.AddSingleton("an unrelated later registration");
        services.AddTransient<IStorageProviderConnection>(_ => fixture.Provider.Create(fixture.ConnectionString));
        using var provider = services.BuildServiceProvider();

        var refusal = Assert.Throws<GroundworkHostingException>(
            () => provider.GetRequiredService<IGroundworkConnections>());
        Assert.Equal(GroundworkHostingDiagnostics.ConnectionLifetime, refusal.Code);
    }

    [Fact]
    public void A_duplicate_connection_name_is_refused()
    {
        var builder = fixture.Services().AddGroundwork().AddConnection("primary", fixture.Connect());
        var refusal = Assert.Throws<GroundworkHostingException>(
            () => builder.AddConnection("primary", fixture.Connect()));
        Assert.Equal(GroundworkHostingDiagnostics.DuplicateConnectionName, refusal.Code);
    }

    [Fact]
    public void An_unregistered_connection_name_is_refused_and_names_the_registered_ones()
    {
        var services = fixture.Services();
        services.AddGroundwork().AddConnection("primary", fixture.Connect());
        using var provider = services.BuildServiceProvider();

        var refusal = Assert.Throws<GroundworkHostingException>(
            () => provider.GetRequiredService<IGroundworkConnections>().Get("reporting"));
        Assert.Equal(GroundworkHostingDiagnostics.UnknownConnectionName, refusal.Code);
        Assert.Contains("primary", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_connection_without_a_provider_factory_is_refused()
    {
        var services = fixture.Services();
        services.AddGroundwork().AddConnection(options => options.ConnectionString = "somewhere");
        using var provider = services.BuildServiceProvider();

        var refusal = Assert.Throws<GroundworkHostingException>(
            () => provider.GetRequiredService<IGroundworkConnections>().Default);
        Assert.Equal(GroundworkHostingDiagnostics.IncompleteConnection, refusal.Code);
        Assert.Contains("provider factory", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_uncommitted_unit_of_work_is_rolled_back_when_its_scope_ends()
    {
        fixture.Deploy(HostingFixture.Orders);
        var services = fixture.Services();
        services.AddGroundwork().AddConnection(fixture.Connect(HostingFixture.Orders));
        using var provider = services.BuildServiceProvider(validateScopes: true);

        using (var scope = provider.CreateScope())
        {
            var storage = scope.ServiceProvider.GetRequiredService<IGroundworkStorage>();
            var work = storage.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, HostingFixture.Orders);
            work.Stage(RowWrite.Upsert(HostingFixture.Orders, Order("abandoned")));
            // No commit and no rollback: the request failed halfway through.
        }

        var session = provider.GetRequiredService<IStorageProviderConnection>()
            .OpenSession(HostingFixture.Orders, StorageAccess.Global);
        Assert.Null(session.Read(new StorageKey(new Dictionary<string, object?> { ["id"] = "abandoned" })));
    }

    [Fact]
    public void A_committed_unit_of_work_survives_its_scope_ending()
    {
        fixture.Deploy(HostingFixture.Orders);
        var services = fixture.Services();
        services.AddGroundwork().AddConnection(fixture.Connect(HostingFixture.Orders));
        using var provider = services.BuildServiceProvider(validateScopes: true);

        using (var scope = provider.CreateScope())
        {
            var storage = scope.ServiceProvider.GetRequiredService<IGroundworkStorage>();
            var work = storage.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, HostingFixture.Orders);
            work.Stage(RowWrite.Upsert(HostingFixture.Orders, Order("kept")));
            work.CommitWithOutcomes();
        }

        var session = provider.GetRequiredService<IStorageProviderConnection>()
            .OpenSession(HostingFixture.Orders, StorageAccess.Global);
        Assert.NotNull(session.Read(new StorageKey(new Dictionary<string, object?> { ["id"] = "kept" })));
    }

    // SQLite is the provider that makes the lifetime model non-negotiable: the store holds one
    // schema-lock file handle for the life of the connection.
    [Fact]
    public void One_registered_sqlite_connection_serves_every_scope_and_a_second_one_is_refused()
    {
        var database = Path.Combine(Path.GetTempPath(), $"gw-hosting-{Guid.NewGuid():N}.db");
        try
        {
            var services = fixture.Services();
            services.AddGroundwork().AddConnection(options => options
                .UseProvider(new SqliteProviderFactory(), $"Data Source={database}")
                .AddUnits(HostingFixture.Orders));
            using var provider = services.BuildServiceProvider(validateScopes: true);

            using var first = provider.CreateScope();
            using var second = provider.CreateScope();
            Assert.Same(
                first.ServiceProvider.GetRequiredService<IGroundworkStorage>().Connection,
                second.ServiceProvider.GetRequiredService<IGroundworkStorage>().Connection);

            var refusal = Assert.Throws<InvalidOperationException>(
                () => new SqliteProviderFactory().Create($"Data Source={database}"));
            Assert.Contains("GW-SQLITE-LIFETIME-001", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("AddGroundwork().AddConnection", refusal.Message, StringComparison.Ordinal);
        }
        finally
        {
            foreach (var path in new[] { database, database + ".schema.lock" })
                File.Delete(path);
        }
    }

    private static StorageValues Order(string id) => new(new Dictionary<string, object?>
    {
        ["id"] = id,
        ["customer"] = "ada",
        ["total"] = 10m
    });
}
