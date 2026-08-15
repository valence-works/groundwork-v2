using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;

namespace Groundwork.Architecture.Tests;

// These tests defend reference direction and public naming, not semantic shape. A neutrally named
// kernel API can still be contract-family-shaped. The second-family proof in Groundwork#263 and
// building Groundwork.Documents outside this repository in Groundwork#265 are the stronger defenses.
public sealed class KernelBoundaryTests
{
    private static readonly ImmutableArray<string> ForbiddenContractVocabulary =
        ["Document", "Envelope", "Record", "Stream", "Diagnostic"];

    private static readonly ImmutableHashSet<string> KnownContractFamilies =
        ImmutableHashSet.Create(StringComparer.Ordinal,
            "Groundwork.Records",
            "Groundwork.Documents");

    [Fact]
    public void Kernel_substrates_and_providers_do_not_reference_contract_families()
    {
        using var universe = AssemblyUniverse.Load();
        var contractFamilies = universe.Assemblies
            .Where(IsContractFamily)
            .Select(assembly => assembly.GetName().Name!)
            .Concat(KnownContractFamilies)
            .ToImmutableHashSet(StringComparer.Ordinal);

        var violations = universe.Assemblies
            .Where(IsKernelSubstrateOrProvider)
            .SelectMany(assembly => assembly.GetReferencedAssemblies()
                .Where(reference => reference.Name is not null && contractFamilies.Contains(reference.Name))
                .Select(reference => $"{assembly.GetName().Name} -> {reference.Name}"))
            .OrderBy(violation => violation, StringComparer.Ordinal)
            .ToArray();

        Assert.True(violations.Length == 0,
            "Kernel, substrate, and provider assemblies must not reference contract families:" +
            Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Groundwork_kernel_references_only_bcl_assemblies()
    {
        using var universe = AssemblyUniverse.Load();
        var violations = universe.Assemblies
            .Where(assembly => IsKernelAssembly(assembly.GetName().Name))
            .SelectMany(assembly => assembly.GetReferencedAssemblies()
                .Where(reference => reference.Name is not null && !universe.BclAssemblyNames.Contains(reference.Name))
                .Select(reference => $"{assembly.GetName().Name} -> {reference.Name}"))
            .OrderBy(violation => violation, StringComparer.Ordinal)
            .ToArray();

        Assert.True(violations.Length == 0,
            "Groundwork.Kernel assemblies may reference only the BCL:" + Environment.NewLine +
            string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Kernel_and_substrate_public_apis_use_contract_neutral_vocabulary()
    {
        using var universe = AssemblyUniverse.Load();
        var violations = universe.Assemblies
            .Where(assembly => IsKernelAssembly(assembly.GetName().Name) ||
                               IsSubstrateAssembly(assembly.GetName().Name))
            .SelectMany(PublicSignatures)
            .SelectMany(signature => ForbiddenContractVocabulary
                .Where(token => signature.Contains(token, StringComparison.OrdinalIgnoreCase))
                .Select(token => $"{token}: {signature}"))
            .OrderBy(violation => violation, StringComparer.Ordinal)
            .ToArray();

        Assert.True(violations.Length == 0,
            "Kernel and substrate public APIs must use contract-neutral vocabulary:" +
            Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    private static bool IsKernelSubstrateOrProvider(Assembly assembly)
    {
        var name = assembly.GetName().Name;
        return IsKernelAssembly(name) || IsSubstrateAssembly(name) || IsProviderAssembly(name);
    }

    private static bool IsKernelAssembly(string? name) =>
        name is not null &&
        (name.StartsWith("Groundwork.Kernel", StringComparison.Ordinal) ||
         string.Equals(name, "Groundwork.Query.Model", StringComparison.Ordinal));

    private static bool IsSubstrateAssembly(string? name) =>
        name?.StartsWith("Groundwork.Substrate.", StringComparison.Ordinal) == true;

    private static bool IsProviderAssembly(string? name) =>
        name?.StartsWith("Groundwork.MongoDb", StringComparison.Ordinal) == true ||
        name?.StartsWith("Groundwork.PostgreSql", StringComparison.Ordinal) == true ||
        name?.StartsWith("Groundwork.Sqlite", StringComparison.Ordinal) == true ||
        name?.StartsWith("Groundwork.SqlServer", StringComparison.Ordinal) == true;

    private static bool IsContractFamily(Assembly assembly) =>
        assembly.GetCustomAttributesData().Any(attribute =>
            attribute.AttributeType.FullName == typeof(AssemblyMetadataAttribute).FullName &&
            attribute.ConstructorArguments.Count == 2 &&
            string.Equals(attribute.ConstructorArguments[0].Value as string,
                "Groundwork.ContractFamily", StringComparison.Ordinal) &&
            string.Equals(attribute.ConstructorArguments[1].Value as string,
                "true", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> PublicSignatures(Assembly assembly)
    {
        foreach (var type in assembly.GetExportedTypes().OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            yield return type.FullName ?? type.Name;

            foreach (var member in type.GetMembers(
                         BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (member.MemberType != MemberTypes.NestedType)
                    yield return Describe(member);
            }
        }
    }

    private static string Describe(MemberInfo member) => member switch
    {
        ConstructorInfo constructor =>
            $"{constructor.DeclaringType?.FullName}.ctor({Parameters(constructor)})",
        MethodInfo method =>
            $"{TypeName(method.ReturnType)} {method.DeclaringType?.FullName}.{method.Name}({Parameters(method)})",
        PropertyInfo property =>
            $"{TypeName(property.PropertyType)} {property.DeclaringType?.FullName}.{property.Name}",
        FieldInfo field =>
            $"{TypeName(field.FieldType)} {field.DeclaringType?.FullName}.{field.Name}",
        EventInfo eventInfo =>
            $"{TypeName(eventInfo.EventHandlerType)} {eventInfo.DeclaringType?.FullName}.{eventInfo.Name}",
        _ => $"{member.DeclaringType?.FullName}.{member.Name}"
    };

    private static string Parameters(MethodBase method) => string.Join(", ",
        method.GetParameters().Select(parameter => $"{TypeName(parameter.ParameterType)} {parameter.Name}"));

    private static string TypeName(Type? type) => type?.ToString() ?? "<null>";

    private sealed class AssemblyUniverse : IDisposable
    {
        private readonly MetadataLoadContext context;

        private AssemblyUniverse(
            MetadataLoadContext context,
            ImmutableArray<Assembly> assemblies,
            ImmutableHashSet<string> bclAssemblyNames)
        {
            this.context = context;
            Assemblies = assemblies;
            BclAssemblyNames = bclAssemblyNames;
        }

        public ImmutableArray<Assembly> Assemblies { get; }

        public ImmutableHashSet<string> BclAssemblyNames { get; }

        public static AssemblyUniverse Load()
        {
            var trustedPlatformAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                ?? throw new InvalidOperationException("The runtime did not expose trusted platform assemblies.");
            var groundworkAssemblies = Directory
                .EnumerateFiles(AppContext.BaseDirectory, "Groundwork*.dll", SearchOption.TopDirectoryOnly)
                .Where(path => !Path.GetFileNameWithoutExtension(path).EndsWith(".Tests", StringComparison.Ordinal))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToImmutableArray();
            var resolver = new PathAssemblyResolver(trustedPlatformAssemblies
                .Concat(groundworkAssemblies)
                .Distinct(StringComparer.Ordinal));
            var context = new MetadataLoadContext(resolver);
            var assemblies = groundworkAssemblies
                .Select(context.LoadFromAssemblyPath)
                .ToImmutableArray();
            var runtimeDirectory = Path.TrimEndingDirectorySeparator(RuntimeEnvironment.GetRuntimeDirectory());
            var bclNames = trustedPlatformAssemblies
                .Where(path => string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(path)!),
                    runtimeDirectory,
                    StringComparison.Ordinal))
                .Select(path => Path.GetFileNameWithoutExtension(path)!)
                .ToImmutableHashSet(StringComparer.Ordinal);

            return new AssemblyUniverse(context, assemblies, bclNames);
        }

        public void Dispose() => context.Dispose();
    }
}
