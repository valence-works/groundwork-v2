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

    // A request scope hides this: the list of owned units of work dies with the request either way.
    // A BackgroundService that holds one scope for the life of the process does not, so a unit of
    // work must stop being owned the moment it becomes terminal.
    [Fact]
    public void A_long_lived_scope_stops_owning_each_unit_of_work_once_it_commits()
    {
        fixture.Deploy(HostingFixture.Orders);
        var counting = new CountingProviderFactory(fixture.Provider);
        var services = fixture.Services();
        services.AddGroundwork().AddConnection(options => options
            .UseProvider(counting, fixture.ConnectionString)
            .AddUnits(HostingFixture.Orders));
        using var provider = services.BuildServiceProvider(validateScopes: true);

        using (var scope = provider.CreateScope())
        {
            var storage = scope.ServiceProvider.GetRequiredService<IGroundworkStorage>();
            foreach (var index in Enumerable.Range(0, 5))
            {
                var work = storage.BeginUnitOfWork(
                    StorageAccess.Global, BatchWriteOptions.Exact, HostingFixture.Orders);
                work.Stage(RowWrite.Upsert(HostingFixture.Orders, Order($"looped-{index}")));
                work.CommitWithOutcomes();
            }

            Assert.Equal(5, counting.Created!.Units.Count);
        }

        // A committed unit is already terminal and already released. If the scope were still holding
        // them, ending it would dispose every one of them here.
        Assert.All(counting.Created!.Units, work => Assert.Equal(0, work.DisposeRequests));
    }

    [Fact]
    public void Finished_scopes_release_their_owned_sessions_instead_of_retaining_provider_handles()
    {
        fixture.Deploy(HostingFixture.Orders);
        var counting = new CountingProviderFactory(fixture.Provider);
        var services = fixture.Services();
        services.AddGroundwork().AddConnection(options => options
            .UseProvider(counting, fixture.ConnectionString)
            .AddUnits(HostingFixture.Orders));
        using var provider = services.BuildServiceProvider(validateScopes: true);

        var sessions = new List<IOwnedStorageSession>();
        foreach (var index in Enumerable.Range(0, 5))
        {
            using var scope = provider.CreateScope();
            var storage = scope.ServiceProvider.GetRequiredService<IGroundworkStorage>();
            var session = storage.OpenSession(HostingFixture.Orders, StorageAccess.Global);
            sessions.Add(Assert.IsAssignableFrom<IOwnedStorageSession>(session));
            Assert.Null(session.Read(new StorageKey(new Dictionary<string, object?>
            {
                ["id"] = $"not-present-{index}"
            })));
        }

        Assert.Equal(5, counting.Created!.Sessions.Count);
        Assert.All(sessions, session => Assert.Throws<ObjectDisposedException>(() => session.Read(
            new StorageKey(new Dictionary<string, object?> { ["id"] = "after-scope" }))));
    }

    [Fact]
    public async Task Async_scope_disposal_releases_owned_sessions()
    {
        fixture.Deploy(HostingFixture.Orders);
        var counting = new CountingProviderFactory(fixture.Provider);
        var services = fixture.Services();
        services.AddGroundwork().AddConnection(options => options
            .UseProvider(counting, fixture.ConnectionString)
            .AddUnits(HostingFixture.Orders));
        await using var provider = services.BuildServiceProvider(validateScopes: true);

        IOwnedStorageSession session;
        await using (var scope = provider.CreateAsyncScope())
        {
            session = Assert.IsAssignableFrom<IOwnedStorageSession>(scope.ServiceProvider
                .GetRequiredService<IGroundworkStorage>()
                .OpenSession(HostingFixture.Orders, StorageAccess.Global));
        }

        Assert.Throws<ObjectDisposedException>(() => session.Read(
            new StorageKey(new Dictionary<string, object?> { ["id"] = "after-async-scope" })));
    }

    [Fact]
    public void A_scope_still_disposes_a_unit_of_work_that_never_reached_a_terminal_call()
    {
        fixture.Deploy(HostingFixture.Orders);
        var counting = new CountingProviderFactory(fixture.Provider);
        var services = fixture.Services();
        services.AddGroundwork().AddConnection(options => options
            .UseProvider(counting, fixture.ConnectionString)
            .AddUnits(HostingFixture.Orders));
        using var provider = services.BuildServiceProvider(validateScopes: true);

        using (var scope = provider.CreateScope())
        {
            var storage = scope.ServiceProvider.GetRequiredService<IGroundworkStorage>();
            var committed = storage.BeginUnitOfWork(
                StorageAccess.Global, BatchWriteOptions.Exact, HostingFixture.Orders);
            committed.Stage(RowWrite.Upsert(HostingFixture.Orders, Order("committed")));
            committed.CommitWithOutcomes();

            var abandoned = storage.BeginUnitOfWork(
                StorageAccess.Global, BatchWriteOptions.Exact, HostingFixture.Orders);
            abandoned.Stage(RowWrite.Upsert(HostingFixture.Orders, Order("abandoned")));
        }

        Assert.Equal(0, counting.Created!.Units[0].DisposeRequests);
        Assert.Equal(1, counting.Created!.Units[1].DisposeRequests);

        var session = provider.GetRequiredService<IStorageProviderConnection>()
            .OpenSession(HostingFixture.Orders, StorageAccess.Global);
        Assert.NotNull(session.Read(new StorageKey(new Dictionary<string, object?> { ["id"] = "committed" })));
        Assert.Null(session.Read(new StorageKey(new Dictionary<string, object?> { ["id"] = "abandoned" })));
    }

    // The default connection is reachable two ways — keyed by name and unkeyed — and both descriptors
    // are factories returning the one instance the container already owns, so the container tracks it
    // for disposal twice. That is safe because IDisposable requires Dispose to be callable more than
    // once, and every Groundwork provider connection honours it. This test is what stops that from
    // being a convention a future provider change could silently break.
    [Fact]
    public void Resolving_the_connection_both_keyed_and_unkeyed_still_disposes_it_exactly_once()
    {
        fixture.Deploy(HostingFixture.Orders);
        var counting = new CountingProviderFactory(fixture.Provider);
        var services = fixture.Services();
        services.AddGroundwork().AddConnection(options => options
            .UseProvider(counting, fixture.ConnectionString)
            .AddUnits(HostingFixture.Orders));

        var provider = services.BuildServiceProvider(validateScopes: true);
        var unkeyed = provider.GetRequiredService<IStorageProviderConnection>();
        var keyed = provider.GetRequiredKeyedService<IStorageProviderConnection>(
            GroundworkConnectionOptions.DefaultName);
        using (var scope = provider.CreateScope())
        {
            var scopedUnkeyed = scope.ServiceProvider.GetRequiredService<IGroundworkStorage>();
            var scopedKeyed = scope.ServiceProvider.GetRequiredKeyedService<IGroundworkStorage>(
                GroundworkConnectionOptions.DefaultName);
            Assert.Same(scopedUnkeyed, scopedKeyed);
            Assert.Same(unkeyed, scopedUnkeyed.Connection);
        }

        var connection = counting.Created!;
        Assert.Same(unkeyed, keyed);
        Assert.Equal(0, connection.DisposeRequests);

        provider.Dispose();

        Assert.True(connection.DisposeRequests > 1,
            "This test only proves anything while both aliases are tracked for disposal. If the " +
            "container stopped double-tracking, tighten the assertion rather than deleting it.");
        Assert.Equal(1, connection.EffectiveDisposals);

        // A second disposal of the whole container must stay quiet too.
        provider.Dispose();
        Assert.Equal(1, connection.EffectiveDisposals);
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
