using Groundwork.Kernel;
using Groundwork.Store;

namespace Groundwork.Substrate.Relational;

public sealed record RelationalAppendCommand(
    StorageUnit Unit,
    AppendIdempotencyDeclaration Declaration,
    OperationId OperationId,
    IReadOnlyList<StorageValues> Values,
    string Scope,
    string Fingerprint,
    bool ExactOutcomes);

public sealed record RelationalAppendLedgerState(
    DateTimeOffset CommittedAt,
    string? Fingerprint,
    string? SerializedOutcomes);

public sealed record RelationalAppendReplayState(string? Fingerprint, string? SerializedOutcomes);

/// <summary>Native commands consumed by the shared idempotent append protocol.</summary>
public abstract class RelationalAppendAdapter : IRelationalAppendAdapter
{
    protected abstract ValueTask<DateTimeOffset> PrepareLedger(
        RelationalAppendCommand operation,
        RelationalExecution execution);

    protected abstract ValueTask ReclaimExpired(
        RelationalAppendCommand operation,
        DateTimeOffset cutoff,
        RelationalExecution execution);

    protected abstract ValueTask<RelationalAppendLedgerState?> ReadLedger(
        RelationalAppendCommand operation,
        RelationalExecution execution);

    protected abstract ValueTask DeleteLedger(
        RelationalAppendCommand operation,
        RelationalAppendLedgerState existing,
        RelationalExecution execution);

    protected abstract ValueTask<bool> TryClaimLedger(
        RelationalAppendCommand operation,
        DateTimeOffset providerNow,
        RelationalExecution execution);

    protected abstract ValueTask<RelationalAppendReplayState?> ReadClaimWinner(
        RelationalAppendCommand operation,
        RelationalExecution execution);

    protected abstract ValueTask<IReadOnlyList<RowWriteOutcome>> InsertPayload(
        RelationalAppendCommand operation,
        RelationalExecution execution);

    protected abstract ValueTask<bool> CompleteLedger(
        RelationalAppendCommand operation,
        string serializedOutcomes,
        RelationalExecution execution);

    ValueTask<DateTimeOffset> IRelationalAppendAdapter.PrepareLedger(
        RelationalAppendOperation operation,
        RelationalExecution execution) => PrepareLedger(ToPublic(operation), execution);

    ValueTask IRelationalAppendAdapter.ReclaimExpired(
        RelationalAppendOperation operation,
        DateTimeOffset cutoff,
        RelationalExecution execution) => ReclaimExpired(ToPublic(operation), cutoff, execution);

    async ValueTask<RelationalAppendLedgerEntry?> IRelationalAppendAdapter.ReadLedger(
        RelationalAppendOperation operation,
        RelationalExecution execution)
    {
        var state = await ReadLedger(ToPublic(operation), execution).ConfigureAwait(false);
        return state is null
            ? null
            : new RelationalAppendLedgerEntry(
                state.CommittedAt,
                state.Fingerprint,
                state.SerializedOutcomes);
    }

    ValueTask IRelationalAppendAdapter.DeleteLedger(
        RelationalAppendOperation operation,
        RelationalAppendLedgerEntry existing,
        RelationalExecution execution) => DeleteLedger(
            ToPublic(operation),
            new RelationalAppendLedgerState(
                existing.CommittedAt,
                existing.Fingerprint,
                existing.SerializedOutcomes),
            execution);

    ValueTask<bool> IRelationalAppendAdapter.TryClaimLedger(
        RelationalAppendOperation operation,
        DateTimeOffset providerNow,
        RelationalExecution execution) => TryClaimLedger(ToPublic(operation), providerNow, execution);

    async ValueTask<RelationalAppendReplayEntry?> IRelationalAppendAdapter.ReadClaimWinner(
        RelationalAppendOperation operation,
        RelationalExecution execution)
    {
        var state = await ReadClaimWinner(ToPublic(operation), execution).ConfigureAwait(false);
        return state is null
            ? null
            : new RelationalAppendReplayEntry(state.Fingerprint, state.SerializedOutcomes);
    }

    ValueTask<IReadOnlyList<RowWriteOutcome>> IRelationalAppendAdapter.InsertPayload(
        RelationalAppendOperation operation,
        RelationalExecution execution) => InsertPayload(ToPublic(operation), execution);

    ValueTask<bool> IRelationalAppendAdapter.CompleteLedger(
        RelationalAppendOperation operation,
        string serializedOutcomes,
        RelationalExecution execution) => CompleteLedger(ToPublic(operation), serializedOutcomes, execution);

    private static RelationalAppendCommand ToPublic(RelationalAppendOperation operation) =>
        new(
            operation.Unit,
            operation.Declaration,
            operation.OperationId,
            operation.Values,
            operation.Scope,
            operation.Fingerprint,
            operation.ExactOutcomes);
}

public sealed record RelationalRetentionCommand(
    StorageUnit Unit,
    RetentionDeclaration Declaration,
    RetentionExecutionOptions Options,
    int KeepNewest,
    string Scope);

