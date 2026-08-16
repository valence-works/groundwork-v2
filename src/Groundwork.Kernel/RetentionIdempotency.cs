using System.Text;

namespace Groundwork.Kernel;

/// <summary>
/// Declares the durable replay window and ledger for operation-identified retention.
/// The owning storage unit must also declare <see cref="RetentionDeclaration"/>;
/// use retention alone when status-only retention is desired.
/// </summary>
public sealed record RetentionIdempotencyDeclaration
{
    internal const string MissingRetentionDiagnosticCode = "GW-RETENTION-004";

    public required TimeSpan Window { get; init; }

    public string LedgerName { get; init; } = ProviderReservedLedgerNames.DefaultRetentionLedger;

    public void Validate() => Validate(null);

    public void Validate(StorageUnit? owner)
    {
        if (owner is not null)
            ValidateOwner(owner);

        if (Window <= TimeSpan.Zero)
            throw new ArgumentException("A retention idempotency window must be positive.", nameof(Window));
        if (string.IsNullOrWhiteSpace(LedgerName) ||
            LedgerName.Length > 128 ||
            Encoding.UTF8.GetByteCount(LedgerName) > 63 ||
            !(char.IsLetter(LedgerName[0]) || LedgerName[0] == '_') ||
            LedgerName.Any(character => !(char.IsLetterOrDigit(character) || character == '_')))
        {
            throw new ArgumentException(
                "A retention idempotency ledger name must be an identifier of at most 63 UTF-8 bytes, using letters, digits, or underscores.",
                nameof(LedgerName));
        }

        if (owner is not null && string.Equals(LedgerName, owner.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"The retention idempotency ledger '{LedgerName}' cannot share a provider storage name with unit '{owner.Name}'.",
                nameof(LedgerName));
        }

        if (owner?.AppendIdempotency is { } append &&
            string.Equals(LedgerName, append.LedgerName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Append and retention idempotency ledgers cannot share provider storage name '{LedgerName}'; declare distinct ledgers.",
                nameof(LedgerName));
        }

        if (ProviderReservedLedgerNames.All.Contains(LedgerName, StringComparer.OrdinalIgnoreCase) &&
            !string.Equals(LedgerName, ProviderReservedLedgerNames.DefaultRetentionLedger, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"The retention idempotency ledger '{LedgerName}' is reserved by a provider-owned catalog.",
                nameof(LedgerName));
        }
    }

    internal static void ValidateOwner(StorageUnit owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (owner.Retention is null && owner.RetentionIdempotency is not null)
        {
            throw new ArgumentException(
                MissingRetentionMessage(owner),
                nameof(owner));
        }
    }

    internal static string MissingRetentionMessage(StorageUnit owner) =>
        $"{MissingRetentionDiagnosticCode}: storage unit '{owner.Name}' declares RetentionIdempotency without Retention. " +
        "Declare Retention(...) before RetentionIdempotency(...); omit RetentionIdempotency for status-only retention.";
}
