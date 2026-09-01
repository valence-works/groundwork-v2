using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Store;
using Groundwork.Substrate.Relational;
using Xunit;

namespace Groundwork.Substrate.Relational.Tests;

public sealed class RelationalSessionRetentionTests
{
    private static readonly DateTimeOffset ProviderNow = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Retention_batches_until_the_provider_reports_a_short_batch()
    {
        var adapter = new FakeRetentionAdapter { DeletedBatches = new Queue<int>([3, 3, 1]) };
        var retention = Create(adapter);
        var operation = retention.Prepare(new RetentionExecutionOptions { MaxRowsPerBatch = 3 });

        var result = await retention.Apply(operation, RelationalExecution.Synchronous);

        Assert.Equal(new RetentionResult(7, 3), result);
        Assert.Equal(["delete-batch", "delete-batch", "delete-batch"], adapter.Events);
        Assert.All(adapter.BatchOperations, item => Assert.Equal(3, item.Options.MaxRowsPerBatch));
    }

    [Fact]
    public async Task Retention_checks_cancellation_between_provider_batches()
    {
        using var cancellation = new CancellationTokenSource();
        var adapter = new FakeRetentionAdapter
        {
            DeletedBatches = new Queue<int>([2, 2]),
            AfterDelete = _ => cancellation.Cancel()
        };
        var retention = Create(adapter);
        var operation = retention.Prepare(new RetentionExecutionOptions
        {
            MaxRowsPerBatch = 2,
            CancellationToken = cancellation.Token
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await retention.Apply(operation, RelationalExecution.Synchronous));

        Assert.Single(adapter.Events);
    }

    [Fact]
    public void Preparation_validates_the_declaration_and_options_before_provider_work()
    {
        var adapter = new FakeRetentionAdapter();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Create(adapter).Prepare(new RetentionExecutionOptions { MaxRowsPerBatch = 0 }));
        Assert.Throws<InvalidOperationException>(() =>
            new RelationalSessionRetention(Unit() with { Retention = null }, StorageAccess.Global, adapter)
                .Prepare(null));

        Assert.Empty(adapter.Events);
    }

    [Fact]
    public async Task Fresh_exact_retention_claims_executes_and_completes_in_order()
    {
        var adapter = new FakeRetentionAdapter { DeletedBatches = new Queue<int>([2, 0]) };
        var retention = Create(adapter);
        var operation = retention.PrepareExact(Operation(), new RetentionExecutionOptions { MaxRowsPerBatch = 2 });

        var result = await retention.ApplyExact(operation, RelationalExecution.Synchronous);

        Assert.Equal(new RetentionOperationResult(RetentionOperationStatus.Executed, 2, 1), result);
        Assert.Equal(
            ["prepare-ledger", "reclaim", "read", "claim", "delete-batch", "delete-batch", "complete"],
            adapter.Events);
        Assert.False(string.IsNullOrWhiteSpace(adapter.CompletedResult));
    }

    [Fact]
    public async Task Exact_replay_returns_the_durable_result_without_deleting_rows()
    {
        var adapter = new FakeRetentionAdapter();
        var retention = Create(adapter);
        var operation = retention.PrepareExact(Operation(), null);
        adapter.ReadResults.Enqueue(new RelationalRetentionLedgerEntry(
            ProviderNow,
            operation.Fingerprint,
            Serialized(5, 2)));

        var result = await retention.ApplyExact(operation, RelationalExecution.Synchronous);

        Assert.Equal(RetentionOperationStatus.Replayed, result.Status);
        Assert.Equal(5, result.DeletedRows);
        Assert.Equal(["prepare-ledger", "reclaim", "read"], adapter.Events);
    }

    [Fact]
    public async Task Count_only_replay_accepts_a_legacy_v1_fingerprint_and_result()
    {
        var adapter = new FakeRetentionAdapter();
        var retention = Create(adapter);
        var operation = retention.PrepareExact(Operation("legacy"), null);
        var legacyFingerprint = SchemaFingerprint.Create(
        [
            "retention-operation-v1",
            operation.Unit.Id.Value,
            operation.Unit.Name,
            operation.Unit.Scope.ToString(),
            operation.Retention.KeepNewest.ToString(System.Globalization.CultureInfo.InvariantCulture),
            operation.Retention.Declaration.OrderColumn,
            operation.Retention.Declaration.Trigger.ToString(),
            .. operation.Retention.Declaration.PartitionColumns,
            operation.Retention.Options.MaxRowsPerBatch.ToString(System.Globalization.CultureInfo.InvariantCulture)
        ]);
        adapter.ReadResults.Enqueue(new RelationalRetentionLedgerEntry(
            ProviderNow,
            legacyFingerprint,
            Serialized(5, 2)));

        var result = await retention.ApplyExact(operation, RelationalExecution.Synchronous);

        Assert.Equal(RetentionOperationStatus.Replayed, result.Status);
        Assert.Equal(5, result.DeletedRows);
        Assert.Empty(result.AffectedKeys);
        Assert.DoesNotContain("delete-batch", adapter.Events);
    }

