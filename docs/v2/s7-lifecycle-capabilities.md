# S7: durable lifecycle capabilities

S7 adds two optional, provider-neutral capabilities for stream consumers that
must survive process restart and acknowledgement loss:

```csharp
var unit = new StorageUnit
{
    // columns, key, and retention declaration omitted here
    RetentionIdempotency = new RetentionIdempotencyDeclaration
    {
        Window = TimeSpan.FromHours(24),
        LedgerName = "stream_retention_operations"
    }
};

var highWater = session.Inspect().LifetimeCommittedSequenceHighWater;
var result = session.ApplyRetention(
    new OperationId(DateTimeOffset.UtcNow, "retention-cycle-2026-08-16"),
    new RetentionExecutionOptions { MaxRowsPerBatch = 128 });
```

`IStorageInspectionSession` is the additive durable high-water capability. Its
empty result is represented by a null high-water, which is distinct from a
committed sequence value. High-water state is keyed by unit and scope, updated
in the same transaction as the generated row, and remains after retention
deletes rows and after a provider/session restart. A rollback never advances
it.

`IExactRetentionStorageSession` is the additive operation-identified retention
capability. It uses `RetentionIdempotencyDeclaration`, deliberately separate
from `AppendIdempotency`: append replay lifetime and retention replay lifetime
are independent policies. The persisted ledger stores a canonical request
fingerprint and a versioned exact result. A same-nonce retry with the same
request returns `RetentionOperationStatus.Replayed`; a changed request raises
`GW-RETENTION-001` before changing rows. Malformed/legacy exact results raise
`GW-RETENTION-002` with a new-nonce remediation.

Exact retention is atomic at the provider boundary. The bounded delete loop,
ledger placeholder, and final result update share one transaction (or an
equivalent provider transaction). Cancellation or a failed batch rolls back the
whole pass, including the ledger, so a retry executes once and its later replay
returns the same cumulative result. Status-only `ApplyRetention()` keeps the
existing resumable behavior and does not require a retention idempotency
declaration.

Providers advertise these contracts through
`BatchWriteCapabilities.DurableHighWaterInspection` and
`BatchWriteCapabilities.ExactRetention`. A provider that cannot provide the
required transactional semantics omits the descriptors and its session does
not implement the capability interfaces; the extension reports
`GW-INSPECT-001` or `GW-RETENTION-003` before provider state is opened. The
InMemory, SQLite, PostgreSQL, SQL Server, and transaction-capable MongoDB
conformance proofs cover scope isolation, restart/replay, rollback-safe
cancellation, and exact conflict behavior.
