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
        EnsureAccess(session, "update-where");
        var (native, predicate, validated) = PrepareUpdate(session, where, assignments, options);
        return native.UpdateWhere(predicate, validated);
    }

    public static ValueTask<SetMutationResult> UpdateWhereAsync(
        this IStorageSession session,
        Predicate where,
        IReadOnlyDictionary<string, object?> assignments,
        SetMutationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureAccess(session, "update-where");
        var (native, predicate, validated) = PrepareUpdate(session, where, assignments, options);
        return native.UpdateWhereAsync(predicate, validated, cancellationToken);
    }

    public static SetMutationResult DeleteWhere(
        this IStorageSession session,
        Predicate where,
        SetMutationOptions? options = null)
    {
        EnsureAccess(session, "delete-where");
        var (native, predicate) = PrepareDelete(session, where, options);
        return native.DeleteWhere(predicate);
    }

    public static ValueTask<SetMutationResult> DeleteWhereAsync(
        this IStorageSession session,
        Predicate where,
        SetMutationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureAccess(session, "delete-where");
        var (native, predicate) = PrepareDelete(session, where, options);
        return native.DeleteWhereAsync(predicate, cancellationToken);
    }

    private static void EnsureAccess(IStorageSession session, string operation)
    {
        ArgumentNullException.ThrowIfNull(session);
        StorageAccessValidation.EnsurePointOperation(session.Access, operation);
    }

    private static (ISetMutationStorageSession Native, Predicate Where, IReadOnlyDictionary<string, object?> Assignments) PrepareUpdate(
        IStorageSession session,
        Predicate where,
        IReadOnlyDictionary<string, object?> assignments,
        SetMutationOptions? options)
    {
        var native = Require(session);
        // Keep this snapshot logical. Native providers validate it again at their public
        // capability seam before physicalizing it, while a unit-of-work wrapper can validate the
        // same shape before its flush barrier. Passing an already-expanded dictionary here would
        // make that second invariant check indistinguishable from a forged provider-owned value.
        var validated = SetMutationValidation.ValidateLogicalAssignments(session.Unit, assignments);
        return (native, SetMutationAdmission.Admit(session, where, options), validated);
    }

    private static (ISetMutationStorageSession Native, Predicate Where) PrepareDelete(
        IStorageSession session,
        Predicate where,
        SetMutationOptions? options)
    {
        var native = Require(session);
        return (native, SetMutationAdmission.Admit(session, where, options));
    }

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

    public static Predicate Admit(
        IStorageSession session,
        Predicate where,
        SetMutationOptions? options = null) =>
        Admit(session, where, options, DateTimeOffset.UtcNow);

    /// <summary>Admits against an explicit clock so scan-acceptance expiry is testable.</summary>
    public static Predicate Admit(
        StorageUnit unit,
        Predicate where,
        SetMutationOptions? options,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return AdmitCore(unit, session: null, where, options, now);
    }

    public static Predicate Admit(
        IStorageSession session,
        Predicate where,
        SetMutationOptions? options,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(session);
        return AdmitCore(session.Unit, session, where, options, now);
    }

    private static Predicate AdmitCore(
        StorageUnit unit,
        IStorageSession? session,
        Predicate where,
        SetMutationOptions? options,
        DateTimeOffset now)
    {
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
        // as the query gate does; the search-key rewrite and portability validation then run in the
        // order a provider's full query renderer runs them.
        (session is null
                ? RuntimeCoverage.ForMutation(unit, null)
                : RuntimeCoverage.ForMutation(session))
            .EnsureCovered(request, now);
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
        return CoverageCandidates.Derive(PortableKeyColumns(unit), PortableDeclaredIndexes(unit));
    }

    /// <summary>
    /// Returns the logical key used by a portable predicate. Relational providers prepend their
    /// scope discriminator to the physical key, but that provider-owned column is not part of a
    /// caller's key predicate and must not make an otherwise key-covered mutation look uncovered.
    /// </summary>
    internal static ImmutableArray<string> PortableKeyColumns(StorageUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return unit.Key.Columns
            .Where(column => !column.StartsWith("__groundwork_", StringComparison.Ordinal))
            .ToImmutableArray();
    }

    internal static ImmutableArray<CoverageIndex> PortableDeclaredIndexes(StorageUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return DeclaredIndexes(unit, stripProviderOwnedColumns: true, includeLocaleSortKeys: false);
    }

    public static ImmutableArray<CoverageIndex> DeclaredIndexes(StorageUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return DeclaredIndexes(unit, stripProviderOwnedColumns: false, includeLocaleSortKeys: true);
    }

    private static ImmutableArray<CoverageIndex> DeclaredIndexes(
        StorageUnit unit,
        bool stripProviderOwnedColumns,
        bool includeLocaleSortKeys)
    {
        var nullable = unit.Columns.ToDictionary(column => column.Name, column => column.IsNullable, StringComparer.Ordinal);
        var logicalByPhysical = unit.DerivedColumns
            .Where(column => column.Projection is PortableProjection.BoundarySearchKey ||
                             (includeLocaleSortKeys && column.Projection is PortableProjection.LocaleSortKey))
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

/// <summary>Builds the one runtime coverage gate shared by reads and set-based mutations.</summary>
internal static class RuntimeCoverage
{
    public static RuntimeCoverageGate ForQuery(
        IStorageSession session,
        IStorageProviderConnection? connection,
        QueryAdmissionProfile? admission)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Create(
            session.Unit,
            StorageUnitCoverage.DeclaredIndexes(session.Unit),
            connection,
            admission,
            session.Unit.Key.Columns.ToImmutableArray());
    }

    public static RuntimeCoverageGate ForMutation(IStorageSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var connection = (session as IProviderBoundStorageSession)?.ProviderConnection;
        return ForMutation(session.Unit, connection);
    }

    public static RuntimeCoverageGate ForMutation(StorageUnit unit, IStorageProviderConnection? connection)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return Create(
            unit,
            StorageUnitCoverage.PortableDeclaredIndexes(unit),
            connection,
            admission: null,
            StorageUnitCoverage.PortableKeyColumns(unit));
    }

    private static RuntimeCoverageGate Create(
        StorageUnit unit,
        ImmutableArray<CoverageIndex> declared,
        IStorageProviderConnection? connection,
        QueryAdmissionProfile? admission,
        ImmutableArray<string> key)
    {
        var declaredCandidates = CoverageCandidates.Derive(key, declared);
        var deployedNames = connection?.Catalog.ReadIndexes(unit.Id)
            .Select(index => index.Name)
            .ToHashSet(StringComparer.Ordinal);
        var deployed = connection is null
            ? declared
            : declared.Where(index => deployedNames!.Contains(index.Name))
                .ToImmutableArray();
        var profile = admission ?? connection?.GetQueryAdmission() ?? QueryAdmissionProfile.Default;
        return new RuntimeCoverageGate(
            declaredCandidates,
            CoverageCandidates.Derive(key, deployed),
            options: new RuntimeCoverageGateOptions
            {
                ValueFence = new RuntimeValueFenceOptions
                {
                    MaximumParameters = profile.MaximumParameters,
                    MaximumInValues = profile.MaximumInValues
                }
            });
    }
}
