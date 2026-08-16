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
}

internal static class RetentionOperationCodec
{
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
            retention.KeepNewest.ToString(CultureInfo.InvariantCulture),
            retention.OrderColumn,
            retention.Trigger.ToString(),
            .. retention.PartitionColumns,
            options.MaxRowsPerBatch.ToString(CultureInfo.InvariantCulture)
        ]);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
