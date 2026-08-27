using Groundwork.Kernel;
using Groundwork.Store;

namespace Groundwork.Extensions.DependencyInjection.Tests;

/// <summary>
/// Wraps a real provider factory and counts how often the container asks the connection to dispose,
/// separately from how often that request actually released anything.
/// </summary>
internal sealed class CountingProviderFactory(IStorageProviderFactory inner) : IStorageProviderFactory
{
    internal CountingConnection? Created { get; private set; }

    public IStorageProviderConnection Create(string connectionString) =>
        Created = new CountingConnection(inner.Create(connectionString));
}

/// <summary>A pass-through connection that records disposal requests and effective disposals.</summary>
internal sealed class CountingConnection(IStorageProviderConnection inner) : IStorageProviderConnection
{
    private readonly List<CountingUnitOfWork> units = [];
    private bool disposed;

    /// <summary>Every unit of work this connection has handed out, in order.</summary>
    internal IReadOnlyList<CountingUnitOfWork> Units => units;

    /// <summary>How many times <see cref="Dispose"/> was called.</summary>
    internal int DisposeRequests { get; private set; }

    /// <summary>How many of those calls actually released the provider resources.</summary>
    internal int EffectiveDisposals { get; private set; }

    public IProviderCatalog Catalog => inner.Catalog;

    public ISchemaCoordinator Schema => inner.Schema;

    public IReadOnlyList<CapabilityDescriptor> Capabilities => inner.Capabilities;

    public IStorageSession OpenSession(StorageUnit unit, StorageAccess access, IProviderCommandObserver? observer = null) =>
        inner.OpenSession(unit, access, observer);

    public IOwnedStorageSession OpenOwnedSession(StorageUnit unit, StorageAccess access, IProviderCommandObserver? observer = null) =>
        inner.OpenOwnedSession(unit, access, observer);

    public IUnitOfWork BeginUnitOfWork(StorageAccess access, params StorageUnit[] storageUnits) =>
        Track(inner.BeginUnitOfWork(access, storageUnits));

    public IUnitOfWork BeginUnitOfWork(StorageAccess access, BatchWriteOptions options, params StorageUnit[] storageUnits) =>
        Track(inner.BeginUnitOfWork(access, options, storageUnits));

    public IUnitOfWork BeginUnitOfWork(
        StorageAccess access,
        BatchWriteOptions options,
        IProviderCommandObserver? observer,
        params StorageUnit[] storageUnits) =>
        Track(inner.BeginUnitOfWork(access, options, observer, storageUnits));

    private IUnitOfWork Track(IUnitOfWork work)
    {
        var counting = new CountingUnitOfWork(work);
        units.Add(counting);
        return counting;
    }

    public void Dispose()
    {
        DisposeRequests++;
        if (disposed)
            return;
        disposed = true;
        EffectiveDisposals++;
        inner.Dispose();
    }
}

/// <summary>A pass-through unit of work that records how often it was asked to dispose.</summary>
internal sealed class CountingUnitOfWork(IUnitOfWork inner) : IUnitOfWork
{
    internal int DisposeRequests { get; private set; }

    public IStorageSession OpenSession(StorageUnit unit) => inner.OpenSession(unit);

    public void Stage(RowWrite write) => inner.Stage(write);

    public BatchWriteSummary Commit() => inner.Commit();

    public BatchWriteReport CommitWithOutcomes() => inner.CommitWithOutcomes();

    public ValueTask<BatchWriteReport> CommitWithOutcomesAsync(CancellationToken cancellationToken = default) =>
        inner.CommitWithOutcomesAsync(cancellationToken);

    public ValueTask<BatchWriteSummary> CommitAsync(CancellationToken cancellationToken = default) =>
        inner.CommitAsync(cancellationToken);

    public void Rollback() => inner.Rollback();

    public void Dispose()
    {
        DisposeRequests++;
        inner.Dispose();
    }
}
