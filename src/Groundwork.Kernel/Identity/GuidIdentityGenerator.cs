namespace Groundwork.Kernel;

public sealed class GuidIdentityGenerator : IIdentityGenerator
{
    public string Generate() => Guid.NewGuid().ToString("N");
}
