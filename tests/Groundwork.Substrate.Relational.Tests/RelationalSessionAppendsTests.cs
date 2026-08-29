using Groundwork.Kernel;
using Groundwork.Store;
using Groundwork.Substrate.Relational;
using Xunit;

namespace Groundwork.Substrate.Relational.Tests;

public sealed class RelationalSessionAppendsTests
{
    private static readonly DateTimeOffset ProviderNow = new(2026, 8, 30, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Fresh_append_claims_writes_and_completes_the_ledger_in_order()
    {
        var adapter = new FakeAppendAdapter();
        var appends = Create(adapter);
        var operation = appends.Prepare(Operation(), Values("one"), exactOutcomes: true);

        var result = await appends.Append(operation, RelationalExecution.Synchronous);

        Assert.Equal(WriteOutcomeStatus.Inserted, result.Status);
        Assert.Equal(["prepare", "reclaim", "read", "claim", "payload", "complete"], adapter.Events);
        Assert.False(string.IsNullOrWhiteSpace(adapter.CompletedResult));
        Assert.Equal(WriteOutcomeStatus.Inserted, result.ToReport().Status);
    }

    [Fact]
    public async Task Exact_replay_returns_original_outcomes_without_claiming_or_writing()
    {
        var adapter = new FakeAppendAdapter();
        var appends = Create(adapter);
        var operation = appends.Prepare(Operation(), Values("one"), exactOutcomes: true);
        adapter.ReadResults.Enqueue(Entry(operation.Fingerprint, await SerializedOutcome(), ProviderNow));

        var result = await appends.Append(operation, RelationalExecution.Synchronous);

        Assert.Equal(WriteOutcomeStatus.Replayed, result.Status);
        Assert.Equal(["prepare", "reclaim", "read"], adapter.Events);
        Assert.Equal(7, Assert.Single(result.Outcomes!).Version);
    }

    [Fact]
    public async Task Non_exact_replay_does_not_compare_the_payload_fingerprint()
    {
        var adapter = new FakeAppendAdapter();
        var appends = Create(adapter);
        var operation = appends.Prepare(Operation(), Values("one"), exactOutcomes: false);
        adapter.ReadResults.Enqueue(Entry("different", await SerializedOutcome(), ProviderNow));

        var result = await appends.Append(operation, RelationalExecution.Synchronous);

        Assert.Equal(WriteOutcomeStatus.Replayed, result.Status);
        Assert.Equal(7, Assert.Single(result.Outcomes!).Version);
        Assert.Equal(["prepare", "reclaim", "read"], adapter.Events);
    }

    [Fact]
    public async Task Exact_replay_refuses_a_different_payload_fingerprint()
    {
        var adapter = new FakeAppendAdapter();
        var appends = Create(adapter);
        var operation = appends.Prepare(Operation(), Values("one"), exactOutcomes: true);
        adapter.ReadResults.Enqueue(Entry("different", await SerializedOutcome(), ProviderNow));

        var failure = await Assert.ThrowsAsync<AppendIdempotencyConflictException>(async () =>
            await appends.Append(operation, RelationalExecution.Synchronous));

        Assert.Equal("different", failure.StoredFingerprint);
        Assert.Equal(operation.Fingerprint, failure.ReceivedFingerprint);
        Assert.Equal(["prepare", "reclaim", "read"], adapter.Events);
    }

    [Fact]
    public async Task Expired_entry_is_deleted_before_a_fresh_claim()
    {
        var adapter = new FakeAppendAdapter();
        var appends = Create(adapter);
        var operation = appends.Prepare(Operation(), Values("one"), exactOutcomes: true);
        adapter.ReadResults.Enqueue(Entry(operation.Fingerprint, await SerializedOutcome(), ProviderNow.AddMinutes(-5)));

        var result = await appends.Append(operation, RelationalExecution.Synchronous);

        Assert.Equal(WriteOutcomeStatus.Inserted, result.Status);
        Assert.Equal(["prepare", "reclaim", "read", "delete", "claim", "payload", "complete"], adapter.Events);
    }

    [Fact]
    public async Task Multi_row_outcomes_round_trip_in_input_order_with_generated_values()
    {
        var values = new[]
        {
            new StorageValues(new Dictionary<string, object?> { ["id"] = "one", ["payload"] = "first" }),
            new StorageValues(new Dictionary<string, object?> { ["id"] = "two", ["payload"] = "second" })
        };
        var writer = new FakeAppendAdapter
        {
            PayloadOutcomes =
            [
                new RowWriteOutcome(
                    RowWrite.Insert(Unit(), values[0]),
                    new WriteOutcome(WriteOutcomeStatus.Inserted, version: 11,
                        generatedValues: new Dictionary<string, object?> { ["sequence"] = 101L })),
                new RowWriteOutcome(
                    RowWrite.Insert(Unit(), values[1]),
                    new WriteOutcome(WriteOutcomeStatus.Inserted, version: 12,
                        generatedValues: new Dictionary<string, object?> { ["sequence"] = 102L }))
            ]
        };
        var appends = Create(writer);
        var operation = appends.Prepare(Operation(), values, exactOutcomes: true);
        _ = await appends.Append(operation, RelationalExecution.Synchronous);

        var reader = new FakeAppendAdapter();
        reader.ReadResults.Enqueue(Entry(operation.Fingerprint, writer.CompletedResult, ProviderNow));
        var replayed = await Create(reader).Append(operation, RelationalExecution.Synchronous);
        var outcomes = Assert.IsAssignableFrom<IReadOnlyList<WriteOutcome>>(replayed.Outcomes);

        Assert.Equal([11L, 12L], outcomes.Select(outcome => outcome.Version));
        Assert.Equal([101L, 102L], outcomes.Select(outcome => outcome.GeneratedValue<long>("sequence")));
    }

    [Fact]
    public async Task Lost_claim_is_resolved_from_the_winning_ledger_entry()
    {
        var adapter = new FakeAppendAdapter { ClaimSucceeds = false };
        var appends = Create(adapter);
        var operation = appends.Prepare(Operation(), Values("one"), exactOutcomes: true);
        adapter.ReadResults.Enqueue(null);
        adapter.WinnerResults.Enqueue(new RelationalAppendReplayEntry(
            operation.Fingerprint,
            await SerializedOutcome()));

        var result = await appends.Append(operation, RelationalExecution.Synchronous);

        Assert.Equal(WriteOutcomeStatus.Replayed, result.Status);
        Assert.Equal(["prepare", "reclaim", "read", "claim", "winner"], adapter.Events);
    }

    [Fact]
    public async Task Lost_exact_claim_refuses_a_winner_with_a_different_fingerprint()
    {
        var adapter = new FakeAppendAdapter { ClaimSucceeds = false };
        var appends = Create(adapter);
        var operation = appends.Prepare(Operation(), Values("one"), exactOutcomes: true);
        adapter.ReadResults.Enqueue(null);
        adapter.WinnerResults.Enqueue(new RelationalAppendReplayEntry("different", await SerializedOutcome()));

        await Assert.ThrowsAsync<AppendIdempotencyConflictException>(async () =>
            await appends.Append(operation, RelationalExecution.Synchronous));

        Assert.DoesNotContain("payload", adapter.Events);
    }

    [Fact]
    public async Task Lost_non_exact_claim_replays_a_winner_without_comparing_its_fingerprint()
    {
        var adapter = new FakeAppendAdapter { ClaimSucceeds = false };
        var appends = Create(adapter);
        var operation = appends.Prepare(Operation(), Values("one"), exactOutcomes: false);
        adapter.ReadResults.Enqueue(null);
        adapter.WinnerResults.Enqueue(new RelationalAppendReplayEntry("different", await SerializedOutcome()));

        var result = await appends.Append(operation, RelationalExecution.Synchronous);

        Assert.Equal(WriteOutcomeStatus.Replayed, result.Status);
        Assert.Equal(7, Assert.Single(result.Outcomes!).Version);
        Assert.DoesNotContain("payload", adapter.Events);
    }

    [Fact]
    public async Task Missing_exact_result_is_refused_for_an_existing_or_raced_entry()
    {
        var existingAdapter = new FakeAppendAdapter();
        var existingAppends = Create(existingAdapter);
        var existing = existingAppends.Prepare(Operation("existing"), Values("one"), exactOutcomes: true);
        existingAdapter.ReadResults.Enqueue(Entry(existing.Fingerprint, null, ProviderNow));

        var existingFailure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await existingAppends.Append(existing, RelationalExecution.Synchronous));

        var racedAdapter = new FakeAppendAdapter { ClaimSucceeds = false };
        var racedAppends = Create(racedAdapter);
        var raced = racedAppends.Prepare(Operation("raced"), Values("one"), exactOutcomes: true);
        racedAdapter.ReadResults.Enqueue(null);
        racedAdapter.WinnerResults.Enqueue(null);

        var racedFailure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await racedAppends.Append(raced, RelationalExecution.Synchronous));

