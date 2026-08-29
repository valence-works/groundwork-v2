using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Groundwork.Query.Model;

public enum QueryType
{
    Boolean,
    Int32,
    Int64,
    Decimal,
    Double,
    String,
    DateTimeOffset,
    Guid,
    Binary
}

public enum QueryConstantKind
{
    Null,
    Boolean,
    Int32,
    Int64,
    Decimal,
    Double,
    String,
    DateTimeOffset,
    Guid,
    Binary
}

/// <summary>Portable comparison policies for text values.</summary>
public enum QueryStringComparisonPolicy
{
    Ordinal,
    UnicodeOrdinalIgnoreCase,
    AsciiIgnoreCase,
    CurrentCulture,
    InvariantCulture,
    AccentInsensitive,
    Icu
}

public sealed record TableId
{
    public TableId(string value)
    {
        if (value is null || (value.Length != 0 && string.IsNullOrWhiteSpace(value)))
            throw new ArgumentException("A table identifier cannot be blank.", nameof(value));

        Value = value;
    }

    public string Value { get; }

    public static TableId Empty { get; } = new("");

    public override string ToString() => Value;
}

public sealed record ColumnRef
{
    public ColumnRef(
        string name,
        QueryType type,
        bool isNullable = true,
        int? maxLength = null,
        byte? decimalPrecision = null,
        byte? decimalScale = null,
        QueryStringComparisonPolicy stringComparison = QueryStringComparisonPolicy.Ordinal)
        : this(TableId.Empty, name, type, isNullable, maxLength, decimalPrecision, decimalScale, stringComparison)
    {
    }

    public ColumnRef(
        TableId table,
        string name,
        QueryType type,
        bool isNullable = true,
        int? maxLength = null,
        byte? decimalPrecision = null,
        byte? decimalScale = null,
        QueryStringComparisonPolicy stringComparison = QueryStringComparisonPolicy.Ordinal)
    {
        Table = table ?? throw new ArgumentNullException(nameof(table));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A column name cannot be blank.", nameof(name));
        if (maxLength is <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxLength));
        if (decimalPrecision is 0 or > 29)
            throw new ArgumentOutOfRangeException(nameof(decimalPrecision));
        if (decimalScale is > 28)
            throw new ArgumentOutOfRangeException(nameof(decimalScale));
        if (decimalScale is not null && decimalPrecision is not null && decimalScale > decimalPrecision)
            throw new ArgumentException("Decimal scale cannot exceed precision.", nameof(decimalScale));

        Name = name;
        Type = type;
        IsNullable = isNullable;
        MaxLength = maxLength;
        DecimalPrecision = decimalPrecision;
        DecimalScale = decimalScale;
        StringComparison = stringComparison;
    }

    public TableId Table { get; }
    public string Name { get; }
    public QueryType Type { get; }
    public bool IsNullable { get; }
    public int? MaxLength { get; }
    public byte? DecimalPrecision { get; }
    public byte? DecimalScale { get; }
    public QueryStringComparisonPolicy StringComparison { get; }

    public override string ToString() => Table.Value.Length == 0 ? Name : Table + "." + Name;
}

internal static class ColumnRefIdentity
{
    internal static bool Same(ColumnRef left, ColumnRef right, bool tableQualified) =>
        string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
        (!tableQualified || left.Table == right.Table);
}

public sealed class QueryConstant : IEquatable<QueryConstant>
{
    private readonly object? _value;

    private QueryConstant(QueryConstantKind kind, QueryType? declaredType, object? value)
    {
        Kind = kind;
        DeclaredType = declaredType;
        _value = value is byte[] bytes ? (byte[])bytes.Clone() : value;
    }

    public QueryConstantKind Kind { get; }
    public QueryType? DeclaredType { get; }
    public QueryType? Type => DeclaredType ?? Kind switch
    {
        QueryConstantKind.Boolean => QueryType.Boolean,
        QueryConstantKind.Int32 => QueryType.Int32,
        QueryConstantKind.Int64 => QueryType.Int64,
        QueryConstantKind.Decimal => QueryType.Decimal,
        QueryConstantKind.Double => QueryType.Double,
        QueryConstantKind.String => QueryType.String,
        QueryConstantKind.DateTimeOffset => QueryType.DateTimeOffset,
        QueryConstantKind.Guid => QueryType.Guid,
        QueryConstantKind.Binary => QueryType.Binary,
        _ => null
    };
    public object? Value => _value is byte[] bytes ? (byte[])bytes.Clone() : _value;

