using System.Collections.Immutable;
using Groundwork.Schema;
using Groundwork.Schema.Generator;
using Groundwork.Query.Model;
using Groundwork.Query.Planning;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Groundwork.Analyzers;

internal sealed class AnalyzerSchema
{
    private AnalyzerSchema(IEnumerable<AnalyzerTable> tables)
    {
        Tables = tables
            .GroupBy(table => table.Name, StringComparer.Ordinal)
            .ToImmutableDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
    }

    public ImmutableDictionary<string, AnalyzerTable> Tables { get; }

    public static AnalyzerSchema Read(Compilation compilation, AnalyzerOptions options)
    {
        var documents = new List<SchemaDocument>();
        AddCurrentSchema(compilation, documents);
        try
        {
            documents.AddRange(GroundworkSchemaMetadata.Read(compilation));
        }
        catch (Exception)
        {
            // A malformed referenced schema is treated as unavailable. Query sites then receive
            // the explicit unresolved diagnostic instead of making an unsafe coverage claim.
        }

        AddAdditionalSchemas(options, documents);
        return new AnalyzerSchema(documents.SelectMany(document => document.Tables.Select(AnalyzerTable.Create)));
    }

    public bool TryGetTable(INamedTypeSymbol type, out AnalyzerTable table)
    {
        var tableName = type.GetAttributes()
            .Where(attribute => string.Equals(attribute.AttributeClass?.ToDisplayString(), typeof(GwTableAttribute).FullName, StringComparison.Ordinal))
            .Select(attribute => attribute.ConstructorArguments.FirstOrDefault().Value as string)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? type.Name;
        return Tables.TryGetValue(tableName, out table!);
    }

    private static void AddCurrentSchema(Compilation compilation, ICollection<SchemaDocument> documents)
    {
        foreach (var attribute in compilation.Assembly.GetAttributes().Where(IsSchemaAttribute))
        {
            if (attribute.ConstructorArguments.Length < 2 ||
                attribute.ConstructorArguments[0].Value is not string json ||
                attribute.ConstructorArguments[1].Value is not string fingerprint)
                continue;
            try
            {
                var schema = GroundworkSchemaCanonical.Parse(json!);
                if (string.Equals(GroundworkSchemaCanonical.Fingerprint(schema), fingerprint, StringComparison.Ordinal))
                    documents.Add(schema);
            }
            catch (Exception)
            {
                // An invalid assembly attribute cannot safely provide coverage metadata.
            }
        }
    }

    private static void AddAdditionalSchemas(AnalyzerOptions options, ICollection<SchemaDocument> documents)
    {
        var configured = ReadOption(options.AnalyzerConfigOptionsProvider.GlobalOptions, "gw_schema_file") ??
                         ReadOption(options.AnalyzerConfigOptionsProvider.GlobalOptions, "build_property.gw_schema_file");
        var files = options.AdditionalFiles
            .Where(file => file.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Where(file => configured is null || PathsEqual(file.Path, configured))
            .ToArray();
        foreach (var file in files)
        {
            var text = file.GetText()?.ToString();
            if (string.IsNullOrWhiteSpace(text))
                continue;
            try
            {
                documents.Add(GroundworkSchemaCanonical.Parse(text!));
                break;
            }
            catch (Exception)
            {
                // The source generator owns schema-file diagnostics. This analyzer simply refuses
                // to infer coverage from an invalid fallback file.
            }
        }
    }

    private static bool IsSchemaAttribute(AttributeData attribute) =>
        string.Equals(attribute.AttributeClass?.ToDisplayString(), typeof(GroundworkSchemaAttribute).FullName, StringComparison.Ordinal);

    private static string? ReadOption(AnalyzerConfigOptions options, string key) =>
        options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim().Trim('"') : null;

    private static bool PathsEqual(string left, string right)
    {
        var normalizedLeft = left.Replace('\\', '/').TrimStart('/');
        var normalizedRight = right.Replace('\\', '/').Trim('"').TrimStart('/');
        return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase) ||
               normalizedLeft.EndsWith('/' + normalizedRight, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class AnalyzerTable
{
    private AnalyzerTable(
        string name,
        ImmutableDictionary<string, AnalyzerColumn> columns,
        ImmutableArray<CoverageIndex> indexes)
    {
        Name = name;
        Columns = columns;
        Indexes = indexes;
    }

    public string Name { get; }
    public ImmutableDictionary<string, AnalyzerColumn> Columns { get; }
    public ImmutableArray<CoverageIndex> Indexes { get; }

    public bool TryGetColumn(string name, out AnalyzerColumn column) => Columns.TryGetValue(name, out column!);

    public static AnalyzerTable Create(SchemaTable table)
    {
        var columns = table.Columns.ToImmutableDictionary(
            column => column.Name,
            AnalyzerColumn.Create,
            StringComparer.Ordinal);
        var indexes = table.Indexes
            .Where(index => index.Columns.All(column => columns.ContainsKey(column.Name)))
            .Select(index => new CoverageIndex(
                index.Name,
                index.Columns.Select(column => new CoverageIndexColumn(
                    column.Name,
                    column.Descending ? OrderDirection.Descending : OrderDirection.Ascending,
                    columns[column.Name].IsNullable)),
                index.IncludeNulls ? IndexMissingValueBehavior.Included : IndexMissingValueBehavior.Excluded));
        return new AnalyzerTable(table.Name, columns, CoverageCandidates.Derive(table.Key, indexes));
    }
}

internal sealed record AnalyzerColumn(
    string Name,
    QueryType Type,
    bool IsNullable,
    int? MaxLength,
    byte? Precision,
    byte? Scale)
{
    public static AnalyzerColumn Create(SchemaColumn column) => new(
        column.Name,
        column.Type switch
        {
            SchemaValueType.String => QueryType.String,
            SchemaValueType.Int32 => QueryType.Int32,
            SchemaValueType.Int64 => QueryType.Int64,
            SchemaValueType.Decimal => QueryType.Decimal,
            SchemaValueType.Boolean => QueryType.Boolean,
            SchemaValueType.DateTimeOffset => QueryType.DateTimeOffset,
            SchemaValueType.Guid => QueryType.Guid,
            SchemaValueType.Binary => QueryType.Binary,
            _ => QueryType.String
        },
        column.IsNullable,
        column.Length,
        ToByte(column.Precision),
        ToByte(column.Scale));

    private static byte? ToByte(int? value) => value is >= 0 and <= byte.MaxValue ? (byte)value : null;
}
