using System.Collections.Immutable;
using Groundwork.Query.Model;

namespace Groundwork.Query.Planning;

/// <summary>
/// The one derivation of the index candidates a declared storage unit offers
/// <see cref="QueryCoverageChecker"/>. The checker takes an <see cref="IEnumerable{T}"/> and never
/// sees the unit, so what counts as a candidate is decided here — once — rather than separately in
/// the analyzer, the schema verifier, and the runtime gate.
/// <para>
/// A declared key is a candidate in its own right. Every relational coordinator emits it as the
/// table's <c>PRIMARY KEY</c>, which PostgreSQL, SQL Server, and SQLite each back with a unique
/// index the planner seeks on, so a key-equality read is not a scan and refusing it states
/// something untrue about the deployed catalog.
/// </para>
/// </summary>
public static class CoverageCandidates
{
    /// <summary>
    /// The name the declared key is reported under. It is deliberately not a spellable
    /// <c>[GwIndex]</c> name: a reader who sees it in "Nearest index ..." must not copy it into a
    /// declaration, because the key is already indexed.
    /// </summary>
    public const string KeyIndexName = "(declared key)";

    /// <summary>
    /// Returns the declared key followed by the declared indexes. The key leads so that a
    /// key-covered read reports the key rather than a secondary index that happens to tie on score.
    /// A unit with no key columns contributes no key candidate.
    /// </summary>
    public static ImmutableArray<CoverageIndex> Derive(
        IEnumerable<string> keyColumns,
        IEnumerable<CoverageIndex> declaredIndexes)
    {
        if (keyColumns is null)
            throw new ArgumentNullException(nameof(keyColumns));
        if (declaredIndexes is null)
            throw new ArgumentNullException(nameof(declaredIndexes));

        var indexes = declaredIndexes.ToImmutableArray();
        var key = keyColumns.ToImmutableArray();
        if (key.Length == 0)
            return indexes;

        // Key columns are never nullable — a relational primary key forbids null and every
        // provider refuses a null key value on write — so the sparse-index rule cannot exclude a
        // row the predicate could match.
        var keyIndex = new CoverageIndex(
            KeyIndexName,
            key.Select(column => new CoverageIndexColumn(column, OrderDirection.Ascending, isNullable: false)))
        {
            IsDeclaredKey = true
        };
        return [keyIndex, .. indexes];
    }
}
