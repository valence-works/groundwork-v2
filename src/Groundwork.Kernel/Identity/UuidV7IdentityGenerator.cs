namespace Groundwork.Kernel;

public sealed class UuidV7IdentityGenerator(TimeProvider? timeProvider = null) : IIdentityGenerator
{
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public string Generate() => Guid.CreateVersion7(timeProvider.GetUtcNow()).ToString("N");
}