        Assert.StartsWith("GW-APPEND-002:", existingFailure.Message, StringComparison.Ordinal);
        Assert.StartsWith("GW-APPEND-002:", racedFailure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Missing_race_winner_fingerprint_is_an_exact_result_refusal(string? fingerprint)
    {
        var adapter = new FakeAppendAdapter { ClaimSucceeds = false };
        var appends = Create(adapter);
        var operation = appends.Prepare(Operation(), Values("one"), exactOutcomes: true);
        adapter.ReadResults.Enqueue(null);
        adapter.WinnerResults.Enqueue(new RelationalAppendReplayEntry(
            fingerprint,
            await SerializedOutcome()));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await appends.Append(operation, RelationalExecution.Synchronous));

        Assert.StartsWith("GW-APPEND-002:", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Non_exact_missing_result_replays_without_fabricating_outcomes()
    {
        var adapter = new FakeAppendAdapter();
        var appends = Create(adapter);
        var operation = appends.Prepare(Operation(), Values("one"), exactOutcomes: false);
        adapter.ReadResults.Enqueue(Entry(operation.Fingerprint, null, ProviderNow));

        var result = await appends.Append(operation, RelationalExecution.Synchronous);

        Assert.Equal(WriteOutcomeStatus.Replayed, result.Status);
        Assert.Null(result.Outcomes);
        Assert.Throws<InvalidOperationException>(result.ToReport);
    }

    [Fact]
    public async Task Failed_payload_refuses_completion_so_the_transaction_can_roll_back()
    {
        var adapter = new FakeAppendAdapter
        {
            PayloadOutcomes = [Outcome(WriteOutcomeStatus.UniqueViolation)]
        };
        var appends = Create(adapter);
        var operation = appends.Prepare(Operation(), Values("one"), exactOutcomes: true);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await appends.Append(operation, RelationalExecution.Synchronous));

        Assert.Contains("payload row was not accepted", failure.Message, StringComparison.Ordinal);
        Assert.Equal(["prepare", "reclaim", "read", "claim", "payload"], adapter.Events);
    }

    [Fact]
    public async Task Payload_must_return_one_successful_outcome_per_input_row()
    {
        var adapter = new FakeAppendAdapter { PayloadOutcomes = [] };
        var appends = Create(adapter);
        var operation = appends.Prepare(Operation(), Values("one"), exactOutcomes: true);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await appends.Append(operation, RelationalExecution.Synchronous));

        Assert.Contains("payload row was not accepted", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("complete", adapter.Events);
    }

    [Fact]
    public async Task Missing_ledger_completion_aborts_the_append_transaction()
    {
        var adapter = new FakeAppendAdapter { CompletionSucceeds = false };
        var appends = Create(adapter);
        var operation = appends.Prepare(Operation(), Values("one"), exactOutcomes: true);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await appends.Append(operation, RelationalExecution.Synchronous));

        Assert.Contains("ledger could not be completed", failure.Message, StringComparison.Ordinal);
        Assert.Equal("complete", adapter.Events[^1]);
    }

