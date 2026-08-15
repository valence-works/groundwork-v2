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
                if (attribute.ConstructorArguments.Length < 1 || attribute.ConstructorArguments[0].Value is not string json)
                    continue;
                result.Add(GroundworkSchemaCanonical.Parse(json));
            }
        }
        return result;
    }

    private static bool IsSchemaAttribute(AttributeData attribute) =>
        string.Equals(attribute.AttributeClass?.Name, "GroundworkSchemaAttribute", StringComparison.Ordinal);
}
