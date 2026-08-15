using Groundwork.Kernel;

namespace Groundwork.Testing;

/// <summary>Shared validation and time rules for provider idempotency ledgers.</summary>
public static class IdempotencyRules
{
    public static AppendIdempotencyDeclaration RequireDeclaration(StorageUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var declaration = unit.AppendIdempotency ?? throw new InvalidOperationException(
            $"Storage unit '{unit.Name}' does not declare append idempotency.");
        declaration.ValidateForProvider();
        return declaration;
    }

    public static void ValidateOperation(OperationId operationId, IReadOnlyList<StorageValues> values)
    {
        if (string.IsNullOrWhiteSpace(operationId.Nonce))
            throw new ArgumentException("An operation id requires a non-empty nonce.", nameof(operationId));
        if (operationId.Nonce.Length > 256)
            throw new ArgumentException("An operation nonce cannot exceed 256 UTF-16 code units.", nameof(operationId));
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
            throw new ArgumentException("An append batch must contain at least one row.", nameof(values));
        if (values.Any(value => value is null))
            throw new ArgumentException("An append batch cannot contain null rows.", nameof(values));
    }

    public static void ValidateOperation(
        StorageUnit unit,
        OperationId operationId,
        IReadOnlyList<StorageValues> values)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ValidateOperation(operationId, values);
        ValidateDistinctKeys(unit, values);
    }

    public static void ValidateDistinctKeys(StorageUnit unit, IReadOnlyList<StorageValues> values)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(values);
        var generatedKeyColumns = unit.Columns
            .Where(column => column.Generation == ColumnGeneration.ProviderSequence)
            .Select(column => column.Name)
            .ToHashSet(StringComparer.Ordinal);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (unit.Key.Columns.Any(column => generatedKeyColumns.Contains(column) && !value.Values.ContainsKey(column)))
                continue;
            var identity = RowWrite.IdentityForAvailableKeys(unit, value.Values);
            if (!identities.Add(identity))
                throw new ArgumentException("An append batch cannot contain duplicate storage keys.", nameof(values));
        }
    }

    public static StorageUnit LogicalUnit(
        StorageUnit unit,
        string scopeColumn)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (unit.Scope != ScopePolicy.Scoped)
            return unit;

        return unit with
        {
            Columns = unit.Columns.Where(column => column.Name != scopeColumn).ToArray(),
            Key = new KeyDefinition { Columns = unit.Key.Columns.Where(column => column != scopeColumn).ToArray() }
        };
    }

    public static bool IsWithinWindow(DateTimeOffset committedAt, DateTimeOffset providerNow, TimeSpan window) =>
        committedAt > ReclamationCutoff(providerNow, window);

    public static DateTimeOffset ReclamationCutoff(DateTimeOffset providerNow, TimeSpan window)
    {
        try
        {
            return providerNow - window;
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.MinValue;
        }
    }

    private static void ValidateForProvider(this AppendIdempotencyDeclaration declaration) =>
        declaration.Validate();
}
