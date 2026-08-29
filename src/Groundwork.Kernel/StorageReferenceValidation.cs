namespace Groundwork.Kernel;

internal sealed record ManifestReferenceFinding(StorageUnitId SourceUnitId, DeclarationFinding Finding);

internal static class StorageReferenceValidation
{
    internal static IReadOnlyList<DeclarationFinding> ValidateLocal(StorageUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var findings = new List<DeclarationFinding>();
        var columns = (unit.Columns ?? [])
            .Where(column => column is not null && column.Name is not null)
            .GroupBy(column => column.Name!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var references = unit.References ?? [];

        for (var referenceIndex = 0; referenceIndex < references.Count; referenceIndex++)
        {
            var reference = references[referenceIndex];
            var path = reference?.Name is { Length: > 0 } name
                ? $"references.{name}"
                : $"references[{referenceIndex}]";
            if (reference is null || string.IsNullOrWhiteSpace(reference.Name) || !names.Add(reference.Name))
            {
                findings.Add(new DeclarationFinding(
                    "GW-DECL-REF-001",
                    "Reference names must be unique and non-empty.",
                    path));
                continue;
            }

            var referenceColumns = reference.Columns ?? [];
            var sourceColumnsAreValid = referenceColumns.Count != 0;
            var seenColumns = new HashSet<string>(StringComparer.Ordinal);
            for (var columnIndex = 0; columnIndex < referenceColumns.Count; columnIndex++)
            {
                var column = referenceColumns[columnIndex];
                if (string.IsNullOrWhiteSpace(column) || !columns.ContainsKey(column) || !seenColumns.Add(column))
                {
                    sourceColumnsAreValid = false;
                    findings.Add(new DeclarationFinding(
                        "GW-DECL-REF-001",
                        $"Reference '{reference.Name}' column '{column}' must name one declared source column exactly once.",
                        $"{path}.columns[{columnIndex}]"));
                }
            }
            if (referenceColumns.Count == 0)
            {
                findings.Add(new DeclarationFinding(
                    "GW-DECL-REF-001",
                    $"Reference '{reference.Name}' requires at least one source column.",
                    $"{path}.columns"));
            }
            if (string.IsNullOrWhiteSpace(reference.TargetUnitId.Value))
            {
                findings.Add(new DeclarationFinding(
                    "GW-DECL-REF-002",
                    $"Reference '{reference.Name}' requires a target storage-unit id.",
                    $"{path}.targetUnitId"));
            }
            if (sourceColumnsAreValid && !HasCoveringIndex(unit, referenceColumns))
            {
                findings.Add(new DeclarationFinding(
                    "GW-DECL-REF-005",
                    $"Reference '{reference.Name}' requires the source columns [{string.Join(", ", referenceColumns)}] " +
                    "as an ordered prefix of the storage key or a declared index.",
                    $"{path}.columns"));
            }
        }

        return findings;
    }

    internal static IReadOnlyList<DeclarationFinding> ValidateTargets(
        StorageUnit source,
        IReadOnlyDictionary<string, StorageUnit> targets)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(targets);
        var findings = new List<DeclarationFinding>();
        foreach (var reference in source.References ?? [])
        {
            if (reference is null || !targets.TryGetValue(reference.Name, out var target))
                continue;
            findings.AddRange(ValidateTarget(source, reference, target));
        }
        return findings;
    }

