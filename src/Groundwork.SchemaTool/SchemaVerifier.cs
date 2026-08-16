using System.Collections.Immutable;
using System.Text.Json;
using Groundwork.Kernel;
using Groundwork.Query.Model;
using Groundwork.Query.Planning;
using Groundwork.Schema;

namespace Groundwork.SchemaTool;

public sealed record SchemaVerificationError(string Code, string Message, string Path);

public sealed class SchemaVerificationResult
{
    public SchemaVerificationResult(IEnumerable<SchemaVerificationError> errors) =>
        Errors = Array.AsReadOnly((errors ?? throw new ArgumentNullException(nameof(errors))).ToArray());

    public IReadOnlyList<SchemaVerificationError> Errors { get; }

    public bool Succeeded => Errors.Count == 0;
}

public static class SchemaVerifier
{
    public static SchemaVerificationResult Verify(string schemaJson, string? coverageInventoryJson = null)
    {
        var schema = GroundworkSchemaCanonical.Read(schemaJson);
        var errors = SchemaCompilation.Compile(schema)
            .SelectMany(unit => PortabilityValidator.Validate(unit).Refusals)
            .Select(refusal => new SchemaVerificationError(refusal.Code, refusal.Message, refusal.Path))
            .ToList();
        if (!string.IsNullOrWhiteSpace(coverageInventoryJson))
            errors.AddRange(VerifyCoverage(schema, coverageInventoryJson!));
        return new SchemaVerificationResult(errors);
    }

    private static IEnumerable<SchemaVerificationError> VerifyCoverage(
        SchemaDocument schema,
        string inventoryJson)
    {
        var inventory = JsonSerializer.Deserialize<CoverageInventory>(inventoryJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new FormatException("Coverage inventory is empty.");
        var tables = schema.Tables.ToDictionary(table => table.Name, StringComparer.Ordinal);
        foreach (var query in inventory.Queries ?? [])
        {
            if (!tables.TryGetValue(query.Table ?? string.Empty, out var table))
            {
                yield return new SchemaVerificationError(
                    "GW-COVER-006",
                    $"Query table '{query.Table}' is not declared.",
                    $"queries.{query.Name ?? query.Table}");
                continue;
            }

            QueryCoverageResult? result = null;
            SchemaVerificationError? error = null;
            try
            {
                result = QueryCoverageChecker.Check(CreateRequest(table, query), CreateIndexes(table));
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException)
            {
                error = new SchemaVerificationError(
                    "GW-COVER-016",
                    exception.Message,
                    $"queries.{query.Name ?? query.Table}");
            }
            if (error is not null)
            {
                yield return error;
                continue;
            }
            if (!result!.IsCovered)
            {
                var refusal = result.Refusal!;
                yield return new SchemaVerificationError(
                    refusal.Code,
                    refusal.Message,
                    $"queries.{query.Name ?? query.Table}");
            }
        }
    }

    private static QueryRequest CreateRequest(SchemaTable table, CoverageQuery query)
    {
        var id = new TableId(table.Name);
        var columns = table.Columns.ToDictionary(
            column => column.Name,
            column => CreateColumn(id, column),
            StringComparer.Ordinal);
        ColumnRef Get(string name) => columns.TryGetValue(name, out var column)
            ? column
            : throw new ArgumentException($"Query column '{name}' is not declared on table '{table.Name}'.");

        var predicates = new List<Predicate>();
        predicates.AddRange((query.Equal ?? []).Select(name =>
        {
            var column = Get(name);
            return (Predicate)new Predicate.Equal(column, QueryConstant.Of(column, Placeholder(column.Type)));
        }));
        predicates.AddRange((query.Range ?? []).Select(name =>
        {
            var column = Get(name);
            return (Predicate)new Predicate.Range(
                column,
                Bound.Inclusive(QueryConstant.Of(column, Placeholder(column.Type))),
                null);
        }));
        var where = predicates.Count switch
        {
            0 => Predicate.AlwaysTrue.Instance,
            1 => predicates[0],
            _ => new Predicate.And(predicates)
        };
        var order = (query.Order ?? []).Select(term => new OrderTerm(
            Get(term.Column ?? throw new FormatException("An order term requires a column.")),
            string.Equals(term.Direction, "desc", StringComparison.OrdinalIgnoreCase)
                ? OrderDirection.Descending
                : OrderDirection.Ascending,
            term.Nulls?.ToLowerInvariant() switch
            {
                "first" => NullOrder.First,
                "last" => NullOrder.Last,
                _ => NullOrder.ProviderDefault
            })).ToImmutableArray();
        var paging = query.Take is int take ? Paging.OffsetLimit(query.Skip ?? 0, take) : Paging.None;
        return new QueryRequest(
            id,
            where,
            order,
            Projection.All,
            paging,
            query.TotalCount ? ResultShape.TotalCount.Instance : ResultShape.Rows.Instance);
    }

    private static IReadOnlyList<CoverageIndex> CreateIndexes(SchemaTable table)
    {
        var columns = table.Columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
        return table.Indexes.Select(index => new CoverageIndex(
            index.Name,
            index.Columns.Select(term => new CoverageIndexColumn(
                term.Name,
                term.Descending ? OrderDirection.Descending : OrderDirection.Ascending,
                columns[term.Name].IsNullable)),
            index.IncludeNulls ? IndexMissingValueBehavior.Included : IndexMissingValueBehavior.Excluded)).ToArray();
    }

    private static ColumnRef CreateColumn(TableId table, SchemaColumn column) => new(
        table,
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
            SchemaValueType.Json => throw new ArgumentException($"JSON column '{column.Name}' is not directly queryable."),
            _ => throw new ArgumentOutOfRangeException(nameof(column.Type), column.Type, null)
        },
        column.IsNullable,
        column.Length,
        column.Precision is null ? null : checked((byte)column.Precision.Value),
        column.Scale is null ? null : checked((byte)column.Scale.Value));

    private static object Placeholder(QueryType type) => type switch
    {
        QueryType.String => string.Empty,
        QueryType.Int32 => 0,
        QueryType.Int64 => 0L,
        QueryType.Decimal => 0m,
        QueryType.Double => 0d,
        QueryType.Boolean => false,
        QueryType.DateTimeOffset => DateTimeOffset.UnixEpoch,
        QueryType.Guid => Guid.Empty,
        QueryType.Binary => Array.Empty<byte>(),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    private sealed class CoverageInventory
    {
        public CoverageQuery[]? Queries { get; set; }
    }

    private sealed class CoverageQuery
    {
        public string? Name { get; set; }
        public string? Table { get; set; }
        public string[]? Equal { get; set; }
        public string[]? Range { get; set; }
        public CoverageOrder[]? Order { get; set; }
        public int? Skip { get; set; }
        public int? Take { get; set; }
        public bool TotalCount { get; set; }
    }

    private sealed class CoverageOrder
    {
        public string? Column { get; set; }
        public string? Direction { get; set; }
        public string? Nulls { get; set; }
    }
}
