using Groundwork.Query.Linq.Execution;
using Groundwork.Query.Model;
using Groundwork.Store;

namespace Groundwork.Query.Linq.Sqlite;

/// <summary>
/// Configured async adapter for a closed LINQ database over an existing SQLite session.
/// <para>
/// SQLite's execution path is not SQLite-specific: it is <see cref="GwLinqExecutor"/>, the one
/// adapter every provider uses. This type remains as the named SQLite entry point and adds nothing
/// of its own.
/// </para>
/// </summary>
public sealed class SqliteLinqExecutor : IGwQueryExecutor
{
    private readonly GwLinqExecutor executor;

    public SqliteLinqExecutor(IStorageSession session)
        : this(session, connection: null)
    {
    }

    /// <summary>
    /// Admits queries against the declared indexes the connection's catalog proves are deployed, and
    /// under the budgets it advertises, so a declared-but-undeployed index cannot rescue a query
    /// during a rolling deploy and the fence uses SQLite's real parameter ceiling.
    /// </summary>
    public SqliteLinqExecutor(IStorageSession session, IStorageProviderConnection? connection) =>
        executor = new GwLinqExecutor(session, connection);

    public Task<IReadOnlyList<T>> ToListAsync<T>(
        QueryRequest request,
        GwTableModel<T>? model = null,
        CancellationToken cancellationToken = default) =>
        executor.ToListAsync(request, model, cancellationToken);

    public Task<long> CountAsync(QueryRequest request, CancellationToken cancellationToken = default) =>
        executor.CountAsync(request, cancellationToken);

    public Task<bool> AnyAsync(QueryRequest request, CancellationToken cancellationToken = default) =>
        executor.AnyAsync(request, cancellationToken);
}
