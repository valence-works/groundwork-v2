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
                !string.Equals(actual.SearchKeyAlgorithmId, ProjectionAlgorithmId(expected.Projection), StringComparison.Ordinal))
            {
                columnDrift.Add(new SchemaRefusal(
                    "GW-RUNTIME-001",
                    $"Persisted search-key algorithm for folded column '{expected.Name}' differs from '{ProjectionAlgorithmId(expected.Projection)}'.",
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

    private static string ProjectionAlgorithmId(PortableProjection projection) => projection switch
    {
        PortableProjection.UnicodeFold => PortableStringComparison.UnicodeOrdinalIgnoreCaseAlgorithmId,
        PortableProjection.BoundarySearchKey => PortableStringComparison.SearchKeyAlgorithmId,
        PortableProjection.Sha256 => PortableStringComparison.LookupHashAlgorithmId,
        _ => throw new ArgumentOutOfRangeException(nameof(projection), projection, null)
    };

    private static string? NormalizeFilter(string? filter) =>
        filter is null
            ? null
            : new string(filter.Where(character =>
                !char.IsWhiteSpace(character) && character is not ('"' or '[' or ']' or '`' or '(' or ')')).ToArray());
}
