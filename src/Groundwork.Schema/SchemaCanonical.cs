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
                columns.Add(new SchemaColumn(
                    RequiredString(columnElement, "name"),
                    EnumValue<SchemaValueType>(columnElement, "type"),
                    RequiredBoolean(columnElement, "nullable"),
                    OptionalInt(columnElement, "length"),
                    OptionalInt(columnElement, "precision"),
                    OptionalInt(columnElement, "scale"),
                    EnumValueOrDefault(columnElement, "folding", TextFolding.None),
                    EnumValueOrDefault(columnElement, "generation", SchemaGeneration.Supplied)));
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
                indexes));
        }

        return new SchemaDocument(tables);
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

    private static bool RequiredBoolean(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
            ? value.GetBoolean()
            : throw new FormatException($"Schema JSON property '{name}' must be a boolean.");

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
