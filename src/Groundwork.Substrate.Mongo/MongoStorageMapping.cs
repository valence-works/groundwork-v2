using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Groundwork.Kernel;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;

namespace Groundwork.Substrate.Mongo;

public sealed record MongoIndexTerm(string Column, SortDirection Direction);

/// <summary>A provider-neutral snapshot used to construct and verify one native MongoDB index.</summary>
public sealed class MongoIndexSpecification
{
    public MongoIndexSpecification(IndexDefinition definition, IReadOnlyList<ColumnDefinition> columns)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(columns);

        Name = definition.Name;
        Terms = Array.AsReadOnly(definition.Columns
            .Select(term => new MongoIndexTerm(term.Column, term.Direction))
            .ToArray());
        IsUnique = definition.IsUnique;
        MissingValues = definition.MissingValues;
        SchemaVersion = definition.SchemaVersion;
        PartialFilter = definition.MissingValues == MissingValueBehavior.Excluded
            ? BuildPartialFilter(definition, columns)
            : null;
    }

    public string Name { get; }

    public IReadOnlyList<MongoIndexTerm> Terms { get; }

    public bool IsUnique { get; }

    public MissingValueBehavior MissingValues { get; }

    public int SchemaVersion { get; }

    internal BsonDocument? PartialFilter { get; }

    private static BsonDocument BuildPartialFilter(
        IndexDefinition definition,
        IReadOnlyList<ColumnDefinition> columns)
    {
        var clauses = definition.Columns.Select(column =>
            new BsonDocument(column.Column,
                new BsonDocument("$type",
                    MongoValueCodec.GetBsonTypeName(columns.Single(item => item.Name == column.Column)))))
            .ToArray();
        return new BsonDocument("$and", new BsonArray(clauses));
    }
}

/// <summary>Maps each declared portable value to a native BSON value without an envelope.</summary>
public static class MongoValueCodec
{
    public static BsonValue Encode(object? value, ColumnDefinition column) =>
        Encode(value, column, isPresent: true);

    public static BsonValue Encode(object? value, ColumnDefinition column, bool isPresent)
    {
        ArgumentNullException.ThrowIfNull(column);

        if (value is null)
        {
            if (!isPresent && column.Default is not null)
                return Encode(column.Default.Value, column, isPresent: true);
            if (!column.IsNullable && !isPresent)
                throw new InvalidOperationException($"Column '{column.Name}' is required.");
            if (!column.IsNullable)
                throw new ArgumentException($"Column '{column.Name}' cannot be null.", nameof(value));
            return BsonNull.Value;
        }

        return column.Type switch
        {
            PortableType.String => EncodeString(value, column),
            PortableType.Int32 => new BsonInt32(ExactInt32(value, column)),
            PortableType.Int64 => new BsonInt64(ExactInt64(value, column)),
            PortableType.Decimal => EncodeDecimal(value, column),
            PortableType.Boolean => new BsonBoolean(ExactBoolean(value, column)),
            // BSON DateTime is millisecond precision; the portable contract is UTC ticks.
            // Store the canonical tick count as an Int64 so Mongo ordering and equality do
            // not silently collapse values that differ inside one millisecond.
            PortableType.DateTimeOffset => new BsonInt64(ExactDateTimeOffset(value, column).UtcTicks),
            PortableType.Guid => new BsonBinaryData(ExactGuid(value, column), GuidRepresentation.Standard),
            PortableType.Binary => EncodeBinary(value, column),
            PortableType.Json => EncodeJson(value, column),
            // The BsonDouble constructor keeps the caller's bit pattern; BsonDouble.Create and the
            // implicit BsonValue conversion route small values through a cache that collapses -0.
            PortableType.Double => new BsonDouble(ExactDouble(value, column)),
            _ => throw new ArgumentOutOfRangeException(nameof(column), column.Type, null)
        };
    }

    public static object? Decode(BsonValue value, ColumnDefinition column)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(column);
        if (value.IsBsonNull)
            return null;

