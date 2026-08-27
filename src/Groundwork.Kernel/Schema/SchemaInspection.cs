using System.Collections.Immutable;

namespace Groundwork.Kernel.Schema;

/// <summary>
/// One deployed column that the compiled target does not declare, described by the facts a
/// provider can read from its catalog. The provider reports facts; <see cref="ForeignColumnAdmission"/>
/// decides what they mean, so the decision has one implementation across every provider.
/// </summary>
public sealed record ForeignPhysicalColumn(
    string Name,
    bool IsNullable,
    bool HasDefault,
    bool IsDatabaseGenerated)
{
    /// <summary>
    /// Whether a writer that omits this column still produces a valid row. This is the whole of the
    /// tolerance question: a foreign column Groundwork can leave out of every statement it writes
    /// is one it can coexist with, and one it cannot is one no policy can make writable.
    /// </summary>
    public bool DatabaseSuppliesValue => IsNullable || HasDefault || IsDatabaseGenerated;
}

/// <summary>What a declaration's <see cref="ForeignColumnPolicy"/> makes of the foreign columns found.</summary>
public sealed record ForeignColumnVerdict(
    ImmutableArray<SchemaRefusal> Drift,
    ImmutableArray<SchemaRefusal> Tolerated)
{
    public static ForeignColumnVerdict Empty { get; } = new([], []);
}

/// <summary>
/// The single decision about deployed columns a declaration does not describe. Every path that
/// compares a catalog to a target — runtime admission, apply-time validation, and adoption — routes
/// through here, so tolerance cannot mean one thing at startup and another at deploy time.
/// </summary>
public static class ForeignColumnAdmission
{
    /// <summary>A foreign column that makes the deployed catalog differ from the declaration.</summary>
    public const string DriftCode = "GW-RUNTIME-001";

    /// <summary>A foreign column an opt-in policy downgraded from drift to a warning.</summary>
    public const string ToleratedCode = "GW-RUNTIME-003";

    public static ForeignColumnVerdict Classify(
        string table,
        ForeignColumnPolicy policy,
        IEnumerable<ForeignPhysicalColumn> foreignColumns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentNullException.ThrowIfNull(foreignColumns);
        var ordered = foreignColumns
            .OrderBy(column => column.Name, StringComparer.Ordinal)
            .ToArray();
        if (ordered.Length == 0)
            return ForeignColumnVerdict.Empty;

        var drift = ImmutableArray.CreateBuilder<SchemaRefusal>();
        var tolerated = ImmutableArray.CreateBuilder<SchemaRefusal>();
        foreach (var column in ordered)
        {
            if (policy == ForeignColumnPolicy.TolerateDatabaseSupplied && column.DatabaseSuppliesValue)
            {
                tolerated.Add(new SchemaRefusal(
                    ToleratedCode,
                    $"Deployed column '{table}.{column.Name}' is not declared by this schema. " +
                    $"The declaration tolerates foreign columns the database supplies a value for, so it is " +
                    $"reported rather than refused; Groundwork neither reads nor writes it.",
                    $"columns.{column.Name}"));
                continue;
            }

            drift.Add(new SchemaRefusal(
                DriftCode,
                $"Deployed column '{table}.{column.Name}' is not declared by this schema" +
                (policy == ForeignColumnPolicy.TolerateDatabaseSupplied
                    ? " and the database supplies no value for it, so a write that omits it cannot succeed. " +
                      "Tolerating foreign columns does not extend to this one."
                    : ".") +
                $" It is {Describe(column)}.",
                $"columns.{column.Name}"));
        }

        return new ForeignColumnVerdict(drift.ToImmutable(), tolerated.ToImmutable());
    }

    private static string Describe(ForeignPhysicalColumn column) =>
        (column.IsNullable ? "nullable" : "not nullable") +
        (column.HasDefault ? ", defaulted" : ", not defaulted") +
        (column.IsDatabaseGenerated ? ", database-generated" : string.Empty);
}
