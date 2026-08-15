using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Groundwork.Schema;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Groundwork.Schema.Generator;

[Generator]
public sealed class SchemaGenerator : ISourceGenerator
{
    private static readonly DiagnosticDescriptor MissingKey = new(
        "GW_SCHEMA_TABLE_001",
        "A generated table requires a key",
        "Table '{0}' has no member marked [GwKey].",
        "Groundwork.Schema", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsupportedType = new(
        "GW_SCHEMA_COLUMN_001",
        "Column type is not portable",
        "Column '{0}' has unsupported type '{1}'.",
        "Groundwork.Schema", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidIndex = new(
        "GW_SCHEMA_INDEX_001",
        "Index specification is invalid",
        "Index '{0}' has invalid specification: {1}",
        "Groundwork.Schema", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidSchemaFile = new(
        "GW_SCHEMA_JSON_001",
        "Schema AdditionalFile is invalid",
        "The Groundwork schema file '{0}' is invalid: {1}",
        "Groundwork.Schema", DiagnosticSeverity.Error, isEnabledByDefault: true);

    public void Initialize(GeneratorInitializationContext context)
    {
    }

    public void Execute(GeneratorExecutionContext context)
    {
        var tables = new List<GeneratedTable>();
        var seenTypes = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var tree in context.Compilation.SyntaxTrees)
        {
            var root = tree.GetRoot(context.CancellationToken);
            var semanticModel = context.Compilation.GetSemanticModel(tree);
            foreach (var declaration in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (semanticModel.GetDeclaredSymbol(declaration, context.CancellationToken) is not INamedTypeSymbol symbol ||
                    FindAttribute(symbol, "GwTableAttribute") is not { } tableAttribute)
                    continue;
                if (!seenTypes.Add(symbol))
                    continue;

                var table = BuildTable(context, semanticModel, declaration, symbol, tableAttribute);
                if (table is not null)
                    tables.Add(new GeneratedTable(table, symbol.Name, symbol.ContainingNamespace.IsGlobalNamespace ? null : symbol.ContainingNamespace.ToDisplayString()));
            }
        }

        if (tables.Count == 0)
        {
            var schemaFile = ReadAdditionalSchema(context);
            if (schemaFile is not null)
            {
                tables.AddRange(schemaFile.Tables.Select(table => new GeneratedTable(table, Identifier(table.Name), null)));
                EmitSchemaAttribute(context, schemaFile);
            }
            return;
        }

        var schema = new SchemaDocument(tables.Select(item => item.Table));
        foreach (var item in tables)
            context.AddSource($"{Identifier(item.TypeName)}.g.cs", RenderStorageUnit(item.Table, item.TypeName, item.NamespaceName));
        EmitSchemaAttribute(context, schema);
    }

    private static SchemaTable? BuildTable(
        GeneratorExecutionContext context,
        SemanticModel semanticModel,
        ClassDeclarationSyntax declaration,
        INamedTypeSymbol symbol,
        AttributeData tableAttribute)
    {
        var tableName = StringArgument(tableAttribute, 0) ?? symbol.Name;
        var columns = new List<SchemaColumn>();
        var keys = new List<string>();
        var columnSymbols = new Dictionary<string, ISymbol>(StringComparer.Ordinal);

        foreach (var member in declaration.Members)
        {
            if (member is not PropertyDeclarationSyntax and not FieldDeclarationSyntax)
                continue;
            var declared = member switch
            {
                PropertyDeclarationSyntax property => semanticModel.GetDeclaredSymbol(property, context.CancellationToken),
                FieldDeclarationSyntax field => field.Declaration.Variables.Select(variable => semanticModel.GetDeclaredSymbol(variable, context.CancellationToken)).FirstOrDefault(),
                _ => null
            };
            if (declared is not ISymbol memberSymbol || FindAttribute(memberSymbol, "GwColumnAttribute") is not { } columnAttribute)
                continue;

            var memberType = memberSymbol switch
            {
                IPropertySymbol property => property.Type,
                IFieldSymbol field => field.Type,
                _ => null
            };
            if (memberType is null || !TryMapType(memberType, out var type))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    UnsupportedType,
                    columnAttribute.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ?? declaration.GetLocation(),
                    memberSymbol.Name,
                    memberType?.ToDisplayString() ?? "<unknown>"));
                continue;
            }

            var name = StringNamedArgument(columnAttribute, "Name") ?? ToSnakeCase(memberSymbol.Name);
            var nullable = IsNullable(memberType) && !BooleanNamedArgument(columnAttribute, "Required");
            var column = new SchemaColumn(
                name,
                type,
                nullable,
                IntNamedArgument(columnAttribute, "Length"),
                IntNamedArgument(columnAttribute, "Precision"),
                IntNamedArgument(columnAttribute, "Scale"),
                EnumNamedArgument(columnAttribute, "Folding", TextFolding.None),
                EnumNamedArgument(columnAttribute, "Generation", SchemaGeneration.Supplied));
            columns.Add(column);
            columnSymbols[name] = memberSymbol;
            if (FindAttribute(memberSymbol, "GwKeyAttribute") is not null)
                keys.Add(name);
        }