        return column.Type switch
        {
            PortableType.String => value.AsString,
            PortableType.Int32 => value.ToInt32(),
            PortableType.Int64 => value.ToInt64(),
            PortableType.Decimal => DecodeDecimal(value),
            PortableType.Boolean => value.AsBoolean,
            PortableType.DateTimeOffset => new DateTimeOffset(value.AsInt64, TimeSpan.Zero),
            PortableType.Guid => value.AsBsonBinaryData.ToGuid(GuidRepresentation.Standard),
            PortableType.Binary => value.AsBsonBinaryData.Bytes.ToArray(),
            PortableType.Json => DecodeJson(value),
            PortableType.Double => value.AsDouble,
            _ => throw new ArgumentOutOfRangeException(nameof(column), column.Type, null)
        };
    }

    public static string GetBsonTypeName(ColumnDefinition column) => column.Type switch
    {
        PortableType.String => "string",
        PortableType.Int32 => "int",
        PortableType.Int64 => "long",
        PortableType.Decimal => "decimal",
        PortableType.Boolean => "bool",
        PortableType.DateTimeOffset => "long",
        PortableType.Guid or PortableType.Binary => "binData",
        PortableType.Json => "object",
        PortableType.Double => "double",
        _ => throw new ArgumentOutOfRangeException(nameof(column), column.Type, null)
    };

    private static BsonValue EncodeString(object value, ColumnDefinition column)
    {
        if (value is not string text)
            throw WrongType(value, column, typeof(string));
        ValidateMaxLength(text.Length, column);
        return new BsonString(text);
    }

    private static BsonValue EncodeBinary(object value, ColumnDefinition column)
    {
        if (value is not byte[] bytes)
            throw WrongType(value, column, typeof(byte[]));
        ValidateMaxLength(bytes.Length, column);
        return new BsonBinaryData(bytes.ToArray());
    }

    private static BsonValue EncodeDecimal(object value, ColumnDefinition column)
    {
        ValidateDecimalDeclaration(column);
        var number = value switch
        {
            decimal decimalValue => decimalValue,
            int intValue => intValue,
            long longValue => longValue,
            string text when decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => throw WrongType(value, column, typeof(decimal))
        };

        var rounded = decimal.Round(number, column.Scale!.Value, MidpointRounding.ToEven);
        var integerDigits = CountIntegerDigits(rounded);
        if (integerDigits > column.Precision!.Value - column.Scale.Value)
        {
            throw new OverflowException(
                $"Value for column '{column.Name}' exceeds Decimal({column.Precision},{column.Scale}).");
        }

        return new BsonDecimal128(new Decimal128(rounded));
    }

    private static object DecodeDecimal(BsonValue value)
    {
        var decimal128 = value.AsDecimal128;
        try
        {
            return Decimal128.ToDecimal(decimal128);
        }
        catch (OverflowException)
        {
            return decimal128.ToString();
        }
    }

    private static BsonValue EncodeJson(object value, ColumnDefinition column)
    {
        try
        {
            if (value is BsonValue bson)
                return bson.DeepClone();
            var json = value switch
            {
                string text => text,
                JsonElement element => element.GetRawText(),
                JsonDocument document => document.RootElement.GetRawText(),
                JsonNode node => node.ToJsonString(),
                _ => JsonSerializer.Serialize(value)
            };
            return BsonSerializer.Deserialize<BsonValue>(json);
        }
        catch (Exception exception) when (exception is FormatException or JsonException or BsonSerializationException)
        {
            throw new ArgumentException($"Column '{column.Name}' does not contain valid JSON.", nameof(value), exception);
        }
    }

    private static JsonElement DecodeJson(BsonValue value)
    {
        using var document = JsonDocument.Parse(value.ToJson(new JsonWriterSettings
        {
            OutputMode = JsonOutputMode.RelaxedExtendedJson
        }));
        return document.RootElement.Clone();
    }

    private static int ExactInt32(object value, ColumnDefinition column) => value switch
    {
        int number => number,
        _ => throw WrongType(value, column, typeof(int))
    };

    private static long ExactInt64(object value, ColumnDefinition column) => value switch
    {
        long number => number,
        int number => number,
        _ => throw WrongType(value, column, typeof(long))
    };

    private static double ExactDouble(object value, ColumnDefinition column) => value switch
    {
        double number => number,
        _ => throw WrongType(value, column, typeof(double))
    };

    private static bool ExactBoolean(object value, ColumnDefinition column) => value switch
    {
        bool boolean => boolean,
        _ => throw WrongType(value, column, typeof(bool))
    };

    private static DateTimeOffset ExactDateTimeOffset(object value, ColumnDefinition column) => value switch
    {
        DateTimeOffset instant => instant,
        _ => throw WrongType(value, column, typeof(DateTimeOffset))
    };

    private static Guid ExactGuid(object value, ColumnDefinition column) => value switch
    {
        Guid guid => guid,
        _ => throw WrongType(value, column, typeof(Guid))
    };

    private static void ValidateDecimalDeclaration(ColumnDefinition column)
    {
        if (column.Precision is not (>= 1 and <= 34) ||
            column.Scale is not (>= 0) ||
            column.Scale > column.Precision)
        {
            throw new InvalidOperationException(
                $"MongoDB Decimal128 requires column '{column.Name}' to declare Precision 1..34 and Scale 0..Precision.");
        }
    }

    private static int CountIntegerDigits(decimal value)
    {
        var absolute = Math.Abs(value);
        if (absolute < 1)
            return 0;
        return decimal.Truncate(absolute).ToString("0", CultureInfo.InvariantCulture).Length;
    }

    private static void ValidateMaxLength(int length, ColumnDefinition column)
    {
        if (column.MaxLength is int maxLength && (maxLength <= 0 || length > maxLength))
            throw new InvalidOperationException(
                $"Column '{column.Name}' exceeds its maximum length of {maxLength}.");
    }

    private static ArgumentException WrongType(object value, ColumnDefinition column, Type expected) =>
        new($"Column '{column.Name}' expects {expected.Name}, received {value.GetType().Name}.", column.Name);
}
