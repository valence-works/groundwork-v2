# S2: idempotent append

`StorageUnit.AppendIdempotency` opts a unit into `IStorageSession.Append`. The
operation identity is an `OperationId` containing an application-issued
timestamp and a caller-chosen nonce. The timestamp is metadata only: replay
expiry is evaluated against the provider's recorded ledger time.

```csharp
var unit = new StorageUnit
{
    Id = new StorageUnitId("events"),
    Name = "events",
    Columns =
    [
        new() { Name = "id", Type = PortableType.String, MaxLength = 450, IsNullable = false },
        new() { Name = "payload", Type = PortableType.String, MaxLength = 450, IsNullable = false }
    ],
    Key = new KeyDefinition { Columns = ["id"] },
    AppendIdempotency = new AppendIdempotencyDeclaration
    {
        Window = TimeSpan.FromMinutes(10)
    }
};

var result = session.Append(
    new OperationId(DateTimeOffset.UtcNow, "import-2026-08-15-001"),
    [new StorageValues(new Dictionary<string, object?>
    {
        ["id"] = "event-1", ["payload"] = "created"
    })]);
```

Every provider owns the durable ledger and commits its `(unit, scope, nonce)`
entry with the payload rows in one transaction. A nonce found within the
declared window returns `WriteOutcomeStatus.Replayed` without issuing a payload
write. Once the provider-recorded entry expires, the append is admitted again.
Expired entries are reclaimed in bounded batches, so retention work cannot
grow a single append without limit.

When the same unit declares `RetentionTrigger.OnAppend`, newly committed payload rows request
retention only after the ledger and payload transaction succeeds. A replay does not duplicate the
payload and may safely request cleanup again, allowing an interrupted post-commit cleanup to
converge on the declared newest-N watermark.

Providers advertise the contract through
`BatchWriteCapabilities.AppendIdempotency`; the stream-capability proof covers
replay, provider-time skew, expiry, and rollback of a failed payload batch.

## Exact generated outcomes

When a caller needs provider-generated values (for example a sequence key), it
can opt into the additive `IExactAppendStorageSession` capability through the
`IStorageSession.AppendWithOutcomes` extension:

```csharp
var first = session.AppendWithOutcomes(operation, values);
var replay = session.AppendWithOutcomes(operation, values);

// Both calls expose the same ordered per-row generated values.
var sequence = replay.Outcomes[0].GeneratedValue<long>("sequence");
```

The provider ledger stores a versioned, injective fingerprint of the declared
portable input values and the versioned exact outcome payload in the same
transaction as the rows. A replay returns those stored outcomes without
allocating another sequence. Reusing the same unit/scope/nonce with a different
payload throws `GW-APPEND-001` and writes nothing. An existing legacy ledger
entry remains replayable through status-only `Append`; exact replay refuses it
with `GW-APPEND-002` because no generated values were persisted. Providers that
cannot guarantee the required transaction semantics omit the exact capability
and the extension reports `GW-APPEND-003` before attempting a write.
