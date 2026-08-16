using System.Buffers.Binary;
using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Groundwork.Kernel;

namespace Groundwork.Store;

/// <summary>Exact per-row evidence returned by an idempotent append.</summary>
public sealed class AppendOutcomeReport
{
    public AppendOutcomeReport(
        WriteOutcomeStatus status,
        IReadOnlyList<WriteOutcome> outcomes)
    {
        if (status is not (WriteOutcomeStatus.Inserted or WriteOutcomeStatus.Replayed))
            throw new ArgumentOutOfRangeException(nameof(status), "An exact append report must be inserted or replayed.");
        ArgumentNullException.ThrowIfNull(outcomes);
        if (outcomes.Count == 0)
            throw new ArgumentException("An exact append report must contain at least one row outcome.", nameof(outcomes));
        if (outcomes.Any(outcome => outcome is null || !outcome.Succeeded))
            throw new ArgumentException("An exact append report can contain only successful row outcomes.", nameof(outcomes));
        Outcomes = Array.AsReadOnly(outcomes.ToArray());
        Status = status;
    }

    /// <summary>The operation status. A replay carries the original per-row outcomes.</summary>
    public WriteOutcomeStatus Status { get; }

    /// <summary>Ordered evidence corresponding one-for-one with the input append values.</summary>
    public IReadOnlyList<WriteOutcome> Outcomes { get; }

    public bool Replayed => Status == WriteOutcomeStatus.Replayed;

    public bool Succeeded => Status is WriteOutcomeStatus.Inserted or WriteOutcomeStatus.Replayed;
}

/// <summary>Refuses reuse of an append operation identity with a different canonical payload.</summary>
public sealed class AppendIdempotencyConflictException : InvalidOperationException
{
    public const string DiagnosticCode = "GW-APPEND-001";

    public AppendIdempotencyConflictException(
        string unit,
        string scope,
        string nonce,
        string storedFingerprint,
        string receivedFingerprint)
        : base(
            $"{DiagnosticCode}: append operation '{nonce}' for unit '{unit}' and scope '{scope}' " +
            $"was already committed with payload fingerprint '{storedFingerprint}', but received '{receivedFingerprint}'. " +
            "Use a new operation nonce for a different payload.")
    {
        Unit = unit;
        Scope = scope;
        Nonce = nonce;
        StoredFingerprint = storedFingerprint;
        ReceivedFingerprint = receivedFingerprint;
    }

    public string Unit { get; }

    public string Scope { get; }

    public string Nonce { get; }

    public string StoredFingerprint { get; }

    public string ReceivedFingerprint { get; }
}

