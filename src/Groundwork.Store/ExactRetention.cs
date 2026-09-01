using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;

namespace Groundwork.Store;

public enum RetentionOperationStatus
{
    Executed,
    Replayed
}

/// <summary>Immutable evidence for one operation-identified retention pass.</summary>
public sealed record RetentionOperationResult(
    RetentionOperationStatus Status,
    int DeletedRows,
    int Batches,
    bool Completed = true)
{
    private static readonly IReadOnlyList<object?> EmptyAffectedKeys =
        Array.AsReadOnly(Array.Empty<object?>());

    public bool Replayed => Status == RetentionOperationStatus.Replayed;

    public bool IsComplete => Completed;

    /// <summary>Complete, distinct, deterministic values for the optional affected-key projection.</summary>
    public IReadOnlyList<object?> AffectedKeys { get; init; } = EmptyAffectedKeys;

}

/// <summary>Refuses reuse of a retention operation identity with a different request.</summary>
public sealed class RetentionIdempotencyConflictException : InvalidOperationException
{
    public const string DiagnosticCode = "GW-RETENTION-001";

    public RetentionIdempotencyConflictException(
        string unit,
        string scope,
        string nonce,
        string storedFingerprint,
        string receivedFingerprint)
        : base(
            $"{DiagnosticCode}: retention operation '{nonce}' for unit '{unit}' and scope '{scope}' " +
            $"was already committed with request fingerprint '{storedFingerprint}', but received '{receivedFingerprint}'. " +
            "Use a new operation nonce for a different retention request.")
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

/// <summary>Optional provider capability for replay-stable operation-identified retention.</summary>
public interface IExactRetentionStorageSession
{
    RetentionOperationResult ApplyRetention(OperationId operationId, RetentionExecutionOptions? options = null);

    ValueTask<RetentionOperationResult> ApplyRetentionAsync(
        OperationId operationId,
        RetentionExecutionOptions? options = null);
}

/// <summary>Optional exact-retention extension that admits bounded affected-key projection results.</summary>
public interface IExactRetentionAffectedKeysStorageSession : IExactRetentionStorageSession
{
}

/// <summary>Public exact-retention entry points that fail clearly when a provider lacks the capability.</summary>
public static class ExactRetentionSessionExtensions
{
    public static RetentionOperationResult ApplyRetention(
        this IStorageSession session,
        OperationId operationId,
        RetentionExecutionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        StorageAccessValidation.EnsurePointOperation(session.Access, "retention");
        options ??= new RetentionExecutionOptions();
        RetentionSessionExtensions.ValidateExecutionOptions(options);
        if (options.AffectedKeyProjection is not null &&
            session is not IExactRetentionAffectedKeysStorageSession)
        {
            throw new NotSupportedException(
                "GW-RETENTION-007: this provider session does not advertise bounded affected retention keys; " +
                "inspect IExactRetentionAffectedKeysStorageSession before requesting a projection.");
        }
        if (session is not IExactRetentionStorageSession exact)
        {
            throw new NotSupportedException(
                "GW-RETENTION-003: this provider session does not advertise exact retention operations; " +
                "inspect IExactRetentionStorageSession before using operation-identified retention.");
        }

        return exact.ApplyRetention(operationId, options);
    }

    public static ValueTask<RetentionOperationResult> ApplyRetentionAsync(
        this IStorageSession session,
        OperationId operationId,
        RetentionExecutionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        StorageAccessValidation.EnsurePointOperation(session.Access, "retention");
        options ??= new RetentionExecutionOptions();
        RetentionSessionExtensions.ValidateExecutionOptions(options);
        if (options.AffectedKeyProjection is not null &&
            session is not IExactRetentionAffectedKeysStorageSession)
        {
            throw new NotSupportedException(
                "GW-RETENTION-007: this provider session does not advertise bounded affected retention keys; " +
                "inspect IExactRetentionAffectedKeysStorageSession before requesting a projection.");
        }
        if (session is not IExactRetentionStorageSession exact)
        {
            throw new NotSupportedException(
                "GW-RETENTION-003: this provider session does not advertise exact retention operations; " +
                "inspect IExactRetentionStorageSession before using operation-identified retention.");
        }

        return exact.ApplyRetentionAsync(operationId, options);
    }
}

internal static class RetentionOperationCodec
{
    private const byte ResultVersion = 2;

