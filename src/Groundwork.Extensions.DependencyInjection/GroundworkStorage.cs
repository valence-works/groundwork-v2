using Groundwork.Kernel;
using Groundwork.Store;

namespace Groundwork.Extensions.DependencyInjection;

/// <summary>
/// The per-scope entry point into one named Groundwork connection — inject this from a controller,
/// a minimal-API handler, or any scoped service.
/// </summary>
/// <remarks>
/// <para>
/// The connection behind it is a process singleton. What is scoped is the work: sessions are cheap
/// non-owning views, and every unit of work opened here is owned by the scope. When the scope ends
/// — the request completes, or fails — units of work that never reached commit or rollback are
/// disposed, which rolls them back.
/// </para>
/// </remarks>
public interface IGroundworkStorage : IDisposable
{
    /// <summary>The name of the connection this instance is bound to.</summary>
    string Name { get; }

    /// <summary>
    /// The process-singleton connection. Use it for capability advertisement, catalog reads, and
    /// schema inspection. Do not dispose it — the container owns it.
    /// </summary>
    IStorageProviderConnection Connection { get; }

    /// <summary>Opens a non-owning session view over one declared unit.</summary>
    IStorageSession OpenSession(StorageUnit unit, StorageAccess access);

    /// <summary>Begins a scope-owned unit of work.</summary>
    IUnitOfWork BeginUnitOfWork(StorageAccess access, params StorageUnit[] units);

    /// <summary>Begins a scope-owned unit of work with explicit batch outcome and flush behavior.</summary>
    IUnitOfWork BeginUnitOfWork(StorageAccess access, BatchWriteOptions options, params StorageUnit[] units);
}

internal sealed class GroundworkStorage : IGroundworkStorage
{
    private readonly List<IUnitOfWork> owned = [];
    private bool disposed;

    internal GroundworkStorage(string name, IStorageProviderConnection connection)
    {
        Name = name;
        Connection = connection;
    }

    public string Name { get; }

    public IStorageProviderConnection Connection { get; }

    public IStorageSession OpenSession(StorageUnit unit, StorageAccess access)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return Connection.OpenSession(unit, access);
    }

    public IUnitOfWork BeginUnitOfWork(StorageAccess access, params StorageUnit[] units)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return Own(Connection.BeginUnitOfWork(access, units));
    }

    public IUnitOfWork BeginUnitOfWork(StorageAccess access, BatchWriteOptions options, params StorageUnit[] units)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return Own(Connection.BeginUnitOfWork(access, options, units));
    }

    private IUnitOfWork Own(IUnitOfWork work)
    {
        owned.Add(work);
        return work;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        for (var index = owned.Count - 1; index >= 0; index--)
            owned[index].Dispose();
        owned.Clear();
    }
}