/// <summary>Versioned provider-neutral canonical state for exact append idempotency.</summary>
internal static class ExactAppendCodec
{
    private const byte Version = 1;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    internal static string Fingerprint(StorageUnit unit, IReadOnlyList<StorageValues> values)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(values);
        var types = unit.Columns.ToDictionary(column => column.Name, column => column.Type, StringComparer.Ordinal);
        var writer = new BufferWriter();
        writer.WriteByte(Version);
        writer.WriteInt32(values.Count);
        foreach (var value in values)
            WriteMap(writer, value.Values, types);
        return Convert.ToHexStringLower(SHA256.HashData(writer.ToArray()));
    }

    internal static string FingerprintRowWrite(
        StorageUnit unit,
        RowWriteMode mode,
        StorageKey key,
        IReadOnlyDictionary<string, object?> expectedValues,
        WriteOptions options)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(expectedValues);
        ArgumentNullException.ThrowIfNull(options);
        var types = unit.Columns.ToDictionary(column => column.Name, column => column.Type, StringComparer.Ordinal);
        var writer = new BufferWriter();
        writer.WriteByte(Version);
        writer.WriteString(unit.Id.Value);
        writer.WriteInt32((int)mode);
        writer.WriteInt32((int)options.Precondition.Kind);
        writer.WriteBoolean(options.Precondition.Version.HasValue);
        if (options.Precondition.Version is { } version)
            writer.WriteInt64(version);
        WriteMap(writer, key.Values, types);
        WriteMap(writer, expectedValues, types);
        return Convert.ToHexStringLower(SHA256.HashData(writer.ToArray()));
    }

    internal static string SerializeOutcomes(IReadOnlyList<WriteOutcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        var writer = new BufferWriter();
        writer.WriteByte(Version);
        writer.WriteInt32(outcomes.Count);
        foreach (var outcome in outcomes)
        {
            writer.WriteInt32((int)outcome.Status);
            writer.WriteBoolean(outcome.Version.HasValue);
            if (outcome.Version is { } version)
                writer.WriteInt64(version);
            WriteMap(writer, outcome.GeneratedValues);
        }
        return Convert.ToBase64String(writer.ToArray());
    }

    internal static IReadOnlyList<WriteOutcome> DeserializeOutcomes(string serialized)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serialized);
        var reader = new BufferReader(Convert.FromBase64String(serialized));
        if (reader.ReadByte() != Version)
            throw new InvalidOperationException("The exact append result ledger uses an unsupported encoding version.");
        var count = reader.ReadCount();
        var outcomes = new WriteOutcome[count];
        for (var index = 0; index < outcomes.Length; index++)
        {
            var status = (WriteOutcomeStatus)reader.ReadInt32();
            if (!Enum.IsDefined(status))
                throw new InvalidOperationException("The exact append result ledger contains an unknown write outcome status.");
            var version = reader.ReadBoolean() ? reader.ReadInt64() : (long?)null;
            outcomes[index] = new WriteOutcome(status, version, generatedValues: ReadMap(reader));
        }
        reader.EnsureEnd();
        return outcomes;
    }

    private static void WriteMap(
        BufferWriter writer,
        IReadOnlyDictionary<string, object?> values,
        IReadOnlyDictionary<string, PortableType>? types = null)
    {
        writer.WriteInt32(values.Count);
        foreach (var pair in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            writer.WriteString(pair.Key);
            WriteValue(writer, pair.Value, types?.GetValueOrDefault(pair.Key));
        }
    }

    private static Dictionary<string, object?> ReadMap(BufferReader reader)
    {
        var count = reader.ReadCount();
        var values = new Dictionary<string, object?>(count, StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
            values.Add(reader.ReadString(), ReadValue(reader));
        return values;
    }

    private static void WriteValue(BufferWriter writer, object? value, PortableType? declaredType = null)
    {
        if (value is null)
        {
            writer.WriteByte(0);
            return;
        }

        if (declaredType is { } type)
        {
            switch (type)
            {
                case PortableType.String when value is string text:
                    writer.WriteByte(1);
                    writer.WriteString(text);
                    return;
                case PortableType.Int32 when value is int number:
                    writer.WriteByte(8);
                    writer.WriteInt32(number);
                    return;
                case PortableType.Int64 when value is int or long:
                    writer.WriteByte(10);
                    writer.WriteInt64(value is int intNumber ? intNumber : (long)value);
                    return;
                case PortableType.Decimal when value is decimal or byte or sbyte or short or ushort or int or uint or long or ulong:
                    writer.WriteByte(14);
                    foreach (var part in decimal.GetBits(CanonicalDecimal(Convert.ToDecimal(value, CultureInfo.InvariantCulture))))
                        writer.WriteInt32(part);
                    return;
                case PortableType.Boolean when value is bool boolean:
                    writer.WriteByte(boolean ? (byte)3 : (byte)2);
                    return;
                case PortableType.DateTimeOffset when value is DateTimeOffset timestamp:
                    writer.WriteByte(18);
                    writer.WriteInt64(timestamp.UtcTicks);
                    return;
                case PortableType.Guid when value is Guid guid:
                    writer.WriteByte(16);
                    writer.WriteBytes(guid.ToByteArray());
                    return;
                case PortableType.Binary when value is byte[] bytes:
                    writer.WriteByte(19);
                    writer.WriteBytes(bytes);
                    return;
                case PortableType.Json:
                    writer.WriteByte(20);
                    writer.WriteString(CanonicalJsonValue(value));
                    return;
                default:
                    throw new ArgumentException($"Value '{value?.GetType().FullName ?? "null"}' cannot be encoded as declared {type}.", nameof(value));
            }
        }

        switch (value)
        {
            case null:
                writer.WriteByte(0);
                return;
            case string text:
                writer.WriteByte(1);
                writer.WriteString(text);
                return;
            case bool boolean:
                writer.WriteByte(boolean ? (byte)3 : (byte)2);
                return;
            case sbyte number:
                writer.WriteByte(4);
                writer.WriteInt32(number);
                return;
            case byte number:
                writer.WriteByte(5);
                writer.WriteInt32(number);
                return;
            case short number:
                writer.WriteByte(6);
                writer.WriteInt32(number);
                return;
            case ushort number:
                writer.WriteByte(7);
                writer.WriteInt32(number);
                return;
            case int number:
                writer.WriteByte(8);
                writer.WriteInt32(number);
                return;
            case uint number:
                writer.WriteByte(9);
                writer.WriteUInt64(number);
                return;
            case long number:
                writer.WriteByte(10);
                writer.WriteInt64(number);
                return;
            case ulong number:
                writer.WriteByte(11);
                writer.WriteUInt64(number);
                return;
            case float number:
                writer.WriteByte(12);
                writer.WriteInt32(BitConverter.SingleToInt32Bits(number));
                return;
            case double number:
                writer.WriteByte(13);
                writer.WriteInt64(BitConverter.DoubleToInt64Bits(number));
                return;
            case decimal number:
                writer.WriteByte(14);
                foreach (var part in decimal.GetBits(CanonicalDecimal(number)))
                    writer.WriteInt32(part);
                return;
            case char character:
                writer.WriteByte(15);
                writer.WriteInt32(character);
                return;
            case Guid guid:
                writer.WriteByte(16);
                writer.WriteBytes(guid.ToByteArray());
                return;
            case DateTime dateTime:
                writer.WriteByte(17);
                writer.WriteInt64(dateTime.Ticks);
                writer.WriteInt32((int)dateTime.Kind);
                return;
            case DateTimeOffset timestamp:
                writer.WriteByte(18);
                writer.WriteInt64(timestamp.UtcTicks);
                return;
            case byte[] bytes:
                writer.WriteByte(19);
                writer.WriteBytes(bytes);
                return;
            case JsonDocument document:
                writer.WriteByte(20);
                writer.WriteString(CanonicalJson(document.RootElement));
                return;
            case JsonElement element:
                writer.WriteByte(20);
                writer.WriteString(CanonicalJson(element));
                return;
            case JsonNode node:
                writer.WriteByte(20);
                using (var parsed = JsonDocument.Parse(node.ToJsonString()))
                    writer.WriteString(CanonicalJson(parsed.RootElement));
                return;
            case IReadOnlyDictionary<string, object?> dictionary:
                writer.WriteByte(21);
                WriteMap(writer, dictionary);
                return;
            case IDictionary dictionary:
                writer.WriteByte(21);
                WriteMap(writer, dictionary.Cast<DictionaryEntry>().ToDictionary(
                    entry => entry.Key as string ?? throw new ArgumentException("Exact append values require string dictionary keys."),
                    entry => entry.Value,
                    StringComparer.Ordinal));
                return;
            case IEnumerable sequence when value is not string:
                writer.WriteByte(22);
                var items = sequence.Cast<object?>().ToArray();
                writer.WriteInt32(items.Length);
                foreach (var item in items)
                    WriteValue(writer, item);
                return;
            default:
                throw new ArgumentException(
                    $"Exact append values do not support '{value.GetType().FullName}' in the canonical encoding.",
                    nameof(value));
        }
    }

    private static object? ReadValue(BufferReader reader) => reader.ReadByte() switch
    {
        0 => null,
        1 => reader.ReadString(),
        2 => false,
        3 => true,
        4 => checked((sbyte)reader.ReadInt32()),
        5 => checked((byte)reader.ReadInt32()),
        6 => checked((short)reader.ReadInt32()),
        7 => checked((ushort)reader.ReadInt32()),
        8 => reader.ReadInt32(),
        9 => checked((uint)reader.ReadUInt64()),
        10 => reader.ReadInt64(),
        11 => reader.ReadUInt64(),
        12 => BitConverter.Int32BitsToSingle(reader.ReadInt32()),
        13 => BitConverter.Int64BitsToDouble(reader.ReadInt64()),
        14 => new decimal([reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32()]),
        15 => checked((char)reader.ReadInt32()),
        16 => new Guid(reader.ReadBytes(16)),
        17 => new DateTime(reader.ReadInt64(), (DateTimeKind)reader.ReadInt32()),
        18 => new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero),
        19 => reader.ReadBytes(),
        20 => ReadJson(reader.ReadString()),
        21 => ReadMap(reader),
        22 => ReadArray(reader),
        var tag => throw new InvalidOperationException($"The exact append result ledger contains unknown value tag '{tag}'.")
    };

    private static object?[] ReadArray(BufferReader reader)
    {
        var count = reader.ReadCount();
        var values = new object?[count];
        for (var index = 0; index < count; index++)
            values[index] = ReadValue(reader);
        return values;
    }

    private static JsonElement ReadJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string CanonicalJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null => "null",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.String => SerializeJsonString(element.GetString()!),
        JsonValueKind.Number => CanonicalJsonNumber(element.GetRawText()),
        JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(CanonicalJson)) + "]",
        JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject()
            .Select((property, index) => (property, index))
            .OrderBy(item => item.property.Name, StringComparer.Ordinal)
            .ThenBy(item => item.index)
            .Select(item => SerializeJsonString(item.property.Name) + ":" + CanonicalJson(item.property.Value))) + "}",
        _ => throw new ArgumentOutOfRangeException(nameof(element))
    };

    private static string CanonicalJsonValue(object? value)
    {
        // Provider mappings treat strings in a Json column as already serialized
        // JSON text. Parse them before canonicalization so a raw JSON string and
        // its JsonElement/object representation have the same fingerprint.
        if (value is string text)
        {
            using var parsedText = JsonDocument.Parse(text);
            return CanonicalJson(parsedText.RootElement);
        }
        if (value is JsonDocument document)
            return CanonicalJson(document.RootElement);
        if (value is JsonElement element)
            return CanonicalJson(element);
        if (value is JsonNode node)
        {
            using var parsedNode = JsonDocument.Parse(node.ToJsonString());
            return CanonicalJson(parsedNode.RootElement);
        }

        using var parsed = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return CanonicalJson(parsed.RootElement);
    }

    private static string SerializeJsonString(string value)
    {
        StrictUtf8.GetBytes(value);
        return JsonSerializer.Serialize(value);
    }

    private static decimal CanonicalDecimal(decimal value)
    {
        var bits = decimal.GetBits(value);
        var coefficient = (BigInteger)(uint)bits[0]
            | ((BigInteger)(uint)bits[1] << 32)
            | ((BigInteger)(uint)bits[2] << 64);
        var scale = (bits[3] >> 16) & 0x7F;
        while (scale > 0 && coefficient % 10 == 0)
        {
            coefficient /= 10;
            scale--;
        }

        return new decimal(
            (int)(uint)(coefficient & uint.MaxValue),
            (int)(uint)((coefficient >> 32) & uint.MaxValue),
            (int)(uint)((coefficient >> 64) & uint.MaxValue),
            bits[3] < 0 && coefficient != 0,
            (byte)scale);
    }

    private static string CanonicalJsonNumber(string raw)
    {
        var signOffset = raw.Length > 0 && raw[0] == '-' ? 1 : 0;
        var exponentMarker = raw.IndexOf('e', signOffset);
        if (exponentMarker < 0)
            exponentMarker = raw.IndexOf('E', signOffset);
        var mantissaEnd = exponentMarker < 0 ? raw.Length : exponentMarker;
        var decimalPoint = raw.IndexOf('.', signOffset, mantissaEnd - signOffset);
        var fractionDigits = decimalPoint < 0 ? 0 : mantissaEnd - decimalPoint - 1;
        var digits = raw[signOffset..mantissaEnd].Replace(".", string.Empty, StringComparison.Ordinal);
        var firstNonZero = 0;
        while (firstNonZero < digits.Length && digits[firstNonZero] == '0')
            firstNonZero++;
        if (firstNonZero == digits.Length)
            return "0";

        digits = digits[firstNonZero..];
        var trailingZeros = digits.Length - 1;
        while (trailingZeros > 0 && digits[trailingZeros] == '0')
            trailingZeros--;
        var removedTrailingZeros = digits.Length - 1 - trailingZeros;
        digits = digits[..(trailingZeros + 1)];

        var exponent = exponentMarker < 0
            ? BigInteger.Zero
            : BigInteger.Parse(raw[(exponentMarker + 1)..], CultureInfo.InvariantCulture);
        var scientificExponent = exponent - fractionDigits + removedTrailingZeros + digits.Length - 1;
        var mantissa = digits.Length == 1 ? digits : digits[0] + "." + digits[1..];
        return (signOffset == 1 ? "-" : string.Empty) + mantissa + "e" + scientificExponent.ToString(CultureInfo.InvariantCulture);
    }

    private sealed class BufferWriter
    {
        private readonly MemoryStream stream = new();

        internal void WriteByte(byte value) => stream.WriteByte(value);

        internal void WriteBoolean(bool value) => WriteByte(value ? (byte)1 : (byte)0);

        internal void WriteInt32(int value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            stream.Write(bytes);
        }

        internal void WriteInt64(long value)
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(bytes, value);
            stream.Write(bytes);
        }

        internal void WriteUInt64(ulong value)
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
            stream.Write(bytes);
        }

        internal void WriteString(string value)
        {
            var bytes = StrictUtf8.GetBytes(value);
            WriteInt32(bytes.Length);
            stream.Write(bytes);
        }

        internal void WriteBytes(byte[] value)
        {
            WriteInt32(value.Length);
            stream.Write(value);
        }

        internal byte[] ToArray() => stream.ToArray();
    }

    private sealed class BufferReader
    {
        private readonly byte[] buffer;
        private int position;

        internal BufferReader(byte[] buffer) => this.buffer = buffer;

        internal byte ReadByte() => position < buffer.Length
            ? buffer[position++]
            : throw new InvalidOperationException("The exact append result ledger is truncated.");

        internal bool ReadBoolean() => ReadByte() switch
        {
            0 => false,
            1 => true,
            _ => throw new InvalidOperationException("The exact append result ledger contains an invalid boolean.")
        };

        internal int ReadInt32()
        {
            Ensure(4);
            var value = BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(position, 4));
            position += 4;
            return value;
        }

        internal long ReadInt64()
        {
            Ensure(8);
            var value = BinaryPrimitives.ReadInt64BigEndian(buffer.AsSpan(position, 8));
            position += 8;
            return value;
        }

        internal ulong ReadUInt64()
        {
            Ensure(8);
            var value = BinaryPrimitives.ReadUInt64BigEndian(buffer.AsSpan(position, 8));
            position += 8;
            return value;
        }

        internal int ReadCount()
        {
            var value = ReadInt32();
            if (value < 0 || value > 1_000_000)
                throw new InvalidOperationException("The exact append result ledger contains an invalid collection length.");
            return value;
        }

        internal string ReadString()
        {
            var bytes = ReadBytes();
            return StrictUtf8.GetString(bytes);
        }

        internal byte[] ReadBytes()
        {
            var count = ReadInt32();
            if (count < 0 || count > buffer.Length - position)
                throw new InvalidOperationException("The exact append result ledger contains an invalid value length.");
            var value = buffer.AsSpan(position, count).ToArray();
            position += count;
            return value;
        }

        internal byte[] ReadBytes(int count)
        {
            if (count < 0 || buffer.Length - position < count)
                throw new InvalidOperationException("The exact append result ledger contains an invalid value length.");
            var value = buffer.AsSpan(position, count).ToArray();
            position += count;
            return value;
        }

        internal void EnsureEnd()
        {
            if (position != buffer.Length)
                throw new InvalidOperationException("The exact append result ledger contains trailing bytes.");
        }

        private void Ensure(int count)
        {
            if (buffer.Length - position < count)
                throw new InvalidOperationException("The exact append result ledger is truncated.");
        }
    }
}
