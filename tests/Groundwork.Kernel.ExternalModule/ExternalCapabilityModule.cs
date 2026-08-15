using Groundwork.Kernel;

namespace Groundwork.Kernel.ExternalModule;

public sealed class ExternalCapabilityModule : IGroundworkModule
{
    public static readonly CapabilityId Capability = new("external.test.capability");

    public string Name => "external.test";

    public void RegisterCapabilities(ICapabilityRegistryBuilder builder) =>
        builder.Add(new CapabilityDescriptor(
            Capability,
            "External capability",
            "Capability contributed by a separate assembly."));
}
