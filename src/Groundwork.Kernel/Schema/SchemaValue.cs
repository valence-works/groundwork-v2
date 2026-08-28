using System.Collections;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Groundwork.Kernel.Schema;

internal static class SchemaValue
{
    private static readonly IReadOnlyList<Type> PortableJsonScalarTypes =
    [
        typeof(string),
        typeof(bool),
        typeof(byte),
        typeof(sbyte),
        typeof(short),
        typeof(ushort),
        typeof(int),
        typeof(uint),
        typeof(long),
        typeof(ulong),
        typeof(float),
        typeof(double),
        typeof(decimal),
        typeof(char),
        typeof(DateTime),
        typeof(DateTimeOffset),
        typeof(Guid),
        typeof(byte[])
    ];

    internal static readonly IReadOnlyList<Type> PortableJsonDefaultClrTypes =
    [
        ..PortableJsonScalarTypes,
        typeof(JsonDocument),
        typeof(JsonElement),
        typeof(IDictionary),
        typeof(IEnumerable)
    ];

    public static object? Snapshot(object? value, PortableType type) =>
        Snapshot(value, type, new HashSet<object>(ReferenceEqualityComparer.Instance));

    internal static bool IsPortableJsonValue(object value) =>
        IsPortableJsonValue(value, new HashSet<object>(ReferenceEqualityComparer.Instance), topLevel: true);

    public static string Canonicalize(object? value, PortableType type) => type switch
    {
        PortableType.Binary when value is byte[] bytes => $"binary:{Convert.ToHexString(bytes)}",
        PortableType.Json => CanonicalJson(value, new HashSet<object>(ReferenceEqualityComparer.Instance)),
        _ => CanonicalScalar(value)
    };

    private static object? Snapshot(object? value, PortableType type, ISet<object> active)
    {
        if (value is null || IsImmutable(value))
            return value;
        if (type == PortableType.Json && value is JsonDocument document)
            return SnapshotJsonElement(document.RootElement);
        if (type == PortableType.Json && value is JsonElement element)
            return SnapshotJsonElement(element);
        if (value is byte[] bytes)
            return bytes.ToArray();
        if (type != PortableType.Json)
            throw new ArgumentException("Mutable schema defaults are supported only for binary and JSON values.", nameof(value));
        if (!active.Add(value))
            throw new ArgumentException("JSON schema defaults cannot contain reference cycles.", nameof(value));

        try
        {
            if (value is IReadOnlyDictionary<string, object?> readOnlyDictionary)
            {
                var snapshot = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var entry in readOnlyDictionary)
                    snapshot[entry.Key] = Snapshot(entry.Value, type, active);
                return snapshot;
            }

