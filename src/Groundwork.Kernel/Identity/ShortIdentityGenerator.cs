namespace Groundwork.Kernel;

public sealed class ShortIdentityGenerator(TimeProvider? timeProvider = null) : IIdentityGenerator
{
    internal static readonly DateTimeOffset Epoch = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const int RandomBits = 22;
    private const int TimestampBits = 64 - RandomBits;
    private const long MaxMilliseconds = (1L << TimestampBits) - 1;
    private const long RandomMask = (1L << RandomBits) - 1;

    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public string Generate()
    {
        var milliseconds = (long)(timeProvider.GetUtcNow() - Epoch).TotalMilliseconds;
        if (milliseconds is < 0 or > MaxMilliseconds)
        {
            throw new InvalidOperationException(
                $"Timestamp {milliseconds} ms is outside the representable {TimestampBits}-bit range [0, {MaxMilliseconds}] relative to epoch {Epoch:O}; cannot generate a short id.");
        }

        var random = Random.Shared.NextInt64(1L << RandomBits);
        var value = ((ulong)milliseconds << RandomBits) | (ulong)(random & RandomMask);
        return Base62.Encode(value);
    }
}
