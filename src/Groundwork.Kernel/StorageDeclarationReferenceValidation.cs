namespace Groundwork.Kernel;

/// <summary>
/// Owns the declaration reference rules shared by authoring and physical schema construction.
/// </summary>
internal static class StorageDeclarationReferenceValidation
{
    internal static IReadOnlyList<DeclarationFinding> Validate(StorageUnit unit, bool missingKey = false)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var diagnostics = new List<DeclarationFinding>();
        var columns = unit.Columns ?? [];
        var declarations = columns
            .Where(column => column?.Name is not null)
            .GroupBy(column => column.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var keyColumns = unit.Key?.Columns ?? [];

        if (missingKey || keyColumns.Count == 0)
            diagnostics.Add(new("GW-DECL-KEY-001", "A storage declaration requires a key.", "key"));

        var seenKeyColumns = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < keyColumns.Count; index++)
        {
            var column = keyColumns[index];
            if (string.IsNullOrWhiteSpace(column) || !declarations.ContainsKey(column))
                diagnostics.Add(new("GW-DECL-KEY-002", $"Key column '{column}' is not declared on the storage unit.", $"key.columns[{index}]"));
            if (!string.IsNullOrWhiteSpace(column) && !seenKeyColumns.Add(column))
                diagnostics.Add(new("GW-DECL-KEY-003", $"Key column '{column}' is listed more than once.", "key.columns"));
        }

        foreach (var declaredIndex in unit.Indexes ?? [])
        {
            if (declaredIndex is null)
                continue;
            var seenIndexColumns = new HashSet<string>(StringComparer.Ordinal);
            var indexColumns = declaredIndex.Columns ?? [];
            for (var columnIndex = 0; columnIndex < indexColumns.Count; columnIndex++)
            {
                var column = indexColumns[columnIndex]?.Column;
                if (string.IsNullOrWhiteSpace(column) || !declarations.ContainsKey(column))
                {
                    diagnostics.Add(new(
                        "GW-DECL-INDEX-001",
                        $"Index '{declaredIndex.Name}' column '{column}' is not declared on the storage unit.",
                        $"indexes.{declaredIndex.Name}.columns[{columnIndex}]"));
                }
                if (!string.IsNullOrWhiteSpace(column) && !seenIndexColumns.Add(column))
                {
                    diagnostics.Add(new(
                        "GW-DECL-INDEX-002",
                        $"Index '{declaredIndex.Name}' column '{column}' is listed more than once.",
                        $"indexes.{declaredIndex.Name}.columns"));
                }
                if (!string.IsNullOrWhiteSpace(column) &&
                    declarations.TryGetValue(column, out var declaration) &&
                    declaration.Type == PortableType.Json)
                {
                    diagnostics.Add(new(
                        "GW-DECL-INDEX-003",
                        $"Index '{declaredIndex.Name}' column '{column}' is JSON and cannot be represented as a portable query index key. Leave the JSON column unindexed or index a declared scalar projection instead.",
                        $"indexes.{declaredIndex.Name}.columns[{columnIndex}]"));
                }
            }
        }

        diagnostics.AddRange(StorageReferenceValidation.ValidateLocal(unit));

        return diagnostics;
    }

    internal static void ThrowIfInvalid(StorageUnit unit)
    {
        var findings = Validate(unit);
        if (findings.Count == 0)
            return;
        throw new ArgumentException(
            "The storage declaration has invalid references: " + string.Join(
                "; ", findings.Select(finding => $"{finding.Code} at {finding.Path}: {finding.Message}")),
            nameof(unit));
    }
}
