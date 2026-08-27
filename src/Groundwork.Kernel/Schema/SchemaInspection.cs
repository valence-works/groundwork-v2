using System.Collections.Immutable;

namespace Groundwork.Kernel.Schema;

/// <summary>Provider-neutral catalog view used to classify deployed schema drift.</summary>
public sealed record PhysicalSchemaColumn(
    string Name,
    string TypeIdentity,
    bool IsNullable,
    string? Collation = null,
    int? MaxLength = null,
    int? Precision = null,
    int? Scale = null,
    string? SearchKeyAlgorithmId = null);

/// <summary>One deployed index, including the properties that affect query coverage.</summary>
public sealed record PhysicalSchemaIndex(
    string Name,
    IReadOnlyList<IndexColumn> Columns,
    bool IsUnique,
    MissingValueBehavior MissingValues = MissingValueBehavior.Included,
    string? PartialFilter = null)
{
    public ImmutableArray<IndexColumn> SnapshotColumns => (Columns ?? []).ToImmutableArray();
}

/// <summary>A provider catalog snapshot for one logical storage subject.</summary>
public sealed record PhysicalSchemaSnapshot(
    StorageUnitId SubjectId,
    string TableName,
    IReadOnlyList<PhysicalSchemaColumn> Columns,
    IReadOnlyList<PhysicalSchemaIndex> Indexes)
{
    public ImmutableArray<PhysicalSchemaColumn> SnapshotColumns => (Columns ?? []).ToImmutableArray();

    public ImmutableArray<PhysicalSchemaIndex> SnapshotIndexes => (Indexes ?? []).ToImmutableArray();
}

public static class PhysicalSchemaInspection
{
    /// <summary>
    /// Compares a deployed catalog to the compiled target. Column drift is process-fatal; index
    /// drift is returned separately so query coverage can degrade only where the index is needed.
    /// </summary>
    public static PhysicalSchemaInspectionResult Compare(
        PhysicalSchemaHistoryState history,
        PhysicalSchemaTarget target,
        PhysicalSchemaSnapshot deployed)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(deployed);

        var columns = deployed.SnapshotColumns.ToDictionary(column => column.Name, StringComparer.Ordinal);
        var columnDrift = new List<SchemaRefusal>();
        if (deployed.SubjectId != target.Subject.Id ||
            !string.Equals(deployed.TableName, target.Subject.Name, StringComparison.Ordinal))
        {
            columnDrift.Add(new SchemaRefusal(
                "GW-RUNTIME-001",
                $"Physical schema table '{deployed.TableName}' does not match compiled table '{target.Subject.Name}'.",
                "table"));
        }

        foreach (var expected in target.Subject.Columns)
        {
            if (!columns.TryGetValue(expected.Name, out var actual))
            {
                columnDrift.Add(new SchemaRefusal(
                    "GW-RUNTIME-001",
                    $"Physical schema is missing column '{expected.Name}'.",
                    $"columns.{expected.Name}"));
                continue;
            }

            var differences = new List<string>();
            if (!string.Equals(actual.TypeIdentity, expected.Type.ToString(), StringComparison.OrdinalIgnoreCase))
                differences.Add($"type '{actual.TypeIdentity}' != '{expected.Type}'");
            if (actual.IsNullable != expected.IsNullable)
                differences.Add($"nullability {actual.IsNullable} != {expected.IsNullable}");
            if (!string.Equals(actual.Collation, expected.Collation?.ToString(), StringComparison.Ordinal))
                differences.Add($"collation '{actual.Collation ?? "<none>"}' != '{expected.Collation?.ToString() ?? "<none>"}'");
            if (actual.MaxLength != expected.MaxLength)
                differences.Add($"length {actual.MaxLength?.ToString() ?? "<none>"} != {expected.MaxLength?.ToString() ?? "<none>"}");
            if (actual.Precision != expected.Precision || actual.Scale != expected.Scale)
                differences.Add($"decimal({actual.Precision?.ToString() ?? "<none>"},{actual.Scale?.ToString() ?? "<none>"}) != decimal({expected.Precision?.ToString() ?? "<none>"},{expected.Scale?.ToString() ?? "<none>"})");
            if (differences.Count != 0)
            {
                columnDrift.Add(new SchemaRefusal(
                    "GW-RUNTIME-001",
                    $"Physical schema column '{expected.Name}' differs: {string.Join(", ", differences)}.",
                    $"columns.{expected.Name}"));
            }
        }

        foreach (var expected in target.Subject.DerivedColumns)
        {
            if (!columns.TryGetValue(expected.Name, out var actual) ||
                !string.Equals(actual.SearchKeyAlgorithmId, ProjectionAlgorithmId(expected), StringComparison.Ordinal))
            {
                columnDrift.Add(new SchemaRefusal(
                    "GW-RUNTIME-001",
                    $"Persisted search-key algorithm for folded column '{expected.Name}' differs from '{ProjectionAlgorithmId(expected)}'.",
                    $"columns.{expected.Name}.searchKeyAlgorithm"));
            }
        }

        var indexes = deployed.SnapshotIndexes.ToDictionary(index => index.Name, StringComparer.Ordinal);
        var indexDrift = new List<SchemaRefusal>();
        foreach (var expected in target.Subject.Indexes)
        {
            if (!indexes.TryGetValue(expected.Name, out var actual))
            {
                indexDrift.Add(new SchemaRefusal(
                    "GW-RUNTIME-002",
                    $"Physical schema is missing declared index '{expected.Name}'.",
                    $"indexes.{expected.Name}"));
                continue;
            }

            var differs = actual.IsUnique != expected.IsUnique ||
                          actual.MissingValues != expected.MissingValues ||
                          !actual.SnapshotColumns.SequenceEqual(expected.Columns) ||
                          !string.Equals(NormalizeFilter(actual.PartialFilter), NormalizeFilter(ExpectedFilter(expected)), StringComparison.Ordinal);
            if (differs)
            {
                indexDrift.Add(new SchemaRefusal(
                    "GW-RUNTIME-002",
                    $"Physical schema index '{expected.Name}' differs in key order, direction, uniqueness, or partial filter.",
                    $"indexes.{expected.Name}"));
            }
        }

        return new PhysicalSchemaInspectionResult(
            history,
            IsAppliedSchemaValid: columnDrift.Count == 0,
            columnDrift.ToImmutableArray(),
            indexDrift.ToImmutableArray());
    }

    private static string? ExpectedFilter(IndexDefinition index) =>
        index.MissingValues == MissingValueBehavior.Excluded
            ? string.Join(" AND ", index.Columns.Select(column => column.Column + " IS NOT NULL"))
            : null;

    private static string ProjectionAlgorithmId(DerivedColumnDefinition definition) => definition.AlgorithmId ?? definition.Projection switch
    {
        PortableProjection.UnicodeFold => PortableStringComparison.UnicodeOrdinalIgnoreCaseAlgorithmId,
        PortableProjection.BoundarySearchKey => PortableStringComparison.SearchKeyAlgorithmId,
        PortableProjection.Sha256 => PortableStringComparison.LookupHashAlgorithmId,
        _ => throw new ArgumentOutOfRangeException(nameof(definition), definition.Projection, null)
    };

    private static string? NormalizeFilter(string? filter) =>
        filter is null
            ? null
            : new string(filter.Where(character =>
                !char.IsWhiteSpace(character) && character is not ('"' or '[' or ']' or '`' or '(' or ')')).ToArray());
}

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
