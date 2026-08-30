namespace Groundwork.Kernel.Schema;

/// <summary>How one data-migration pass ended.</summary>
public enum DataMigrationStatus
{
    /// <summary>The source was exhausted and the ledger records the migration as durably finished.</summary>
    Completed,

    /// <summary>The pass stopped on its budget with rows left. The ledger carries the resume cursor.</summary>
    Interrupted,

    /// <summary>The ledger already recorded this migration as finished; no row was touched.</summary>
    Replayed
}

/// <summary>Progress reported after each committed chunk.</summary>
public sealed record DataMigrationProgress(
    string MigrationId,
    long RowsScanned,
    long RowsChanged,
    int Batches,
    string? Cursor);

/// <summary>Evidence returned by one data-migration pass.</summary>
public sealed record DataMigrationRunResult(
    string MigrationId,
    DataMigrationStatus Status,
    long RowsScanned,
    long RowsChanged,
    int Batches,
    string? ResumeCursor)
{
    /// <summary>True only when the migration is durably finished; an interrupted pass is never "success".</summary>
    public bool IsComplete => Status is DataMigrationStatus.Completed or DataMigrationStatus.Replayed;

    public DataMigrationRunResult EnsureComplete()
    {
        if (IsComplete)
            return this;
        throw new DataMigrationRefusedException(
            DataMigrationCodes.Incomplete,
            $"data migration '{MigrationId}' stopped after {Batches} batches and {RowsScanned} rows with its source not exhausted; " +
            "resume it with the recorded cursor before treating the target as migrated.");
    }
}

/// <summary>
/// Provider-neutral chunked execution of one data migration. The runner owns budgets, ledger state
/// transitions, and refusals; the provider owns reading, writing, and committing a chunk atomically.
/// </summary>
public static class DataMigrationRunner
{
    /// <summary>Capabilities every data migration needs, whatever the provider is.</summary>
    public const DataMigrationCapabilities Required =
        DataMigrationCapabilities.KeysetScan |
        DataMigrationCapabilities.AtomicChunkProgress |
        DataMigrationCapabilities.AppliedLedger;

    public static DataMigrationRunResult Run(
        IDataMigrationExecutor executor,
        PhysicalSchemaTargetIdentity target,
        StorageUnit unit,
        DataMigration migration,
        DataMigrationBudget? budget = null,
        DateTimeOffset? now = null,
        IProgress<DataMigrationProgress>? progress = null) =>
        RunCore(executor, target, unit, migration, budget, now, progress, DataMigrationExecution.Synchronous)
            .GetAwaiter().GetResult();

