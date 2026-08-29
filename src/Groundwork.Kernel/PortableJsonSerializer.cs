using System.Buffers;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Groundwork.Kernel;

/// <summary>
/// Serializes Groundwork's closed portable JSON value graph without reflection. Managed callers
/// retain the compatibility fallback for arbitrary CLR objects; Native AOT callers receive an
/// explicit refusal when no generated JSON metadata exists instead of failing unpredictably.
/// </summary>
internal static class PortableJsonSerializer
{
    public static string Serialize(object? value)
    {
        if (!IsClosedPortableValue(value, new HashSet<object>(ReferenceEqualityComparer.Instance)))
        {
            if (!JsonSerializer.IsReflectionEnabledByDefault)
            {
                throw new NotSupportedException(
                    $"Native AOT JSON supports portable scalar, dictionary, array, JsonDocument, JsonElement, " +
                    $"and JsonNode values; '{value?.GetType().FullName}' requires reflection-based JSON metadata.");
            }

            return SerializeWithReflection(value);
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteClosedPortableValue(writer, value);
            writer.Flush();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static string SerializeString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStringValue(value);
            writer.Flush();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static void WriteClosed(Utf8JsonWriter writer, object? value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (!IsClosedPortableValue(value, new HashSet<object>(ReferenceEqualityComparer.Instance)))
            throw new JsonException($"Unsupported portable JSON value type '{value?.GetType()}'.");
        WriteClosedPortableValue(writer, value);
    }

    private static bool IsClosedPortableValue(object? value, ISet<object> active)
    {
        if (value is null or string or char or bool or byte or sbyte or short or ushort or int or uint or long or ulong
            or float or double or decimal or DateTime or DateTimeOffset or Guid or byte[] or JsonDocument or JsonElement or JsonNode)
            return true;
        if (!active.Add(value))
            return false;
        try
        {
            if (value is IReadOnlyDictionary<string, object?> readOnlyDictionary)
                return readOnlyDictionary.All(entry => IsClosedPortableValue(entry.Value, active));
            if (value is IDictionary dictionary)
            {
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Key is not string || !IsClosedPortableValue(entry.Value, active))
                        return false;
                }
                return true;
            }
            if (value is IEnumerable sequence)
            {
                foreach (var item in sequence)
                {
                    if (!IsClosedPortableValue(item, active))
                        return false;
                }
                return true;
            }
            return false;
        }
        finally
        {
            active.Remove(value);
        }
    }

    private static void WriteClosedPortableValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNullValue(); return;
            case string text: writer.WriteStringValue(text); return;
            case char character: writer.WriteStringValue(character.ToString()); return;
            case bool boolean: writer.WriteBooleanValue(boolean); return;
            case byte number: writer.WriteNumberValue(number); return;
            case sbyte number: writer.WriteNumberValue(number); return;
            case short number: writer.WriteNumberValue(number); return;
            case ushort number: writer.WriteNumberValue(number); return;
            case int number: writer.WriteNumberValue(number); return;
            case uint number: writer.WriteNumberValue(number); return;
            case long number: writer.WriteNumberValue(number); return;
            case ulong number: writer.WriteNumberValue(number); return;
            case float number: writer.WriteNumberValue(number); return;
            case double number: writer.WriteNumberValue(number); return;
            case decimal number: writer.WriteNumberValue(number); return;
            case DateTime dateTime: writer.WriteStringValue(dateTime); return;
            case DateTimeOffset timestamp: writer.WriteStringValue(timestamp); return;
            case Guid guid: writer.WriteStringValue(guid); return;
            case byte[] bytes: writer.WriteBase64StringValue(bytes); return;
            case JsonDocument document: document.RootElement.WriteTo(writer); return;
            case JsonElement element: element.WriteTo(writer); return;
            case JsonNode node: node.WriteTo(writer); return;
            case IReadOnlyDictionary<string, object?> dictionary:
                writer.WriteStartObject();
                foreach (var entry in dictionary)
                {
                    writer.WritePropertyName(entry.Key);
                    WriteClosedPortableValue(writer, entry.Value);
                }
                writer.WriteEndObject();
                return;
            case IDictionary dictionary:
                writer.WriteStartObject();
                foreach (DictionaryEntry entry in dictionary)
                {
                    writer.WritePropertyName((string)entry.Key);
                    WriteClosedPortableValue(writer, entry.Value);
                }
                writer.WriteEndObject();
                return;
            case IEnumerable sequence:
                writer.WriteStartArray();
                foreach (var item in sequence)
                    WriteClosedPortableValue(writer, item);
                writer.WriteEndArray();
                return;
            default:
                throw new InvalidOperationException("The portable JSON graph changed between validation and serialization.");
        }
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "The call is guarded by JsonSerializer.IsReflectionEnabledByDefault; Native AOT uses the closed portable writer.")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "The call is guarded by JsonSerializer.IsReflectionEnabledByDefault; Native AOT uses the closed portable writer.")]
    private static string SerializeWithReflection(object? value) => JsonSerializer.Serialize(value);
}
