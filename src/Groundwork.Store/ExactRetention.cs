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
    public bool Replayed => Status == RetentionOperationStatus.Replayed;

    public bool IsComplete => Completed;
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
    private const string ResultVersion = "1";

    internal static string Fingerprint(StorageUnit unit, RetentionExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(options);
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

    internal static string SerializeResult(RetentionOperationResult result) => string.Join(
        '|',
        ResultVersion,
        (int)result.Status,
        result.DeletedRows.ToString(CultureInfo.InvariantCulture),
        result.Batches.ToString(CultureInfo.InvariantCulture),
        result.Completed ? "1" : "0");

    internal static RetentionOperationResult DeserializeResult(string serialized)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serialized);
        var parts = serialized.Split('|');
        if (parts.Length != 5 || parts[0] != ResultVersion ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var status) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var deleted) ||
            !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var batches) ||
            parts[4] is not ("0" or "1") ||
            !Enum.IsDefined((RetentionOperationStatus)status) || deleted < 0 || batches < 0)
        {
            throw new InvalidOperationException(
                "GW-RETENTION-002: the exact retention ledger contains an invalid or unsupported result; " +
                "use a new operation nonce after repairing the ledger entry.");
        }

        return new RetentionOperationResult(
            (RetentionOperationStatus)status,
            deleted,
            batches,
            parts[4] == "1");
    }

    internal static void ValidateOperation(OperationId operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId.Nonce))
            throw new ArgumentException("An operation id requires a non-empty nonce.", nameof(operationId));
        if (operationId.Nonce.Length > 256)
            throw new ArgumentException("An operation nonce cannot exceed 256 UTF-16 code units.", nameof(operationId));
    }
}