    public static QueryConstant Of(ColumnRef column, object? value)
    {
        if (column is null)
            throw new ArgumentNullException(nameof(column));
        if (value is null)
        {
            if (!column.IsNullable)
                throw new ArgumentException($"Column '{column}' is not nullable.", nameof(value));
            return new(QueryConstantKind.Null, column.Type, null);
        }

        var normalized = NormalizeValue(column, value);
        return new(KindFor(column.Type), column.Type, normalized);
    }

    public static QueryConstant Of(object? value)
    {
        if (value is null)
            return new(QueryConstantKind.Null, null, null);
        var (kind, normalized) = InferValue(value);
        return new(kind, null, normalized);
    }

    internal QueryConstant Bind(ColumnRef column) => Of(column, Value);

    internal static int Compare(QueryConstant left, QueryConstant right)
    {
        if (left is null || right is null)
            throw new ArgumentNullException(left is null ? nameof(left) : nameof(right));
        if (left.Kind == QueryConstantKind.Null || right.Kind == QueryConstantKind.Null)
            return left.Kind == right.Kind ? 0 : left.Kind == QueryConstantKind.Null ? -1 : 1;
        if (left.Type != right.Type)
            throw new ArgumentException("Only constants of the same type can be compared.");
        if (left.Value is byte[] leftBytes && right.Value is byte[] rightBytes)
        {
            for (var index = 0; index < Math.Min(leftBytes.Length, rightBytes.Length); index++)
            {
                var comparison = leftBytes[index].CompareTo(rightBytes[index]);
                if (comparison != 0)
                    return comparison;
            }
            return leftBytes.Length.CompareTo(rightBytes.Length);
        }
        if (left.Value is Guid leftGuid && right.Value is Guid rightGuid)
            return PortableValueComparison.CompareGuid(leftGuid, rightGuid);
        if (left.Value is string leftString && right.Value is string rightString)
            return string.CompareOrdinal(leftString, rightString);
        if (left.Value is IComparable comparable)
            return comparable.CompareTo(right.Value);
        return string.CompareOrdinal(left.ToCanonicalString(), right.ToCanonicalString());
    }

    public string ToCanonicalString(bool includeValue = true)
    {
        var type = Type?.ToString() ?? "Null";
        if (!includeValue)
            return "constant<" + type + ">";
        if (Kind == QueryConstantKind.Null)
            return "null<" + type + ">";
        return type.ToLowerInvariant() + ":" + CanonicalValue(Value, Kind);
    }

