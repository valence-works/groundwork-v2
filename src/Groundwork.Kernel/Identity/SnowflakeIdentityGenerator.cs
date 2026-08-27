namespace Groundwork.Kernel;

public sealed class SnowflakeIdentityGenerator : IIdentityGenerator
{
    private const int WorkerIdBits = 10;
    private const int SequenceBits = 12;
    private const int TimestampBits = 41;
    private const long MaxSequence = (1L << SequenceBits) - 1;
    private const long MaxTimestamp = (1L << TimestampBits) - 1;
    private const int TimestampShift = WorkerIdBits + SequenceBits;
    private const int WorkerIdShift = SequenceBits;

    private readonly TimeProvider timeProvider;
    private readonly SnowflakeIdentityGeneratorOptions options;
    // System.Threading.Lock arrived in .NET 9 and is unavailable on the net8.0 target. Monitor over a
    // plain object gives this generator exactly the same mutual exclusion on both targets; the only
    // thing given up is Lock's uncontended fast path, which is noise next to the clock read and the
    // Base62 encode inside the critical section. A target-conditional field would instead give the
    // two targets two different lock implementations for no observable gain.
    private readonly object gate = new();

    private long lastTimestamp = -1;
    private long sequence;

    public SnowflakeIdentityGenerator(TimeProvider timeProvider, SnowflakeIdentityGeneratorOptions options)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);
        this.timeProvider = timeProvider;
        this.options = options;
    }

    public string Generate()
    {
        lock (gate)
        {
            var timestamp = CurrentMilliseconds();
            if (timestamp is < 0 or > MaxTimestamp)
                throw new InvalidOperationException(
                    $"Timestamp {timestamp} ms is outside the representable {TimestampBits}-bit range [0, {MaxTimestamp}] relative to epoch {options.Epoch:O}; cannot generate a snowflake id.");
            if (timestamp < lastTimestamp)
                throw new InvalidOperationException($"Clock moved backwards. Refusing to generate id for {lastTimestamp - timestamp} ms.");

            if (timestamp == lastTimestamp)
            {
                sequence = (sequence + 1) & MaxSequence;
                if (sequence == 0)
                    timestamp = WaitForNextMillisecond(lastTimestamp);
            }
            else
            {
                sequence = 0;
            }

            lastTimestamp = timestamp;
            var value = ((ulong)timestamp << TimestampShift)
                        | ((ulong)options.WorkerId << WorkerIdShift)
                        | (ulong)sequence;
            return Base62.Encode(value);
        }
    }

    private long CurrentMilliseconds() => (long)(timeProvider.GetUtcNow() - options.Epoch).TotalMilliseconds;

    private long WaitForNextMillisecond(long currentTimestamp)
    {
        var timestamp = CurrentMilliseconds();
        while (timestamp <= currentTimestamp)
            timestamp = CurrentMilliseconds();
        return timestamp;
    }
}
