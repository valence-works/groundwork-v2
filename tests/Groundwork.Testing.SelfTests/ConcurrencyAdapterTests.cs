using Groundwork.Kernel;
using Groundwork.Store;
using Groundwork.Testing;

namespace Groundwork.Testing.SelfTests;

public sealed class ConcurrencyAdapterTests
{
    [Fact]
    public void Storage_adapter_owns_and_releases_each_concurrency_session()
    {
        var declaration = StorageUnit.Declare("concurrency-owned", "concurrency_owned")
            .String("id", 64, column => column.Required())
            .Key("id")
            .Build();
        var observing = new SessionOpeningConnection(
            new InMemoryProviderFactory().Create("memory://concurrency-owned"));
        using var connection = new StorageProviderConcurrencyConnection(observing, declaration);
        connection.ApplySchema();

        using (connection.OpenSession())
        {
        }

        Assert.Equal(0, observing.NonOwningSessionCount);
        Assert.Equal(1, observing.OwnedSessionCount);
        Assert.Throws<ObjectDisposedException>(() => observing.LastOwnedSession!.Read(
            new StorageKey(new Dictionary<string, object?> { ["id"] = "missing" })));
    }

    private sealed class SessionOpeningConnection(IStorageProviderConnection inner) : IStorageProviderConnection
    {
        public int NonOwningSessionCount { get; private set; }
        public int OwnedSessionCount { get; private set; }
        public IOwnedStorageSession? LastOwnedSession { get; private set; }

        public IProviderCatalog Catalog => inner.Catalog;
        public ISchemaCoordinator Schema => inner.Schema;
        public IReadOnlyList<CapabilityDescriptor> Capabilities => inner.Capabilities;

        public IStorageSession OpenSession(
            StorageUnit unit,
            StorageAccess access,
            IProviderCommandObserver? observer = null)
        {
            NonOwningSessionCount++;
            return inner.OpenSession(unit, access, observer);
        }

        public IOwnedStorageSession OpenOwnedSession(
            StorageUnit unit,
            StorageAccess access,
            IProviderCommandObserver? observer = null)
        {
            OwnedSessionCount++;
            return LastOwnedSession = inner.OpenOwnedSession(unit, access, observer);
        }

        public IUnitOfWork BeginUnitOfWork(StorageAccess access, params StorageUnit[] units) =>
            inner.BeginUnitOfWork(access, units);

        public IUnitOfWork BeginUnitOfWork(
            StorageAccess access,
            BatchWriteOptions options,
            params StorageUnit[] units) => inner.BeginUnitOfWork(access, options, units);

        public IUnitOfWork BeginUnitOfWork(
            StorageAccess access,
            BatchWriteOptions options,
            IProviderCommandObserver? observer,
            params StorageUnit[] units) => inner.BeginUnitOfWork(access, options, observer, units);

        public void Dispose() => inner.Dispose();
    }
}
