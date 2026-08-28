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
    /// Selects whether the mutation returns only its matched-row count or one keyed
    /// <see cref="WriteOutcome"/> for every row selected by the predicate.
    /// </summary>
    public SetMutationOutcomeMode OutcomeMode { get; init; } = SetMutationOutcomeMode.Aggregate;

    /// <summary>
    /// The scan acceptance carried into the same coverage decision an equivalent read takes. Null
    /// means the mutation is admitted only when the predicate is index-covered.
    /// </summary>
    public ScanAcceptance? AcceptedScan { get; init; }

    /// <summary>Uses the exact, per-key outcome path.</summary>
    public static SetMutationOptions Exact { get; } = new() { OutcomeMode = SetMutationOutcomeMode.Exact };

    /// <summary>Uses the low-cost affected-count path.</summary>
    public static SetMutationOptions Aggregate { get; } = new();

    internal void Validate()
    {
        if (!Enum.IsDefined(OutcomeMode))
            throw new ArgumentOutOfRangeException(nameof(OutcomeMode));
    }
}

/// <summary>Controls the evidence returned by a set-based mutation.</summary>
public enum SetMutationOutcomeMode
{
    /// <summary>Return the provider's affected/matched-row count only.</summary>
    Aggregate,
    /// <summary>Return one keyed <see cref="WriteOutcome"/> for each selected row.</summary>
    Exact
}

/// <summary>Exact evidence for one key selected by a set-based mutation.</summary>
public sealed record SetMutationOutcome
{
    public SetMutationOutcome(StorageKey key, WriteOutcome outcome)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Outcome = outcome ?? throw new ArgumentNullException(nameof(outcome));
    }

    /// <summary>The declared key of the selected row.</summary>
    public StorageKey Key { get; }

    /// <summary>The keyed mutation outcome observed for this row.</summary>
    public WriteOutcome Outcome { get; }
}

/// <summary>Evidence returned by one set-based mutation.</summary>
/// <param name="MatchedRows">
/// The number of rows selected by the predicate. In aggregate mode this is the one count every
/// provider reports the same way: SQL's affected-row count for <c>UPDATE</c>/<c>DELETE</c> and
/// MongoDB's <c>matchedCount</c>/<c>deletedCount</c>. MongoDB's <c>modifiedCount</c> is deliberately
/// not surfaced — it excludes rows whose assigned values were already equal, which no relational
/// provider can distinguish, so reporting it would be a number that means different things per
/// provider. In exact mode it is the key-snapshot count; inspect <see cref="Outcomes"/> for the
/// keyed mutation statuses.
/// </param>
public sealed record SetMutationResult(long MatchedRows)
{
    private static readonly IReadOnlyList<SetMutationOutcome> EmptyOutcomes =
        Array.AsReadOnly(Array.Empty<SetMutationOutcome>());

    /// <summary>
    /// Exact keyed outcomes, or an empty list when <see cref="OutcomeMode"/> is
    /// <see cref="SetMutationOutcomeMode.Aggregate"/>.
    /// </summary>
    public IReadOnlyList<SetMutationOutcome> Outcomes { get; private init; } = EmptyOutcomes;

    /// <summary>The evidence mode used to produce this result.</summary>
    public SetMutationOutcomeMode OutcomeMode { get; private init; } = SetMutationOutcomeMode.Aggregate;

    public bool IsExact => OutcomeMode == SetMutationOutcomeMode.Exact;

    internal static SetMutationResult Exact(
        IReadOnlyList<SetMutationOutcome> outcomes) =>
        new(outcomes?.Count ?? throw new ArgumentNullException(nameof(outcomes)))
        {
            OutcomeMode = SetMutationOutcomeMode.Exact,
            Outcomes = Array.AsReadOnly(outcomes.ToArray())
        };
}

/// <summary>
/// Provider capability for set-based mutation: one provider-native statement that updates or
/// deletes every row matching a portable predicate.
/// </summary>
/// <remarks>
/// The seam receives an already-admitted predicate and a logical assignment snapshot. Providers
/// validate and physicalize assignments again at this public capability boundary, because callers
/// may reach the optional interface directly and must not be able to mutate keys or provider-owned
/// projections. Admission — access, capability, portability, and index coverage — belongs to
/// <see cref="SetMutationSessionExtensions"/> so that it is decided once for every provider rather
/// than four times.
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
    /// Refuses an assignment set that no provider could apply faithfully and returns a defensive
    /// logical snapshot. The physical expansion is a separate step so providers can enforce these
    /// invariants again at their public capability seam without accepting already-expanded caller
    /// input as if it had been validated.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> ValidateLogicalAssignments(
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

        return new ReadOnlyDictionary<string, object?>(canonical);
    }

    /// <summary>
    /// Converts a validated logical assignment snapshot to the provider's physical assignment
    /// set. Providers call this after validating their public capability input, while the LINQ
    /// and unit-of-work adapters validate logical values before admission or a flush barrier.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> PhysicalizeAssignments(
        StorageUnit unit,
        IReadOnlyDictionary<string, object?> logicalAssignments)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(logicalAssignments);
        return new ReadOnlyDictionary<string, object?>(
            SearchKeyProjection.Populate(unit, logicalAssignments).ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal));
    }

    /// <summary>
    /// Validates a public capability assignment and expands its logical values into the
    /// provider-owned physical representation.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> ValidateAndPhysicalizeAssignments(
        StorageUnit unit,
        IReadOnlyDictionary<string, object?> assignments) =>
        PhysicalizeAssignments(unit, ValidateLogicalAssignments(unit, assignments));
}
