using System.Text;

namespace Groundwork.Kernel;

/// <summary>Declares the durable replay window and ledger for operation-identified retention.</summary>
public sealed record RetentionIdempotencyDeclaration
{
    private static readonly string[] ProviderReservedLedgerNames =
    [
        "__groundwork_metadata", "__groundwork_sequences", "__groundwork_schema_history",
        "__groundwork_schema_locks", "__groundwork_schema_fences", "__groundwork_search_key_algorithms"
    ];

    public required TimeSpan Window { get; init; }

    public string LedgerName { get; init; } = "__groundwork_retention_operations";

    public void Validate() => Validate(null);

    public void Validate(StorageUnit? owner)
    {
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

        if (ProviderReservedLedgerNames.Contains(LedgerName, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"The retention idempotency ledger '{LedgerName}' is reserved by a provider-owned catalog.",
                nameof(LedgerName));
        }
    }
}
