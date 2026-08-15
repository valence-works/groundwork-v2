namespace Groundwork.Kernel;

public sealed class SnowflakeIdentityGeneratorOptions
{
    public const long MinWorkerId = 0;
    public const long MaxWorkerId = 1023;

    private readonly long workerId;

    public long WorkerId
    {
        get => workerId;
        init
        {
            if (value is < MinWorkerId or > MaxWorkerId)
                throw new ArgumentOutOfRangeException(
                    nameof(WorkerId),
                    value,
                    $"WorkerId must be in [{MinWorkerId}, {MaxWorkerId}].");
            workerId = value;
        }
    }

    public DateTimeOffset Epoch { get; init; } = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
}
