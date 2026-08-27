namespace Groundwork.Kernel;

public sealed class UuidV7IdentityGenerator(TimeProvider? timeProvider = null) : IIdentityGenerator
{
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public string Generate() => UuidV7.Create(timeProvider.GetUtcNow()).ToString("N");
}
