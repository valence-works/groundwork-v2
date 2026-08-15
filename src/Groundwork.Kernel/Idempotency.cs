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
    public string LedgerName { get; init; } = "__groundwork_operations";

    public void Validate()
    {
        if (Window <= TimeSpan.Zero)
            throw new ArgumentException("An append idempotency window must be positive.", nameof(Window));
        if (string.IsNullOrWhiteSpace(LedgerName) ||
            LedgerName.Length > 128 ||
            !(char.IsLetter(LedgerName[0]) || LedgerName[0] == '_') ||
            LedgerName.Any(character => !(char.IsLetterOrDigit(character) || character == '_')))
        {
            throw new ArgumentException(
                "An append idempotency ledger name must be an identifier of at most 128 letters, digits, or underscores.",
                nameof(LedgerName));
        }
    }

}

/// <summary>Caller-supplied identity for one append operation.</summary>
public readonly record struct OperationId(DateTimeOffset IssuedAt, string Nonce);
