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
