using Groundwork.Kernel;
using Groundwork.Store;

namespace Groundwork.Substrate.Relational;

/// <summary>
/// Owns provider-neutral retention batching and the exact-retention ledger protocol. The adapter
/// retains provider SQL, durable clock, claim mechanics, and command observation inside the
/// transaction selected by the provider session.
/// </summary>
internal sealed class RelationalSessionRetention
{
    private const string MissingExactResult =
        "GW-RETENTION-002: an existing exact retention ledger entry has no exact result; use a new operation nonce.";

    private readonly StorageUnit unit;
    private readonly StorageAccess access;
    private readonly IRelationalRetentionAdapter adapter;

    internal RelationalSessionRetention(
        StorageUnit unit,
        StorageAccess access,
        IRelationalRetentionAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(adapter);
        this.unit = unit;
        this.access = access;
        this.adapter = adapter;
    }

    internal RelationalRetentionOperation Prepare(RetentionExecutionOptions? options)
    {
        options ??= new RetentionExecutionOptions();
        RetentionSessionExtensions.ValidateExecutionOptions(options);
        var declaration = unit.Retention ??
            throw new InvalidOperationException($"Storage unit '{unit.Name}' does not declare retention.");
        return new RelationalRetentionOperation(
            unit,
            declaration,
            options with { },
            RetentionSessionExtensions.EffectiveKeepNewest(unit, options),
            access.Scope?.Value ?? string.Empty);
    }

    internal RelationalExactRetentionOperation PrepareExact(
        OperationId operationId,
        RetentionExecutionOptions? options)
    {
        var declaration = unit.RetentionIdempotency ?? throw new InvalidOperationException(
            $"Storage unit '{unit.Name}' does not declare retention idempotency; declare RetentionIdempotency before using operation-identified retention.");
        declaration.Validate(unit);
        var retention = Prepare(options);
        RetentionOperationCodec.ValidateOperation(operationId);
        RetentionAffectedKeys.Validate(unit, retention.Options);
        var scope = access.Scope?.Value ?? string.Empty;
        return new RelationalExactRetentionOperation(
            unit,
            declaration,
            retention,
            operationId,
            scope,
            RetentionOperationCodec.Fingerprint(unit, operationId, scope, retention.Options));
    }

    internal async ValueTask<RetentionResult> Apply(
        RelationalRetentionOperation operation,
        RelationalExecution execution)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var deleted = 0;
        var batches = 0;
        while (true)
        {
            operation.Options.CancellationToken.ThrowIfCancellationRequested();
            var affected = await adapter.DeleteBatch(operation, execution).ConfigureAwait(false);
            if (affected < 0 || affected > operation.Options.MaxRowsPerBatch)
            {
                throw new InvalidOperationException(
                    "A relational retention adapter reported an invalid deleted-row count.");
            }
            if (affected == 0)
                break;
            deleted += affected;
            batches++;
            if (affected < operation.Options.MaxRowsPerBatch)
                break;
        }
        return new RetentionResult(deleted, batches);
    }

    internal async ValueTask<RetentionOperationResult> ApplyExact(
        RelationalExactRetentionOperation operation,
        RelationalExecution execution)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var providerNow = await adapter.PrepareLedger(operation, execution).ConfigureAwait(false);
        var cutoff = IdempotencyRules.ReclamationCutoff(providerNow, operation.Declaration.Window);
        await adapter.ReclaimExpired(operation, cutoff, execution).ConfigureAwait(false);

        var existing = await adapter.ReadLedger(operation, execution).ConfigureAwait(false);
        if (existing is not null)
        {
            if (IdempotencyRules.IsWithinWindow(
                existing.CommittedAt,
                providerNow,
                operation.Declaration.Window))
            {
                return Replay(operation, existing.Fingerprint, existing.SerializedResult);
            }

            await adapter.DeleteLedger(operation, existing, execution).ConfigureAwait(false);
        }

        if (!await adapter.TryClaimLedger(operation, providerNow, execution).ConfigureAwait(false))
        {
            var winner = await adapter.ReadClaimWinner(operation, execution).ConfigureAwait(false);
            return Replay(operation, winner?.Fingerprint, winner?.SerializedResult);
        }

        var affectedKeys = operation.Retention.Options.AffectedKeyProjection is { } projection
            ? await ReadAffectedKeysAtomically(operation, projection, execution).ConfigureAwait(false)
            : Array.Empty<object?>();
        var retention = await Apply(operation.Retention, execution).ConfigureAwait(false);
        var result = new RetentionOperationResult(
            RetentionOperationStatus.Executed,
            retention.DeletedRows,
            retention.Batches,
            retention.Completed)
        {
            AffectedKeys = Array.AsReadOnly(affectedKeys.ToArray())
        };
        if (!await adapter.CompleteLedger(
            operation,
            RetentionOperationCodec.SerializeResult(result),
            execution).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "The exact retention ledger could not be completed; its row deletes and ledger claim were rolled back.");
        }
        return result;
    }

    private async ValueTask<IReadOnlyList<object?>> ReadAffectedKeys(
        RelationalExactRetentionOperation operation,
        RetentionAffectedKeyProjection projection,
        RelationalExecution execution)
    {
        var values = await adapter.ReadAffectedKeys(operation, execution).ConfigureAwait(false);
        return RetentionAffectedKeys.DistinctAndOrderValues(
            values,
            projection,
            projection.MaxDistinctValues);
    }

    private async ValueTask<IReadOnlyList<object?>> ReadAffectedKeysAtomically(
        RelationalExactRetentionOperation operation,
        RetentionAffectedKeyProjection projection,
        RelationalExecution execution)
    {
        // PostgreSQL intentionally retains ReadCommitted for ordinary writes. Its affected-key
        // adapter acquires a table write-intent lock immediately before capture instead, freezing
        // the victim set for the subsequent bounded delete statements without changing isolation
        // for unrelated transactions.
        if (adapter is IRelationalAffectedRetentionSnapshotAdapter snapshot)
            await snapshot.AcquireAffectedRetentionSnapshot(operation, execution).ConfigureAwait(false);
        return await ReadAffectedKeys(operation, projection, execution).ConfigureAwait(false);
    }

    private static RetentionOperationResult Replay(
        RelationalExactRetentionOperation operation,
        string? storedFingerprint,
        string? serializedResult)
    {
        if (string.IsNullOrEmpty(storedFingerprint) || string.IsNullOrEmpty(serializedResult))
            throw new InvalidOperationException(MissingExactResult);
        if (!string.Equals(storedFingerprint, operation.Fingerprint, StringComparison.Ordinal))
        {
            throw new RetentionIdempotencyConflictException(
                operation.Unit.Id.Value,
                operation.Scope,
                operation.OperationId.Nonce,
                storedFingerprint,
                operation.Fingerprint);
        }
        return RetentionOperationCodec.DeserializeResult(serializedResult) with
        {
            Status = RetentionOperationStatus.Replayed
        };
    }
}

