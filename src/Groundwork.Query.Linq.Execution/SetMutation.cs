using System.Collections.Immutable;
using Groundwork.Kernel;
using Groundwork.Query.Model;
using Groundwork.Query.Planning;
using Groundwork.Store;

namespace Groundwork.Query.Linq.Execution;

/// <summary>
/// The public set-based mutation entry point. It admits the mutation under the coverage rule an
/// equivalent read is admitted under, and only then reaches the provider.
/// <para>
/// It lives here, beside <see cref="GwLinqExecutor"/>, because this is where a read is admitted:
/// the coverage checker is provider-neutral and depends only on the query model, and
/// <c>Groundwork.Store</c> is held to referencing the two kernel assemblies and nothing else. The
/// capability seam itself stays in <c>Groundwork.Store</c>, where providers implement it — so a
/// caller who reaches <see cref="ISetMutationStorageSession"/> directly bypasses admission exactly
/// as a caller who reaches <see cref="IStorageSession.Query"/> directly bypasses the query gate.
/// </para>
/// </summary>
public static class SetMutationSessionExtensions
{
    public static SetMutationResult UpdateWhere(
        this IStorageSession session,
        Predicate where,
        IReadOnlyDictionary<string, object?> assignments,
        SetMutationOptions? options = null)
    {
        var (native, predicate, physical) = PrepareUpdate(session, where, assignments, options);
        return native.UpdateWhere(predicate, physical);
    }

    public static ValueTask<SetMutationResult> UpdateWhereAsync(
        this IStorageSession session,
        Predicate where,
        IReadOnlyDictionary<string, object?> assignments,
        SetMutationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var (native, predicate, physical) = PrepareUpdate(session, where, assignments, options);
        return native.UpdateWhereAsync(predicate, physical, cancellationToken);
    }

    public static SetMutationResult DeleteWhere(
        this IStorageSession session,
        Predicate where,
        SetMutationOptions? options = null)
    {
        var (native, predicate) = PrepareDelete(session, where, options);
        return native.DeleteWhere(predicate);
    }

    public static ValueTask<SetMutationResult> DeleteWhereAsync(
        this IStorageSession session,
        Predicate where,
        SetMutationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var (native, predicate) = PrepareDelete(session, where, options);
        return native.DeleteWhereAsync(predicate, cancellationToken);
    }

    private static (ISetMutationStorageSession Native, Predicate Where, IReadOnlyDictionary<string, object?> Assignments) PrepareUpdate(
        IStorageSession session,
        Predicate where,
        IReadOnlyDictionary<string, object?> assignments,
        SetMutationOptions? options)
    {
        var native = Require(session);
        var validated = SetMutationValidation.ValidateAssignments(session.Unit, assignments);
        return (native, SetMutationAdmission.Admit(session.Unit, where, options), validated);
    }

    private static (ISetMutationStorageSession Native, Predicate Where) PrepareDelete(
        IStorageSession session,
        Predicate where,
        SetMutationOptions? options)
    {
        var native = Require(session);
        return (native, SetMutationAdmission.Admit(session.Unit, where, options));
    }

    /// <summary>
    /// There is deliberately no access check here. A privileged cross-scope session has no scope to
    /// write to, and every provider session refuses one itself — which is also the refusal a caller
    /// who reaches the capability interface directly meets, so restating it here would add a second
    /// copy of a rule without adding a case it catches.
    /// </summary>
    private static ISetMutationStorageSession Require(IStorageSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session as ISetMutationStorageSession ?? throw new NotSupportedException(
            "GW-SET-001: this provider session does not advertise set-based mutation; " +
            "inspect ISetMutationStorageSession before using UpdateWhere or DeleteWhere.");
    }
}

/// <summary>The coverage decision one set-based mutation is admitted under.</summary>
internal static class SetMutationAdmission
{
    /// <summary>
    /// Runs the portability and coverage decisions a read of the same predicate would take, then
    /// returns the physical predicate the provider renders. An uncovered predicate is refused with
    /// the same <c>GW-COVER-*</c> code and the same named remedy as the equivalent query, so an
    /// unbounded <c>DeleteWhere</c> is refused by the rule that already governs unbounded reads
    /// rather than by a second rule invented for writes.
    /// </summary>
    public static Predicate Admit(StorageUnit unit, Predicate where, SetMutationOptions? options = null) =>
        Admit(unit, where, options, DateTimeOffset.UtcNow);

