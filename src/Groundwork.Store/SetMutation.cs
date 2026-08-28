using System.Collections.ObjectModel;
using Groundwork.Kernel;
using Groundwork.Query.Model;

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

internal static class SetMutationValidation
{
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
        WritePreconditionValidator.ValidateWrittenValues(unit, assignments);

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
