namespace Groundwork.Kernel;

public enum IdentityGeneratorKind
{
    Short,
    UuidV7,
    Snowflake,
    Guid
}

public static class GroundworkIdentityGenerators
{
    public static IIdentityGenerator Create(
        IdentityGeneratorKind kind,
        TimeProvider? timeProvider = null,
        SnowflakeIdentityGeneratorOptions? snowflakeOptions = null)
    {
        timeProvider ??= TimeProvider.System;
        return kind switch
        {
            IdentityGeneratorKind.Short => new ShortIdentityGenerator(timeProvider),
            IdentityGeneratorKind.UuidV7 => new UuidV7IdentityGenerator(timeProvider),
            IdentityGeneratorKind.Snowflake => new SnowflakeIdentityGenerator(
                timeProvider,
                snowflakeOptions ?? throw new ArgumentNullException(
                    nameof(snowflakeOptions),
                    "Snowflake generator requires options with a worker id.")),
            IdentityGeneratorKind.Guid => new GuidIdentityGenerator(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown identity generator kind.")
        };
    }
}