    public bool Equals(QueryConstant? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null || Kind != other.Kind || DeclaredType != other.DeclaredType)
            return false;
        if (Value is byte[] left && other.Value is byte[] right)
            return left.SequenceEqual(right);
        return Equals(Value, other.Value);
    }

    public override bool Equals(object? obj) => Equals(obj as QueryConstant);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = 17;
            hash = hash * 31 + Kind.GetHashCode();
            hash = hash * 31 + (DeclaredType?.GetHashCode() ?? 0);
            hash = hash * 31 + (Value is byte[] ? 0 : Value?.GetHashCode() ?? 0);
            if (Value is byte[] bytes)
                foreach (var item in bytes)
                    hash = hash * 31 + item;
            return hash;
        }
    }

    public override string ToString() => ToCanonicalString();

    private static QueryConstantKind KindFor(QueryType type) => type switch
    {
        QueryType.Boolean => QueryConstantKind.Boolean,
        QueryType.Int32 => QueryConstantKind.Int32,
        QueryType.Int64 => QueryConstantKind.Int64,
        QueryType.Decimal => QueryConstantKind.Decimal,
        QueryType.Double => QueryConstantKind.Double,
        QueryType.String => QueryConstantKind.String,
        QueryType.DateTimeOffset => QueryConstantKind.DateTimeOffset,
        QueryType.Guid => QueryConstantKind.Guid,
        QueryType.Binary => QueryConstantKind.Binary,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static object NormalizeValue(ColumnRef column, object value)
    {
        switch (column.Type)
        {
            case QueryType.Boolean when value is bool boolean:
                return boolean;
            case QueryType.Int32 when value is int intValue:
                return intValue;
            case QueryType.Int64 when value is long longValue:
                return longValue;
            case QueryType.Decimal when value is decimal decimalValue:
                ValidateDecimal(column, decimalValue);
                return decimalValue;
            case QueryType.String when value is string text:
                ValidateString(column, text);
                return text;
            case QueryType.DateTimeOffset when value is DateTimeOffset instant:
                return instant;
            case QueryType.Guid when value is Guid guid:
                return guid;
            case QueryType.Binary when value is byte[] binary:
                return (byte[])binary.Clone();
        }

        throw new ArgumentException($"Value '{value}' is not valid for {column.Type} column '{column}'.", nameof(value));
    }

    private static (QueryConstantKind Kind, object Value) InferValue(object value)
    {
        if (value is bool boolean)
            return (QueryConstantKind.Boolean, boolean);
        if (value is int integer)
            return (QueryConstantKind.Int32, integer);
        if (value is long longInteger)
            return (QueryConstantKind.Int64, longInteger);
        if (value is decimal decimalValue)
            return (QueryConstantKind.Decimal, decimalValue);
        if (value is double doubleValue && !double.IsNaN(doubleValue) && !double.IsInfinity(doubleValue))
            return (QueryConstantKind.Double, doubleValue);
        if (value is string text && IsWellFormedUtf16(text))
            return (QueryConstantKind.String, text);
        if (value is DateTimeOffset instant)
            return (QueryConstantKind.DateTimeOffset, instant);
        if (value is Guid guid)
            return (QueryConstantKind.Guid, guid);
        if (value is byte[] binary)
            return (QueryConstantKind.Binary, (byte[])binary.Clone());
        throw new ArgumentException($"Value of type '{value.GetType()}' is not a supported query constant.", nameof(value));
    }

    private static void ValidateDecimal(ColumnRef column, decimal value)
    {
        var precision = column.DecimalPrecision ?? 18;
        var scale = column.DecimalScale ?? 4;
        var text = value.ToString("0.#############################", CultureInfo.InvariantCulture);
        var parts = text.TrimStart('-').Split('.');
        var fractionalDigits = parts.Length == 2 ? parts[1].Length : 0;
        var significantInteger = parts[0].TrimStart('0');
        var digits = (significantInteger.Length == 0 ? 1 : significantInteger.Length) + fractionalDigits;
        if (fractionalDigits > scale || digits > precision)
            throw new ArgumentException($"Decimal value '{value}' exceeds decimal({precision},{scale}) for '{column}'.", nameof(value));
    }

    private static void ValidateString(ColumnRef column, string value)
    {
        if (!IsWellFormedUtf16(value))
            throw new ArgumentException("Query strings must contain well-formed UTF-16.", nameof(value));
        if (column.MaxLength is int maxLength && value.Length > maxLength)
            throw new ArgumentException($"String value exceeds the declared length {maxLength} for '{column}'.", nameof(value));
    }

    internal static bool IsWellFormedUtf16(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (!char.IsSurrogate(value[index]))
                continue;
            if (!char.IsHighSurrogate(value[index]) || index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                return false;
            index++;
        }
        return true;
    }

    private static string CanonicalValue(object? value, QueryConstantKind kind) => kind switch
    {
        QueryConstantKind.Boolean => ((bool)value!).ToString().ToLowerInvariant(),
        QueryConstantKind.Int32 or QueryConstantKind.Int64 or QueryConstantKind.Decimal => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        QueryConstantKind.Double => ((double)value!).ToString("R", CultureInfo.InvariantCulture),
        QueryConstantKind.String => Hex(value as string ?? string.Empty),
        QueryConstantKind.DateTimeOffset => ((DateTimeOffset)value!).UtcTicks.ToString(CultureInfo.InvariantCulture),
        QueryConstantKind.Guid => ((Guid)value!).ToString("D").ToLowerInvariant(),
        QueryConstantKind.Binary => Convert.ToBase64String((byte[])value!),
        _ => "null"
    };

    private static string Hex(string value)
    {
        var builder = new StringBuilder(value.Length * 2);
        foreach (var item in Encoding.UTF8.GetBytes(value))
            builder.Append(item.ToString("X2", CultureInfo.InvariantCulture));
        return builder.ToString();
    }
}

public sealed record Bound
{
    private Bound(QueryConstant value, bool inclusive)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
        IsInclusive = inclusive;
    }

    public QueryConstant Value { get; }
    public bool IsInclusive { get; }

    public static Bound Inclusive(QueryConstant value) => new(value, true);
    public static Bound Exclusive(QueryConstant value) => new(value, false);
}

public sealed record ElementSetRef
{
    public ElementSetRef(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("An element set name cannot be blank.", nameof(name));
        Name = name;
    }

    public ElementSetRef(string name, QueryType type)
        : this(name)
    {
        if (!Enum.IsDefined(typeof(QueryType), type))
            throw new ArgumentOutOfRangeException(nameof(type));
        Type = type;
    }

    public string Name { get; }
    public QueryType? Type { get; }
}

public enum SetQuantifier
{
    Any,
    All
}

public enum Anchor
{
    Contains,
    EndsWith
}

public enum CompareOp
{
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual
}
