using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
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

    private static readonly DiagnosticDescriptor DuplicateColumn = new(
        "GW_SCHEMA_COLUMN_002",
        "Column name is duplicated",
        "Table '{0}' declares multiple members as column '{1}'.",
        "Groundwork.Schema", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DuplicateTable = new(
        "GW_SCHEMA_TABLE_002",
        "Table name is duplicated",
        "Schema table name '{0}' is declared more than once.",
        "Groundwork.Schema", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidDefault = new(
        "GW_SCHEMA_COLUMN_003",
        "Column default is not readable",
        "Column '{0}' declares a default that is not a valid {1}: {2}",
        "Groundwork.Schema", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidTablePolicy = new(
        "GW_SCHEMA_TABLE_003",
        "Table lifecycle policy is invalid",
        "Table '{0}' declares an invalid lifecycle policy: {1}",
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
        var hasTableDeclaration = false;
        var tableNames = new HashSet<string>(StringComparer.Ordinal);
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
                hasTableDeclaration = true;
                if (!seenTypes.Add(symbol))
                    continue;

                var table = BuildTable(context, GetClassParts(context, symbol), symbol, tableAttribute);
                if (table is null)
                    continue;

                if (!tableNames.Add(table.Name))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        DuplicateTable,
                        tableAttribute.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ?? declaration.GetLocation(),
                        table.Name));
                    continue;
                }

                tables.Add(new GeneratedTable(table, symbol.Name, symbol.ContainingNamespace.IsGlobalNamespace ? null : symbol.ContainingNamespace.ToDisplayString()));
            }
        }

        if (!hasTableDeclaration)
        {
            var schemaFile = ReadAdditionalSchema(context);
            if (schemaFile is not null)
            {
                tables.AddRange(schemaFile.Tables.Select(table => new GeneratedTable(table, Identifier(table.Name), null)));
                EmitStorageUnits(context, tables);
                EmitSchemaAttribute(context, schemaFile);
            }
            return;
        }

        var schema = new SchemaDocument(tables.Select(item => item.Table));
        EmitStorageUnits(context, tables);
        EmitSchemaAttribute(context, schema);
    }

    private static void EmitStorageUnits(
        GeneratorExecutionContext context,
        IReadOnlyList<GeneratedTable> tables)
    {
        for (var index = 0; index < tables.Count; index++)
        {
            var item = tables[index];
            var qualifiedName = string.IsNullOrWhiteSpace(item.NamespaceName)
                ? item.TypeName
                : $"{item.NamespaceName}_{item.TypeName}";
            context.AddSource(
                $"{Identifier(qualifiedName)}_{index}.g.cs",
                RenderStorageUnit(item.Table, item.TypeName, item.NamespaceName));
        }
    }

    private static SchemaTable? BuildTable(
        GeneratorExecutionContext context,
        IReadOnlyList<ClassPart> declarations,
        INamedTypeSymbol symbol,
        AttributeData tableAttribute)
    {
        var tableName = StringArgument(tableAttribute, 0) ?? symbol.Name;
        if (IsEmpty(context, tableAttribute, symbol.Name, "the table name", tableName))
            return null;
        var columns = new List<SchemaColumn>();
        var keys = new List<string>();
        var columnSymbols = new Dictionary<string, ISymbol>(StringComparer.Ordinal);

        foreach (var part in declarations)
        {
            var declaration = part.Declaration;
            var semanticModel = part.SemanticModel;
            foreach (var member in declaration.Members)
            {
                if (member is not PropertyDeclarationSyntax and not FieldDeclarationSyntax)
                    continue;

                var declaredMembers = member switch
                {
                    PropertyDeclarationSyntax property => new[] { semanticModel.GetDeclaredSymbol(property, context.CancellationToken) },
                    FieldDeclarationSyntax field => field.Declaration.Variables
                        .Select(variable => semanticModel.GetDeclaredSymbol(variable, context.CancellationToken)),
                    _ => Array.Empty<ISymbol?>()
                };

                foreach (var declared in declaredMembers)
                {
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
                    if (IsEmpty(context, columnAttribute, tableName, $"the name of column '{memberSymbol.Name}'", name))
                        continue;
                    var nullable = IsNullable(memberType) && !BooleanNamedArgument(columnAttribute, "Required");
                    SchemaDefault? columnDefault = null;
                    if (StringNamedArgument(columnAttribute, "Default") is { } defaultText)
                    {
                        try
                        {
                            columnDefault = GroundworkSchemaCanonical.ReadDefault(defaultText, type);
                        }
                        catch (Exception exception) when (exception is FormatException or OverflowException or System.Text.Json.JsonException)
                        {
                            context.ReportDiagnostic(Diagnostic.Create(
                                InvalidDefault,
                                columnAttribute.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ?? declaration.GetLocation(),
                                name,
                                type.ToString(),
                                exception.Message));
                            continue;
                        }
                    }
                    var column = new SchemaColumn(
                        name,
                        type,
                        nullable,
                        IntNamedArgument(columnAttribute, "Length"),
                        IntNamedArgument(columnAttribute, "Precision"),
                        IntNamedArgument(columnAttribute, "Scale"),
                        EnumNamedArgument(columnAttribute, "Folding", TextFolding.None),
                        EnumNamedArgument(columnAttribute, "Generation", SchemaGeneration.Supplied),
                        columnDefault,
                        StringNamedArgument(columnAttribute, "Id"));
                    if (columnSymbols.ContainsKey(name))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            DuplicateColumn,
                            columnAttribute.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ?? declaration.GetLocation(),
                            tableName,
                            name));
                        continue;
                    }

                    columns.Add(column);
                    columnSymbols[name] = memberSymbol;
                    if (FindAttribute(memberSymbol, "GwKeyAttribute") is not null)
                        keys.Add(name);
                }
            }
        }

        if (keys.Count == 0)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                MissingKey,
                tableAttribute.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ??
                declarations.FirstOrDefault()?.Declaration.GetLocation() ?? Location.None,
                tableName));
            return null;
        }

        var indexes = new List<SchemaIndex>();
        var indexNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var indexAttribute in symbol.GetAttributes().Where(attribute => IsAttribute(attribute, "GwIndexAttribute")))
        {
            var indexName = StringArgument(indexAttribute, 0) ?? "<unnamed>";
            if (IsEmpty(context, indexAttribute, tableName, "an index name", indexName))
                continue;
            if (!indexNames.Add(indexName))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    InvalidIndex,
                    IndexSpecificationLocation(indexAttribute, context.CancellationToken),
                    indexName,
                    "an index with this name is already declared on the table."));
                continue;
            }

            if (!TryParseIndex(context, indexAttribute, columnSymbols.Keys, out var index))
                continue;
            indexes.Add(new SchemaIndex(
                index.Name,
                index.Columns,
                BooleanNamedArgument(indexAttribute, "IncludeNulls", defaultValue: true),
                BooleanNamedArgument(indexAttribute, "Unique")));
        }

        var aggregations = new List<SchemaAggregation>();
        var aggregationNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var aggregateAttribute in symbol.GetAttributes().Where(attribute => IsAttribute(attribute, "GwAggregateAttribute")))
        {
            if (!TryParseAggregation(context, aggregateAttribute, tableName, out var aggregation))
                continue;
            if (!aggregationNames.Add(aggregation.Name))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    InvalidTablePolicy,
                    AttributeLocation(aggregateAttribute, context),
                    tableName,
                    $"aggregation '{aggregation.Name}' is declared more than once."));
                continue;
            }
            aggregations.Add(aggregation);
        }

        var token = StringNamedArgument(tableAttribute, "ConcurrencyToken");
        if (token is not null && IsEmpty(context, tableAttribute, tableName, "the concurrency token", token))
            return null;

        return new SchemaTable(
            tableName,
            columns,
            keys,
            indexes,
            EnumNamedArgument(tableAttribute, "Scope", SchemaScope.Global),
            token is null ? null : new SchemaConcurrency(token),
            EnumNamedArgument(tableAttribute, "Timestamps", SchemaTimestamps.None),
            ReadRetention(context, symbol, tableName),
            ReadIdempotency(context, symbol, tableName, "GwAppendIdempotencyAttribute"),
            ReadIdempotency(context, symbol, tableName, "GwRetentionIdempotencyAttribute"),
            aggregations,
            StringNamedArgument(tableAttribute, "Id"));
    }

    private static SchemaRetention? ReadRetention(
        GeneratorExecutionContext context,
        INamedTypeSymbol symbol,
        string tableName)
    {
        if (FindAttribute(symbol, "GwRetentionAttribute") is not { } attribute)
            return null;
        var orderBy = StringArgument(attribute, 1);
        if (IsEmpty(context, attribute, tableName, "the retention order column", orderBy))
            return null;
        return new SchemaRetention(
            attribute.ConstructorArguments.Length > 0 && attribute.ConstructorArguments[0].Value is int keepNewest ? keepNewest : 0,
            orderBy!,
            EnumNamedArgument(attribute, "Trigger", SchemaRetentionTrigger.Explicit),
            (StringNamedArgument(attribute, "PartitionBy") ?? string.Empty)
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(column => column.Trim())
                .Where(column => column.Length != 0));
    }

    private static SchemaIdempotency? ReadIdempotency(
        GeneratorExecutionContext context,
        INamedTypeSymbol symbol,
        string tableName,
        string attributeName)
    {
        if (FindAttribute(symbol, attributeName) is not { } attribute)
            return null;
        var window = StringArgument(attribute, 0) ?? string.Empty;
        if (!TimeSpan.TryParse(window, CultureInfo.InvariantCulture, out var parsed))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                InvalidTablePolicy, AttributeLocation(attribute, context), tableName, $"'{window}' is not a time span."));
            return null;
        }
        var ledger = StringNamedArgument(attribute, "LedgerName");
        if (ledger is not null && IsEmpty(context, attribute, tableName, "the idempotency ledger name", ledger))
            return null;
        return new SchemaIdempotency(parsed, ledger);
    }

    private static bool TryParseAggregation(
        GeneratorExecutionContext context,
        AttributeData attribute,
        string tableName,
        out SchemaAggregation aggregation)
    {
        var name = StringArgument(attribute, 0) ?? "<unnamed>";
        if (IsEmpty(context, attribute, tableName, "an aggregation name", name))
        {
            aggregation = null!;
            return false;
        }
        var groupByColumns = new List<string>();
        var groupBy = new List<SchemaAggregationGroup>();
        var aggregates = new List<SchemaAggregate>();
        var location = AttributeLocation(attribute, context);
        foreach (var term in (StringArgument(attribute, 1) ?? string.Empty).Split(','))
        {
            var tokens = term.Trim().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var parsed = tokens.Length != 0 && tokens[0] switch
            {
                "group" when tokens.Length == 2 => Add(groupByColumns, tokens[1]),
                "bucket" when tokens.Length == 4 && TimeSpan.TryParse(tokens[3], CultureInfo.InvariantCulture, out var width) =>
                    Add(groupBy, SchemaAggregationGroup.FixedUtcBucket(tokens[1], tokens[2], width)),
                "day" when tokens.Length == 3 => Add(groupBy, SchemaAggregationGroup.LocalCalendarDayBucket(tokens[1], tokens[2])),
                "count" when tokens.Length == 2 => Add(aggregates, SchemaAggregate.Count(tokens[1])),
                "min" when tokens.Length == 3 => Add(aggregates, SchemaAggregate.Min(tokens[1], tokens[2])),
                "max" when tokens.Length == 3 => Add(aggregates, SchemaAggregate.Max(tokens[1], tokens[2])),
                "sum" when tokens.Length == 3 => Add(aggregates, SchemaAggregate.Sum(tokens[1], tokens[2])),
                "setUnion" when tokens.Length == 4 && int.TryParse(tokens[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxValues) =>
                    Add(aggregates, SchemaAggregate.SetUnion(tokens[1], tokens[2], maxValues)),
                "firstBy" when tokens.Length == 5 && tokens[4] is "ASC" or "DESC" =>
                    Add(aggregates, SchemaAggregate.FirstBy(tokens[1], tokens[2], tokens[3], tokens[4] == "DESC")),
                _ => false
            };
            if (!parsed)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    InvalidTablePolicy, location, tableName,
                    $"aggregation '{name}' has an unreadable term '{term.Trim()}'."));
                aggregation = null!;
                return false;
            }
        }

        string? invalid = null;
        if (aggregates.Count == 0)
            invalid = $"aggregation '{name}' declares no aggregate.";
        else if (groupByColumns.Count != 0 && groupBy.Count != 0)
            invalid = $"aggregation '{name}' mixes plain 'group' terms with bucket grouping; use one or the other.";
        else if (groupByColumns.Count == 0 && groupBy.Count == 0)
            invalid = $"aggregation '{name}' declares no grouping term.";
        else if (groupBy.Count(group => group.Bucket != SchemaTimeBucket.None) > 1)
            invalid = $"aggregation '{name}' declares more than one time bucket.";
        if (invalid is not null)
        {
            context.ReportDiagnostic(Diagnostic.Create(InvalidTablePolicy, location, tableName, invalid));
            aggregation = null!;
            return false;
        }

        aggregation = new SchemaAggregation(name, aggregates, groupByColumns, groupBy);
        return true;
    }

    private static Location AttributeLocation(AttributeData attribute, GeneratorExecutionContext context) =>
        attribute.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ?? Location.None;

    /// <summary>
    /// Refuses an empty attribute value as a build diagnostic. The schema model requires these to be
    /// non-empty, so without this the generator would fault instead of naming what is missing.
    /// </summary>
    private static bool IsEmpty(
        GeneratorExecutionContext context,
        AttributeData attribute,
        string tableName,
        string what,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            return false;
        context.ReportDiagnostic(Diagnostic.Create(
            InvalidTablePolicy, AttributeLocation(attribute, context), tableName, $"{what} cannot be empty."));
        return true;
    }

    private static bool Add<T>(List<T> target, T value)
    {
        target.Add(value);
        return true;
    }

    private static IReadOnlyList<ClassPart> GetClassParts(
        GeneratorExecutionContext context,
        INamedTypeSymbol target)
    {
        var parts = new List<ClassPart>();
        foreach (var tree in context.Compilation.SyntaxTrees)
        {
            var root = tree.GetRoot(context.CancellationToken);
            var semanticModel = context.Compilation.GetSemanticModel(tree);
            foreach (var declaration in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (semanticModel.GetDeclaredSymbol(declaration, context.CancellationToken) is INamedTypeSymbol symbol &&
                    SymbolEqualityComparer.Default.Equals(symbol, target))
                {
                    parts.Add(new ClassPart(declaration, semanticModel));
                }
            }
        }

        return parts;
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
        builder.AppendLine("#nullable enable");
        if (!string.IsNullOrWhiteSpace(namespaceName))
            builder.Append("namespace ").Append(namespaceName).AppendLine(";");
        builder.Append("public static class ").Append(typeName).AppendLine();
        builder.AppendLine("{");
        builder.AppendLine("    public static global::Groundwork.Kernel.StorageUnit Definition { get; } = Build();");
        builder.AppendLine();
        builder.AppendLine("    private static global::Groundwork.Kernel.StorageUnit Build()");
        builder.AppendLine("    {");
        builder.Append("        var declaration = global::Groundwork.Kernel.StorageUnit.Declare(")
            .Append(Literal(table.LogicalId)).Append(", ").Append(Literal(table.Name)).AppendLine(")");
        foreach (var column in table.Columns)
        {
            builder.Append("            .Column(").Append(Literal(column.Name)).Append(", global::Groundwork.Kernel.PortableType.").Append(column.Type switch
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
            }).Append(",");
            var columnOptions = new List<string>();
            if (!column.IsNullable)
                columnOptions.Add("Required()");
            if (column.Length.HasValue)
                columnOptions.Add("MaxLength(" + column.Length.Value + ")");
            if (column.Precision.HasValue && column.Scale.HasValue)
                columnOptions.Add("Precision(" + column.Precision.Value + ", " + column.Scale.Value + ")");
            if (column.Folding != TextFolding.None)
                columnOptions.Add("Collation(global::Groundwork.Kernel.PortableCollation." +
                    (column.Folding == TextFolding.AsciiIgnoreCase ? "OrdinalIgnoreCase" : "UnicodeOrdinalIgnoreCase") + ")");
            if (column.Generation == SchemaGeneration.ProviderSequence)
                columnOptions.Add("ProviderSequence()");
            if (column.Default is { } columnDefault)
                columnOptions.Add("Default(" + DefaultLiteral(columnDefault.Value, column.Type) + ")");
            if (column.Id is { } columnId && columnId != column.Name)
                columnOptions.Add("LogicalId(" + Literal(columnId) + ")");
            if (columnOptions.Count > 0)
                builder.Append(" column => column.").Append(string.Join(".", columnOptions));
            builder.AppendLine(")");
        }
        builder.AppendLine("            .Key(");
        builder.AppendLine("                new string[]");
        builder.AppendLine("                {");
        foreach (var key in table.Key)
            builder.Append("                    ").Append(Literal(key)).AppendLine(",");
        builder.AppendLine("                })");
        foreach (var index in table.Indexes)
        {
            builder.Append("            .").Append(index.Unique ? "UniqueIndex" : "Index").Append("(").Append(Literal(index.Name)).AppendLine(", index => index");
            foreach (var column in index.Columns)
                builder.Append("                .").Append(column.Descending ? "Descending" : "Column").Append("(").Append(Literal(column.Name)).AppendLine(")");
            if (!index.IncludeNulls)
                builder.AppendLine("                .ExcludeMissingValues()");
            builder.AppendLine("            )");
        }
        if (table.Scope == SchemaScope.Scoped)
            builder.AppendLine("            .Scoped()");
        if (table.Concurrency is { } concurrency)
            builder.Append("            .OptimisticConcurrency(").Append(Literal(concurrency.TokenColumn)).AppendLine(")");
        if (table.Retention is { } retention)
        {
            builder.Append("            .Retention(").Append(retention.KeepNewest.ToString(CultureInfo.InvariantCulture))
                .Append(", ").Append(Literal(retention.OrderBy))
                .Append(", global::Groundwork.Kernel.RetentionTrigger.").Append(retention.Trigger);
            foreach (var column in retention.PartitionBy)
                builder.Append(", ").Append(Literal(column));
            builder.AppendLine(")");
        }
        if (table.AppendIdempotency is { } append)
            builder.Append("            .AppendIdempotency(").Append(Window(append)).AppendLine(")");
        if (table.RetentionIdempotency is { } retentionIdempotency)
            builder.Append("            .RetentionIdempotency(").Append(Window(retentionIdempotency)).AppendLine(")");
        foreach (var aggregation in table.Aggregations)
        {
            builder.Append("            .Aggregate(").Append(Literal(aggregation.Name)).AppendLine(", aggregate => aggregate");
            if (aggregation.GroupByColumns.Count != 0)
            {
                builder.Append("                .GroupBy(")
                    .Append(string.Join(", ", aggregation.GroupByColumns.Select(Literal))).AppendLine(")");
            }
            foreach (var group in aggregation.GroupBy)
            {
                builder.Append("                ").AppendLine(group.Bucket switch
                {
                    SchemaTimeBucket.FixedUtc => $".FixedUtcBucket({Literal(group.Alias)}, {Literal(group.SourceColumn!)}, {Ticks(group.Width)})",
                    SchemaTimeBucket.LocalCalendarDay => $".LocalCalendarDayBucket({Literal(group.Alias)}, {Literal(group.SourceColumn!)})",
                    _ => $".GroupBy(new global::Groundwork.Kernel.AggregationGroup.Column({Literal(group.Alias)}))"
                });
            }
            foreach (var aggregate in aggregation.Aggregates)
            {
                builder.Append("                ").AppendLine(aggregate.Kind switch
                {
                    SchemaAggregateKind.Count => $".Count({Literal(aggregate.Alias)})",
                    SchemaAggregateKind.SetUnion => $".SetUnion({Literal(aggregate.Alias)}, {Literal(aggregate.Column!)}, {aggregate.MaxValues.ToString(CultureInfo.InvariantCulture)})",
                    SchemaAggregateKind.FirstBy => $".FirstBy({Literal(aggregate.Alias)}, {Literal(aggregate.Column!)}, {Literal(aggregate.OrderBy!)}, global::Groundwork.Kernel.SortDirection.{(aggregate.Descending ? "Descending" : "Ascending")})",
                    _ => $".{aggregate.Kind}({Literal(aggregate.Alias)}, {Literal(aggregate.Column!)})"
                });
            }
            builder.AppendLine("            )");
        }
        builder.AppendLine("        ;");
        builder.AppendLine("        return declaration.Build();");
        builder.AppendLine("    }");
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static string Window(SchemaIdempotency idempotency) =>
        Ticks(idempotency.Window) + (idempotency.LedgerName is null ? string.Empty : ", " + Literal(idempotency.LedgerName));

    private static string Ticks(TimeSpan value) =>
        "global::System.TimeSpan.FromTicks(" + value.Ticks.ToString(CultureInfo.InvariantCulture) + "L)";

    private static string DefaultLiteral(object? value, SchemaValueType type)
    {
        if (value is null)
            return "null";
        return type switch
        {
            SchemaValueType.String => Literal((string)value),
            SchemaValueType.Int32 => Convert.ToInt32(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            SchemaValueType.Int64 => Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) + "L",
            SchemaValueType.Decimal => Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) + "m",
            SchemaValueType.Boolean => Convert.ToBoolean(value, CultureInfo.InvariantCulture) ? "true" : "false",
            SchemaValueType.DateTimeOffset =>
                $"new global::System.DateTimeOffset({((DateTimeOffset)value).UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture)}L, global::System.TimeSpan.Zero)",
            SchemaValueType.Guid => $"new global::System.Guid({Literal(((Guid)value).ToString("D", CultureInfo.InvariantCulture))})",
            SchemaValueType.Binary => $"global::System.Convert.FromBase64String({Literal(Convert.ToBase64String((byte[])value))})",
            _ => JsonLiteral(value)
        };
    }

    private static string JsonLiteral(object? value)
    {
        switch (value)
        {
            case null:
                return "null";
            case string text:
                return Literal(text);
            case bool boolean:
                return boolean ? "true" : "false";
            case int int32:
                return int32.ToString(CultureInfo.InvariantCulture);
            case long int64:
                return int64.ToString(CultureInfo.InvariantCulture) + "L";
            case decimal number:
                return number.ToString(CultureInfo.InvariantCulture) + "m";
            case IReadOnlyDictionary<string, object?> map:
                return "new global::System.Collections.Generic.Dictionary<string, object?>(global::System.StringComparer.Ordinal) { " +
                    string.Join(", ", map.Select(entry => $"[{Literal(entry.Key)}] = {JsonLiteral(entry.Value)}")) + " }";
            case IEnumerable sequence:
                return "new global::System.Collections.Generic.List<object?> { " +
                    string.Join(", ", sequence.Cast<object?>().Select(JsonLiteral)) + " }";
            default:
                throw new ArgumentOutOfRangeException(nameof(value));
        }
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

    private static bool PathsEqual(string left, string right)
    {
        var normalizedLeft = left.Replace('\\', '/');
        var normalizedRight = right.Trim().Trim('"').Replace('\\', '/').TrimStart('/');
        if (string.Equals(normalizedLeft.TrimStart('/'), normalizedRight, StringComparison.OrdinalIgnoreCase))
            return true;

        return !System.IO.Path.IsPathRooted(right) &&
            normalizedLeft.EndsWith('/' + normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ClassPart(ClassDeclarationSyntax Declaration, SemanticModel SemanticModel);

    private sealed record GeneratedTable(SchemaTable Table, string TypeName, string? NamespaceName);
}
