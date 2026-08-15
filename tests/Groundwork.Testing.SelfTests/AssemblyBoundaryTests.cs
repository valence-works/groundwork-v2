using System.Reflection;
using Groundwork.Testing;

namespace Groundwork.Testing.SelfTests;

public sealed class AssemblyBoundaryTests
{
    [Fact]
    public void Public_contract_is_external_and_has_no_provider_sdk_reference()
    {
        var assembly = typeof(IStorageProviderFactory).Assembly;
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
            reference.Name?.StartsWith("Groundwork.", StringComparison.Ordinal) == true &&
            reference.Name != "Groundwork.Kernel");
    }
}
