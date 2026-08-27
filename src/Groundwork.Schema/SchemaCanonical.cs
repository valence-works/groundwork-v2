using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Groundwork.Schema;

/// <summary>Canonical serialization shared by source generation, metadata, and file fallback.</summary>
public static class GroundworkSchemaCanonical
{
    public static string Emit(SchemaDocument document) => Serialize(document);

    public static SchemaDocument Read(string json) => Parse(json);

    public static string Serialize(SchemaDocument document)
    {
        if (document is null)
            throw new ArgumentNullException(nameof(document));

        var builder = new StringBuilder("{\"tables\":[");
        var tables = document.Tables.OrderBy(table => table.Name, StringComparer.Ordinal).ToArray();
        for (var tableIndex = 0; tableIndex < tables.Length; tableIndex++)
        {
            if (tableIndex != 0) builder.Append(',');
            var table = tables[tableIndex];
            builder.Append("{\"name\":").Append(String(table.Name)).Append(",\"columns\":[");
            for (var columnIndex = 0; columnIndex < table.Columns.Count; columnIndex++)
            {
                if (columnIndex != 0) builder.Append(',');
                var column = table.Columns[columnIndex];
                builder.Append("{\"name\":").Append(String(column.Name))
                    .Append(",\"type\":").Append(String(column.Type.ToString()))
                    .Append(",\"nullable\":").Append(column.IsNullable ? "true" : "false")
                    .Append(",\"length\":").Append(Number(column.Length))
                    .Append(",\"precision\":").Append(Number(column.Precision))
                    .Append(",\"scale\":").Append(Number(column.Scale))
                    .Append(",\"folding\":").Append(String(column.Folding.ToString()))
                    .Append(",\"generation\":").Append(String(column.Generation.ToString()))
                    .Append(",\"default\":").Append(Default(column))
                    .Append('}');
            }

            builder.Append("],\"key\":[");
            for (var keyIndex = 0; keyIndex < table.Key.Count; keyIndex++)
            {
                if (keyIndex != 0) builder.Append(',');
                builder.Append(String(table.Key[keyIndex]));
            }

            builder.Append("],\"indexes\":[");
            var indexes = table.Indexes.OrderBy(index => index.Name, StringComparer.Ordinal).ToArray();
            for (var indexIndex = 0; indexIndex < indexes.Length; indexIndex++)
            {
                if (indexIndex != 0) builder.Append(',');
                var index = indexes[indexIndex];
                builder.Append("{\"name\":").Append(String(index.Name)).Append(",\"columns\":[");
                for (var columnIndex = 0; columnIndex < index.Columns.Count; columnIndex++)
                {
                    if (columnIndex != 0) builder.Append(',');
                    var column = index.Columns[columnIndex];
                    builder.Append("{\"name\":").Append(String(column.Name))
                        .Append(",\"descending\":").Append(column.Descending ? "true" : "false")
                        .Append('}');
                }

                builder.Append("],\"includeNulls\":").Append(index.IncludeNulls ? "true" : "false")
                    .Append(",\"unique\":").Append(index.Unique ? "true" : "false")
                    .Append('}');
            }

            builder.Append("],\"scope\":").Append(String(table.Scope.ToString()))
                .Append(",\"concurrency\":").Append(table.Concurrency is null
                    ? "null"
                    : "{\"token\":" + String(table.Concurrency.TokenColumn) + "}")
                .Append(",\"timestamps\":").Append(String(table.Timestamps.ToString()))
                .Append(",\"retention\":").Append(Retention(table.Retention))
                .Append(",\"appendIdempotency\":").Append(Idempotency(table.AppendIdempotency))
                .Append(",\"retentionIdempotency\":").Append(Idempotency(table.RetentionIdempotency))
                .Append(",\"aggregations\":[");
            var aggregations = table.Aggregations.OrderBy(aggregation => aggregation.Name, StringComparer.Ordinal).ToArray();
            for (var aggregationIndex = 0; aggregationIndex < aggregations.Length; aggregationIndex++)
            {
                if (aggregationIndex != 0) builder.Append(',');
                Append(builder, aggregations[aggregationIndex]);
            }

            builder.Append("]}");
        }

        return builder.Append("]}").ToString();
    }

