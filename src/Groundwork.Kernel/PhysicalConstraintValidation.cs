using Groundwork.Kernel.Schema;

namespace Groundwork.Kernel;

internal static class PhysicalConstraintValidation
{
    public static void ThrowIfInvalid(StorageUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var columns = (unit.Columns ?? []).ToDictionary(column => column.Name, StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var reference in unit.References ?? [])
        {
            if (reference.Enforcement != ReferenceEnforcement.Physical)
                continue;
            RequireUniqueName(reference.Name, "reference", names, unit);
            if (string.IsNullOrWhiteSpace(reference.TargetName) ||
                reference.TargetKeyColumns is null ||
                reference.TargetKeyColumns.Count == 0 ||
                reference.TargetKeyHasProviderSequence is null ||
                reference.TargetKeyColumns.Count != reference.Columns.Count)
            {
                throw new ArgumentException(
                    $"Physical reference '{reference.Name}' requires resolved target storage and key metadata in reference order.",
                    nameof(unit));
            }
        }

        foreach (var check in unit.CheckConstraints ?? [])
        {
            if (check is null || string.IsNullOrWhiteSpace(check.Name))
                throw new ArgumentException("Check constraint names must be non-empty.", nameof(unit));
            RequireUniqueName(check.Name, "check constraint", names, unit);
            if (string.IsNullOrWhiteSpace(check.Column) || !columns.TryGetValue(check.Column, out var column))
            {
                throw new ArgumentException(
                    $"Check constraint '{check.Name}' must name one declared column.",
                    nameof(unit));
            }
            if (!Enum.IsDefined(check.Operator))
                throw new ArgumentException($"Check constraint '{check.Name}' uses an unsupported operator.", nameof(unit));
            if (check.Value is null)
                throw new ArgumentException($"Check constraint '{check.Name}' requires a value wrapper.", nameof(unit));
            if (check.Value.Value is null && check.Operator is not (CheckConstraintOperator.Equal or CheckConstraintOperator.NotEqual))
            {
                throw new ArgumentException(
                    $"Check constraint '{check.Name}' can compare null only with Equal or NotEqual.",
                    nameof(unit));
            }
            if (column.Type is PortableType.Double or PortableType.Json)
            {
                throw new ArgumentException(
                    $"Check constraint '{check.Name}' cannot compare {column.Type} column '{column.Name}' portably.",
                    nameof(unit));
            }
            if (check.Operator is not (CheckConstraintOperator.Equal or CheckConstraintOperator.NotEqual) &&
                column.Type is PortableType.Boolean or PortableType.Guid or PortableType.Binary)
            {
                throw new ArgumentException(
                    $"Check constraint '{check.Name}' supports only equality operators for {column.Type} column '{column.Name}'.",
                    nameof(unit));
            }
            _ = SchemaValue.Snapshot(check.Value.Value, column.Type);
        }
    }

    private static void RequireUniqueName(
        string name,
        string kind,
        HashSet<string> names,
        StorageUnit unit)
    {
        if (string.IsNullOrWhiteSpace(name) || !names.Add(name))
        {
            throw new ArgumentException(
                $"Physical reference and check constraint names must be unique; {kind} '{name}' collides.",
                nameof(unit));
        }
    }
}