        if (keys.Count == 0)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                MissingKey,
                tableAttribute.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ?? declaration.GetLocation(),
                tableName));
            return null;
        }

        var indexes = new List<SchemaIndex>();
        foreach (var indexAttribute in symbol.GetAttributes().Where(attribute => IsAttribute(attribute, "GwIndexAttribute")))
        {
            if (!TryParseIndex(context, indexAttribute, columnSymbols.Keys, out var index))
                continue;
            indexes.Add(new SchemaIndex(
                index.Name,
                index.Columns,
                BooleanNamedArgument(indexAttribute, "IncludeNulls", defaultValue: true),
                BooleanNamedArgument(indexAttribute, "Unique")));
        }

        return new SchemaTable(tableName, columns, keys, indexes);
    }

    private static bool TryParseIndex(
        GeneratorExecutionContext context,
        AttributeData attribute,
        IEnumerable<string> declaredColumns,
        out SchemaIndex index)
    {
        var name = StringArgument(attribute, 0) ?? "<unnamed>";
        var specification = StringArgument(attribute, 1) ?? string.Empty;
        var known = new HashSet<string>(declaredColumns, StringComparer.Ordinal);
        var parsed = new List<SchemaIndexColumn>();
        var location = IndexSpecificationLocation(attribute, context.CancellationToken);

        foreach (var part in specification.Split(','))
        {
            var tokens = part.Trim().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length != 2 || (tokens[1] != "ASC" && tokens[1] != "DESC"))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    InvalidIndex, location, name,
                    $"expected '<column> ASC|DESC', got '{part.Trim()}'."));
                index = null!;
                return false;
            }

            if (!known.Contains(tokens[0]))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    InvalidIndex, location, name,
                    $"column '{tokens[0]}' is not declared on the table."));
                index = null!;
                return false;
            }

            if (parsed.Any(column => string.Equals(column.Name, tokens[0], StringComparison.Ordinal)))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    InvalidIndex, location, name,
                    $"column '{tokens[0]}' appears more than once."));
                index = null!;
                return false;
            }

            parsed.Add(new SchemaIndexColumn(tokens[0], tokens[1] == "DESC"));
        }

        if (parsed.Count == 0)
        {
            context.ReportDiagnostic(Diagnostic.Create(InvalidIndex, location, name, "at least one column is required."));
            index = null!;
            return false;
        }

        index = new SchemaIndex(name, parsed);
        return true;
    }

    private static SchemaDocument? ReadAdditionalSchema(GeneratorExecutionContext context)
    {
        var configuredPath = GetOption(context, "gw_schema_file");
        var candidates = context.AdditionalFiles
            .Where(file => file.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Where(file => configuredPath is null || PathsEqual(file.Path, configuredPath))
            .ToArray();
        if (configuredPath is null)
        {
            foreach (var file in candidates)
            {
                var filePath = GetFileOption(context, file, "gw_schema_file");
                if (!string.IsNullOrWhiteSpace(filePath))
                {
                    configuredPath = filePath;
                    break;
                }
            }
            if (configuredPath is not null)
                candidates = candidates.Where(file => PathsEqual(file.Path, configuredPath)).ToArray();
        }
        if (candidates.Length == 0)
            return null;
        var selected = candidates.First();
        var text = selected.GetText(context.CancellationToken)?.ToString();
        if (string.IsNullOrWhiteSpace(text))
            return null;
        try
        {
            return GroundworkSchemaCanonical.Parse(text!);
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or System.Text.Json.JsonException)
        {
            context.ReportDiagnostic(Diagnostic.Create(InvalidSchemaFile, Location.None, selected.Path, exception.Message));
            return null;
        }
    }

    private static string? GetOption(GeneratorExecutionContext context, string key)
    {
        if (context.AnalyzerConfigOptions.GlobalOptions.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            return value;
        if (context.AnalyzerConfigOptions.GlobalOptions.TryGetValue($"build_property.{key}", out value) && !string.IsNullOrWhiteSpace(value))
            return value;
        return null;
    }

    private static string? GetFileOption(GeneratorExecutionContext context, AdditionalText file, string key)
    {
        if (context.AnalyzerConfigOptions.GetOptions(file).TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            return value.Trim().Trim('"');
        return null;
    }

    private static void EmitSchemaAttribute(GeneratorExecutionContext context, SchemaDocument schema)
    {
        var canonical = GroundworkSchemaCanonical.Serialize(schema);
        var fingerprint = GroundworkSchemaCanonical.Fingerprint(schema);
        context.AddSource("GroundworkSchema.g.cs", $"// <auto-generated />\n[assembly: global::Groundwork.Schema.GroundworkSchemaAttribute({Literal(canonical)}, {Literal(fingerprint)})]\n");
    }

    private static string RenderStorageUnit(SchemaTable table, string clrTypeName, string? namespaceName)
    {
        var typeName = Identifier(clrTypeName) + "StorageUnit";
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated />");
        if (!string.IsNullOrWhiteSpace(namespaceName))
            builder.Append("namespace ").Append(namespaceName).AppendLine(";");
        builder.Append("public static class ").Append(typeName).AppendLine();
        builder.AppendLine("{");
        builder.AppendLine("    public static global::Groundwork.Kernel.StorageUnit Definition { get; } = new()");
        builder.AppendLine("    {");
        builder.Append("        Id = new global::Groundwork.Kernel.StorageUnitId(").Append(Literal(table.Name)).AppendLine("),");
        builder.Append("        Name = ").Append(Literal(table.Name)).AppendLine(",");
        builder.AppendLine("        Columns = new global::Groundwork.Kernel.ColumnDefinition[]");
        builder.AppendLine("        {");
        foreach (var column in table.Columns)
        {
            builder.AppendLine("            new global::Groundwork.Kernel.ColumnDefinition");
            builder.AppendLine("            {");
            builder.Append("                Name = ").Append(Literal(column.Name)).AppendLine(",");
            builder.Append("                Type = global::Groundwork.Kernel.PortableType.").Append(column.Type switch
            {
                SchemaValueType.String => "String",
                SchemaValueType.Int32 => "Int32",
                SchemaValueType.Int64 => "Int64",
                SchemaValueType.Decimal => "Decimal",
                SchemaValueType.Boolean => "Boolean",
                SchemaValueType.DateTimeOffset => "DateTimeOffset",
                SchemaValueType.Guid => "Guid",
                SchemaValueType.Binary => "Binary",
                _ => "Json"
            }).AppendLine(",");
            builder.Append("                IsNullable = ").Append(column.IsNullable ? "true" : "false").AppendLine(",");
            if (column.Length.HasValue) builder.Append("                MaxLength = ").Append(column.Length.Value).AppendLine(",");
            if (column.Precision.HasValue) builder.Append("                Precision = ").Append(column.Precision.Value).AppendLine(",");
            if (column.Scale.HasValue) builder.Append("                Scale = ").Append(column.Scale.Value).AppendLine(",");
            if (column.Folding != TextFolding.None)
            {
                builder.Append("                Collation = global::Groundwork.Kernel.PortableCollation.").Append(column.Folding == TextFolding.AsciiIgnoreCase ? "OrdinalIgnoreCase" : "UnicodeOrdinalIgnoreCase").AppendLine(",");
            }
            if (column.Generation == SchemaGeneration.ProviderSequence)
                builder.AppendLine("                Generation = global::Groundwork.Kernel.ColumnGeneration.ProviderSequence,");
            builder.AppendLine("            },");
        }
        builder.AppendLine("        },");
        builder.AppendLine("        Key = new global::Groundwork.Kernel.KeyDefinition");
        builder.AppendLine("        {");
        builder.AppendLine("            Columns = new string[]");
        builder.AppendLine("            {");
        foreach (var key in table.Key)
            builder.Append("                ").Append(Literal(key)).AppendLine(",");
        builder.AppendLine("            }");
        builder.AppendLine("        },");
        builder.AppendLine("        Indexes = new global::Groundwork.Kernel.IndexDefinition[]");
        builder.AppendLine("        {");
        foreach (var index in table.Indexes)
        {
            builder.AppendLine("            new global::Groundwork.Kernel.IndexDefinition");
            builder.AppendLine("            {");
            builder.Append("                Name = ").Append(Literal(index.Name)).AppendLine(",");
            builder.Append("                IsUnique = ").Append(index.Unique ? "true" : "false").AppendLine(",");
            builder.Append("                MissingValues = global::Groundwork.Kernel.MissingValueBehavior.").Append(index.IncludeNulls ? "Included" : "Excluded").AppendLine(",");
            builder.AppendLine("                Columns = new global::Groundwork.Kernel.IndexColumn[]");
            builder.AppendLine("                {");
            foreach (var column in index.Columns)
                builder.Append("                    new global::Groundwork.Kernel.IndexColumn(").Append(Literal(column.Name)).Append(", global::Groundwork.Kernel.SortDirection.").Append(column.Descending ? "Descending" : "Ascending").AppendLine("),");
            builder.AppendLine("                }");
            builder.AppendLine("            },");
        }
        builder.AppendLine("        }");
        builder.AppendLine("    }; ");
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static AttributeData? FindAttribute(ISymbol symbol, string name) =>
        symbol.GetAttributes().FirstOrDefault(attribute => IsAttribute(attribute, name));

    private static bool IsAttribute(AttributeData attribute, string name) =>
        string.Equals(attribute.AttributeClass?.Name, name, StringComparison.Ordinal) ||
        string.Equals(attribute.AttributeClass?.Name, name.Replace("Attribute", string.Empty), StringComparison.Ordinal);

    private static string? StringArgument(AttributeData attribute, int index) =>
        attribute.ConstructorArguments.Length > index
            ? attribute.ConstructorArguments[index].Value as string
            : AttributeSyntaxArgument(attribute, index)?.Token.ValueText;

    private static string? StringNamedArgument(AttributeData attribute, string name) =>
        NamedStringValue(attribute, name) ?? SyntaxStringValue(attribute, name);

    private static string? NamedStringValue(AttributeData attribute, string name) =>
        attribute.NamedArguments.FirstOrDefault(argument => argument.Key == name).Value.Value as string;

    private static string? SyntaxStringValue(AttributeData attribute, string name)
    {
        var expression = NamedSyntaxArgument(attribute, name)?.Expression;
        return expression is LiteralExpressionSyntax literal ? literal.Token.ValueText : null;
    }

    private static int? IntNamedArgument(AttributeData attribute, string name)
    {
        var value = attribute.NamedArguments.FirstOrDefault(argument => argument.Key == name).Value;
        if (value.Value is int number && number >= 0)
            return number;
        return int.TryParse(NamedSyntaxArgument(attribute, name)?.Expression.ToString(), out var syntaxNumber) && syntaxNumber >= 0
            ? syntaxNumber
            : null;
    }

    private static bool BooleanNamedArgument(AttributeData attribute, string name, bool defaultValue = false)
    {
        var value = attribute.NamedArguments.FirstOrDefault(argument => argument.Key == name).Value;
        if (value.Value is bool result)
            return result;
        return bool.TryParse(NamedSyntaxArgument(attribute, name)?.Expression.ToString(), out var syntaxResult)
            ? syntaxResult
            : defaultValue;
    }

    private static T EnumNamedArgument<T>(AttributeData attribute, string name, T defaultValue) where T : struct, Enum
    {
        var value = attribute.NamedArguments.FirstOrDefault(argument => argument.Key == name).Value;
        if (value.Value is int number && Enum.IsDefined(typeof(T), number))
            return (T)Enum.ToObject(typeof(T), number);
        var text = NamedSyntaxArgument(attribute, name)?.Expression.ToString();
        return Enum.TryParse<T>(text, ignoreCase: false, out var syntaxValue) ? syntaxValue : defaultValue;
    }

    private static bool IsNullable(ITypeSymbol type) =>
        type.IsReferenceType ? type.NullableAnnotation == NullableAnnotation.Annotated :
        type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;

    private static bool TryMapType(ITypeSymbol type, out SchemaValueType mapped)
    {
        var underlying = type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T && type is INamedTypeSymbol nullable
            ? nullable.TypeArguments[0]
            : type;
        mapped = underlying.SpecialType switch
        {
            SpecialType.System_String => SchemaValueType.String,
            SpecialType.System_Int32 => SchemaValueType.Int32,
            SpecialType.System_Int64 => SchemaValueType.Int64,
            SpecialType.System_Decimal => SchemaValueType.Decimal,
            SpecialType.System_Boolean => SchemaValueType.Boolean,
            _ => default
        };
        if (mapped != default || underlying.SpecialType == SpecialType.System_String)
            return true;
        var display = underlying.ToDisplayString();
        if (display == "System.DateTimeOffset") { mapped = SchemaValueType.DateTimeOffset; return true; }
        if (display == "System.Guid") { mapped = SchemaValueType.Guid; return true; }
        if (display == "byte[]") { mapped = SchemaValueType.Binary; return true; }
        if (display == "object") { mapped = SchemaValueType.Json; return true; }
        return false;
    }

    private static Location IndexSpecificationLocation(AttributeData attribute, CancellationToken cancellationToken)
    {
        if (attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken) is AttributeSyntax syntax && syntax.ArgumentList?.Arguments.Count > 1)
            return syntax.ArgumentList.Arguments[1].GetLocation();
        return attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation() ?? Location.None;
    }

    private static LiteralExpressionSyntax? AttributeSyntaxArgument(AttributeData attribute, int index)
    {
        var syntax = attribute.ApplicationSyntaxReference?.GetSyntax() as AttributeSyntax;
        return syntax?.ArgumentList?.Arguments.Count > index
            ? syntax.ArgumentList.Arguments[index].Expression as LiteralExpressionSyntax
            : null;
    }

    private static AttributeArgumentSyntax? NamedSyntaxArgument(AttributeData attribute, string name)
    {
        var syntax = attribute.ApplicationSyntaxReference?.GetSyntax() as AttributeSyntax;
        return syntax?.ArgumentList?.Arguments
            .FirstOrDefault(argument => argument.NameEquals?.Name.Identifier.ValueText == name);
    }

    private static string Identifier(string value)
    {
        var builder = new StringBuilder();
        foreach (var character in value)
            builder.Append(char.IsLetterOrDigit(character) || character == '_' ? character : '_');
        if (builder.Length == 0 || char.IsDigit(builder[0])) builder.Insert(0, '_');
        return builder.ToString();
    }

    private static string ToSnakeCase(string value)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsUpper(character) && index > 0) builder.Append('_');
            builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }

    private static string Literal(string value) => Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(value, quote: true);

    private static bool PathsEqual(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(System.IO.Path.GetFileName(left), System.IO.Path.GetFileName(right), StringComparison.OrdinalIgnoreCase);

    private sealed record GeneratedTable(SchemaTable Table, string TypeName, string? NamespaceName);
}