    public static string Fingerprint(SchemaDocument document)
    {
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(Serialize(document)));
        return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
    }

    public static SchemaDocument Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("Schema JSON is required.", nameof(json));

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("tables", out var tableElements) || tableElements.ValueKind != JsonValueKind.Array)
            throw new FormatException("Schema JSON must contain a 'tables' array.");

        var tables = new List<SchemaTable>();
        foreach (var tableElement in tableElements.EnumerateArray())
        {
            var columns = new List<SchemaColumn>();
            foreach (var columnElement in RequiredArray(tableElement, "columns"))
            {
                var type = EnumValue<SchemaValueType>(columnElement, "type");
                columns.Add(new SchemaColumn(
                    RequiredString(columnElement, "name"),
                    type,
                    RequiredBoolean(columnElement, "nullable"),
                    OptionalInt(columnElement, "length"),
                    OptionalInt(columnElement, "precision"),
                    OptionalInt(columnElement, "scale"),
                    EnumValueOrDefault(columnElement, "folding", TextFolding.None),
                    EnumValueOrDefault(columnElement, "generation", SchemaGeneration.Supplied),
                    ReadDefault(columnElement, type)));
            }

            var indexes = new List<SchemaIndex>();
            foreach (var indexElement in RequiredArray(tableElement, "indexes"))
            {
                var indexColumns = RequiredArray(indexElement, "columns")
                    .Select(column => new SchemaIndexColumn(
                        RequiredString(column, "name"),
                        RequiredBoolean(column, "descending")))
                    .ToArray();
                indexes.Add(new SchemaIndex(
                    RequiredString(indexElement, "name"),
                    indexColumns,
                    RequiredBoolean(indexElement, "includeNulls"),
                    RequiredBoolean(indexElement, "unique")));
            }

            tables.Add(new SchemaTable(
                RequiredString(tableElement, "name"),
                columns,
                RequiredArray(tableElement, "key").Select(element => element.GetString() ?? throw new FormatException("Schema key names must be strings.")),
                indexes,
                EnumValueOrDefault(tableElement, "scope", SchemaScope.Global),
                ReadConcurrency(tableElement),
                EnumValueOrDefault(tableElement, "timestamps", SchemaTimestamps.None),
                ReadRetention(tableElement),
                ReadIdempotency(tableElement, "appendIdempotency"),
                ReadIdempotency(tableElement, "retentionIdempotency"),
                ReadAggregations(tableElement)));
        }

        return new SchemaDocument(tables);
    }

    private static void Append(StringBuilder builder, SchemaAggregation aggregation)
    {
        builder.Append("{\"name\":").Append(String(aggregation.Name)).Append(",\"groupByColumns\":[")
            .Append(string.Join(",", aggregation.GroupByColumns.Select(String)))
            .Append("],\"groupBy\":[");
        for (var index = 0; index < aggregation.GroupBy.Count; index++)
        {
            if (index != 0) builder.Append(',');
            var group = aggregation.GroupBy[index];
            builder.Append("{\"alias\":").Append(String(group.Alias))
                .Append(",\"bucket\":").Append(String(group.Bucket.ToString()))
                .Append(",\"sourceColumn\":").Append(group.SourceColumn is null ? "null" : String(group.SourceColumn))
                .Append(",\"widthTicks\":").Append(group.Width.Ticks.ToString(CultureInfo.InvariantCulture))
                .Append('}');
        }

        builder.Append("],\"aggregates\":[");
        for (var index = 0; index < aggregation.Aggregates.Count; index++)
        {
            if (index != 0) builder.Append(',');
            var aggregate = aggregation.Aggregates[index];
            builder.Append("{\"kind\":").Append(String(aggregate.Kind.ToString()))
                .Append(",\"alias\":").Append(String(aggregate.Alias))
                .Append(",\"column\":").Append(aggregate.Column is null ? "null" : String(aggregate.Column))
                .Append(",\"orderBy\":").Append(aggregate.OrderBy is null ? "null" : String(aggregate.OrderBy))
                .Append(",\"descending\":").Append(aggregate.Descending ? "true" : "false")
                .Append(",\"maxValues\":").Append(aggregate.MaxValues.ToString(CultureInfo.InvariantCulture))
                .Append('}');
        }

        builder.Append("]}");
    }

    private static string Retention(SchemaRetention? retention) => retention is null
        ? "null"
        : "{\"keepNewest\":" + retention.KeepNewest.ToString(CultureInfo.InvariantCulture) +
          ",\"orderBy\":" + String(retention.OrderBy) +
          ",\"trigger\":" + String(retention.Trigger.ToString()) +
          ",\"partitionBy\":[" + string.Join(",", retention.PartitionBy.Select(String)) + "]}";

    private static string Idempotency(SchemaIdempotency? idempotency) => idempotency is null
        ? "null"
        : "{\"windowTicks\":" + idempotency.Window.Ticks.ToString(CultureInfo.InvariantCulture) +
          ",\"ledger\":" + (idempotency.LedgerName is null ? "null" : String(idempotency.LedgerName)) + "}";

    private static string Default(SchemaColumn column) => column.Default is null
        ? "null"
        : "{\"value\":" + Literal(column.Default.Value, column.Type) + "}";

    private static string Literal(object? value, SchemaValueType type)
    {
        if (value is null)
            return "null";
        return type switch
        {
            SchemaValueType.String => String((string)value),
            SchemaValueType.Int32 => Convert.ToInt32(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            SchemaValueType.Int64 => Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            SchemaValueType.Decimal => Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            SchemaValueType.Boolean => Convert.ToBoolean(value, CultureInfo.InvariantCulture) ? "true" : "false",
            SchemaValueType.DateTimeOffset => String(((DateTimeOffset)value).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
            SchemaValueType.Guid => String(((Guid)value).ToString("D", CultureInfo.InvariantCulture)),
            SchemaValueType.Binary => String(Convert.ToBase64String((byte[])value)),
            SchemaValueType.Json => JsonSerializer.Serialize(value),
            _ => throw new FormatException($"Schema default values are not supported for '{type}'.")
        };
    }

    /// <summary>Reads a declared default in the invariant text form used by attributes.</summary>
    public static SchemaDefault ReadDefault(string text, SchemaValueType type)
    {
        if (text is null)
            throw new ArgumentNullException(nameof(text));
        return new SchemaDefault(type switch
        {
            SchemaValueType.String => text,
            SchemaValueType.Int32 => int.Parse(text, CultureInfo.InvariantCulture),
            SchemaValueType.Int64 => long.Parse(text, CultureInfo.InvariantCulture),
            SchemaValueType.Decimal => decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture),
            SchemaValueType.Boolean => bool.Parse(text),
            SchemaValueType.DateTimeOffset => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            SchemaValueType.Guid => Guid.Parse(text),
            SchemaValueType.Binary => Convert.FromBase64String(text),
            SchemaValueType.Json => ReadJson(JsonDocument.Parse(text).RootElement),
            _ => throw new FormatException($"Schema default values are not supported for '{type}'.")
        });
    }

    private static SchemaDefault? ReadDefault(JsonElement parent, SchemaValueType type)
    {
        if (!parent.TryGetProperty("default", out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("value", out var literal))
            throw new FormatException("Schema JSON property 'default' must be null or an object with a 'value'.");
        if (literal.ValueKind == JsonValueKind.Null)
            return new SchemaDefault(null);
        return new SchemaDefault(type switch
        {
            SchemaValueType.String => literal.GetString(),
            SchemaValueType.Int32 => literal.GetInt32(),
            SchemaValueType.Int64 => literal.GetInt64(),
            SchemaValueType.Decimal => literal.GetDecimal(),
            SchemaValueType.Boolean => literal.GetBoolean(),
            SchemaValueType.DateTimeOffset => literal.GetDateTimeOffset(),
            SchemaValueType.Guid => literal.GetGuid(),
            SchemaValueType.Binary => literal.GetBytesFromBase64(),
            SchemaValueType.Json => ReadJson(literal),
            _ => throw new FormatException($"Schema default values are not supported for '{type}'.")
        });
    }

    private static object? ReadJson(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when value.TryGetInt32(out var int32) => int32,
        JsonValueKind.Number when value.TryGetInt64(out var int64) => int64,
        JsonValueKind.Number => value.GetDecimal(),
        JsonValueKind.Array => value.EnumerateArray().Select(ReadJson).ToList(),
        JsonValueKind.Object => value.EnumerateObject().ToDictionary(
            property => property.Name,
            property => ReadJson(property.Value),
            StringComparer.Ordinal),
        _ => throw new FormatException($"Unsupported JSON default token '{value.ValueKind}'.")
    };

    private static SchemaConcurrency? ReadConcurrency(JsonElement table)
    {
        if (!table.TryGetProperty("concurrency", out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        return new SchemaConcurrency(RequiredString(value, "token"));
    }

    private static SchemaRetention? ReadRetention(JsonElement table)
    {
        if (!table.TryGetProperty("retention", out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        return new SchemaRetention(
            RequiredInt(value, "keepNewest"),
            RequiredString(value, "orderBy"),
            EnumValueOrDefault(value, "trigger", SchemaRetentionTrigger.Explicit),
            RequiredArray(value, "partitionBy").Select(element =>
                element.GetString() ?? throw new FormatException("Retention partition columns must be strings.")));
    }

    private static SchemaIdempotency? ReadIdempotency(JsonElement table, string name)
    {
        if (!table.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        return new SchemaIdempotency(
            TimeSpan.FromTicks(RequiredLong(value, "windowTicks")),
            value.TryGetProperty("ledger", out var ledger) && ledger.ValueKind == JsonValueKind.String
                ? ledger.GetString()
                : null);
    }

    private static IReadOnlyList<SchemaAggregation> ReadAggregations(JsonElement table)
    {
        if (!table.TryGetProperty("aggregations", out var value) || value.ValueKind != JsonValueKind.Array)
            return Array.Empty<SchemaAggregation>();
        return value.EnumerateArray().Select(element => new SchemaAggregation(
            RequiredString(element, "name"),
            RequiredArray(element, "aggregates").Select(aggregate => SchemaAggregate.Create(
                EnumValue<SchemaAggregateKind>(aggregate, "kind"),
                RequiredString(aggregate, "alias"),
                OptionalString(aggregate, "column"),
                OptionalString(aggregate, "orderBy"),
                RequiredBoolean(aggregate, "descending"),
                RequiredInt(aggregate, "maxValues"))).ToArray(),
            RequiredArray(element, "groupByColumns").Select(column =>
                column.GetString() ?? throw new FormatException("Aggregation group columns must be strings.")),
            RequiredArray(element, "groupBy").Select(ReadAggregationGroup).ToArray())).ToArray();
    }

    private static SchemaAggregationGroup ReadAggregationGroup(JsonElement element)
    {
        var alias = RequiredString(element, "alias");
        return EnumValue<SchemaTimeBucket>(element, "bucket") switch
        {
            SchemaTimeBucket.None => SchemaAggregationGroup.Column(alias),
            SchemaTimeBucket.FixedUtc => SchemaAggregationGroup.FixedUtcBucket(
                alias,
                RequiredString(element, "sourceColumn"),
                TimeSpan.FromTicks(RequiredLong(element, "widthTicks"))),
            _ => SchemaAggregationGroup.LocalCalendarDayBucket(alias, RequiredString(element, "sourceColumn"))
        };
    }

    private static string String(string value) => JsonSerializer.Serialize(value);

    private static string Number(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "null";

    private static IReadOnlyList<JsonElement> RequiredArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            throw new FormatException($"Schema JSON property '{name}' must be an array.");
        return value.EnumerateArray().ToArray();
    }

    private static string RequiredString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? throw new FormatException($"Schema JSON property '{name}' cannot be null.")
            : throw new FormatException($"Schema JSON property '{name}' must be a string.");

    private static string? OptionalString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool RequiredBoolean(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
            ? value.GetBoolean()
            : throw new FormatException($"Schema JSON property '{name}' must be a boolean.");

    private static int RequiredInt(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : throw new FormatException($"Schema JSON property '{name}' must be an integer.");

    private static long RequiredLong(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : throw new FormatException($"Schema JSON property '{name}' must be an integer.");

    private static int? OptionalInt(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
            throw new FormatException($"Schema JSON property '{name}' must be an integer or null.");
        return number;
    }

    private static T EnumValue<T>(JsonElement parent, string name) where T : struct, Enum
    {
        var text = RequiredString(parent, name);
        return Enum.TryParse<T>(text, ignoreCase: false, out var value)
            ? value
            : throw new FormatException($"Schema JSON property '{name}' has unknown value '{text}'.");
    }

    private static T EnumValueOrDefault<T>(JsonElement parent, string name, T defaultValue) where T : struct, Enum =>
        !parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null
            ? defaultValue
            : EnumValue<T>(parent, name);
}