    public static ValueTask<DataMigrationRunResult> RunAsync(
        IDataMigrationExecutor executor,
        PhysicalSchemaTargetIdentity target,
        StorageUnit unit,
        DataMigration migration,
        DataMigrationBudget? budget = null,
        DateTimeOffset? now = null,
        IProgress<DataMigrationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        RunCore(executor, target, unit, migration, budget, now, progress,
            DataMigrationExecution.Asynchronous(cancellationToken));

    /// <summary>
    /// Refuses when the provider does not advertise everything the facility promises, naming the
    /// missing capability rather than falling back to something weaker.
    /// </summary>
    public static void EnsureCapabilities(IDataMigrationExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(executor);
        var missing = Required & ~executor.Capabilities;
        if (missing == DataMigrationCapabilities.None)
            return;
        throw new DataMigrationRefusedException(
            DataMigrationCodes.MissingCapability,
            $"this provider does not advertise data-migration capability {missing}; " +
            "it cannot move data under the facility's interruption guarantees.");
    }

    private static async ValueTask<DataMigrationRunResult> RunCore(
        IDataMigrationExecutor executor,
        PhysicalSchemaTargetIdentity target,
        StorageUnit unit,
        DataMigration migration,
        DataMigrationBudget? budget,
        DateTimeOffset? now,
        IProgress<DataMigrationProgress>? progress,
        DataMigrationExecution mode)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(migration);
        var bounds = (budget ?? DataMigrationBudget.Default).Validate();
        mode.CancellationToken.ThrowIfCancellationRequested();

        var fingerprint = migration.RequestFingerprint(unit);
        var clock = now ?? DateTimeOffset.UtcNow;
        var recorded = await mode.ReadLedgerEntry(executor, target, migration.Id).ConfigureAwait(false);
        if (recorded is not null)
        {
            if (!string.Equals(recorded.RequestFingerprint, fingerprint, StringComparison.Ordinal))
            {
                throw new DataMigrationRefusedException(
                    DataMigrationCodes.RequestConflict,
                    $"data migration '{migration.Id}' on '{unit.Name}' was recorded with request fingerprint " +
                    $"'{recorded.RequestFingerprint}' but now describes '{fingerprint}'. " +
                    "Use a new semantic migration identity for a changed transform.");
            }
            if (recorded.IsComplete)
            {
                return new DataMigrationRunResult(
                    migration.Id,
                    DataMigrationStatus.Replayed,
                    recorded.RowsScanned,
                    recorded.RowsChanged,
                    recorded.Batches,
                    null);
            }
        }

        // A completed migration may be replayed after contract has retired one of its source
        // columns. Validate the live projection only while work can still be scheduled.
        var projection = migration.ValidateAgainst(unit);
        EnsureCapabilities(executor);
        DataMigrationLedgerEntry entry;
        DataMigrationCursor? cursor = null;
        if (recorded is null)
        {
            entry = DataMigrationLedgerEntry.Start(target, migration, unit, clock);
            // Recorded before the first row moves, so an interruption during the very first chunk
            // still leaves durable evidence that this migration was started and did not finish.
            await mode.WriteLedgerEntry(executor, entry).ConfigureAwait(false);
        }
        else
        {
            entry = recorded;
            if (entry.Cursor is { } canonical && !DataMigrationCursor.TryDecode(unit, canonical, out cursor))
            {
                throw new DataMigrationRefusedException(
                    DataMigrationCodes.LedgerCorrupt,
                    $"the resume cursor recorded for data migration '{migration.Id}' on '{unit.Name}' " +
                    "does not decode against that unit's declared key.");
            }
        }

        var batchesThisPass = 0;
        var rowsThisPass = 0L;
        while (true)
        {
            mode.CancellationToken.ThrowIfCancellationRequested();
            if (bounds.MaxBatches is { } maxBatches && batchesThisPass >= maxBatches)
                break;
            var admitted = bounds.MaxRowsPerBatch;
            if (bounds.MaxRows is { } maxRows)
            {
                var remaining = maxRows - rowsThisPass;
                if (remaining <= 0)
                    break;
                admitted = (int)Math.Min(admitted, remaining);
            }

            var request = new DataMigrationChunkRequest(migration, unit, entry, cursor, projection, admitted);
            var outcome = await mode.ExecuteChunk(executor, request).ConfigureAwait(false);
            var advanced = outcome.Entry;
            if (!string.Equals(advanced.MigrationId, entry.MigrationId, StringComparison.Ordinal) ||
                !string.Equals(advanced.RequestFingerprint, entry.RequestFingerprint, StringComparison.Ordinal) ||
                advanced.RowsScanned < entry.RowsScanned ||
                advanced.RowsChanged < entry.RowsChanged ||
                advanced.Batches < entry.Batches)
            {
                throw new DataMigrationRefusedException(
                    DataMigrationCodes.LedgerCorrupt,
                    $"the provider returned a data-migration entry for '{migration.Id}' that does not extend the one it was given.");
            }
            if (!outcome.IsExhausted && advanced.RowsScanned == entry.RowsScanned)
            {
                // A chunk that scans nothing and does not report the source exhausted would spin
                // forever while claiming progress.
                throw new DataMigrationRefusedException(
                    DataMigrationCodes.LedgerCorrupt,
                    $"the provider advanced data migration '{migration.Id}' without scanning a row and without reporting its source exhausted.");
            }

            rowsThisPass += advanced.RowsScanned - entry.RowsScanned;
            if (advanced.Batches != entry.Batches)
                batchesThisPass++;
            entry = advanced;
            progress?.Report(new DataMigrationProgress(
                migration.Id, entry.RowsScanned, entry.RowsChanged, entry.Batches, entry.Cursor));

            if (outcome.IsExhausted)
            {
                var completed = entry.Complete(outcome.Evidence, now ?? DateTimeOffset.UtcNow);
                await mode.WriteLedgerEntry(executor, completed).ConfigureAwait(false);
                return new DataMigrationRunResult(
                    migration.Id,
                    DataMigrationStatus.Completed,
                    completed.RowsScanned,
                    completed.RowsChanged,
                    completed.Batches,
                    null);
            }

            if (entry.Cursor is null)
            {
                throw new DataMigrationRefusedException(
                    DataMigrationCodes.LedgerCorrupt,
                    $"the provider advanced data migration '{migration.Id}' without recording a resume cursor.");
            }
            if (!DataMigrationCursor.TryDecode(unit, entry.Cursor, out cursor))
            {
                throw new DataMigrationRefusedException(
                    DataMigrationCodes.LedgerCorrupt,
                    $"the provider recorded a resume cursor for data migration '{migration.Id}' that does not decode against '{unit.Name}'.");
            }
        }

        return new DataMigrationRunResult(
            migration.Id,
            DataMigrationStatus.Interrupted,
            entry.RowsScanned,
            entry.RowsChanged,
            entry.Batches,
            entry.Cursor);
    }
}

