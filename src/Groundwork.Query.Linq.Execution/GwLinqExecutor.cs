using System.Threading;
using Groundwork.Kernel;
using Groundwork.Query.Model;
using Groundwork.Query.Planning;
using Groundwork.Store;

namespace Groundwork.Query.Linq.Execution;

/// <summary>
/// The one execution adapter behind the closed LINQ front-end, for every provider.
/// <para>
/// There is deliberately no per-provider executor. Everything an executor does — admitting the
/// request through the shared coverage gate, honoring an explicit scan acceptance, carrying the
/// caller's paging and keyset continuation, materializing rows, and answering the async terminals —
/// is provider-neutral, and a second copy of it per provider would be a second place for the
/// flagship safety property to drift. What is genuinely provider-specific is the dialect, which each
/// provider already owns behind <see cref="IStorageSession.Query"/>, and its native budgets, which it
/// advertises through <see cref="QueryAdmissionProfile"/>.
/// </para>
/// </summary>
public sealed class GwLinqExecutor : IGwQueryExecutor
{
    private readonly IStorageSession session;
    private readonly IStorageProviderConnection? connection;
    private readonly QueryAdmissionProfile? admission;
    private readonly Lazy<RuntimeCoverageGate> gate;

    /// <summary>
    /// Executes against one open session, admitting queries against the indexes its storage unit
    /// declares and under the portable fence defaults. Prefer the overload taking the connection the
    /// session came from: without it an index that was declared but never deployed can still satisfy
    /// the gate, and the fence cannot use the provider's real budgets.
    /// </summary>
    public GwLinqExecutor(IStorageSession session)
        : this(session, connection: null)
    {
    }

    /// <summary>
    /// Executes against one open session, admitting queries against the declared indexes the
    /// connection's catalog proves are deployed, and under the budgets that connection advertises.
    /// An index that exists only in the database is never a candidate, so a query cannot pass here
    /// and fail after the next clean deploy.
    /// <para>
    /// Both inputs come from the connection rather than the session on purpose: a session decorator
    /// that does not forward an optional interface would silently drop them, and the budgets would
    /// then fall back to defaults that are not this provider's.
    /// </para>
    /// </summary>
    public GwLinqExecutor(IStorageSession session, IStorageProviderConnection? connection)
        : this(session, connection, admission: null)
    {
    }

    /// <summary>
    /// Executes against one open session under budgets the caller already knows, for a
    /// provider-named adapter that is bound to its provider at compile time and so does not need a
    /// connection to learn them. Coverage still admits against the declaration alone: without a
    /// connection there is no catalog, so a declared-but-undeployed index can still satisfy the gate.
    /// </summary>
    /// <remarks>
    /// A factory rather than a constructor. A second two-argument constructor taking a reference
    /// type would make the existing <c>new GwLinqExecutor(session, null)</c> ambiguous — neither
    /// <see cref="IStorageProviderConnection"/> nor <see cref="QueryAdmissionProfile"/> is more
    /// specific for a null literal, so overload resolution has no tie to break and the call stops
    /// compiling (CS0121). Naming the profile at the call site costs nothing and breaks nobody.
    /// </remarks>
    public static GwLinqExecutor WithAdmission(IStorageSession session, QueryAdmissionProfile admission) =>
        new(session, connection: null, admission ?? throw new ArgumentNullException(nameof(admission)));

    private GwLinqExecutor(
        IStorageSession session,
        IStorageProviderConnection? connection,
        QueryAdmissionProfile? admission)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.connection = connection;
        this.admission = admission;
        gate = new Lazy<RuntimeCoverageGate>(CreateGate, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public async Task<IReadOnlyList<T>> ToListAsync<T>(
        QueryRequest request,
        GwTableModel<T>? model = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await ExecuteAsync(request, request, cancellationToken).ConfigureAwait(false);
        var materialize = LinqRowMaterializer.For(model);
        var rows = new T[result.Rows.Count];
        for (var index = 0; index < rows.Length; index++)
            rows[index] = materialize(result.Rows[index]);
        return rows;
    }

    public async Task<long> CountAsync(QueryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await ExecuteAsync(request, QueryRequestExecution.ForProviderCount(request), cancellationToken)
            .ConfigureAwait(false);
        return QueryRequestExecution.RequireTotalCount(request, result.TotalCount);
    }

    public async Task<bool> AnyAsync(QueryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await ExecuteAsync(request, QueryRequestExecution.ForExistenceProbe(request), cancellationToken)
            .ConfigureAwait(false);
        return result.Rows.Count != 0;
    }

    /// <summary>
    /// Admits the request the caller actually wrote — not the narrowed count or existence probe
    /// derived from it — so a refusal carries the same code and the same named fix the analyzer
    /// reported at build time. Only then is the derived request executed.
    /// </summary>
    private Task<QueryMaterializedResult> ExecuteAsync(
        QueryRequest admitted,
        QueryRequest executed,
        CancellationToken cancellationToken)
    {
        // A caller who already cancelled gets cancellation, not a coverage verdict on work that
        // will never run.
        cancellationToken.ThrowIfCancellationRequested();
        gate.Value.EnsureCovered(admitted, DateTimeOffset.UtcNow);
        // Deliberately no render options. Passing the unit's declared index metadata would buy
        // optional index-selection evidence and cost correctness: that conversion is eager over
        // every declared index and refuses a non-queryable one (GW-QUERY-018), so a single JSON
        // index column would fail every query against the unit — including ones that never touch
        // it. Declaration validation does not catch it either, because the guard that refuses a
        // JSON index key runs only in the fluent builder, not in Schema.Apply. The provider adds
        // its own identity tie-breaks regardless, so nothing the executor needs is lost.
        return session.QueryAsync(executed, options: null, cancellationToken).AsTask();
    }

    private RuntimeCoverageGate CreateGate()
        => RuntimeCoverage.ForQuery(session, connection, admission);
}
