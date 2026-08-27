using System.Collections.Immutable;
using System.Collections.ObjectModel;
using Groundwork.Kernel;
using Groundwork.Query.Model;
using Groundwork.Query.Planning;

namespace Groundwork.Store;

/// <summary>Explicit admission input for one set-based mutation.</summary>
/// <remarks>
/// There is deliberately no <see cref="WriteOptions"/> counterpart. A precondition names one row's
/// version, and a set-based mutation has no one row; accepting <c>IfVersion</c> here would have to
/// mean either "every matched row" or "some matched row", and neither is what the caller wrote.
/// </remarks>
public sealed record SetMutationOptions
{
    /// <summary>
    /// The scan acceptance carried into the same coverage decision an equivalent read takes. Null
    /// means the mutation is admitted only when the predicate is index-covered.
    /// </summary>
    public ScanAcceptance? AcceptedScan { get; init; }
}

/// <summary>Evidence returned by one set-based mutation.</summary>
/// <param name="MatchedRows">
/// The number of rows the predicate selected, which is also the number of rows the provider wrote.
/// This is the one count every provider reports the same way: SQL's affected-row count for
/// <c>UPDATE</c>/<c>DELETE</c> and MongoDB's <c>matchedCount</c>/<c>deletedCount</c>. MongoDB's
/// <c>modifiedCount</c> is deliberately not surfaced — it excludes rows whose assigned values were
/// already equal, which no relational provider can distinguish, so reporting it would be a number
/// that means different things per provider.
/// </param>
public sealed record SetMutationResult(long MatchedRows);

/// <summary>
/// Provider capability for set-based mutation: one provider-native statement that updates or
/// deletes every row matching a portable predicate.
/// </summary>
/// <remarks>
/// The seam receives an already-admitted predicate and already-validated, already-physicalized
/// assignments. Admission — access, capability, assignment validity, portability, and index
/// coverage — belongs to <see cref="SetMutationSessionExtensions"/> so that it is decided once for
/// every provider rather than four times.
/// </remarks>
public interface ISetMutationStorageSession
{
    SetMutationResult UpdateWhere(Predicate where, IReadOnlyDictionary<string, object?> assignments);

    ValueTask<SetMutationResult> UpdateWhereAsync(
        Predicate where,
        IReadOnlyDictionary<string, object?> assignments,
        CancellationToken cancellationToken = default);

    SetMutationResult DeleteWhere(Predicate where);

    ValueTask<SetMutationResult> DeleteWhereAsync(Predicate where, CancellationToken cancellationToken = default);
}

/// <summary>
/// The public set-based mutation entry point. It admits the mutation under the coverage rule an
/// equivalent read is admitted under, and only then reaches the provider.
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
        var native = Require(session, "update-where");
        var validated = SetMutationValidation.ValidateAssignments(session.Unit, assignments);
        return (native, SetMutationValidation.Admit(session.Unit, where, options), validated);
    }

    private static (ISetMutationStorageSession Native, Predicate Where) PrepareDelete(
        IStorageSession session,
        Predicate where,
        SetMutationOptions? options)
    {
        var native = Require(session, "delete-where");
        return (native, SetMutationValidation.Admit(session.Unit, where, options));
    }

    private static ISetMutationStorageSession Require(IStorageSession session, string operation)
    {
        ArgumentNullException.ThrowIfNull(session);
        StorageAccessValidation.EnsurePointOperation(session.Access, operation);
        return session as ISetMutationStorageSession ?? throw new NotSupportedException(
            "GW-SET-001: this provider session does not advertise set-based mutation; " +
            "inspect ISetMutationStorageSession before using UpdateWhere or DeleteWhere.");
    }
}

/// <summary>Fail-closed admission shared by every set-based mutation, whatever the provider.</summary>
public static class SetMutationValidation
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

    /// <summary>
    /// Refuses an assignment set that no provider could apply faithfully, and expands it into the
    /// physical assignment set — including the derived search-key column of every folded source
    /// column it assigns, so a set-based update cannot leave a search key describing the old value.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> ValidateAssignments(
        StorageUnit unit,
        IReadOnlyDictionary<string, object?> assignments)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(assignments);
        if (assignments.Count == 0)
        {
            throw new ArgumentException(
                "GW-SET-003: a set-based update requires at least one column assignment.",
                nameof(assignments));
        }

        // The optimistic token is system-owned by the same rule that governs a keyed write.
        WritePreconditionValidator.ValidateSystemOwnedValues(unit, assignments);

        var declared = unit.Columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
        var keyColumns = unit.Key.Columns.ToHashSet(StringComparer.Ordinal);
        var canonical = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in assignments)
        {
            if (!declared.TryGetValue(pair.Key, out var definition) ||
                pair.Key.StartsWith("__groundwork_", StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"GW-SET-002: assignment column '{pair.Key}' is not an application-declared column of '{unit.Name}'.",
                    nameof(assignments));
            }

            if (keyColumns.Contains(pair.Key))
            {
                throw new ArgumentException(
                    $"GW-SET-002: assignment column '{pair.Key}' is a declared key column of '{unit.Name}'; " +
                    "a set-based update cannot move rows between identities.",
                    nameof(assignments));
            }

            if (definition.Type == PortableType.Json)
            {
                throw new ArgumentException(
                    $"GW-SET-004: assignment column '{pair.Key}' uses PortableType.Json, which set-based update does not support; " +
                    "assign a portable scalar or binary column instead.",
                    nameof(assignments));
            }

            canonical.Add(pair.Key, CompareAndDeleteValidation.CanonicalizeValue(
                definition, pair.Value, pair.Key, nameof(assignments), "Assignment value"));
        }

        return new ReadOnlyDictionary<string, object?>(
            SearchKeyProjection.Populate(unit, canonical).ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal));
    }
}

/// <summary>
/// The one conversion from a declared storage unit to the coverage checker's provider-neutral index
/// shape. Reads and set-based mutations run the same checker over the same conversion, so a
/// mutation cannot be admitted under a rule the equivalent query is not.
/// </summary>
public static class StorageUnitCoverage
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
        return DeclaredIndexes(unit with
        {
            Indexes = unit.Indexes
                .Select(index => index with
                {
                    Columns = index.Columns
                        .Where(column => !column.Column.StartsWith("__groundwork_", StringComparison.Ordinal))
                        .ToArray()
                })
                .Where(index => index.Columns.Count != 0)
                .ToArray()
        });
    }

    public static ImmutableArray<CoverageIndex> DeclaredIndexes(StorageUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
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
}