internal sealed record RelationalRetentionOperation(
    StorageUnit Unit,
    RetentionDeclaration Declaration,
    RetentionExecutionOptions Options,
    int KeepNewest,
    string Scope);

internal sealed record RelationalExactRetentionOperation(
    StorageUnit Unit,
    RetentionIdempotencyDeclaration Declaration,
    RelationalRetentionOperation Retention,
    OperationId OperationId,
    string Scope,
    string Fingerprint);

internal sealed record RelationalRetentionLedgerEntry(
    DateTimeOffset CommittedAt,
    string? Fingerprint,
    string? SerializedResult);

internal sealed record RelationalRetentionReplayEntry(
    string? Fingerprint,
    string? SerializedResult);

internal interface IRelationalRetentionAdapter
{
    ValueTask<int> DeleteBatch(
        RelationalRetentionOperation operation,
        RelationalExecution execution);

    ValueTask<IReadOnlyList<object?>> ReadAffectedKeys(
        RelationalExactRetentionOperation operation,
        RelationalExecution execution);

    ValueTask<DateTimeOffset> PrepareLedger(
        RelationalExactRetentionOperation operation,
        RelationalExecution execution);

    ValueTask ReclaimExpired(
        RelationalExactRetentionOperation operation,
        DateTimeOffset cutoff,
        RelationalExecution execution);

    ValueTask<RelationalRetentionLedgerEntry?> ReadLedger(
        RelationalExactRetentionOperation operation,
        RelationalExecution execution);

    ValueTask DeleteLedger(
        RelationalExactRetentionOperation operation,
        RelationalRetentionLedgerEntry existing,
        RelationalExecution execution);

    ValueTask<bool> TryClaimLedger(
        RelationalExactRetentionOperation operation,
        DateTimeOffset providerNow,
        RelationalExecution execution);

    ValueTask<RelationalRetentionReplayEntry?> ReadClaimWinner(
        RelationalExactRetentionOperation operation,
        RelationalExecution execution);

    ValueTask<bool> CompleteLedger(
        RelationalExactRetentionOperation operation,
        string serializedResult,
        RelationalExecution execution);
}

internal interface IRelationalAffectedRetentionSnapshotAdapter
{
    ValueTask AcquireAffectedRetentionSnapshot(
        RelationalExactRetentionOperation operation,
        RelationalExecution execution);
}