    internal static string Fingerprint(StorageUnit unit, OperationId operationId, string scope, RetentionExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(unit);
        RetentionOperationCodec.ValidateOperation(operationId);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(options);
        var retention = unit.Retention ?? throw new InvalidOperationException(
            $"Storage unit '{unit.Name}' does not declare retention.");
        var projection = options.AffectedKeyProjection;
        if (projection is null)
            return LegacyFingerprint(unit, options);

        var canonical = SchemaFingerprint.Canonicalize(
        [
            "retention-operation-v2",
            unit.Id.Value,
            unit.Name,
            unit.Scope.ToString(),
            scope,
            operationId.IssuedAt.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
            operationId.Nonce,
            RetentionSessionExtensions.EffectiveKeepNewest(unit, options).ToString(CultureInfo.InvariantCulture),
            retention.OrderColumn,
            retention.Trigger.ToString(),
            .. retention.PartitionColumns,
            options.MaxRowsPerBatch.ToString(CultureInfo.InvariantCulture),
            projection.Column,
            projection.MaxDistinctValues.ToString(CultureInfo.InvariantCulture)
        ]);
        return PortableHex.Lower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string LegacyFingerprint(StorageUnit unit, RetentionExecutionOptions options)
    {
        var retention = unit.Retention ?? throw new InvalidOperationException(
            $"Storage unit '{unit.Name}' does not declare retention.");
        var canonical = SchemaFingerprint.Canonicalize(
        [
            "retention-operation-v1",
            unit.Id.Value,
            unit.Name,
            unit.Scope.ToString(),
            RetentionSessionExtensions.EffectiveKeepNewest(unit, options).ToString(CultureInfo.InvariantCulture),
            retention.OrderColumn,
            retention.Trigger.ToString(),
            .. retention.PartitionColumns,
            options.MaxRowsPerBatch.ToString(CultureInfo.InvariantCulture)
        ]);
        return PortableHex.Lower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    internal static string SerializeResult(RetentionOperationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.AffectedKeys.Count == 0)
            return string.Join(
                '|',
                "1",
                (int)result.Status,
                result.DeletedRows.ToString(CultureInfo.InvariantCulture),
                result.Batches.ToString(CultureInfo.InvariantCulture),
                result.Completed ? "1" : "0");

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(ResultVersion);
        writer.Write((int)result.Status);
        writer.Write(result.DeletedRows);
        writer.Write(result.Batches);
        writer.Write(result.Completed);
        writer.Write(result.AffectedKeys.Count);
        foreach (var value in result.AffectedKeys)
            WriteValue(writer, value);
        writer.Flush();
        return Convert.ToBase64String(stream.ToArray());
    }

    internal static RetentionOperationResult DeserializeResult(string serialized)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serialized);
        try
        {
            // Version 1 was the count-only format. Keep reading it so an already committed
            // operation can still be replayed after upgrading the provider package.
            if (serialized.StartsWith("1|", StringComparison.Ordinal))
            {
                var parts = serialized.Split('|');
                if (parts.Length != 5 ||
                    !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var legacyStatus) ||
                    !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var legacyDeleted) ||
                    !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var legacyBatches) ||
                    parts[4] is not ("0" or "1") ||
                    !Enum.IsDefined((RetentionOperationStatus)legacyStatus) || legacyDeleted < 0 || legacyBatches < 0)
                    throw new InvalidDataException();
                return new RetentionOperationResult(
                    (RetentionOperationStatus)legacyStatus,
                    legacyDeleted,
                    legacyBatches,
                    parts[4] == "1");
            }

            using var stream = new MemoryStream(Convert.FromBase64String(serialized));
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
            if (reader.ReadByte() != ResultVersion)
                throw new InvalidDataException();
            var status = (RetentionOperationStatus)reader.ReadInt32();
            var deleted = reader.ReadInt32();
            var batches = reader.ReadInt32();
            var completed = reader.ReadBoolean();
            var count = reader.ReadInt32();
            if (!Enum.IsDefined(status) || deleted < 0 || batches < 0 || count < 0 || count > 1_000_000)
                throw new InvalidDataException();
            var values = new object?[count];
            for (var index = 0; index < values.Length; index++)
                values[index] = ReadValue(reader);
            if (stream.Position != stream.Length)
                throw new InvalidDataException();
            return new RetentionOperationResult(status, deleted, batches, completed)
            {
                AffectedKeys = Array.AsReadOnly(values)
            };
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or EndOfStreamException or InvalidDataException or IOException or ArgumentException)
        {
            throw new InvalidOperationException(
                "GW-RETENTION-002: the exact retention ledger contains an invalid or unsupported result; " +
                "use a new operation nonce after repairing the ledger entry.", exception);
        }
    }

    private static void WriteValue(BinaryWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.Write((byte)0);
                break;
            case string text:
                writer.Write((byte)1);
                writer.Write(text);
                break;
            case int number:
                writer.Write((byte)2);
                writer.Write(number);
                break;
            case long number:
                writer.Write((byte)3);
                writer.Write(number);
                break;
            case decimal number:
                writer.Write((byte)4);
                writer.Write(number);
                break;
            case bool boolean:
                writer.Write((byte)5);
                writer.Write(boolean);
                break;
            case DateTimeOffset instant:
                writer.Write((byte)6);
                writer.Write(instant.ToUniversalTime().Ticks);
                break;
            case Guid guid:
                writer.Write((byte)7);
                writer.Write(guid.ToByteArray());
                break;
            case byte[] bytes:
                writer.Write((byte)8);
                writer.Write(bytes.Length);
                writer.Write(bytes);
                break;
            default:
                throw new InvalidDataException($"Unsupported affected-key value type '{value.GetType().FullName}'.");
        }
    }

    private static object? ReadValue(BinaryReader reader) => reader.ReadByte() switch
    {
        0 => null,
        1 => reader.ReadString(),
        2 => reader.ReadInt32(),
        3 => reader.ReadInt64(),
        4 => reader.ReadDecimal(),
        5 => reader.ReadBoolean(),
        6 => new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero),
        7 => new Guid(reader.ReadBytes(16)),
        8 => ReadBytes(reader),
        _ => throw new InvalidDataException()
    };

    private static byte[] ReadBytes(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > 16 * 1024 * 1024)
            throw new InvalidDataException();
        var bytes = reader.ReadBytes(count);
        if (bytes.Length != count)
            throw new EndOfStreamException();
        return bytes;
    }

    internal static void ValidateOperation(OperationId operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId.Nonce))
            throw new ArgumentException("An operation id requires a non-empty nonce.", nameof(operationId));
        if (operationId.Nonce.Length > 256)
            throw new ArgumentException("An operation nonce cannot exceed 256 UTF-16 code units.", nameof(operationId));
    }
}
