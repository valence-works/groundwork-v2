using System;
using System.Collections.Generic;
using System.Linq;
using Groundwork.Schema;
using Microsoft.CodeAnalysis;

namespace Groundwork.Schema.Generator;

/// <summary>Reads generated schema attributes through Roslyn metadata references.</summary>
public static class GroundworkSchemaMetadata
{
    public static IReadOnlyList<SchemaDocument> Read(Compilation compilation)
    {
        if (compilation is null)
            throw new ArgumentNullException(nameof(compilation));

        var result = new List<SchemaDocument>();
        foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            foreach (var attribute in assembly.GetAttributes().Where(IsSchemaAttribute))
            {
                if (attribute.ConstructorArguments.Length < 2 ||
                    attribute.ConstructorArguments[0].Value is not string json ||
                    attribute.ConstructorArguments[1].Value is not string fingerprint)
                    continue;

                var schema = GroundworkSchemaCanonical.Parse(json);
                if (!string.Equals(
                        GroundworkSchemaCanonical.Fingerprint(schema),
                        fingerprint,
                        StringComparison.Ordinal))
                {
                    throw new FormatException(
                        $"Assembly '{assembly.Name}' contains a Groundwork schema attribute with a stale fingerprint.");
                }

                result.Add(schema);
            }
        }
        return result;
    }

    private static bool IsSchemaAttribute(AttributeData attribute) =>
        string.Equals(
            attribute.AttributeClass?.ToDisplayString(),
            typeof(GroundworkSchemaAttribute).FullName,
            StringComparison.Ordinal);
}
