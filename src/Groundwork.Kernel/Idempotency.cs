using System.Text;

namespace Groundwork.Kernel;

/// <summary>Declares the durable replay window for append operations on a storage unit.</summary>
public sealed record AppendIdempotencyDeclaration
{
    private static readonly string[] ProviderReservedLedgerNames =
    [
        "__groundwork_metadata", "__groundwork_sequences", "__groundwork_schema_history",
        "__groundwork_schema_locks", "__groundwork_schema_fences", "__groundwork_search_key_algorithms"
    ];

    /// <summary>
    /// The amount of provider-recorded time for which an operation nonce is retained. A replay
    /// inside this window is acknowledged without writing the payload again.
    /// </summary>
    public required TimeSpan Window { get; init; }

    /// <summary>
    /// Kernel-owned ledger table/collection name. The default is shared by all units.
    /// </summary>
    public string LedgerName { get; init; } = "__groundwork_operations";

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

        if (ProviderReservedLedgerNames.Contains(LedgerName, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"The append idempotency ledger '{LedgerName}' is reserved by a provider-owned catalog.",
                nameof(LedgerName));
        }
    }

}

/// <summary>Caller-supplied identity for one append operation.</summary>
public readonly record struct OperationId(DateTimeOffset IssuedAt, string Nonce);
