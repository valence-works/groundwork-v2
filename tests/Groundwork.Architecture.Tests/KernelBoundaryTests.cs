using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.InteropServices;
using Groundwork.Store;
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
        var unclassified = universe.Assemblies
            .Where(assembly => !IsClassifiedProductAssembly(assembly))
            .Select(assembly => assembly.GetName().Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.True(unclassified.Length == 0,
            "Every Groundwork product assembly must be classified as kernel, substrate, provider, " +
            "contract family, testing, or tooling. New providers must set " +
            "[assembly: AssemblyMetadata(\"Groundwork.Provider\", \"true\")]:" +
            Environment.NewLine + string.Join(Environment.NewLine, unclassified));

        var violations = universe.Assemblies
            .Where(IsKernelSubstrateOrProvider)
            .SelectMany(assembly => universe.NonBclReferenceClosure(assembly)
                .Where(reference => contractFamilies.Contains(reference.Name))
                .Select(reference => reference.Path))
            .OrderBy(violation => violation, StringComparer.Ordinal)
            .ToArray();

        Assert.True(violations.Length == 0,
            "Kernel, substrate, and provider assemblies must not reference contract families:" +
            Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Groundwork_kernel_references_only_bcl_and_the_query_model_kernel()
    {
        using var universe = AssemblyUniverse.Load();
        var violations = universe.Assemblies
            .Where(assembly => IsKernelAssembly(assembly.GetName().Name))
            .SelectMany(assembly => universe.NonBclReferenceClosure(assembly)
                .Where(reference => !string.Equals(reference.Name, "Groundwork.Query.Model", StringComparison.Ordinal))
                .Select(reference => reference.Path))
            .OrderBy(violation => violation, StringComparer.Ordinal)
            .ToArray();

        Assert.True(violations.Length == 0,
            "Groundwork.Kernel assemblies may reference only the BCL and provider-neutral query model:" + Environment.NewLine +
            string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Groundwork_query_model_is_bcl_only()
    {
        using var universe = AssemblyUniverse.Load();
        var queryModel = universe.Assemblies.Single(assembly =>
            string.Equals(assembly.GetName().Name, "Groundwork.Query.Model", StringComparison.Ordinal));
        var violations = universe.NonBclReferenceClosure(queryModel)
            .Select(reference => reference.Path)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void Kernel_and_substrate_public_apis_use_contract_neutral_vocabulary()
    {
        using var universe = AssemblyUniverse.Load();
        var violations = universe.Assemblies
            .Where(assembly => IsKernelAssembly(assembly.GetName().Name) ||
                               IsSubstrateAssembly(assembly.GetName().Name) ||
                               string.Equals(assembly.GetName().Name, "Groundwork.Query.Planning", StringComparison.Ordinal))
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

    [Fact]
    public void Store_public_api_is_production_neutral_and_has_no_testing_dependency()
    {
        using var universe = AssemblyUniverse.Load();
        var store = universe.Assemblies.Single(assembly =>
            string.Equals(assembly.GetName().Name, "Groundwork.Store", StringComparison.Ordinal));
        var forbidden = new[] { "Conformance", "Fixture", "Probe", "TestMode", "TestAdapter" };
        var vocabularyViolations = PublicSignatures(store)
            .Where(signature => forbidden.Any(token =>
                signature.Contains(token, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(signature => signature, StringComparer.Ordinal)
            .ToArray();
        Assert.True(vocabularyViolations.Length == 0,
            "Groundwork.Store public signatures must not expose testing vocabulary:" +
            Environment.NewLine + string.Join(Environment.NewLine, vocabularyViolations));

        var testingReferences = universe.NonBclReferenceClosure(store)
            .Where(reference => reference.Name.StartsWith("Groundwork.Testing", StringComparison.Ordinal))
            .Select(reference => reference.Path)
            .ToArray();
        Assert.Empty(testingReferences);
    }

    [Fact]
    public void Store_public_lifetime_contract_keeps_resource_ownership_explicit()
    {
        Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(IStorageProviderConnection)));
        Assert.False(typeof(IDisposable).IsAssignableFrom(typeof(IStorageSession)));
        Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(IUnitOfWork)));
    }

    private static bool IsKernelSubstrateOrProvider(Assembly assembly)
    {
        var name = assembly.GetName().Name;
        return IsKernelAssembly(name) || IsSubstrateAssembly(name) || IsProviderAssembly(assembly);
    }

    private static bool IsClassifiedProductAssembly(Assembly assembly)
    {
        var name = assembly.GetName().Name;
        return IsKernelAssembly(name) || IsSubstrateAssembly(name) || IsProviderAssembly(assembly) ||
               IsContractFamily(assembly) ||
               HasMetadata(assembly, "Groundwork.Tool", "true") ||
               string.Equals(name, "Groundwork.Store", StringComparison.Ordinal) ||
               string.Equals(name, "Groundwork.Diagnostics", StringComparison.Ordinal) ||
               string.Equals(name, "Groundwork.Records.Store", StringComparison.Ordinal) ||
               string.Equals(name, "Groundwork.Query.Planning", StringComparison.Ordinal) ||
               string.Equals(name, "Groundwork.Testing", StringComparison.Ordinal) ||
               name?.StartsWith("Groundwork.Tool", StringComparison.Ordinal) == true;
    }

    private static bool IsKernelAssembly(string? name) =>
        name is not null &&
        (name.StartsWith("Groundwork.Kernel", StringComparison.Ordinal) ||
         string.Equals(name, "Groundwork.Query.Model", StringComparison.Ordinal));

    private static bool IsSubstrateAssembly(string? name) =>
        name?.StartsWith("Groundwork.Substrate.", StringComparison.Ordinal) == true;

    private static bool IsProviderAssembly(Assembly assembly)
    {
        var name = assembly.GetName().Name;
        return name?.StartsWith("Groundwork.MongoDb", StringComparison.Ordinal) == true ||
               name?.StartsWith("Groundwork.PostgreSql", StringComparison.Ordinal) == true ||
               name?.StartsWith("Groundwork.Sqlite", StringComparison.Ordinal) == true ||
               name?.StartsWith("Groundwork.SqlServer", StringComparison.Ordinal) == true ||
               HasMetadata(assembly, "Groundwork.Provider", "true");
    }

    private static bool IsContractFamily(Assembly assembly) =>
        HasMetadata(assembly, "Groundwork.ContractFamily", "true");

    private static bool HasMetadata(Assembly assembly, string key, string value) =>
        assembly.GetCustomAttributesData().Any(attribute =>
            attribute.AttributeType.FullName == typeof(AssemblyMetadataAttribute).FullName &&
            attribute.ConstructorArguments.Count == 2 &&
            string.Equals(attribute.ConstructorArguments[0].Value as string,
                key, StringComparison.Ordinal) &&
            string.Equals(attribute.ConstructorArguments[1].Value as string,
                value, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> PublicSignatures(Assembly assembly)
    {
        foreach (var type in assembly.GetExportedTypes().OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            yield return Describe(type);

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
        Type type =>
            $"{type.FullName ?? type.Name}{Inheritance(type)}{GenericConstraints(type.GetGenericArguments())}",
        ConstructorInfo constructor =>
            $"{constructor.DeclaringType?.FullName}.ctor({Parameters(constructor)})",
        MethodInfo method =>
            $"{TypeName(method.ReturnType)} {method.DeclaringType?.FullName}.{method.Name}({Parameters(method)})" +
            GenericConstraints(method.GetGenericArguments()),
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

    private static string Inheritance(Type type)
    {
        var inheritedTypes = type.GetInterfaces()
            .Concat(type.BaseType is null || type.BaseType.FullName == typeof(object).FullName
                ? []
                : [type.BaseType])
            .Select(TypeName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        return inheritedTypes.Length == 0 ? string.Empty : " : " + string.Join(", ", inheritedTypes);
    }

    private static string GenericConstraints(IEnumerable<Type> genericArguments)
    {
        var constraints = genericArguments
            .Where(argument => argument.IsGenericParameter)
            .Select(argument =>
            {
                var types = argument.GetGenericParameterConstraints().Select(TypeName);
                var parts = new[] { argument.GenericParameterAttributes.ToString() }
                    .Where(value => value != GenericParameterAttributes.None.ToString())
                    .Concat(types);
                return $" where {argument.Name} : {string.Join(", ", parts)}";
            });
        return string.Concat(constraints);
    }

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

        public IEnumerable<AssemblyReference> NonBclReferenceClosure(Assembly root)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Queue<(Assembly Assembly, string Path)>();
            pending.Enqueue((root, root.GetName().Name!));

            while (pending.Count > 0)
            {
                var current = pending.Dequeue();
                foreach (var reference in current.Assembly.GetReferencedAssemblies()
                             .Where(reference => reference.Name is not null &&
                                                 !BclAssemblyNames.Contains(reference.Name))
                             .OrderBy(reference => reference.Name, StringComparer.Ordinal))
                {
                    var path = $"{current.Path} -> {reference.Name}";
                    yield return new AssemblyReference(reference.Name!, path);

                    if (visited.Add(reference.FullName))
                        pending.Enqueue((context.LoadFromAssemblyName(reference), path));
                }
            }
        }

        public static AssemblyUniverse Load()
        {
            var trustedPlatformAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                ?? throw new InvalidOperationException("The runtime did not expose trusted platform assemblies.");
            var outputAssemblies = Directory
                .EnumerateFiles(AppContext.BaseDirectory, "*.dll", SearchOption.TopDirectoryOnly)
                .Where(IsManagedAssembly)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToImmutableArray();
            var groundworkAssemblies = outputAssemblies
                .Where(path => !Path.GetFileNameWithoutExtension(path).EndsWith(".Tests", StringComparison.Ordinal))
                .Where(path => Path.GetFileNameWithoutExtension(path).StartsWith("Groundwork.", StringComparison.Ordinal))
                .ToImmutableArray();
            if (!groundworkAssemblies.Any(path =>
                    Path.GetFileNameWithoutExtension(path).StartsWith("Groundwork.Kernel", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    "Architecture assembly discovery found no Groundwork.Kernel assembly; refusing a vacuous pass.");
            }

            var resolverAssemblyNames = new HashSet<string>(StringComparer.Ordinal);
            var resolverPaths = trustedPlatformAssemblies
                .Concat(outputAssemblies)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path.Contains("/runtimes/", StringComparison.OrdinalIgnoreCase))
                .ThenBy(path => path, StringComparer.Ordinal)
                .Where(path => TryClaimAssemblyName(path, resolverAssemblyNames, out _))
                .ToArray();
            var resolver = new PathAssemblyResolver(resolverPaths);
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

        private static bool IsManagedAssembly(string path)
        {
            try
            {
                _ = AssemblyName.GetAssemblyName(path);
                return true;
            }
            catch (BadImageFormatException)
            {
                return false;
            }
        }

        public void Dispose() => context.Dispose();

        private static bool TryClaimAssemblyName(string path, ISet<string> assemblyNames, out string? name)
        {
            name = AssemblyName.GetAssemblyName(path).FullName;
            return assemblyNames.Add(name);
        }
    }

    private sealed record AssemblyReference(string Name, string Path);
}
