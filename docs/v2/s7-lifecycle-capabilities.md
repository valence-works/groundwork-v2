# S7: durable lifecycle capabilities

S7 adds two optional, provider-neutral capabilities for stream consumers that
must survive process restart and acknowledgement loss:

```csharp
var unit = new StorageUnit
{
    // columns and key omitted here
    Retention = new RetentionDeclaration
    {
        KeepNewest = 100,
        OrderColumn = "sequence"
    },
    RetentionIdempotency = new RetentionIdempotencyDeclaration
    {
        Window = TimeSpan.FromHours(24),
        LedgerName = "stream_retention_operations"
    }
};

var highWater = session.Inspect().LifetimeCommittedSequenceHighWater;
var result = session.ApplyRetention(
    new OperationId(DateTimeOffset.UtcNow, "retention-cycle-2026-08-16"),
    new RetentionExecutionOptions { MaxRowsPerBatch = 128, KeepNewestOverride = 10 });
```

`RetentionIdempotency` is valid only when the same unit declares `Retention`; schema
admission and declaration builders refuse the invalid combination with `GW-RETENTION-004`.
Use `Retention` alone for status-only retention; it does not require an operation ledger.

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
request (including its effective keep value) returns `RetentionOperationStatus.Replayed`; a changed request raises
`GW-RETENTION-001` before changing rows. Malformed/legacy exact results raise
`GW-RETENTION-002` with a new-nonce remediation.

`RetentionExecutionOptions.KeepNewestOverride` is an optional non-negative per-pass value. Null
uses the declaration, a positive value keeps that many newest rows, and zero deletes every row in
each partition without resetting the durable sequence high-water. The effective value is part of
the exact-operation fingerprint, so a same-nonce retry with a different override is refused.

An exact pass may also request a bounded affected-key projection:

```csharp
var result = session.ApplyRetention(operationId, new RetentionExecutionOptions
{
    MaxRowsPerBatch = 128,
    AffectedKeyProjection = new RetentionAffectedKeyProjection("tenant", maxDistinctValues: 100)
});
// result.AffectedKeys is complete, distinct, and in portable deterministic order.
```

The projection names one declared scalar column and a finite maximum. Providers materialize at
most `maximum + 1` values natively; the extra value refuses the pass with
`GW-RETENTION-005` before any row, ledger claim, or completion is committed. JSON and storage-only
Double columns are refused because they have no portable total ordering. Projection, bound, scope,
and operation identity are part of the canonical fingerprint, so a same-nonce retry returns the
identical affected-key evidence while any changed request raises `GW-RETENTION-001`. The optional
`IExactRetentionAffectedKeysStorageSession` marker and
`BatchWriteCapabilities.ExactRetentionAffectedKeys` descriptor are advertised only by providers
that can preserve this transaction boundary; standalone MongoDB intentionally omits and refuses it.

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
not implement the capability interfaces. Unsupported inspection reports
`GW-INSPECT-001`; inspection on a unit without a `ProviderSequence` reports
`GW-INSPECT-002`; unsupported exact retention reports `GW-RETENTION-003`.

Append and retention declarations must use distinct ledger names. Groundwork
reserves its metadata, schema, search-key, high-water, and default operation
ledger names; custom declarations cannot claim those names. SQL Server creates
the lifecycle identity columns with `Latin1_General_100_BIN2` so unit, scope,
and nonce values use ordinal identity semantics. Existing lifecycle tables
with a different collation are refused with migration guidance rather than
silently merging case-distinct identities.

The InMemory, SQLite, PostgreSQL, SQL Server, and transaction-capable MongoDB
conformance proofs cover scope isolation, restart/replay, rollback-safe
cancellation, exact conflict behavior, and capability refusal.