    [Fact]
    public void Preparation_validates_the_entire_append_before_provider_work()
    {
        var adapter = new FakeAppendAdapter();
        var appends = Create(adapter);

        Assert.Throws<ArgumentException>(() => appends.Prepare(
            Operation(),
            [new StorageValues(new Dictionary<string, object?>
            {
                ["id"] = "one",
                ["payload"] = "value",
                ["metric"] = double.NaN
            })],
            exactOutcomes: true));

        Assert.Empty(adapter.Events);
    }

    [Fact]
    public async Task Preparation_snapshots_the_payload_used_by_the_fingerprint_and_writer()
    {
        var adapter = new FakeAppendAdapter();
        var appends = Create(adapter);
        var values = Values("original").ToList();
        var operation = appends.Prepare(Operation(), values, exactOutcomes: true);
        values[0] = Values("changed")[0];

        _ = await appends.Append(operation, RelationalExecution.Synchronous);

        Assert.Equal("original", Assert.Single(adapter.PayloadValues!).Values["payload"]);
    }

    private static RelationalSessionAppends Create(FakeAppendAdapter adapter) =>
        new(Unit(), StorageAccess.Global, adapter);

    private static OperationId Operation(string nonce = "operation") => new(ProviderNow, nonce);

    private static IReadOnlyList<StorageValues> Values(string payload) =>
        [new(new Dictionary<string, object?> { ["id"] = "one", ["payload"] = payload })];