            if (value is IDictionary dictionary)
            {
                var snapshot = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Key is not string key)
                        throw new ArgumentException("JSON schema default object keys must be strings.", nameof(value));
                    snapshot[key] = Snapshot(entry.Value, type, active);
                }
                return snapshot;
            }

            if (value is IEnumerable sequence)
            {
                var snapshot = new List<object?>();
                foreach (var item in sequence)
                    snapshot.Add(Snapshot(item, type, active));
                return snapshot;
            }
        }
        finally
        {
            active.Remove(value);
        }

        throw new ArgumentException("JSON schema defaults must contain only portable scalar, object, or array values.", nameof(value));
    }

    private static string CanonicalJson(object? value, ISet<object> active)
    {
        if (value is null || IsImmutable(value))
            return CanonicalScalar(value);
        if (value is JsonDocument document)
            return CanonicalJsonElement(document.RootElement);
        if (value is JsonElement element)
            return CanonicalJsonElement(element);
        if (!active.Add(value))
            throw new ArgumentException("JSON schema defaults cannot contain reference cycles.", nameof(value));

        try
        {
            if (value is IReadOnlyDictionary<string, object?> readOnlyDictionary)
            {
                return "object:" + SchemaFingerprint.Canonicalize(readOnlyDictionary
                    .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .Select(entry => SchemaFingerprint.Canonicalize([entry.Key, CanonicalJson(entry.Value, active)])));
            }

            if (value is IDictionary dictionary)
            {
                var entries = new List<(string Key, object? Value)>();
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Key is not string key)
                        throw new ArgumentException("JSON schema default object keys must be strings.", nameof(value));
                    entries.Add((key, entry.Value));
                }
                return "object:" + SchemaFingerprint.Canonicalize(entries
                    .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .Select(entry => SchemaFingerprint.Canonicalize([entry.Key, CanonicalJson(entry.Value, active)])));
            }

            if (value is IEnumerable sequence && value is not string)
                return "array:" + SchemaFingerprint.Canonicalize(sequence.Cast<object?>().Select(item => CanonicalJson(item, active)));
        }
        finally
        {
            active.Remove(value);
        }

        throw new ArgumentException("JSON schema defaults must contain only portable scalar, object, or array values.", nameof(value));
    }

    private static object? SnapshotJsonElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when element.TryGetInt32(out var int32) => int32,
        JsonValueKind.Number when element.TryGetInt64(out var int64) => int64,
        JsonValueKind.Number when element.TryGetDecimal(out var decimalValue) => decimalValue,
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.Array => element.EnumerateArray().Select(SnapshotJsonElement).ToList(),
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(
            property => property.Name,
            property => SnapshotJsonElement(property.Value),
            StringComparer.Ordinal),
        _ => throw new ArgumentException($"Unsupported JSON schema default token '{element.ValueKind}'.", nameof(element))
    };

    private static bool IsPortableJsonValue(object value, ISet<object> active, bool topLevel)
    {
        if (topLevel && value is string text)
            return IsSerializedJsonText(text);
        if (value is float single)
            return float.IsFinite(single);
        if (value is double number)
            return double.IsFinite(number);
        if (value is JsonDocument document)
            return document.RootElement.ValueKind != JsonValueKind.Undefined;
        if (value is JsonElement element)
            return element.ValueKind != JsonValueKind.Undefined;
        if (PortableJsonScalarTypes.Contains(value.GetType()))
            return true;

        if (!active.Add(value))
            return false;

        try
        {
            if (value is IReadOnlyDictionary<string, object?> readOnlyDictionary)
                return readOnlyDictionary.Values.All(item => item is null || IsPortableJsonValue(item, active, topLevel: false));

            if (value is IDictionary dictionary)
            {
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Key is not string ||
                        entry.Value is not null && !IsPortableJsonValue(entry.Value, active, topLevel: false))
                    {
                        return false;
                    }
                }

                return true;
            }

            if (value is IEnumerable sequence)
            {
                foreach (var item in sequence)
                {
                    if (item is not null && !IsPortableJsonValue(item, active, topLevel: false))
                        return false;
                }

                return true;
            }
        }
        finally
        {
            active.Remove(value);
        }

        return false;
    }

    private static bool IsSerializedJsonText(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind != JsonValueKind.Undefined;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string CanonicalJsonElement(JsonElement element) =>
        CanonicalJson(SnapshotJsonElement(element), new HashSet<object>(ReferenceEqualityComparer.Instance));

    private static string CanonicalScalar(object? value) => value switch
    {
        null => "null",
        string text => $"string:{text}",
        bool boolean => boolean ? "boolean:true" : "boolean:false",
        byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal =>
            $"number:{Convert.ToString(value, CultureInfo.InvariantCulture)}",
        char character => $"string:{character}",
        DateTime dateTime => $"datetime:{dateTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture)}",
        DateTimeOffset timestamp => $"timestamp:{timestamp.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture)}",
        Guid guid => $"guid:{guid:D}",
        byte[] bytes => $"binary:{Convert.ToHexString(bytes)}",
        _ => throw new ArgumentException($"Unsupported schema default value type '{value.GetType()}'.", nameof(value))
    };

    private static bool IsImmutable(object value) => value is
        string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or
        float or double or decimal or char or DateTime or DateTimeOffset or Guid;

    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static ReferenceEqualityComparer Instance { get; } = new();

        public new bool Equals(object? left, object? right) => ReferenceEquals(left, right);

        public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
    }
}