    /// <summary>Admits against an explicit clock so scan-acceptance expiry is testable.</summary>
    public static Predicate Admit(
        StorageUnit unit,
        Predicate where,
        SetMutationOptions? options,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(where);
        var request = new QueryRequest(
            new TableId(unit.Name),
            where,
            ImmutableArray<OrderTerm>.Empty,
            Projection.All,
            Paging.None,
            ResultShape.Rows.Instance,
            latestPerKey: null,
            acceptedScan: options?.AcceptedScan);

        // The order is the read path's order. Coverage decides on the predicate the caller wrote,
        // as the query gate does; the search-key rewrite and the portability validation then run in
        // the order a provider's full query renderer runs them.
        QueryCoverageEnforcer.EnsureCovered(request, StorageUnitCoverage.PortableIndexes(unit), now);
        var physical = QuerySearchKeyRewriter.Rewrite(request, SearchKeyQueryMappings.For(unit));

        // A relational RenderPredicateFragment and MongoDB's filter renderer both skip the
        // portability validation their full query renderers run, so it is run here rather than
        // assumed. Without it a non-portable predicate reaches four providers and is refused by
        // none of them at the point the caller can act on.
        var validation = PortableQuerySemantics.Validate(physical);
        if (!validation.IsPortable)
        {
            var refusal = validation.Refusals[0];
            throw new QueryRenderException(refusal.Code, refusal.Message + " (" + refusal.Path + ").");
        }

        return physical.Where;
    }
}

/// <summary>
/// The one conversion from a declared storage unit to the coverage checker's provider-neutral index
/// shape. Reads and set-based mutations run the same checker over the same conversion, so a
/// mutation cannot be admitted under a rule the equivalent query is not.
/// </summary>
internal static class StorageUnitCoverage
{
    /// <summary>
    /// The index shape as the caller declared it, with provider-owned index columns removed.
    /// <para>
    /// A session's storage unit is the physical one, and a relational provider prepends its scope
    /// column to every index of a scoped unit while MongoDB gives each scope its own collection and
    /// prepends nothing. Admitting a caller's logical predicate against the physical index would
    /// therefore refuse on three providers and admit on the fourth — for a physical statement that
    /// binds the scope column as an equality and does use the index. Removing the provider-owned
    /// columns admits the shape the caller actually wrote, identically on all four.
    /// </para>
    /// </summary>
    public static ImmutableArray<CoverageIndex> PortableIndexes(StorageUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var indexes = DeclaredIndexes(unit, stripProviderOwnedColumns: true);
        var key = unit.Key.Columns
            .Where(column => !column.StartsWith("__groundwork_", StringComparison.Ordinal));
        return CoverageCandidates.Derive(key, indexes);
    }

    public static ImmutableArray<CoverageIndex> DeclaredIndexes(StorageUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return DeclaredIndexes(unit, stripProviderOwnedColumns: false);
    }

    private static ImmutableArray<CoverageIndex> DeclaredIndexes(
        StorageUnit unit,
        bool stripProviderOwnedColumns)
    {
        var nullable = unit.Columns.ToDictionary(column => column.Name, column => column.IsNullable, StringComparer.Ordinal);
        var logicalByPhysical = unit.DerivedColumns
            .Where(column => column.Projection is PortableProjection.BoundarySearchKey or PortableProjection.LocaleSortKey)
            .ToDictionary(column => column.Name, column => column.SourceColumn, StringComparer.Ordinal);
        return unit.Indexes
            .Select(index =>
            {
                var columns = index.Columns
                    .Where(column => !stripProviderOwnedColumns ||
                                     !column.Column.StartsWith("__groundwork_", StringComparison.Ordinal) ||
                                     logicalByPhysical.ContainsKey(column.Column))
                    .Select(column =>
                    {
                        var logical = logicalByPhysical.TryGetValue(column.Column, out var source)
                            ? source
                            : column.Column;
                        return new CoverageIndexColumn(
                            logical,
                            column.Direction == SortDirection.Descending
                                ? OrderDirection.Descending
                                : OrderDirection.Ascending,
                            !nullable.TryGetValue(logical, out var isNullable) || isNullable);
                    });
                return (index, columns: columns.ToArray());
            })
            .Where(item => item.columns.Length != 0)
            .Select(item => new CoverageIndex(
                item.index.Name,
                item.columns,
                item.index.MissingValues == MissingValueBehavior.Excluded
                    ? IndexMissingValueBehavior.Excluded
                    : IndexMissingValueBehavior.Included))
            .ToImmutableArray();
    }
}