public sealed record RelationalExactRetentionCommand(
    StorageUnit Unit,
    RetentionIdempotencyDeclaration Declaration,
    RelationalRetentionCommand Retention,
    OperationId OperationId,
    string Scope,
    string Fingerprint);

public sealed record RelationalRetentionLedgerState(
    DateTimeOffset CommittedAt,
    string? Fingerprint,
    string? SerializedResult);

public sealed record RelationalRetentionReplayState(string? Fingerprint, string? SerializedResult);

/// <summary>Native commands consumed by the shared retention and exact-retention protocols.</summary>
public abstract class RelationalRetentionAdapter : IRelationalRetentionAdapter
{
    protected abstract ValueTask<int> DeleteBatch(
        RelationalRetentionCommand operation,
        RelationalExecution execution);

    protected abstract ValueTask<DateTimeOffset> PrepareLedger(
        RelationalExactRetentionCommand operation,
        RelationalExecution execution);

    protected abstract ValueTask ReclaimExpired(
        RelationalExactRetentionCommand operation,
        DateTimeOffset cutoff,
        RelationalExecution execution);

    protected abstract ValueTask<RelationalRetentionLedgerState?> ReadLedger(
        RelationalExactRetentionCommand operation,
        RelationalExecution execution);

    protected abstract ValueTask DeleteLedger(
        RelationalExactRetentionCommand operation,
        RelationalRetentionLedgerState existing,
        RelationalExecution execution);

    protected abstract ValueTask<bool> TryClaimLedger(
        RelationalExactRetentionCommand operation,
        DateTimeOffset providerNow,
        RelationalExecution execution);

    protected abstract ValueTask<RelationalRetentionReplayState?> ReadClaimWinner(
        RelationalExactRetentionCommand operation,
        RelationalExecution execution);

    protected abstract ValueTask<bool> CompleteLedger(
        RelationalExactRetentionCommand operation,
        string serializedResult,
        RelationalExecution execution);

    ValueTask<int> IRelationalRetentionAdapter.DeleteBatch(
        RelationalRetentionOperation operation,
        RelationalExecution execution) => DeleteBatch(ToPublic(operation), execution);

    ValueTask<DateTimeOffset> IRelationalRetentionAdapter.PrepareLedger(
        RelationalExactRetentionOperation operation,
        RelationalExecution execution) => PrepareLedger(ToPublic(operation), execution);

    ValueTask IRelationalRetentionAdapter.ReclaimExpired(
        RelationalExactRetentionOperation operation,
        DateTimeOffset cutoff,
        RelationalExecution execution) => ReclaimExpired(ToPublic(operation), cutoff, execution);

    async ValueTask<RelationalRetentionLedgerEntry?> IRelationalRetentionAdapter.ReadLedger(
        RelationalExactRetentionOperation operation,
        RelationalExecution execution)
    {
        var state = await ReadLedger(ToPublic(operation), execution).ConfigureAwait(false);
        return state is null
            ? null
            : new RelationalRetentionLedgerEntry(
                state.CommittedAt,
                state.Fingerprint,
                state.SerializedResult);
    }

    ValueTask IRelationalRetentionAdapter.DeleteLedger(
        RelationalExactRetentionOperation operation,
        RelationalRetentionLedgerEntry existing,
        RelationalExecution execution) => DeleteLedger(
            ToPublic(operation),
            new RelationalRetentionLedgerState(
                existing.CommittedAt,
                existing.Fingerprint,
                existing.SerializedResult),
            execution);

    ValueTask<bool> IRelationalRetentionAdapter.TryClaimLedger(
        RelationalExactRetentionOperation operation,
        DateTimeOffset providerNow,
        RelationalExecution execution) => TryClaimLedger(ToPublic(operation), providerNow, execution);

    async ValueTask<RelationalRetentionReplayEntry?> IRelationalRetentionAdapter.ReadClaimWinner(
        RelationalExactRetentionOperation operation,
        RelationalExecution execution)
    {
        var state = await ReadClaimWinner(ToPublic(operation), execution).ConfigureAwait(false);
        return state is null
            ? null
            : new RelationalRetentionReplayEntry(state.Fingerprint, state.SerializedResult);
    }

    ValueTask<bool> IRelationalRetentionAdapter.CompleteLedger(
        RelationalExactRetentionOperation operation,
        string serializedResult,
        RelationalExecution execution) => CompleteLedger(ToPublic(operation), serializedResult, execution);

    private static RelationalRetentionCommand ToPublic(RelationalRetentionOperation operation) =>
        new(
            operation.Unit,
            operation.Declaration,
            operation.Options,
            operation.KeepNewest,
            operation.Scope);

    private static RelationalExactRetentionCommand ToPublic(RelationalExactRetentionOperation operation) =>
        new(
            operation.Unit,
            operation.Declaration,
            ToPublic(operation.Retention),
            operation.OperationId,
            operation.Scope,
            operation.Fingerprint);
}
