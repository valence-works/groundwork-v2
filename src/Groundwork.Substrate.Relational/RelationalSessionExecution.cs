using System.Data.Common;
using Groundwork.Store;

namespace Groundwork.Substrate.Relational;

/// <summary>
/// Owns the relational session execution lifecycle: connection serialization, direct-write
/// transactions, nested-write refusal, cleanup, and provider-neutral concurrency translation.
/// </summary>
internal sealed class RelationalSessionExecution
{
    private readonly StorageAccess access;
    private readonly DbTransaction? ambientTransaction;
    private readonly bool ownsConnection;
    private readonly IRelationalSessionExecutionAdapter adapter;
    private readonly string objectName;
    private readonly AsyncLocal<bool> batchFallbackScope = new();
    private readonly AsyncLocal<bool> writeExecutionScope = new();
    private DbTransaction? activeTransaction;
    private bool closed;

    internal RelationalSessionExecution(
        StorageAccess access,
        DbTransaction? ambientTransaction,
        bool ownsConnection,
        IRelationalSessionExecutionAdapter adapter,
        string objectName)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectName);
        this.access = access;
        this.ambientTransaction = ambientTransaction;
        this.ownsConnection = ownsConnection;
        this.adapter = adapter;
        this.objectName = objectName;
    }

    internal DbTransaction? Transaction => activeTransaction ?? ambientTransaction;

    internal bool IsReleased => closed;

    internal void Close() => closed = true;

    internal void EnsureOpen()
    {
        adapter.EnsureUsable();
        if (closed)
            throw new ObjectDisposedException(objectName);
    }

    internal IDisposable EnterBatchFallback()
    {
        var previous = batchFallbackScope.Value;
        batchFallbackScope.Value = true;
        return new DelegateScope(() => batchFallbackScope.Value = previous);
    }

    internal async ValueTask<T> Execute<T>(
        Func<ValueTask<T>> operation,
        RelationalExecution execution)
    {
        ArgumentNullException.ThrowIfNull(operation);
        using var lease = await EnterReadGate(execution).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            return await operation().ConfigureAwait(false);
        }
        catch (RelationalConcurrencyConflictException exception) when (typeof(T) == typeof(WriteOutcome))
        {
            return (T)(object)new WriteOutcome(WriteOutcomeStatus.ConcurrencyConflict, exception.Version);
        }
    }

    internal async ValueTask<T> ExecuteWrite<T>(
        Func<ValueTask<T>> operation,
        RelationalExecution execution)
    {
        ArgumentNullException.ThrowIfNull(operation);
        StorageAccessValidation.EnsurePointOperation(access, "write");
        EnsureOpen();
        if (ambientTransaction is not null || batchFallbackScope.Value)
            return await Translate(operation).ConfigureAwait(false);
        if (writeExecutionScope.Value)
            WritePreconditionValidator.EnsureNoNestedTransaction(activeTransaction);

        using var lease = ownsConnection
            ? null
            : await adapter.EnterGate(execution).ConfigureAwait(false);
        EnsureOpen();
        WritePreconditionValidator.EnsureNoNestedTransaction(activeTransaction);
        var transaction = await adapter.BeginWrite(execution).ConfigureAwait(false);
        activeTransaction = transaction;
        var previousScope = writeExecutionScope.Value;
        writeExecutionScope.Value = true;
        try
        {
            var result = await Translate(operation).ConfigureAwait(false);
            await execution.Commit(transaction).ConfigureAwait(false);
            return result;
        }
        catch (Exception failure)
        {
            await WriteFailureCleanup.Run(
                failure,
                () => adapter.Rollback(transaction, execution)).ConfigureAwait(false);
            throw;
        }
        finally
        {
            writeExecutionScope.Value = previousScope;
            activeTransaction = null;
            await execution.Dispose(transaction).ConfigureAwait(false);
        }
    }

    private async ValueTask<IDisposable?> EnterReadGate(RelationalExecution execution)
    {
        if (ownsConnection || batchFallbackScope.Value ||
            (ambientTransaction is not null && !adapter.SerializeAmbientReads))
        {
            return null;
        }
        return await adapter.EnterGate(execution).ConfigureAwait(false);
    }

    private static async ValueTask<T> Translate<T>(Func<ValueTask<T>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (RelationalConcurrencyConflictException exception) when (typeof(T) == typeof(WriteOutcome))
        {
            return (T)(object)new WriteOutcome(WriteOutcomeStatus.ConcurrencyConflict, exception.Version);
        }
    }

    private sealed class DelegateScope(Action dispose) : IDisposable
    {
        private Action? remaining = dispose;

        public void Dispose() => Interlocked.Exchange(ref remaining, null)?.Invoke();
    }
}

internal interface IRelationalSessionExecutionAdapter
{
    bool SerializeAmbientReads { get; }

    void EnsureUsable();

    ValueTask<IDisposable> EnterGate(RelationalExecution execution);

    ValueTask<DbTransaction> BeginWrite(RelationalExecution execution);

    ValueTask Rollback(DbTransaction transaction, RelationalExecution execution);
}

internal sealed class RelationalConcurrencyConflictException(long? version = null) : Exception
{
    internal long? Version { get; } = version;
}
