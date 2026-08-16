using System.Text;

namespace Groundwork.Kernel;

/// <summary>Declares the durable replay window for append operations on a storage unit.</summary>
public sealed record AppendIdempotencyDeclaration
{
    /// <summary>
    /// The amount of provider-recorded time for which an operation nonce is retained. A replay
    /// inside this window is acknowledged without writing the payload again.
    /// </summary>
    public required TimeSpan Window { get; init; }

    /// <summary>
    /// Kernel-owned ledger table/collection name. The default is shared by all units.
    /// </summary>
    public string LedgerName { get; init; } = ProviderReservedLedgerNames.DefaultAppendLedger;

    public void Validate()
    {
        if (Window <= TimeSpan.Zero)
            throw new ArgumentException("An append idempotency window must be positive.", nameof(Window));
        if (string.IsNullOrWhiteSpace(LedgerName) ||
            LedgerName.Length > 128 ||
            Encoding.UTF8.GetByteCount(LedgerName) > 63 ||
            !(char.IsLetter(LedgerName[0]) || LedgerName[0] == '_') ||
            LedgerName.Any(character => !(char.IsLetterOrDigit(character) || character == '_')))
        {
            throw new ArgumentException(
                "An append idempotency ledger name must be an identifier of at most 63 UTF-8 bytes, using letters, digits, or underscores.",
                nameof(LedgerName));
        }
    }

    /// <summary>Validates the ledger name against the storage unit that owns it.</summary>
    public void Validate(StorageUnit owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        Validate();
        if (string.Equals(LedgerName, owner.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"The append idempotency ledger '{LedgerName}' cannot share a provider storage name with unit '{owner.Name}'.",
                nameof(LedgerName));
        }

        if (owner.RetentionIdempotency is { } retention &&
            string.Equals(LedgerName, retention.LedgerName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Append and retention idempotency ledgers cannot share provider storage name '{LedgerName}'; declare distinct ledgers.",
                nameof(LedgerName));
        }

        if (ProviderReservedLedgerNames.All.Contains(LedgerName, StringComparer.OrdinalIgnoreCase) &&
            !string.Equals(LedgerName, ProviderReservedLedgerNames.DefaultAppendLedger, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"The append idempotency ledger '{LedgerName}' is reserved by a provider-owned catalog.",
                nameof(LedgerName));
        }
    }

}

/// <summary>Caller-supplied identity for one append operation.</summary>
public readonly record struct OperationId(DateTimeOffset IssuedAt, string Nonce);