    [Fact]
    public async Task Exact_replay_refuses_a_different_fingerprint_or_missing_result()
    {
        var conflictAdapter = new FakeRetentionAdapter();
        var conflictRetention = Create(conflictAdapter);
        var conflict = conflictRetention.PrepareExact(Operation("conflict"), null);
        conflictAdapter.ReadResults.Enqueue(new RelationalRetentionLedgerEntry(
            ProviderNow,
            "different",
            Serialized(1, 1)));

        await Assert.ThrowsAsync<RetentionIdempotencyConflictException>(async () =>
            await conflictRetention.ApplyExact(conflict, RelationalExecution.Synchronous));

        var missingAdapter = new FakeRetentionAdapter();
        var missingRetention = Create(missingAdapter);
        var missing = missingRetention.PrepareExact(Operation("missing"), null);
        missingAdapter.ReadResults.Enqueue(new RelationalRetentionLedgerEntry(
            ProviderNow,
            missing.Fingerprint,
            null));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await missingRetention.ApplyExact(missing, RelationalExecution.Synchronous));
        Assert.StartsWith("GW-RETENTION-002:", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Expired_entry_is_deleted_before_a_fresh_claim()
    {
        var adapter = new FakeRetentionAdapter { DeletedBatches = new Queue<int>([0]) };
        var retention = Create(adapter);
        var operation = retention.PrepareExact(Operation(), null);
        adapter.ReadResults.Enqueue(new RelationalRetentionLedgerEntry(
            ProviderNow.AddMinutes(-10),
            operation.Fingerprint,
            Serialized(1, 1)));

        _ = await retention.ApplyExact(operation, RelationalExecution.Synchronous);

        Assert.Equal(
            ["prepare-ledger", "reclaim", "read", "delete-ledger", "claim", "delete-batch", "complete"],
            adapter.Events);
    }

    [Fact]
    public async Task Lost_claim_replays_the_winner_and_completion_must_be_durable()
    {
        var replayAdapter = new FakeRetentionAdapter { ClaimSucceeds = false };
        var replayRetention = Create(replayAdapter);
        var replay = replayRetention.PrepareExact(Operation("raced"), null);
        replayAdapter.WinnerResults.Enqueue(new RelationalRetentionReplayEntry(
            replay.Fingerprint,
            Serialized(3, 1)));

        var replayed = await replayRetention.ApplyExact(replay, RelationalExecution.Synchronous);

        Assert.Equal(RetentionOperationStatus.Replayed, replayed.Status);
        Assert.DoesNotContain("delete-batch", replayAdapter.Events);

        var incompleteAdapter = new FakeRetentionAdapter
        {
            DeletedBatches = new Queue<int>([0]),
            CompletionSucceeds = false
        };
        var incompleteRetention = Create(incompleteAdapter);
        var incomplete = incompleteRetention.PrepareExact(Operation("incomplete"), null);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await incompleteRetention.ApplyExact(incomplete, RelationalExecution.Synchronous));
        Assert.Contains("could not be completed", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Affected_exact_retention_does_not_publish_when_ledger_completion_fails()
    {
        var adapter = new FakeRetentionAdapter
        {
            DeletedBatches = new Queue<int>([1, 0]),
            CompletionSucceeds = false,
            AffectedValues = ["old"]
        };
        var retention = new RelationalSessionRetention(AffectedUnit(), StorageAccess.Global, adapter);
        var operation = retention.PrepareExact(Operation("affected-incomplete"), new RetentionExecutionOptions
        {
            MaxRowsPerBatch = 1,
            AffectedKeyProjection = new RetentionAffectedKeyProjection("category", 1)
        });

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await retention.ApplyExact(operation, RelationalExecution.Synchronous));

        Assert.Contains("could not be completed", failure.Message, StringComparison.Ordinal);
        Assert.Equal(
            ["prepare-ledger", "reclaim", "read", "claim", "affected", "delete-batch", "delete-batch", "complete"],
            adapter.Events);
    }

    private static RelationalSessionRetention Create(FakeRetentionAdapter adapter) =>
        new(Unit(), StorageAccess.Global, adapter);

    private static OperationId Operation(string nonce = "retention-operation") => new(ProviderNow, nonce);

    private static string Serialized(int deletedRows, int batches) =>
        $"1|0|{deletedRows}|{batches}|1";

    private static StorageUnit Unit() => new()
    {
        Id = new StorageUnitId("retention-state-machine"),
        Name = "retention_state_machine",
        Columns =
        [
            new ColumnDefinition { Name = "id", Type = PortableType.String, IsNullable = false },
            new ColumnDefinition { Name = "createdAt", Type = PortableType.DateTimeOffset, IsNullable = false }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        Retention = new RetentionDeclaration { KeepNewest = 4, OrderColumn = "createdAt" },
        RetentionIdempotency = new RetentionIdempotencyDeclaration { Window = TimeSpan.FromMinutes(5) }
    };

    private static StorageUnit AffectedUnit() => Unit() with
    {
        Columns =
        [
            new ColumnDefinition { Name = "id", Type = PortableType.String, IsNullable = false },
            new ColumnDefinition { Name = "createdAt", Type = PortableType.DateTimeOffset, IsNullable = false },
            new ColumnDefinition { Name = "category", Type = PortableType.String, IsNullable = true }
        ]
    };

    private sealed class FakeRetentionAdapter : IRelationalRetentionAdapter
    {
        internal List<string> Events { get; } = [];
        internal List<RelationalRetentionOperation> BatchOperations { get; } = [];
        internal Queue<int> DeletedBatches { get; init; } = new([0]);
        internal Queue<RelationalRetentionLedgerEntry?> ReadResults { get; } = new();
        internal Queue<RelationalRetentionReplayEntry?> WinnerResults { get; } = new();
        internal IReadOnlyList<object?> AffectedValues { get; init; } = [];
        internal Action<int>? AfterDelete { get; init; }
        internal bool ClaimSucceeds { get; init; } = true;
        internal bool CompletionSucceeds { get; init; } = true;
        internal string? CompletedResult { get; private set; }

        public ValueTask<int> DeleteBatch(
            RelationalRetentionOperation operation,
            RelationalExecution execution)
        {
            Events.Add("delete-batch");
            BatchOperations.Add(operation);
            var deleted = DeletedBatches.Dequeue();
            AfterDelete?.Invoke(deleted);
            return ValueTask.FromResult(deleted);
        }

        public ValueTask<IReadOnlyList<object?>> ReadAffectedKeys(
            RelationalExactRetentionOperation operation,
            RelationalExecution execution)
        {
            Events.Add("affected");
            return ValueTask.FromResult(AffectedValues);
        }

        public ValueTask<DateTimeOffset> PrepareLedger(
            RelationalExactRetentionOperation operation,
            RelationalExecution execution)
        {
            Events.Add("prepare-ledger");
            return ValueTask.FromResult(ProviderNow);
        }

        public ValueTask ReclaimExpired(
            RelationalExactRetentionOperation operation,
            DateTimeOffset cutoff,
            RelationalExecution execution)
        {
            Events.Add("reclaim");
            return default;
        }

        public ValueTask<RelationalRetentionLedgerEntry?> ReadLedger(
            RelationalExactRetentionOperation operation,
            RelationalExecution execution)
        {
            Events.Add("read");
            return ValueTask.FromResult(ReadResults.Count == 0 ? null : ReadResults.Dequeue());
        }

        public ValueTask DeleteLedger(
            RelationalExactRetentionOperation operation,
            RelationalRetentionLedgerEntry existing,
            RelationalExecution execution)
        {
            Events.Add("delete-ledger");
            return default;
        }

        public ValueTask<bool> TryClaimLedger(
            RelationalExactRetentionOperation operation,
            DateTimeOffset providerNow,
            RelationalExecution execution)
        {
            Events.Add("claim");
            return ValueTask.FromResult(ClaimSucceeds);
        }

        public ValueTask<RelationalRetentionReplayEntry?> ReadClaimWinner(
            RelationalExactRetentionOperation operation,
            RelationalExecution execution)
        {
            Events.Add("winner");
            return ValueTask.FromResult(WinnerResults.Count == 0 ? null : WinnerResults.Dequeue());
        }

        public ValueTask<bool> CompleteLedger(
            RelationalExactRetentionOperation operation,
            string serializedResult,
            RelationalExecution execution)
        {
            Events.Add("complete");
            CompletedResult = serializedResult;
            return ValueTask.FromResult(CompletionSucceeds);
        }
    }
}
