using System.Collections.Immutable;
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
    private readonly IProviderCatalog? catalog;
    private readonly Lazy<RuntimeCoverageGate> gate;
    private readonly Lazy<QueryRenderOptions> renderOptions;

    /// <summary>
    /// Executes against one open session, admitting queries against the indexes its storage unit
    /// declares. Prefer the overload taking the provider catalog: without it an index that was
    /// declared but never deployed can still satisfy the gate.
    /// </summary>
    public GwLinqExecutor(IStorageSession session)
        : this(session, catalog: null)
    {
    }

    /// <summary>
    /// Executes against one open session, admitting queries against the declared indexes the
    /// provider catalog proves are deployed. An index that exists only in the database is never a
    /// candidate, so a query cannot pass here and fail after the next clean deploy.
    /// </summary>
    public GwLinqExecutor(IStorageSession session, IProviderCatalog? catalog)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.catalog = catalog;
        gate = new Lazy<RuntimeCoverageGate>(CreateGate, LazyThreadSafetyMode.ExecutionAndPublication);
        renderOptions = new Lazy<QueryRenderOptions>(
            () => this.session.Unit.CreateQueryRenderOptions(), LazyThreadSafetyMode.ExecutionAndPublication);
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
        return session is IAsyncQueryStorageSession asyncQuery
            ? asyncQuery.QueryAsync(executed, renderOptions.Value, cancellationToken)
            : Task.FromResult(session.Query(executed, renderOptions.Value));
    }

    private RuntimeCoverageGate CreateGate()
    {
        var declared = DeclaredIndexes(session.Unit);
        var profile = session.GetQueryAdmission();
        return new RuntimeCoverageGate(
            declared,
            DeployedIndexes(declared),
            options: new RuntimeCoverageGateOptions
            {
                ValueFence = new RuntimeValueFenceOptions
                {
                    MaximumParameters = profile.MaximumParameters,
                    MaximumInValues = profile.MaximumInValues
                }
            });
    }

    /// <summary>Converts the admitted declaration into the checker's provider-neutral index shape.</summary>
    private static ImmutableArray<CoverageIndex> DeclaredIndexes(StorageUnit unit)
    {
        var nullable = unit.Columns.ToDictionary(column => column.Name, column => column.IsNullable, StringComparer.Ordinal);
        return unit.Indexes
            .Select(index => new CoverageIndex(
                index.Name,
                index.Columns.Select(column => new CoverageIndexColumn(
                    column.Column,
                    column.Direction == SortDirection.Descending ? OrderDirection.Descending : OrderDirection.Ascending,
                    !nullable.TryGetValue(column.Column, out var isNullable) || isNullable)),
                index.MissingValues == MissingValueBehavior.Excluded
                    ? IndexMissingValueBehavior.Excluded
                    : IndexMissingValueBehavior.Included))
            .ToImmutableArray();
    }

    /// <summary>
    /// Keeps only the declared indexes the catalog reports as present. Every provider catalog reports
    /// declared names, so an extra native index nobody declared is never a candidate — on MongoDB just
    /// as on the relational providers.
    /// </summary>
    private ImmutableArray<CoverageIndex> DeployedIndexes(ImmutableArray<CoverageIndex> declared)
    {
        if (catalog is null)
            return declared;
        var deployed = catalog.ReadIndexes(session.Unit.Id)
            .Select(index => index.Name)
            .ToHashSet(StringComparer.Ordinal);
        return declared.Where(index => deployed.Contains(index.Name)).ToImmutableArray();
    }
}
