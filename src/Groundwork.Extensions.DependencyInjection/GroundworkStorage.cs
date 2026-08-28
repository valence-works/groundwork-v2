using Groundwork.Kernel;
using Groundwork.Store;
using System.Runtime.ExceptionServices;

namespace Groundwork.Extensions.DependencyInjection;

/// <summary>
/// The per-scope entry point into one named Groundwork connection — inject this from a controller,
/// a minimal-API handler, or any scoped service.
/// </summary>
/// <remarks>
/// <para>
/// The connection behind it is a process singleton. What is scoped is the work: sessions opened here
/// are scope-owned handles, and every unit of work opened here is owned by the scope. When the scope
/// ends — the request completes, or fails — sessions are released and units of work that never
/// reached commit or rollback are disposed, which rolls them back.
/// </para>
/// <para>
/// A unit of work stops being owned the moment it becomes terminal, so a scope that outlives a
/// single request — a <c>BackgroundService</c> holding one scope for the life of the process — does
/// not accumulate them.
/// </para>
/// </remarks>
public interface IGroundworkStorage : IDisposable, IAsyncDisposable
{
    /// <summary>The name of the connection this instance is bound to.</summary>
    string Name { get; }

    /// <summary>
    /// The process-singleton connection. Use it for capability advertisement, catalog reads, and
    /// schema inspection. Do not dispose it — the container owns it.
    /// </summary>
    IStorageProviderConnection Connection { get; }

    /// <summary>Opens a scope-owned session over one declared unit.</summary>
    IStorageSession OpenSession(StorageUnit unit, StorageAccess access);

    /// <summary>Begins a scope-owned unit of work.</summary>
    IUnitOfWork BeginUnitOfWork(StorageAccess access, params StorageUnit[] units);

    /// <summary>Begins a scope-owned unit of work with explicit batch outcome and flush behavior.</summary>
    IUnitOfWork BeginUnitOfWork(StorageAccess access, BatchWriteOptions options, params StorageUnit[] units);
}

internal sealed class GroundworkStorage : IGroundworkStorage
{
    private readonly object gate = new();
    private readonly List<IOwnedStorageSession> sessions = [];
    private readonly List<ScopedUnitOfWork> owned = [];
    private bool disposed;

    internal int TrackedSessionCount
    {
        get
        {
            lock (gate)
                return sessions.Count;
        }
    }

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
        var session = Connection.OpenOwnedSession(unit, access);
        lock (gate)
        {
            if (!disposed)
            {
                sessions.RemoveAll(static candidate => candidate.IsReleased);
                sessions.Add(session);
                return session;
            }
        }

        // The scope ended while the provider was opening this session. Nothing will ever come back
        // for it, so release it here rather than retaining a checked-out provider resource.
        session.Dispose();
        throw new ObjectDisposedException(nameof(GroundworkStorage));
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
        var scoped = new ScopedUnitOfWork(this, work);
        lock (gate)
        {
            if (!disposed)
            {
                owned.Add(scoped);
                return scoped;
            }
        }

        // The scope ended while the provider was opening this unit of work. Nothing will ever come
        // back for it, so release it here rather than leaking its provider connection.
        work.Dispose();
        throw new ObjectDisposedException(nameof(GroundworkStorage));
    }

    private void Release(ScopedUnitOfWork work)
    {
        lock (gate)
        {
            if (!disposed)
                owned.Remove(work);
        }
    }

    public void Dispose()
    {
        IOwnedStorageSession[] pendingSessions;
        ScopedUnitOfWork[] pending;
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            pendingSessions = [.. sessions];
            sessions.Clear();
            pending = [.. owned];
            owned.Clear();
        }

        ResourceCleanup.RunAll(
            pendingSessions.AsEnumerable().Reverse().Select(session => (Action)(() => session.Dispose()))
                .Concat(pending.AsEnumerable().Reverse().Select(work => (Action)(() => work.Dispose()))));
    }

    public async ValueTask DisposeAsync()
    {
        IOwnedStorageSession[] pendingSessions;
        ScopedUnitOfWork[] pending;
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            pendingSessions = [.. sessions];
            sessions.Clear();
            pending = [.. owned];
            owned.Clear();
        }

        await ResourceCleanup.RunAllAsync(
            pendingSessions.AsEnumerable().Reverse().Select(session => (Func<ValueTask>)(() => session.DisposeAsync()))
                .Concat(pending.AsEnumerable().Reverse().Select(work => (Func<ValueTask>)(() =>
                {
                    work.Dispose();
                    return ValueTask.CompletedTask;
                })))).ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps the scope's list of live units of work honest.
    /// </summary>
    /// <remarks>
    /// Without this, the list only ever grows: a request scope hides that because the list dies with
    /// the request, but a scope held for the life of a process would retain one dead unit of work per
    /// iteration, each holding a provider connection nobody will release. Releasing on the terminal
    /// call rather than trusting the caller to dispose keeps the correct thing automatic — the same
    /// reason the connection lifetime is not a setting.
    ///
    /// Release happens only when the terminal call returns normally. A commit that throws leaves the
    /// unit owned, so scope disposal still gets its chance at it.
    /// </remarks>
    private sealed class ScopedUnitOfWork(GroundworkStorage owner, IUnitOfWork inner) : IUnitOfWork
    {
        public IStorageSession OpenSession(StorageUnit unit) => inner.OpenSession(unit);

        public void Stage(RowWrite write) => inner.Stage(write);

        public BatchWriteSummary Commit() => Complete(inner.Commit());

        public BatchWriteReport CommitWithOutcomes() => Complete(inner.CommitWithOutcomes());

        public async ValueTask<BatchWriteReport> CommitWithOutcomesAsync(CancellationToken cancellationToken = default) =>
            Complete(await inner.CommitWithOutcomesAsync(cancellationToken).ConfigureAwait(false));

        public async ValueTask<BatchWriteSummary> CommitAsync(CancellationToken cancellationToken = default) =>
            Complete(await inner.CommitAsync(cancellationToken).ConfigureAwait(false));

        public void Rollback()
        {
            inner.Rollback();
            owner.Release(this);
        }

        public void Dispose()
        {
            try
            {
                inner.Dispose();
            }
            finally
            {
                owner.Release(this);
            }
        }

        private T Complete<T>(T result)
        {
            owner.Release(this);
            return result;
        }
    }
}

/// <summary>Runs every cleanup step even when an earlier resource fails to dispose.</summary>
internal static class ResourceCleanup
{
    internal static void RunAll(IEnumerable<Action> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var failures = new List<Exception>();
        foreach (var step in steps)
        {
            ArgumentNullException.ThrowIfNull(step);
            try { step(); }
            catch (Exception failure) { failures.Add(failure); }
        }
        ThrowIfAny(failures);
    }

    internal static async ValueTask RunAllAsync(IEnumerable<Func<ValueTask>> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var failures = new List<Exception>();
        foreach (var step in steps)
        {
            ArgumentNullException.ThrowIfNull(step);
            try { await step().ConfigureAwait(false); }
            catch (Exception failure) { failures.Add(failure); }
        }
        ThrowIfAny(failures);
    }

    private static void ThrowIfAny(IReadOnlyCollection<Exception> failures)
    {
        if (failures.Count == 0)
            return;

        // Reuse the store's cleanup-failure convention: preserve the first exception and its stack,
        // and attach any later failures rather than replacing the primary signal.
        WriteFailureCleanup.RunAll(failures
            .Select(failure => (Action)(() => ExceptionDispatchInfo.Capture(failure).Throw()))
            .ToArray());
    }
}