    private static StorageUnit Unit() => new()
    {
        Id = new StorageUnitId("append-state-machine"),
        Name = "append_state_machine",
        Columns =
        [
            new ColumnDefinition { Name = "id", Type = PortableType.String, IsNullable = false },
            new ColumnDefinition { Name = "payload", Type = PortableType.String, IsNullable = false },
            new ColumnDefinition { Name = "metric", Type = PortableType.Double, IsNullable = true }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        AppendIdempotency = new AppendIdempotencyDeclaration { Window = TimeSpan.FromMinutes(5) }
    };

    private static RelationalAppendLedgerEntry Entry(
        string? fingerprint,
        string? result,
        DateTimeOffset committedAt) =>
        new(committedAt, fingerprint, result);

    private static async ValueTask<string> SerializedOutcome()
    {
        var adapter = new FakeAppendAdapter();
        var appends = Create(adapter);
        var operation = appends.Prepare(Operation("seed-result"), Values("one"), exactOutcomes: true);
        _ = await appends.Append(operation, RelationalExecution.Synchronous);
        return adapter.CompletedResult!;
    }

    private static RowWriteOutcome Outcome(WriteOutcomeStatus status) =>
        new(
            RowWrite.Insert(Unit(), Values("one")[0]),
            new WriteOutcome(status, version: status == WriteOutcomeStatus.Inserted ? 7 : null));

    private sealed class FakeAppendAdapter : IRelationalAppendAdapter
    {
        internal Queue<RelationalAppendLedgerEntry?> ReadResults { get; } = new();
        internal Queue<RelationalAppendReplayEntry?> WinnerResults { get; } = new();
        internal List<string> Events { get; } = [];
        internal bool ClaimSucceeds { get; init; } = true;
        internal bool CompletionSucceeds { get; init; } = true;
        internal IReadOnlyList<RowWriteOutcome> PayloadOutcomes { get; init; } = [Outcome(WriteOutcomeStatus.Inserted)];
        internal string? CompletedResult { get; private set; }
        internal IReadOnlyList<StorageValues>? PayloadValues { get; private set; }

        public ValueTask<DateTimeOffset> PrepareLedger(
            RelationalAppendOperation operation,
            RelationalExecution execution)
        {
            Events.Add("prepare");
            return ValueTask.FromResult(ProviderNow);
        }

        public ValueTask ReclaimExpired(
            RelationalAppendOperation operation,
            DateTimeOffset cutoff,
            RelationalExecution execution)
        {
            Events.Add("reclaim");
            return default;
        }

        public ValueTask<RelationalAppendLedgerEntry?> ReadLedger(
            RelationalAppendOperation operation,
            RelationalExecution execution)
        {
            Events.Add("read");
            return ValueTask.FromResult(ReadResults.Count == 0 ? null : ReadResults.Dequeue());
        }

        public ValueTask DeleteLedger(
            RelationalAppendOperation operation,
            RelationalAppendLedgerEntry existing,
            RelationalExecution execution)
        {
            Events.Add("delete");
            return default;
        }

        public ValueTask<bool> TryClaimLedger(
            RelationalAppendOperation operation,
            DateTimeOffset providerNow,
            RelationalExecution execution)
        {
            Events.Add("claim");
            return ValueTask.FromResult(ClaimSucceeds);
        }

        public ValueTask<RelationalAppendReplayEntry?> ReadClaimWinner(
            RelationalAppendOperation operation,
            RelationalExecution execution)
        {
            Events.Add("winner");
            return ValueTask.FromResult(WinnerResults.Count == 0 ? null : WinnerResults.Dequeue());
        }

        public ValueTask<IReadOnlyList<RowWriteOutcome>> InsertPayload(
            RelationalAppendOperation operation,
            RelationalExecution execution)
        {
            Events.Add("payload");
            PayloadValues = operation.Values;
            return ValueTask.FromResult(PayloadOutcomes);
        }

        public ValueTask<bool> CompleteLedger(
            RelationalAppendOperation operation,
            string serializedOutcomes,
            RelationalExecution execution)
        {
            Events.Add("complete");
            CompletedResult = serializedOutcomes;
            return ValueTask.FromResult(CompletionSucceeds);
        }
    }
}