    internal static IReadOnlyList<DeclarationFinding> ValidateTarget(
        StorageUnit source,
        ReferenceDefinition reference,
        StorageUnit target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(target);
        var findings = new List<DeclarationFinding>();
        var path = $"references.{reference.Name}";
        var sourceColumns = Resolve(source, reference.Columns ?? []);
        var targetKeyNames = target.Key?.Columns ?? [];
        var targetColumns = Resolve(target, targetKeyNames);

        if (!reference.TargetUnitId.Equals(target.Id) ||
            reference.Columns is null || reference.Columns.Count == 0 ||
            reference.Columns.Count != targetKeyNames.Count ||
            sourceColumns.Any(column => column is null) ||
            targetColumns.Any(column => column is null))
        {
            findings.Add(new DeclarationFinding(
                "GW-DECL-REF-002",
                $"Reference '{reference.Name}' must map exactly one declared source column to each key column " +
                $"of target unit '{reference.TargetUnitId.Value}', in key order.",
                $"{path}.targetUnitId"));
        }
        else
        {
            for (var index = 0; index < sourceColumns.Count; index++)
            {
                if (Compatible(sourceColumns[index]!, targetColumns[index]!))
                    continue;
                findings.Add(new DeclarationFinding(
                    "GW-DECL-REF-004",
                    $"Reference '{reference.Name}' source column '{reference.Columns[index]}' has portable type " +
                    $"{Describe(sourceColumns[index]!)} but target key column '{targetKeyNames[index]}' has {Describe(targetColumns[index]!)}.",
                    $"{path}.columns[{index}]"));
            }
        }

        if (source.Scope != target.Scope)
        {
            findings.Add(new DeclarationFinding(
                "GW-DECL-REF-003",
                $"Reference '{reference.Name}' cannot relate a {source.Scope} source unit to a {target.Scope} target unit; " +
                "both units must use the same scope policy.",
                $"{path}.targetUnitId"));
        }

        return findings;
    }

    internal static IReadOnlyList<DeclarationFinding> ValidateManifest(IReadOnlyList<StorageUnit> units)
        => ValidateManifestBySource(units).Select(result => result.Finding).ToArray();

    internal static IReadOnlyList<ManifestReferenceFinding> ValidateManifestBySource(IReadOnlyList<StorageUnit> units)
    {
        ArgumentNullException.ThrowIfNull(units);
        var findings = new List<ManifestReferenceFinding>();
        var targets = units
            .GroupBy(unit => unit.Id.Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        foreach (var source in units)
        {
            foreach (var reference in source.References ?? [])
            {
                if (reference is null || string.IsNullOrWhiteSpace(reference.TargetUnitId.Value))
                    continue;
                if (!targets.TryGetValue(reference.TargetUnitId.Value, out var matches) || matches.Length != 1)
                {
                    findings.Add(new ManifestReferenceFinding(source.Id, new DeclarationFinding(
                        "GW-DECL-REF-002",
                        $"Reference '{reference.Name}' on unit '{source.Id.Value}' must resolve to exactly one target " +
                        $"unit with logical id '{reference.TargetUnitId.Value}'.",
                        $"references.{reference.Name}.targetUnitId")));
                    continue;
                }
                findings.AddRange(ValidateTarget(source, reference, matches[0])
                    .Select(finding => new ManifestReferenceFinding(source.Id, finding)));
            }
        }
        return findings;
    }

    private static IReadOnlyList<ColumnDefinition?> Resolve(StorageUnit unit, IReadOnlyList<string> names)
    {
        var columns = unit.Columns ?? [];
        return names.Select(name => columns.FirstOrDefault(column =>
            column is not null && string.Equals(column.Name, name, StringComparison.Ordinal))).ToArray();
    }

    private static bool HasCoveringIndex(StorageUnit unit, IReadOnlyList<string> columns) =>
        Prefix(unit.Key?.Columns ?? [], columns) ||
        (unit.Indexes ?? []).Any(index => index?.Columns is not null &&
            Prefix(index.Columns.Select(column => column?.Column ?? string.Empty).ToArray(), columns));

    private static bool Prefix(IReadOnlyList<string> candidate, IReadOnlyList<string> required) =>
        candidate.Count >= required.Count &&
        required.Select((column, index) => string.Equals(candidate[index], column, StringComparison.Ordinal)).All(matches => matches);

    private static bool Compatible(ColumnDefinition source, ColumnDefinition target) => source.Type == target.Type;

    private static string Describe(ColumnDefinition column) => column.Type.ToString();
}
