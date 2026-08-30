using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Groundwork.Store;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Groundwork.Architecture.Tests;

// These tests defend reference direction and public naming, not semantic shape. A neutrally named
// kernel API can still be contract-family-shaped. The second-family proof in Groundwork#263 and
// building Groundwork.Documents outside this repository in Groundwork#265 are the stronger defenses.
public sealed class KernelBoundaryTests
{
    private static readonly Regex DiagnosticCodePattern = new(
        @"GW-[A-Z0-9]+(?:-[A-Z0-9]+)+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly NullabilityInfoContext NullabilityContext = new();

    private static readonly ImmutableHashSet<string> ContractAttributeNames =
        new[]
        {
            "Microsoft.CodeAnalysis.CodeFixes.ExportCodeFixProviderAttribute",
            "Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzerAttribute",
            "Microsoft.CodeAnalysis.GeneratorAttribute",
            "Microsoft.Build.Framework.RequiredAttribute",
            "System.AttributeUsageAttribute",
            "System.Diagnostics.CodeAnalysis.RequiresDynamicCodeAttribute",
            "System.Diagnostics.CodeAnalysis.RequiresUnreferencedCodeAttribute",
            "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute",
            "System.FlagsAttribute",
            "System.ObsoleteAttribute",
            "System.ParamArrayAttribute",
            "System.Reflection.DefaultMemberAttribute",
            "System.Runtime.CompilerServices.CompilerFeatureRequiredAttribute",
            "System.Runtime.CompilerServices.ExtensionAttribute",
            "System.Runtime.CompilerServices.IsReadOnlyAttribute",
            "System.Runtime.CompilerServices.PreserveBaseOverridesAttribute",
            "System.Runtime.CompilerServices.RequiredMemberAttribute",
            "System.Runtime.CompilerServices.TupleElementNamesAttribute",
            "System.Runtime.InteropServices.OptionalAttribute",
            "System.Text.Json.Serialization.JsonDerivedTypeAttribute",
            "System.Text.Json.Serialization.JsonIgnoreAttribute",
            "System.Text.Json.Serialization.JsonPolymorphicAttribute"
        }.ToImmutableHashSet(StringComparer.Ordinal);

    private static readonly ImmutableArray<CSharpParseOptions> ShippedParseOptions =
    [
        new CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols:
            ["NETSTANDARD", "NETSTANDARD2_0", "NETSTANDARD2_0_OR_GREATER"]),
        new CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols:
            ["NET8_0", "NET8_0_OR_GREATER", "NETCOREAPP", "NETCOREAPP3_1_OR_GREATER"]),
        new CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols:
            ["NET10_0", "NET10_0_OR_GREATER", "NET9_0_OR_GREATER", "NET8_0_OR_GREATER", "NETCOREAPP", "NETCOREAPP3_1_OR_GREATER"])
    ];

    private static readonly ImmutableArray<string> ForbiddenContractVocabulary =
        ["Document", "Envelope", "Record", "Stream", "Diagnostic"];

    // Platform assemblies that Microsoft.NETCore.App carries in-box on the newest target Groundwork
    // ships for, but delivers as a servicing package on an older one. They are the BCL either way:
    // no Groundwork layering rule is about how the platform chooses to deliver its own libraries.
    // System.IO.Pipelines reaches Groundwork.Kernel only as a transitive dependency of the pinned
    // System.Text.Json, which both targets deliberately share so their JSON behavior is identical.
    private static readonly ImmutableHashSet<string> PlatformServicingAssemblies =
        ImmutableHashSet.Create(StringComparer.Ordinal, "System.IO.Pipelines");

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
            .SelectMany(PublicVocabularySignatures)
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

    // The hosting integration sits outside the Store contract, so it is the one assembly that could
    // quietly make "reference Groundwork" mean "reference four database drivers". It must reach
    // providers only through IStorageProviderFactory, which the application supplies.
    [Fact]
    public void Hosting_integration_reaches_providers_only_through_the_factory_seam()
    {
        using var universe = AssemblyUniverse.Load();
        var hosting = universe.Assemblies.Single(assembly => string.Equals(
            assembly.GetName().Name, "Groundwork.Extensions.DependencyInjection", StringComparison.Ordinal));
        var violations = universe.NonBclReferenceClosure(hosting)
            .Where(reference => reference.Name.StartsWith("Groundwork.", StringComparison.Ordinal))
            .Where(reference => IsProviderAssemblyName(reference.Name) ||
                                reference.Name.StartsWith("Groundwork.Substrate.", StringComparison.Ordinal) ||
                                KnownContractFamilies.Contains(reference.Name) ||
                                reference.Name.StartsWith("Groundwork.Testing", StringComparison.Ordinal))
            .Select(reference => reference.Path)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.True(violations.Length == 0,
            "Groundwork.Extensions.DependencyInjection must not reference a provider, a substrate, a " +
            "contract family, or the testing package:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Ef_import_adapter_reaches_groundwork_only_through_the_kernel()
    {
        using var universe = AssemblyUniverse.Load();
        var adapter = universe.Assemblies.Single(assembly => string.Equals(
            assembly.GetName().Name, "Groundwork.EntityFrameworkCore", StringComparison.Ordinal));
        var violations = universe.NonBclReferenceClosure(adapter)
            .Where(reference => reference.Name.StartsWith("Groundwork.", StringComparison.Ordinal))
            .Where(reference => !string.Equals(reference.Name, "Groundwork.Kernel", StringComparison.Ordinal) &&
                                !string.Equals(reference.Name, "Groundwork.Query.Model", StringComparison.Ordinal))
            .Select(reference => reference.Path)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.True(violations.Length == 0,
            "Groundwork.EntityFrameworkCore must produce kernel declarations without reaching a " +
            "provider, substrate, runtime store, or contract family:" + Environment.NewLine +
            string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Store_public_lifetime_contract_keeps_resource_ownership_explicit()
    {
        Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(IStorageProviderConnection)));
        Assert.False(typeof(IDisposable).IsAssignableFrom(typeof(IStorageSession)));
        Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(IUnitOfWork)));
    }

    [Fact]
    public void Public_api_matches_the_frozen_v1_contract()
    {
        var root = FindRepositoryRoot();
        var expectedAssemblies = File.ReadAllLines(Path.Combine(root, "eng", "public-packages.txt"))
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
            .Select(line => line.Split('|', 2)[1])
            .Select(projectPath =>
            {
                var project = XDocument.Load(Path.Combine(root, projectPath));
                return project.Descendants("AssemblyName").Select(element => element.Value).SingleOrDefault()
                    ?? Path.GetFileNameWithoutExtension(projectPath);
            })
            .Where(name => RuntimeTargetFramework == "net10.0" ||
                           !string.Equals(name, "Groundwork.SchemaTool.MSBuild", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        using var universe = AssemblyUniverse.Load();
        var publicAssemblies = universe.Assemblies
            .Where(assembly => expectedAssemblies.Contains(assembly.GetName().Name, StringComparer.Ordinal))
            .OrderBy(assembly => assembly.GetName().Name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedAssemblies, publicAssemblies.Select(assembly => assembly.GetName().Name).ToArray());

        var actual = publicAssemblies
            .SelectMany(assembly => PublicSignatures(assembly)
                .Select(signature => $"{assembly.GetName().Name}: {signature}"))
            .OrderBy(signature => signature, StringComparer.Ordinal)
            .ToArray();

#if NET10_0
        Assert.Contains(actual, signature =>
            signature.Contains("Groundwork.SchemaTool.MSBuild.GroundworkVerify.SchemaFile", StringComparison.Ordinal) &&
            signature.Contains("Microsoft.Build.Framework.RequiredAttribute", StringComparison.Ordinal));
#endif

        AssertContractBaseline($"public-api-v1-{RuntimeTargetFramework}.txt", actual);
    }

    [Fact]
    public void Diagnostic_codes_match_the_frozen_v1_contract()
    {
        var root = FindRepositoryRoot();
        var projects = PublicProjectFiles(root);
        var actual = ProductSourceFiles(root, projects)
            .SelectMany(path => DiagnosticCodesFromSource(File.ReadAllText(path), path))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToArray();

        AssertContractBaseline("diagnostic-codes-v1.txt", actual);
    }

    [Fact]
    public void Public_api_serialization_preserves_compatibility_significant_metadata()
    {
        var signatures = PublicSignaturesForTypes(
                [typeof(ApiShapeFixture), typeof(ApiShapeExtensions), typeof(ApiShapeConstants)])
            .ToArray();

        Assert.Contains(signatures, signature =>
            signature.Contains("ApiShapeFixture.ProtectedVirtual", StringComparison.Ordinal) &&
            signature.Contains("Family", StringComparison.Ordinal) &&
            signature.Contains("Virtual", StringComparison.Ordinal));
        Assert.DoesNotContain(signatures, signature =>
            signature.Contains("ApiShapeFixture.PrivateMethod", StringComparison.Ordinal));
        Assert.Contains(signatures, signature =>
            signature.Contains("ReadonlyField", StringComparison.Ordinal) &&
            signature.Contains("InitOnly", StringComparison.Ordinal));
        Assert.Contains(signatures, signature =>
            signature.Contains("ref System.Int32", StringComparison.Ordinal) &&
            signature.Contains("out System.Int32", StringComparison.Ordinal) &&
            signature.Contains("in System.Int32", StringComparison.Ordinal) &&
            signature.Contains("ParamArrayAttribute", StringComparison.Ordinal));
        Assert.Contains(signatures, signature =>
            signature.Contains("RequiredName", StringComparison.Ordinal) &&
            signature.Contains("RequiredMemberAttribute", StringComparison.Ordinal));
        Assert.Contains(signatures, signature =>
            signature.Contains("ApiShapeExtensions.Extend", StringComparison.Ordinal) &&
            signature.Contains("ExtensionAttribute", StringComparison.Ordinal));
        Assert.Contains(signatures, signature =>
            signature.Contains("NamedTuple", StringComparison.Ordinal) &&
            signature.Contains("TupleElementNamesAttribute", StringComparison.Ordinal) &&
            signature.Contains("Nullable/Nullable", StringComparison.Ordinal));
        var notNullGeneric = Describe(typeof(ApiShapeFixture).GetMethod(nameof(ApiShapeFixture.NotNullGeneric))!);
        var unconstrainedGeneric = Describe(
            typeof(ApiShapeFixture).GetMethod(nameof(ApiShapeFixture.UnconstrainedGeneric))!);
        Assert.NotEqual(
            notNullGeneric.Replace(nameof(ApiShapeFixture.NotNullGeneric), "Generic", StringComparison.Ordinal),
            unconstrainedGeneric.Replace(nameof(ApiShapeFixture.UnconstrainedGeneric), "Generic", StringComparison.Ordinal));
        Assert.All(signatures, signature =>
        {
            Assert.DoesNotContain('\r', signature);
            Assert.DoesNotContain('\n', signature);
        });
        Assert.Contains(signatures, signature =>
            signature.Contains("line\\nquote\\\"slash\\\\", StringComparison.Ordinal));
    }

    [Fact]
    public void Public_api_serialization_ignores_implementation_only_compiler_metadata()
    {
        var automatic = Describe(typeof(ApiShapeAutoProperty).GetProperty(nameof(ApiShapeAutoProperty.Name))!);
        var manual = Describe(typeof(ApiShapeManualProperty).GetProperty(nameof(ApiShapeManualProperty.Name))!);

        Assert.Equal(
            automatic.Replace(nameof(ApiShapeAutoProperty), nameof(ApiShapeManualProperty), StringComparison.Ordinal),
            manual);
        Assert.DoesNotContain("CompilerGeneratedAttribute", automatic, StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnostic_extraction_ignores_comments_and_inactive_code()
    {
        const string source = """"
            // GW-COMMENT-001
            #if false
            internal static class Disabled { public const string Code = "GW-INACTIVE-001"; }
            #endif
            #if NET10_0_OR_GREATER
            internal static class NewestTarget { public const string Code = "GW-TARGET-001"; }
            #endif
            internal static class Active { public const string Code = "GW-ACTIVE-001"; }
            internal static class Raw { public const string Code = """GW-RAW-001"""; }
            internal static class MultiLineRaw { public const string Code = """
                GW-MULTILINE-001
                """; }
            internal static class Utf8 { public static ReadOnlySpan<byte> Code => "GW-UTF8-001"u8; }
            internal static class Combined { public const string Code = "GW-" + "CONCAT-001"; }
            """";

        Assert.Equal(
            new[]
            {
                "GW-ACTIVE-001", "GW-CONCAT-001", "GW-MULTILINE-001", "GW-RAW-001",
                "GW-TARGET-001", "GW-UTF8-001"
            },
            DiagnosticCodesFromSource(source));
    }

    [Fact]
    public void Product_source_discovery_includes_linked_compile_items_and_excludes_obj()
    {
        using var fixture = new ProductSourceFixture();

        Assert.Equal(
            new[] { fixture.LinkedSource, fixture.ProductSource },
            ProductSourceFiles(fixture.Root, [fixture.Project]).OrderBy(path => path, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Contract_baseline_updates_replace_one_complete_file_atomically()
    {
        using var fixture = new ProductSourceFixture();
        var baseline = Path.Combine(fixture.Root, "baseline.txt");

        Parallel.For(0, 8, writer => WriteContractBaselineAtomically(
            baseline,
            Enumerable.Repeat($"writer-{writer}", 64).ToArray()));

        var lines = File.ReadAllLines(baseline);
        Assert.Equal(64, lines.Length);
        Assert.Single(lines.Distinct(StringComparer.Ordinal));
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, ".baseline.txt.*.tmp"));
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
               HasMetadata(assembly, "Groundwork.Adapter", "true") ||
               HasMetadata(assembly, "Groundwork.Tool", "true") ||
               string.Equals(name, "Groundwork.Store", StringComparison.Ordinal) ||
               string.Equals(name, "Groundwork.Diagnostics", StringComparison.Ordinal) ||
               string.Equals(name, "Groundwork.Records.Store", StringComparison.Ordinal) ||
               string.Equals(name, "Groundwork.Query.Planning", StringComparison.Ordinal) ||
               string.Equals(name, "Groundwork.Testing", StringComparison.Ordinal) ||
               name?.StartsWith("Groundwork.Extensions.", StringComparison.Ordinal) == true ||
               name?.StartsWith("Groundwork.Tool", StringComparison.Ordinal) == true;
    }

    private static bool IsKernelAssembly(string? name) =>
        name is not null &&
        (name.StartsWith("Groundwork.Kernel", StringComparison.Ordinal) ||
         string.Equals(name, "Groundwork.Query.Model", StringComparison.Ordinal));

    private static bool IsSubstrateAssembly(string? name) =>
        name?.StartsWith("Groundwork.Substrate.", StringComparison.Ordinal) == true;

    private static bool IsProviderAssembly(Assembly assembly) =>
        IsProviderAssemblyName(assembly.GetName().Name) ||
        HasMetadata(assembly, "Groundwork.Provider", "true");

    private static bool IsProviderAssemblyName(string? name) =>
        name?.StartsWith("Groundwork.MongoDb", StringComparison.Ordinal) == true ||
        name?.StartsWith("Groundwork.PostgreSql", StringComparison.Ordinal) == true ||
        name?.StartsWith("Groundwork.Sqlite", StringComparison.Ordinal) == true ||
        name?.StartsWith("Groundwork.SqlServer", StringComparison.Ordinal) == true;

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

    private static IEnumerable<string> PublicSignatures(Assembly assembly) =>
        PublicSignaturesForTypes(assembly.GetTypes().Where(IsPublicOrProtectedType));

    private static IEnumerable<string> PublicVocabularySignatures(Assembly assembly)
    {
        foreach (var type in assembly.GetTypes().Where(IsPublicOrProtectedType))
        {
            yield return $"{type.FullName}{Inheritance(type)}";
            foreach (var member in PublicMembers(type))
            {
                yield return member switch
                {
                    MethodInfo method =>
                        $"{TypeName(method.ReturnType)} {method.DeclaringType?.FullName}.{method.Name}" +
                        $"({string.Join(", ", method.GetParameters().Select(parameter => TypeName(parameter.ParameterType)))})",
                    ConstructorInfo constructor =>
                        $"{constructor.DeclaringType?.FullName}.ctor" +
                        $"({string.Join(", ", constructor.GetParameters().Select(parameter => TypeName(parameter.ParameterType)))})",
                    PropertyInfo property =>
                        $"{TypeName(property.PropertyType)} {property.DeclaringType?.FullName}.{property.Name}",
                    FieldInfo field => $"{TypeName(field.FieldType)} {field.DeclaringType?.FullName}.{field.Name}",
                    EventInfo eventInfo =>
                        $"{TypeName(eventInfo.EventHandlerType)} {eventInfo.DeclaringType?.FullName}.{eventInfo.Name}",
                    _ => $"{member.DeclaringType?.FullName}.{member.Name}"
                };
            }
        }
    }

    private static IEnumerable<string> PublicSignaturesForTypes(IEnumerable<Type> types)
    {
        foreach (var type in types.OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            yield return Describe(type);

            foreach (var member in PublicMembers(type))
                yield return Describe(member);
        }
    }

    private static IEnumerable<MemberInfo> PublicMembers(Type type) => type.GetMembers(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
            BindingFlags.Static | BindingFlags.DeclaredOnly)
        .Where(member => member.MemberType != MemberTypes.NestedType &&
                         IsPublicOrProtectedMember(member) &&
                         !IsPropertyOrEventAccessor(member));

    private static bool IsPublicOrProtectedType(Type type) =>
        type.IsPublic ||
        ((type.IsNestedPublic || type.IsNestedFamily || type.IsNestedFamORAssem) &&
         type.DeclaringType is not null &&
         IsPublicOrProtectedType(type.DeclaringType));

    private static bool IsPublicOrProtectedMember(MemberInfo member) => member switch
    {
        MethodBase method => IsPublicOrProtected(method),
        FieldInfo field => field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly,
        PropertyInfo property => property.GetAccessors(nonPublic: true).Any(IsPublicOrProtected),
        EventInfo eventInfo => eventInfo.GetAddMethod(nonPublic: true) is { } add && IsPublicOrProtected(add) ||
                               eventInfo.GetRemoveMethod(nonPublic: true) is { } remove && IsPublicOrProtected(remove),
        _ => false
    };

    private static bool IsPublicOrProtected(MethodBase method) =>
        method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly;

    private static bool IsPropertyOrEventAccessor(MemberInfo member) =>
        member is MethodInfo { IsSpecialName: true } method &&
        (method.Name.StartsWith("get_", StringComparison.Ordinal) ||
         method.Name.StartsWith("set_", StringComparison.Ordinal) ||
         method.Name.StartsWith("add_", StringComparison.Ordinal) ||
         method.Name.StartsWith("remove_", StringComparison.Ordinal));

    private static string Describe(MemberInfo member) => member switch
    {
        Type type =>
            $"type [{TypeContractFlags(type)}] {type.FullName ?? type.Name}{Inheritance(type)}" +
            $"{GenericConstraints(type.GetGenericArguments())}{CustomAttributes(type)}",
        ConstructorInfo constructor =>
            $"constructor [{MethodContractFlags(constructor)}] " +
            $"{constructor.DeclaringType?.FullName}.ctor({Parameters(constructor)}){CustomAttributes(constructor)}",
        MethodInfo method =>
            $"method [{MethodContractFlags(method)}] {Return(method)} " +
            $"{method.DeclaringType?.FullName}.{method.Name}{GenericArguments(method)}({Parameters(method)})" +
            $"{GenericConstraints(method.GetGenericArguments())}{CustomAttributes(method)}",
        PropertyInfo property =>
            $"property [{PropertyContractFlags(property)}] {TypeUse(property.PropertyType, property)} " +
            $"{property.DeclaringType?.FullName}.{property.Name}{IndexerParameters(property)} " +
            $"{PropertyAccessors(property)}{CustomAttributes(property)}",
        FieldInfo field =>
            $"field [{FieldContractFlags(field)}] {TypeUse(field.FieldType, field)} " +
            $"{field.DeclaringType?.FullName}.{field.Name}" +
            (field.IsLiteral ? $" = {Constant(field.GetRawConstantValue())}" : string.Empty) +
            CustomAttributes(field),
        EventInfo eventInfo =>
            $"event [{EventContractFlags(eventInfo)}] {TypeUse(eventInfo.EventHandlerType!, eventInfo)} " +
            $"{eventInfo.DeclaringType?.FullName}.{eventInfo.Name} {EventAccessors(eventInfo)}" +
            CustomAttributes(eventInfo),
        _ => $"{member.DeclaringType?.FullName}.{member.Name}"
    };

    private static string Parameters(MethodBase method) => string.Join(", ",
        method.GetParameters().Select(Parameter));

    private static string Parameter(ParameterInfo parameter) => Parameter(parameter, includeName: true);

    private static string Parameter(ParameterInfo parameter, bool includeName)
    {
        var parameterType = parameter.ParameterType;
        var prefix = string.Empty;
        if (parameterType.IsByRef)
        {
            prefix = includeName
                ? parameter.IsOut
                    ? "out "
                    : parameter.IsIn || HasCustomAttribute(parameter, "System.Runtime.CompilerServices.IsReadOnlyAttribute")
                        ? "in "
                        : "ref "
                : parameter.IsIn ||
                  HasCustomAttribute(parameter, "System.Runtime.CompilerServices.IsReadOnlyAttribute") ||
                  parameter.GetRequiredCustomModifiers().Any(modifier => string.Equals(
                      modifier.FullName,
                      "System.Runtime.InteropServices.InAttribute",
                      StringComparison.Ordinal))
                    ? "ref readonly "
                    : "ref ";
            parameterType = parameterType.GetElementType()!;
        }

        return prefix + TypeUse(parameterType, parameter) +
               (includeName ? $" {parameter.Name}" : string.Empty) +
               $" [flags={ParameterContractFlags(parameter)}]" +
               (includeName && parameter.HasDefaultValue ? $" = {Constant(parameter.RawDefaultValue)}" : string.Empty) +
               CustomAttributes(parameter);
    }

    private static string Return(MethodInfo method) => Parameter(method.ReturnParameter, includeName: false);

    private static string GenericArguments(MethodInfo method)
    {
        var arguments = method.GetGenericArguments();
        return arguments.Length == 0
            ? string.Empty
            : $"<{string.Join(", ", arguments.Select(argument => argument.Name))}>";
    }

    private static string IndexerParameters(PropertyInfo property)
    {
        var parameters = property.GetIndexParameters();
        return parameters.Length == 0
            ? string.Empty
            : $"[{string.Join(", ", parameters.Select(Parameter))}]";
    }

    private static string PropertyAccessors(PropertyInfo property)
    {
        var accessors = new List<string>(2);
        if (property.GetMethod is { } getter && IsPublicOrProtected(getter))
            accessors.Add($"get [{MethodContractFlags(getter)}]{CustomAttributes(getter)};");
        if (property.SetMethod is { } setter && IsPublicOrProtected(setter))
        {
            var isInit = setter.ReturnParameter
                .GetRequiredCustomModifiers()
                .Any(modifier => string.Equals(
                    modifier.FullName,
                    "System.Runtime.CompilerServices.IsExternalInit",
                    StringComparison.Ordinal));
            accessors.Add($"{(isInit ? "init" : "set")} [{MethodContractFlags(setter)}]" +
                          $"{CustomModifiers(setter.ReturnParameter)}{CustomAttributes(setter)};");
        }

        return $"{{ {string.Join(" ", accessors)} }}";
    }

    private static string EventAccessors(EventInfo eventInfo)
    {
        var accessors = new List<string>(2);
        if (eventInfo.GetAddMethod(nonPublic: true) is { } add && IsPublicOrProtected(add))
            accessors.Add($"add [{MethodContractFlags(add)}]{CustomAttributes(add)};");
        if (eventInfo.GetRemoveMethod(nonPublic: true) is { } remove && IsPublicOrProtected(remove))
            accessors.Add($"remove [{MethodContractFlags(remove)}]{CustomAttributes(remove)};");
        return $"{{ {string.Join(" ", accessors)} }}";
    }

    private static string TypeUse(Type type, ICustomAttributeProvider provider) =>
        $"{TypeName(type)}{CustomModifiers(provider)}{Nullability(provider)}";

    private static string TypeContractFlags(Type type) => JoinFlags(
        type.IsPublic ? "Public" :
        type.IsNestedPublic ? "NestedPublic" :
        type.IsNestedFamily ? "NestedFamily" : "NestedFamilyOrAssembly",
        type.IsInterface ? "Interface" : null,
        type.IsAbstract ? "Abstract" : null,
        type.IsSealed ? "Sealed" : null,
        type.IsExplicitLayout ? "ExplicitLayout" : type.IsLayoutSequential ? "SequentialLayout" : null);

    private static string MethodContractFlags(MethodBase method) => JoinFlags(
        method.IsPublic ? "Public" : method.IsFamily ? "Family" : "FamilyOrAssembly",
        method.IsStatic ? "Static" : null,
        method.IsAbstract ? "Abstract" : null,
        method.IsVirtual ? "Virtual" : null,
        method.IsFinal ? "Final" : null,
        (method.Attributes & MethodAttributes.NewSlot) != 0 ? "NewSlot" : null,
        method.IsHideBySig ? "HideBySig" : null,
        method.IsSpecialName ? "SpecialName" : null,
        (method.Attributes & MethodAttributes.RTSpecialName) != 0 ? "RTSpecialName" : null,
        (method.Attributes & MethodAttributes.PinvokeImpl) != 0 ? "PInvokeImpl" : null);

    private static string FieldContractFlags(FieldInfo field) => JoinFlags(
        field.IsPublic ? "Public" : field.IsFamily ? "Family" : "FamilyOrAssembly",
        field.IsStatic ? "Static" : null,
        field.IsInitOnly ? "InitOnly" : null,
        field.IsLiteral ? "Literal" : null,
        field.IsSpecialName ? "SpecialName" : null,
        (field.Attributes & FieldAttributes.HasDefault) != 0 ? "HasDefault" : null,
        (field.Attributes & FieldAttributes.HasFieldMarshal) != 0 ? "HasFieldMarshal" : null);

    private static string PropertyContractFlags(PropertyInfo property) => JoinFlags(
        property.IsSpecialName ? "SpecialName" : null,
        (property.Attributes & PropertyAttributes.HasDefault) != 0 ? "HasDefault" : null);

    private static string EventContractFlags(EventInfo eventInfo) =>
        JoinFlags(eventInfo.IsSpecialName ? "SpecialName" : null);

    private static string ParameterContractFlags(ParameterInfo parameter) => JoinFlags(
        parameter.IsIn ? "In" : null,
        parameter.IsOut ? "Out" : null,
        parameter.IsOptional ? "Optional" : null,
        (parameter.Attributes & ParameterAttributes.HasDefault) != 0 ? "HasDefault" : null,
        (parameter.Attributes & ParameterAttributes.HasFieldMarshal) != 0 ? "HasFieldMarshal" : null);

    private static string JoinFlags(params string?[] flags)
    {
        var values = flags.Where(flag => flag is not null).ToArray();
        return values.Length == 0 ? "None" : string.Join(", ", values!);
    }

    private static string Nullability(ICustomAttributeProvider provider)
    {
        System.Reflection.NullabilityInfo? info = provider switch
        {
            ParameterInfo parameter => NullabilityContext.Create(parameter),
            PropertyInfo property => NullabilityContext.Create(property),
            FieldInfo field => NullabilityContext.Create(field),
            EventInfo eventInfo => NullabilityContext.Create(eventInfo),
            _ => null
        };
        return info is null ? string.Empty : $" [nullability: {NullabilityShape(info)}]";
    }

    private static string NullabilityShape(System.Reflection.NullabilityInfo info)
    {
        var children = info.ElementType is not null
            ? new[] { NullabilityShape(info.ElementType) }
            : info.GenericTypeArguments.Select(NullabilityShape).ToArray();
        var suffix = children.Length == 0 ? string.Empty : $"<{string.Join(",", children)}>";
        return $"{info.ReadState}/{info.WriteState}{suffix}";
    }

    private static string CustomModifiers(ICustomAttributeProvider provider)
    {
        Type[] required;
        Type[] optional;
        switch (provider)
        {
            case ParameterInfo parameter:
                required = parameter.GetRequiredCustomModifiers();
                optional = parameter.GetOptionalCustomModifiers();
                break;
            case PropertyInfo property:
                required = property.GetRequiredCustomModifiers();
                optional = property.GetOptionalCustomModifiers();
                break;
            case FieldInfo field:
                required = field.GetRequiredCustomModifiers();
                optional = field.GetOptionalCustomModifiers();
                break;
            default:
                return string.Empty;
        }

        return string.Concat(
            required.OrderBy(TypeName, StringComparer.Ordinal).Select(type => $" modreq({TypeName(type)})")
                .Concat(optional.OrderBy(TypeName, StringComparer.Ordinal)
                    .Select(type => $" modopt({TypeName(type)})")));
    }

    private static string CustomAttributes(ICustomAttributeProvider provider)
    {
        var attributes = provider switch
        {
            MemberInfo member => member.GetCustomAttributesData(),
            ParameterInfo parameter => parameter.GetCustomAttributesData(),
            _ => []
        };
        var serialized = attributes
            .Where(IsContractAttribute)
            .Select(CustomAttribute)
            .OrderBy(attribute => attribute, StringComparer.Ordinal)
            .ToArray();
        return serialized.Length == 0 ? string.Empty : $" [attributes: {string.Join("; ", serialized)}]";
    }

    private static bool IsContractAttribute(CustomAttributeData attribute)
    {
        var name = attribute.AttributeType.FullName;
        return name is not null &&
               (ContractAttributeNames.Contains(name) || name.StartsWith("Groundwork.", StringComparison.Ordinal));
    }

    private static bool HasCustomAttribute(ParameterInfo parameter, string fullName) =>
        parameter.GetCustomAttributesData().Any(attribute =>
            string.Equals(attribute.AttributeType.FullName, fullName, StringComparison.Ordinal));

    private static string CustomAttribute(CustomAttributeData attribute)
    {
        var arguments = attribute.ConstructorArguments.Select(CustomAttributeArgument)
            .Concat(attribute.NamedArguments
                .OrderBy(argument => argument.MemberName, StringComparer.Ordinal)
                .Select(argument => $"{argument.MemberName}={CustomAttributeArgument(argument.TypedValue)}"));
        return $"{attribute.AttributeType.FullName}({string.Join(", ", arguments)})";
    }

    private static string CustomAttributeArgument(CustomAttributeTypedArgument argument)
    {
        if (argument.Value is IReadOnlyCollection<CustomAttributeTypedArgument> items)
            return $"[{string.Join(", ", items.Select(CustomAttributeArgument))}]";
        if (argument.Value is Type type)
            return $"typeof({TypeName(type)})";
        return $"{TypeName(argument.ArgumentType)}:{Constant(argument.Value)}";
    }

    private static string Constant(object? value) => value switch
    {
        null => "null",
        string text => $"\"{Escape(text, '\"')}\"",
        char character => $"'{Escape(character.ToString(), '\'')}'",
        bool boolean => boolean ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "null"
    };

    private static string Escape(string value, char quote)
    {
        var result = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
        {
            result.Append(character switch
            {
                '\\' => @"\\",
                '\n' => @"\n",
                '\r' => @"\r",
                '\t' => @"\t",
                '\0' => @"\0",
                '\b' => @"\b",
                '\f' => @"\f",
                '\v' => @"\v",
                _ when character == quote => "\\" + character,
                _ when char.IsControl(character) => $"\\u{(int)character:x4}",
                _ => character.ToString()
            });
        }

        return result.ToString();
    }

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
                var genericAttributes = argument.GenericParameterAttributes;
                var parts = new[] { genericAttributes.ToString() }
                    .Where(value => value != GenericParameterAttributes.None.ToString())
                    .Concat(types)
                    .Concat(GenericNullabilityConstraint(argument, genericAttributes))
                    .ToArray();
                var attributes = CustomAttributes(argument);
                return (parts.Length == 0
                    ? string.Empty
                    : $" where {argument.Name} : {string.Join(", ", parts)}") + attributes;
            });
        return string.Concat(constraints);
    }

    private static IEnumerable<string> GenericNullabilityConstraint(
        Type argument,
        GenericParameterAttributes attributes)
    {
        if ((attributes & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0)
            yield break;

        var flag = NullableFlag(argument, "System.Runtime.CompilerServices.NullableAttribute") ??
                   NullableContextFlag(argument.DeclaringMethod) ??
                   NullableContextFlag(argument.DeclaringType);
        if ((attributes & GenericParameterAttributes.ReferenceTypeConstraint) != 0)
        {
            yield return flag == 2 ? "class?" : "class";
        }
        else if (flag == 1)
        {
            yield return "notnull";
        }
    }

    private static byte? NullableContextFlag(MemberInfo? member)
    {
        for (var current = member; current is not null; current = current.DeclaringType)
        {
            var flag = NullableFlag(current, "System.Runtime.CompilerServices.NullableContextAttribute");
            if (flag is not null)
                return flag;
        }

        return null;
    }

    private static byte? NullableFlag(ICustomAttributeProvider provider, string attributeName)
    {
        var attribute = (provider switch
        {
            MemberInfo member => member.GetCustomAttributesData(),
            ParameterInfo parameter => parameter.GetCustomAttributesData(),
            _ => []
        }).SingleOrDefault(candidate =>
            string.Equals(candidate.AttributeType.FullName, attributeName, StringComparison.Ordinal));
        if (attribute is null || attribute.ConstructorArguments.Count != 1)
            return null;
        var value = attribute.ConstructorArguments[0].Value;
        if (value is byte scalar)
            return scalar;
        if (value is IReadOnlyCollection<CustomAttributeTypedArgument> items && items.FirstOrDefault().Value is byte first)
            return first;
        return null;
    }

    private static string TypeName(Type? type) => type?.ToString() ?? "<null>";

    private static string[] PublicProjectFiles(string root) => File.ReadAllLines(
            Path.Combine(root, "eng", "public-packages.txt"))
        .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
        .Select(line => Path.GetFullPath(Path.Combine(root, line.Split('|', 2)[1])))
        .OrderBy(path => path, StringComparer.Ordinal)
        .ToArray();

    private static IEnumerable<string> ProductSourceFiles(string root, IEnumerable<string> projectFiles)
    {
        var sources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var projectFile in projectFiles)
        {
            var project = XDocument.Load(projectFile);
            var projectDirectory = Path.GetDirectoryName(projectFile)!;
            var defaultItems = !project.Descendants("EnableDefaultCompileItems")
                .Any(element => string.Equals(element.Value.Trim(), "false", StringComparison.OrdinalIgnoreCase));
            if (defaultItems)
            {
                foreach (var path in Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories))
                {
                    if (!IsBuildOutput(path, projectDirectory))
                        sources.Add(Path.GetFullPath(path));
                }
            }

            foreach (var include in project.Descendants("Compile")
                         .Select(element => element.Attribute("Include")?.Value)
                         .Where(value => !string.IsNullOrWhiteSpace(value))
                         .SelectMany(value => value!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
            {
                foreach (var path in ExpandCompileItem(projectDirectory, include))
                {
                    if (!IsBuildOutput(path, root))
                        sources.Add(path);
                }
            }

            foreach (var remove in project.Descendants("Compile")
                         .Select(element => element.Attribute("Remove")?.Value)
                         .Where(value => !string.IsNullOrWhiteSpace(value))
                         .SelectMany(value => value!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
            {
                foreach (var path in ExpandCompileItem(projectDirectory, remove))
                    sources.Remove(path);
            }
        }

        return sources.OrderBy(path => path, StringComparer.Ordinal);
    }

    private static IEnumerable<string> ExpandCompileItem(string projectDirectory, string item)
    {
        var fullPattern = Path.GetFullPath(Path.Combine(projectDirectory, item));
        if (item.IndexOfAny(['*', '?']) < 0)
        {
            if (File.Exists(fullPattern))
                yield return fullPattern;
            yield break;
        }

        var wildcard = fullPattern.IndexOfAny(['*', '?']);
        var separator = fullPattern.LastIndexOf(Path.DirectorySeparatorChar, wildcard);
        var searchRoot = separator < 0 ? projectDirectory : fullPattern[..separator];
        if (!Directory.Exists(searchRoot))
            yield break;
        var pattern = GlobPattern(fullPattern);
        foreach (var path in Directory.EnumerateFiles(searchRoot, "*.cs", SearchOption.AllDirectories))
        {
            var fullPath = Path.GetFullPath(path);
            if (pattern.IsMatch(fullPath.Replace(Path.DirectorySeparatorChar, '/')))
                yield return fullPath;
        }
    }

    private static Regex GlobPattern(string pattern)
    {
        var normalized = pattern.Replace(Path.DirectorySeparatorChar, '/');
        var escaped = Regex.Escape(normalized)
            .Replace(@"\*\*/", "(?:.*/)?", StringComparison.Ordinal)
            .Replace(@"\*", "[^/]*", StringComparison.Ordinal)
            .Replace(@"\?", "[^/]", StringComparison.Ordinal);
        return new Regex("^" + escaped + "$", RegexOptions.CultureInvariant);
    }

    private static bool IsBuildOutput(string path, string relativeTo)
    {
        var relative = Path.GetRelativePath(relativeTo, path);
        return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase));
    }

    private static string[] DiagnosticCodesFromSource(string source, string path = "fixture.cs")
    {
        return ShippedParseOptions.Select(parseOptions => CSharpSyntaxTree.ParseText(source, parseOptions, path))
            .SelectMany(tree => DiagnosticTextCandidates(tree.GetRoot()))
            .SelectMany(text => DiagnosticCodePattern.Matches(text).Select(match => match.Value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<string> DiagnosticTextCandidates(SyntaxNode root)
    {
        foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
        {
            if (literal.Token.Value is string)
                yield return literal.Token.ValueText;
        }

        foreach (var text in root.DescendantTokens().Where(token =>
                     token.IsKind(SyntaxKind.InterpolatedStringTextToken)))
        {
            yield return text.ValueText;
        }

        foreach (var expression in root.DescendantNodes().OfType<BinaryExpressionSyntax>()
                     .Where(expression => expression.IsKind(SyntaxKind.AddExpression)))
        {
            if (TryConstantString(expression, out var value))
                yield return value;
        }
    }

    private static bool TryConstantString(ExpressionSyntax expression, out string value)
    {
        switch (expression)
        {
            case LiteralExpressionSyntax literal when literal.Token.Value is string:
                value = literal.Token.ValueText;
                return true;
            case ParenthesizedExpressionSyntax parenthesized:
                return TryConstantString(parenthesized.Expression, out value);
            case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression) &&
                                                    TryConstantString(binary.Left, out var left) &&
                                                    TryConstantString(binary.Right, out var right):
                value = left + right;
                return true;
            case InterpolatedStringExpressionSyntax interpolated
                when interpolated.Contents.All(content => content is InterpolatedStringTextSyntax):
                value = string.Concat(interpolated.Contents
                    .Cast<InterpolatedStringTextSyntax>()
                    .Select(content => content.TextToken.ValueText));
                return true;
            default:
                value = string.Empty;
                return false;
        }
    }

    private static string RuntimeTargetFramework =>
#if NET10_0
        "net10.0";
#elif NET8_0
        "net8.0";
#else
#error Groundwork public API freeze must name every supported target framework explicitly.
#endif

    private static void AssertContractBaseline(string fileName, IReadOnlyList<string> actual)
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(root, "eng", fileName);
        if (string.Equals(
                Environment.GetEnvironmentVariable("GROUNDWORK_UPDATE_CONTRACT_BASELINES"),
                "1",
                StringComparison.Ordinal))
        {
            WriteContractBaselineAtomically(path, actual);
        }

        Assert.True(File.Exists(path), $"The frozen contract baseline '{path}' does not exist.");
        Assert.Equal(File.ReadAllLines(path), actual);
    }

    private static void WriteContractBaselineAtomically(string path, IReadOnlyList<string> lines)
    {
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(path)!,
            $".{Path.GetFileName(path)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllLines(temporaryPath, lines);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Groundwork.slnx")))
                return directory.FullName;
        }

        throw new InvalidOperationException("Could not find the Groundwork repository root.");
    }

    public abstract class ApiShapeFixture
    {
        public readonly int ReadonlyField;

        public required string? RequiredName { get; init; }

        public (string? Name, int Count) NamedTuple { get; protected set; }

        protected virtual void ProtectedVirtual()
        {
        }

        private void PrivateMethod()
        {
        }

        public void ParameterKinds(ref int byReference, out int output, in int input, params string[] remaining)
        {
            output = input;
        }

        public void NotNullGeneric<T>() where T : notnull
        {
        }

        public void UnconstrainedGeneric<T>()
        {
        }
    }

    public static class ApiShapeConstants
    {
        public const string Escaped = "line\nquote\"slash\\";
        public const char Quote = '\'';
    }

    public sealed class ApiShapeAutoProperty
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed class ApiShapeManualProperty
    {
        private string name = string.Empty;

        public string Name
        {
            get => name;
            set => name = value;
        }
    }

    private sealed class ProductSourceFixture : IDisposable
    {
        public ProductSourceFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "groundwork-architecture-" + Guid.NewGuid().ToString("N"));
            var projectDirectory = Path.Combine(Root, "src", "Product");
            var sharedDirectory = Path.Combine(Root, "shared");
            Directory.CreateDirectory(Path.Combine(projectDirectory, "obj"));
            Directory.CreateDirectory(sharedDirectory);
            Project = Path.Combine(projectDirectory, "Product.csproj");
            ProductSource = Path.Combine(projectDirectory, "Product.cs");
            LinkedSource = Path.Combine(sharedDirectory, "Linked.cs");
            File.WriteAllText(Project, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <Compile Include="../../shared/Linked.cs" Link="Linked.cs" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(ProductSource, "internal static class Product;");
            File.WriteAllText(LinkedSource, "internal static class Linked;");
            File.WriteAllText(Path.Combine(projectDirectory, "obj", "Generated.cs"),
                "internal static class Generated;");
        }

        public string Root { get; }

        public string Project { get; }

        public string ProductSource { get; }

        public string LinkedSource { get; }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

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
            // What counts as the BCL is the shared framework's own contents, not the trusted-platform
            // assembly list. Groundwork ships for more than one target framework and this suite runs
            // once per target, so the classification has to survive two differences. Where the repo
            // pins a platform package (System.Text.Json, System.Collections.Immutable) the app-local
            // copy takes the assembly's slot in the trusted list and the in-box path never appears
            // there, even though the platform does provide it. And a few platform assemblies are
            // in-box on the newest supported target but delivered as a servicing package on an older
            // one, so they are named below.
            var runtimeDirectory = Path.TrimEndingDirectorySeparator(RuntimeEnvironment.GetRuntimeDirectory());
            var bclNames = Directory
                .EnumerateFiles(runtimeDirectory, "*.dll", SearchOption.TopDirectoryOnly)
                .Select(path => Path.GetFileNameWithoutExtension(path)!)
                .Concat(PlatformServicingAssemblies)
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

public static class ApiShapeExtensions
{
    public static void Extend(this KernelBoundaryTests.ApiShapeFixture value)
    {
    }
}
