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
/// capability seam itself stays in <c>Groundwork.Store</c>, where providers implement it. This
/// entry point binds internal execution evidence only after coverage succeeds, so direct native
/// capability calls cannot bypass coverage or explicit scan acceptance.
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
        options ??= SetMutationOptions.Aggregate;
        options.Validate();
        var (native, predicate, validated) = PrepareUpdate(session, where, assignments, options);
        if (options.OutcomeMode == SetMutationOutcomeMode.Exact)
            return ExecuteExactUpdate(session, where, validated, options);
        using var admission = SetMutationExecutionAdmission.Enter(predicate);
        return native.UpdateWhere(predicate, validated);
    }

    public static async ValueTask<SetMutationResult> UpdateWhereAsync(
        this IStorageSession session,
        Predicate where,
        IReadOnlyDictionary<string, object?> assignments,
        SetMutationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureAccess(session, "update-where");
        options ??= SetMutationOptions.Aggregate;
        options.Validate();
        var (native, predicate, validated) = PrepareUpdate(session, where, assignments, options);
        if (options.OutcomeMode == SetMutationOutcomeMode.Exact)
            return await ExecuteExactUpdateAsync(session, where, validated, options, cancellationToken)
                .ConfigureAwait(false);
        using var admission = SetMutationExecutionAdmission.Enter(predicate);
        return await native.UpdateWhereAsync(predicate, validated, cancellationToken).ConfigureAwait(false);
    }

    public static SetMutationResult DeleteWhere(
        this IStorageSession session,
        Predicate where,
        SetMutationOptions? options = null)
    {
        EnsureAccess(session, "delete-where");
        options ??= SetMutationOptions.Aggregate;
        options.Validate();
        var (native, predicate) = PrepareDelete(session, where, options);
        if (options.OutcomeMode == SetMutationOutcomeMode.Exact)
            return ExecuteExactDelete(session, where, options);
        using var admission = SetMutationExecutionAdmission.Enter(predicate);
        return native.DeleteWhere(predicate);
    }

    public static async ValueTask<SetMutationResult> DeleteWhereAsync(
        this IStorageSession session,
        Predicate where,
        SetMutationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureAccess(session, "delete-where");
        options ??= SetMutationOptions.Aggregate;
        options.Validate();
        var (native, predicate) = PrepareDelete(session, where, options);
        if (options.OutcomeMode == SetMutationOutcomeMode.Exact)
            return await ExecuteExactDeleteAsync(session, where, options, cancellationToken).ConfigureAwait(false);
        using var admission = SetMutationExecutionAdmission.Enter(predicate);
        return await native.DeleteWhereAsync(predicate, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes an admitted update with one keyed <see cref="WriteOutcome"/> per selected row.
    /// Exact mode deliberately uses the existing keyed write contract: providers can therefore
    /// report the same version, conflict, and not-found semantics as an ordinary update. The
    /// initial key read and all keyed writes are one transaction when the session belongs to a
    /// unit of work; outside a unit of work, callers should use aggregate mode when atomicity of
    /// the whole set is required.
    /// </summary>
    private static SetMutationResult ExecuteExactUpdate(
        IStorageSession session,
        Predicate where,
        IReadOnlyDictionary<string, object?> assignments,
        SetMutationOptions options)
    {
        try
        {
            var keys = FindMatchingKeys(session, where, options);
            var outcomes = new List<SetMutationOutcome>(keys.Count);
            foreach (var key in keys)
                outcomes.Add(new SetMutationOutcome(key, session.Update(ValuesFor(key, assignments))));
            return SetMutationResult.Exact(outcomes);
        }
        catch (Exception exception)
        {
            FailUnitOfWork(session, exception);
            throw;
        }
    }

    private static async ValueTask<SetMutationResult> ExecuteExactUpdateAsync(
        IStorageSession session,
        Predicate where,
        IReadOnlyDictionary<string, object?> assignments,
        SetMutationOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            var keys = await FindMatchingKeysAsync(session, where, options, cancellationToken).ConfigureAwait(false);
            var outcomes = new List<SetMutationOutcome>(keys.Count);
            foreach (var key in keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                outcomes.Add(new SetMutationOutcome(
                    key,
                    await session.UpdateAsync(ValuesFor(key, assignments), cancellationToken: cancellationToken)
                        .ConfigureAwait(false)));
            }
            return SetMutationResult.Exact(outcomes);
        }
        catch (Exception exception)
        {
            FailUnitOfWork(session, exception);
            throw;
        }
    }

    private static SetMutationResult ExecuteExactDelete(
        IStorageSession session,
        Predicate where,
        SetMutationOptions options)
    {
        try
        {
            var keys = FindMatchingKeys(session, where, options);
            var outcomes = new List<SetMutationOutcome>(keys.Count);
            foreach (var key in keys)
                outcomes.Add(new SetMutationOutcome(key, session.Delete(key)));
            return SetMutationResult.Exact(outcomes);
        }
        catch (Exception exception)
        {
            FailUnitOfWork(session, exception);
            throw;
        }
    }

    private static async ValueTask<SetMutationResult> ExecuteExactDeleteAsync(
        IStorageSession session,
        Predicate where,
        SetMutationOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            var keys = await FindMatchingKeysAsync(session, where, options, cancellationToken).ConfigureAwait(false);
            var outcomes = new List<SetMutationOutcome>(keys.Count);
            foreach (var key in keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                outcomes.Add(new SetMutationOutcome(
                    key,
                    await session.DeleteAsync(key, cancellationToken: cancellationToken).ConfigureAwait(false)));
            }
            return SetMutationResult.Exact(outcomes);
        }
        catch (Exception exception)
        {
            FailUnitOfWork(session, exception);
            throw;
        }
    }

    private static void FailUnitOfWork(IStorageSession session, Exception exception)
    {
        if (session is BatchStorageSession batched)
            batched.Fail(exception);
    }

    private static IReadOnlyList<StorageKey> FindMatchingKeys(
        IStorageSession session,
        Predicate where,
        SetMutationOptions options)
    {
        var request = KeyRequest(session.Unit, where, options);
        return OrderedKeys(session.Unit, session.Query(request).Rows);
    }

    private static async ValueTask<IReadOnlyList<StorageKey>> FindMatchingKeysAsync(
        IStorageSession session,
        Predicate where,
        SetMutationOptions options,
        CancellationToken cancellationToken)
    {
        var request = KeyRequest(session.Unit, where, options);
        var result = await session.QueryAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
        return OrderedKeys(session.Unit, result.Rows);
    }

    private static IReadOnlyList<StorageKey> OrderedKeys(
        StorageUnit unit,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var logicalUnit = LogicalKeyUnit(unit);
        return rows.Select(row => KeyFromRow(unit, row))
            .OrderBy(key => RowWrite.IdentityFor(logicalUnit, key.Values), StringComparer.Ordinal)
            .ToArray();
    }

    private static StorageUnit LogicalKeyUnit(StorageUnit unit) => unit with
    {
        Key = new KeyDefinition { Columns = LogicalKeyColumns(unit) }
    };

    private static QueryRequest KeyRequest(
        StorageUnit unit,
        Predicate where,
        SetMutationOptions options)
    {
        var table = new TableId(unit.Name);
        var columns = LogicalKeyColumns(unit)
            .Select(column => unit.Columns.Single(definition => definition.Name == column))
            .Select(definition => new ColumnRef(
                table,
                definition.Name,
                QueryTypeFor(definition.Type),
                definition.IsNullable,
                maxLength: definition.MaxLength))
            .ToArray();
        return new QueryRequest(
            table,
            where,
            ImmutableArray<OrderTerm>.Empty,
            Projection.ColumnsOnly(columns),
            Paging.None,
            acceptedScan: options.AcceptedScan);
    }

    private static StorageKey KeyFromRow(
        StorageUnit unit,
        IReadOnlyDictionary<string, object?> row) =>
        new(LogicalKeyColumns(unit)
            .ToDictionary(
                column => column,
                column => row.TryGetValue(column, out var value)
                    ? value
                    : throw new InvalidOperationException(
                        $"The exact set-mutation key projection did not return '{column}'."),
                StringComparer.Ordinal));

    private static IReadOnlyList<string> LogicalKeyColumns(StorageUnit unit) =>
        unit.Key.Columns
            .Where(column => !column.StartsWith("__groundwork_", StringComparison.Ordinal))
            .ToArray();

    private static StorageValues ValuesFor(
        StorageKey key,
        IReadOnlyDictionary<string, object?> assignments) =>
        new(key.Values.Concat(assignments)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));

    private static QueryType QueryTypeFor(PortableType type) => type switch
    {
        PortableType.Boolean => QueryType.Boolean,
        PortableType.Int32 => QueryType.Int32,
        PortableType.Int64 => QueryType.Int64,
        PortableType.Decimal => QueryType.Decimal,
        PortableType.Double => QueryType.Double,
        PortableType.String => QueryType.String,
        PortableType.DateTimeOffset => QueryType.DateTimeOffset,
        PortableType.Guid => QueryType.Guid,
        PortableType.Binary => QueryType.Binary,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

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
        var physical = QueryElementSearchKeyRewriter.Rewrite(
            QuerySearchKeyRewriter.Rewrite(request, SearchKeyQueryMappings.For(unit)),
            SearchKeyQueryMappings.ElementFor(unit));

        // A relational RenderPredicateFragment and MongoDB's filter renderer both skip the
        // portability validation their full query renderers run, so it is run here rather than
        // assumed. Without it a non-portable predicate reaches five providers and is refused by
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
    /// columns admits the shape the caller actually wrote, identically on all five.
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

    /// <summary>
    /// Returns the logical key used by an ordinary scoped query. Relational providers prepend the
    /// scope discriminator to the physical key, but the session's scope predicate supplies that
    /// equality separately. Only that leading provider-owned column is removed; other physical
    /// columns remain visible to coverage just as they do on a global unit.
    /// </summary>
    internal static ImmutableArray<string> ScopedQueryKeyColumns(StorageUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var columns = unit.Key.Columns;
        return columns.Count > 0 && columns[0] == ProviderOwnedColumns.Scope
            ? columns.Skip(1).ToImmutableArray()
            : columns.ToImmutableArray();
    }

    internal static ImmutableArray<CoverageIndex> PortableDeclaredIndexes(StorageUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return DeclaredIndexes(unit, stripProviderOwnedColumns: true, includeLocaleSortKeys: false);
    }

    /// <summary>
    /// Returns declared query indexes in the logical shape seen by an ordinary scoped caller. The
    /// provider-bound scope equality covers the physical prefix, while search-key mappings and all
    /// remaining index metadata stay unchanged for the shared coverage checker.
    /// </summary>
    internal static ImmutableArray<CoverageIndex> ScopedQueryDeclaredIndexes(StorageUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return DeclaredIndexes(
            unit,
            stripProviderOwnedColumns: false,
            includeLocaleSortKeys: true,
            stripLeadingScope: true);
    }

    public static ImmutableArray<CoverageIndex> DeclaredIndexes(StorageUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return DeclaredIndexes(unit, stripProviderOwnedColumns: false, includeLocaleSortKeys: true);
    }

    private static ImmutableArray<CoverageIndex> DeclaredIndexes(
        StorageUnit unit,
        bool stripProviderOwnedColumns,
        bool includeLocaleSortKeys,
        bool stripLeadingScope = false)
    {
        var nullable = unit.Columns.ToDictionary(column => column.Name, column => column.IsNullable, StringComparer.Ordinal);
        var logicalByPhysical = unit.DerivedColumns
            .Where(column => column.Projection is PortableProjection.BoundarySearchKey ||
                             (includeLocaleSortKeys && column.Projection is PortableProjection.LocaleSortKey))
            .ToDictionary(column => column.Name, column => column.SourceColumn, StringComparer.Ordinal);
        return unit.Indexes
            .Select(index =>
            {
                var physicalColumns = stripLeadingScope && index.Columns.Count > 0 &&
                    index.Columns[0].Column == ProviderOwnedColumns.Scope
                    ? index.Columns.Skip(1)
                    : index.Columns.AsEnumerable();
                var columns = physicalColumns
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
        if (session.Access.IsPrivilegedAcrossScopes)
        {
            // A cross-scope query cannot bind the physical scope prefix. Do not hand provider-owned
            // candidates to the checker: its nearest-index and suggested-declaration diagnostics
            // must never expose the hidden scope column to a caller who cannot spell it.
            return Create(session.Unit, [], connection, admission, []);
        }

        var isScopedQuery = session.Unit.Scope == ScopePolicy.Scoped &&
            session.Access.Kind == StorageAccessKind.Scoped;
        return Create(
            session.Unit,
            isScopedQuery
                ? StorageUnitCoverage.ScopedQueryDeclaredIndexes(session.Unit)
                : StorageUnitCoverage.DeclaredIndexes(session.Unit),
            connection,
            admission,
            isScopedQuery
                ? StorageUnitCoverage.ScopedQueryKeyColumns(session.Unit)
                : session.Unit.Key.Columns.ToImmutableArray());
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
