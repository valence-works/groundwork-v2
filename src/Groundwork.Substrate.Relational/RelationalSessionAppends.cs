using Groundwork.Kernel;
using Groundwork.Store;

namespace Groundwork.Substrate.Relational;

/// <summary>
/// Owns the provider-neutral exact-append ledger protocol. The adapter retains provider time,
/// durable claim, payload, and ledger command mechanics so the entire protocol can remain inside
/// the provider transaction selected by <see cref="RelationalSessionExecution"/>.
/// </summary>
internal sealed class RelationalSessionAppends
{
    private const string MissingExactResult =
        "GW-APPEND-002: an existing append ledger entry has no exact result; use a new operation nonce.";

    private readonly StorageUnit unit;
    private readonly StorageAccess access;
    private readonly IRelationalAppendAdapter adapter;

    internal RelationalSessionAppends(
        StorageUnit unit,
        StorageAccess access,
        IRelationalAppendAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(adapter);
        this.unit = unit;
        this.access = access;
        this.adapter = adapter;
    }

    internal RelationalAppendOperation Prepare(
        OperationId operationId,
        IReadOnlyList<StorageValues> values,
        bool exactOutcomes)
    {
        var declaration = IdempotencyRules.RequireDeclaration(unit);
        IdempotencyRules.ValidateOperation(unit, operationId, values);
        var snapshot = values.Select(value => new StorageValues(value.Values)).ToArray();
        foreach (var value in snapshot)
            WritePreconditionValidator.ValidateWrittenValues(unit, value.Values);
        return new RelationalAppendOperation(
            unit,
            declaration,
            operationId,
            snapshot,
            access.Scope?.Value ?? string.Empty,
            ExactAppendCodec.Fingerprint(unit, snapshot),
            exactOutcomes);
    }

    internal async ValueTask<RelationalAppendResult> Append(
        RelationalAppendOperation operation,
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
                return Replay(operation, existing.Fingerprint, existing.SerializedOutcomes);
            }

            await adapter.DeleteLedger(operation, existing, execution).ConfigureAwait(false);
        }

        if (!await adapter.TryClaimLedger(operation, providerNow, execution).ConfigureAwait(false))
        {
            var winner = await adapter.ReadClaimWinner(operation, execution).ConfigureAwait(false);
            return Replay(operation, winner?.Fingerprint, winner?.SerializedOutcomes);
        }

        var rowOutcomes = await adapter.InsertPayload(operation, execution).ConfigureAwait(false);
        if (rowOutcomes.Count != operation.Values.Count ||
            rowOutcomes.Any(outcome => !outcome.Outcome.Succeeded))
        {
            throw new InvalidOperationException(
                "An idempotent append payload row was not accepted; the ledger and payload were rolled back.");
        }

        var outcomes = rowOutcomes.Select(outcome => outcome.Outcome).ToArray();
        if (!await adapter.CompleteLedger(
            operation,
            ExactAppendCodec.SerializeOutcomes(outcomes),
            execution).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "The idempotent append ledger could not be completed; the ledger and payload were rolled back.");
        }
        return new RelationalAppendResult(WriteOutcomeStatus.Inserted, outcomes);
    }

    private static RelationalAppendResult Replay(
        RelationalAppendOperation operation,
        string? storedFingerprint,
        string? serializedOutcomes)
    {
        if (string.IsNullOrEmpty(storedFingerprint) || string.IsNullOrEmpty(serializedOutcomes))
        {
            if (!operation.ExactOutcomes)
                return new RelationalAppendResult(WriteOutcomeStatus.Replayed, null);
            throw new InvalidOperationException(MissingExactResult);
        }

        if (operation.ExactOutcomes &&
            !string.Equals(storedFingerprint, operation.Fingerprint, StringComparison.Ordinal))
        {
            throw new AppendIdempotencyConflictException(
                operation.Unit.Id.Value,
                operation.Scope,
                operation.OperationId.Nonce,
                storedFingerprint,
                operation.Fingerprint);
        }

        return new RelationalAppendResult(
            WriteOutcomeStatus.Replayed,
            ExactAppendCodec.DeserializeOutcomes(serializedOutcomes));
    }
}

internal sealed record RelationalAppendOperation(
    StorageUnit Unit,
    AppendIdempotencyDeclaration Declaration,
    OperationId OperationId,
    IReadOnlyList<StorageValues> Values,
    string Scope,
    string Fingerprint,
    bool ExactOutcomes);

internal sealed record RelationalAppendLedgerEntry(
    DateTimeOffset CommittedAt,
    string? Fingerprint,
    string? SerializedOutcomes);

internal sealed record RelationalAppendReplayEntry(
    string? Fingerprint,
    string? SerializedOutcomes);

internal sealed record RelationalAppendResult(
    WriteOutcomeStatus Status,
    IReadOnlyList<WriteOutcome>? Outcomes)
{
    internal AppendOutcomeReport ToReport() =>
        new(Status, Outcomes ?? throw new InvalidOperationException(
            "GW-APPEND-002: an exact append result was not recorded."));
}

internal interface IRelationalAppendAdapter
{
    ValueTask<DateTimeOffset> PrepareLedger(
        RelationalAppendOperation operation,
        RelationalExecution execution);

    ValueTask ReclaimExpired(
        RelationalAppendOperation operation,
        DateTimeOffset cutoff,
        RelationalExecution execution);

    ValueTask<RelationalAppendLedgerEntry?> ReadLedger(
        RelationalAppendOperation operation,
        RelationalExecution execution);

    ValueTask DeleteLedger(
        RelationalAppendOperation operation,
        RelationalAppendLedgerEntry existing,
        RelationalExecution execution);

    ValueTask<bool> TryClaimLedger(
        RelationalAppendOperation operation,
        DateTimeOffset providerNow,
        RelationalExecution execution);

    ValueTask<RelationalAppendReplayEntry?> ReadClaimWinner(
        RelationalAppendOperation operation,
        RelationalExecution execution);

    ValueTask<IReadOnlyList<RowWriteOutcome>> InsertPayload(
        RelationalAppendOperation operation,
        RelationalExecution execution);

    ValueTask<bool> CompleteLedger(
        RelationalAppendOperation operation,
        string serializedOutcomes,
        RelationalExecution execution);
}