/// <summary>
/// Selects the synchronous or the asynchronous executor surface for one shared runner body, so the
/// two entry points cannot drift into different orchestration.
/// </summary>
internal readonly struct DataMigrationExecution
{
    private DataMigrationExecution(bool isAsync, CancellationToken cancellationToken)
    {
        IsAsync = isAsync;
        CancellationToken = cancellationToken;
    }

    internal static DataMigrationExecution Synchronous { get; } = new(false, CancellationToken.None);

    internal static DataMigrationExecution Asynchronous(CancellationToken cancellationToken) =>
        new(true, cancellationToken);

    internal bool IsAsync { get; }

    internal CancellationToken CancellationToken { get; }

    /// <summary>
    /// Every recorded entry for one target, or an empty list when the provider records none at all.
    /// A provider with no data-migration execution is not an error here: it simply has no evidence,
    /// which the expand–contract gate then reports as an incomplete backfill.
    /// </summary>
    internal ValueTask<IReadOnlyList<DataMigrationLedgerEntry>> ReadLedgerEntries(
        IDataMigrationExecutor? executor,
        PhysicalSchemaTargetIdentity target)
    {
        if (executor is null)
            return new ValueTask<IReadOnlyList<DataMigrationLedgerEntry>>([]);
        return IsAsync
            ? executor.ReadLedgerEntriesAsync(target, CancellationToken)
            : new ValueTask<IReadOnlyList<DataMigrationLedgerEntry>>(executor.ReadLedgerEntries(target));
    }

    internal ValueTask<DataMigrationLedgerEntry?> ReadLedgerEntry(
        IDataMigrationExecutor executor,
        PhysicalSchemaTargetIdentity target,
        string migrationId) => IsAsync
        ? executor.ReadLedgerEntryAsync(target, migrationId, CancellationToken)
        : new(executor.ReadLedgerEntry(target, migrationId));

    internal ValueTask WriteLedgerEntry(IDataMigrationExecutor executor, DataMigrationLedgerEntry entry)
    {
        if (IsAsync)
            return executor.WriteLedgerEntryAsync(entry, CancellationToken);
        executor.WriteLedgerEntry(entry);
        return default;
    }

    internal ValueTask<DataMigrationChunkOutcome> ExecuteChunk(
        IDataMigrationExecutor executor,
        DataMigrationChunkRequest request) => IsAsync
        ? executor.ExecuteChunkAsync(request, CancellationToken)
        : new(executor.ExecuteChunk(request));
}
