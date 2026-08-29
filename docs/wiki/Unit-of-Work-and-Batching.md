# Unit of Work & Batching

A unit of work owns **one transaction**, the sessions it creates, and their provider resources.
Staged writes are coalesced and flushed as native provider batches.

## Basic usage

```csharp
using var work = connection.BeginUnitOfWork(
    StorageAccess.Global,
    new BatchWriteOptions
    {
        MaxRowsPerFlush = 1_000,
        OutcomeMode = BatchOutcomeMode.Exact
    },
    ordersUnit, auditUnit);   // every unit this transaction will touch

work.Stage(RowWrite.Upsert(ordersUnit, values));
work.Stage(RowWrite.Insert(auditUnit, auditValues));

var report = await work.CommitWithOutcomesAsync();
```

**Commit and rollback are terminal.** Disposing a non-terminal unit rolls it back. Sessions obtained
from `work.OpenSession(unit)` become invalid at that point — do not retain them.

## Outcome modes: pick deliberately

`BatchWriteOptions.OutcomeMode` is chosen **when the unit begins** and applies to automatic
cap/read/query flushes as well as to commit.

| | `Aggregate` (default) | `Exact` |
| --- | --- | --- |
| Select with | `BatchWriteOptions.Default` | `BatchWriteOptions.Exact` |
| Commit with | `Commit()` / `CommitAsync()` | `CommitWithOutcomes()` / `CommitWithOutcomesAsync()` |
| Returns | `BatchWriteSummary` — counts only | `BatchWriteReport` — one `RowWriteOutcome` per staged input |
| Provider path | Lowest cost | May cost more (notably on MongoDB) |

```csharp
// Aggregate — cheapest.
var summary = work.Commit();
// summary.Submitted / Applied / Succeeded / Failed / Superseded / IsSuccessful

// Exact — one outcome per staged input, in declaration order.
var report = work.CommitWithOutcomes();
for (var ordinal = 0; ordinal < report.Outcomes.Count; ordinal++)
{
    var outcome = report.Outcomes[ordinal];
    Console.WriteLine(outcome.IsSuperseded
        ? $"{ordinal}: superseded by #{outcome.WinnerOrdinal} ({outcome.WinnerEvidence?.Status})"
        : $"{ordinal}: {outcome.Outcome.Status}");
}
```

`Commit`/`CommitAsync` **never** expose provider row statuses. `CommitWithOutcomes` **rejects an
aggregate-mode unit** rather than claiming exact evidence after an earlier aggregate flush.

## Flush semantics

Staged writes are flushed when:
1. the unit of work **commits**,
2. a session **reads a key that has staged writes**, or
3. **`MaxRowsPerFlush`** is reached.

A staged-key read is a synchronization point: matching staged writes flush before the read is
delegated. A **query** flushes the whole staged set.

Set-based mutations are synchronization points too. For example, an exact mutation through a
session opened from the unit of work first flushes all earlier staged writes, takes its key
snapshot, and applies one keyed write per selected key. Staged writes added afterward are committed
after the set operation. The snapshot and keyed writes remain inside the unit's transaction:

```csharp
work.Stage(RowWrite.Insert(unit, before));
var result = work.OpenSession(unit).DeleteWhere(predicate, SetMutationOptions.Exact);
work.Stage(RowWrite.Upsert(unit, after));
work.Commit();
```

`result.Outcomes` is exact evidence for the keys selected at the synchronization point. Outside a
unit of work, exact mode still uses the keyed contract but does not make the read-plus-writes one
transaction; use aggregate mode when whole-set atomicity is required.

As with other reads, the barrier applies to writes staged with `work.Stage`. Keyed methods called
directly on a unit-of-work session are immediate provider writes, not staged declarations; use
`Stage` when their declaration order relative to the set operation matters.

> The row cap is a **memory and parameter-safety boundary, not a transaction boundary.** A
> cap-triggered flush stays inside the enclosing transaction and rolls back with it.

## Coalescing and `Superseded`

Writes are first coalesced **by storage unit and key in declaration order**. The last write wins —
even when earlier inputs used a different mode or column set. Only then are the final writes grouped
into a provider batch.

Earlier inputs are reported as `RowWriteDisposition.Superseded`, carrying `WinnerOrdinal` (the
zero-based declaration position that won) and `WinnerEvidence` (that winner's `WriteOutcome`).
**They are not reported as provider successes.**

`report.Outcomes` holds one entry per staged input in declaration order, so an entry's own ordinal is
its index in that list — `RowWriteOutcome` itself has no `Ordinal` member.

```csharp
work.Stage(RowWrite.Insert(unit, valuesA));   // ordinal 0 → Superseded
work.Stage(RowWrite.Upsert(unit, valuesB));   // ordinal 1 → Upserted (same key)
```

Writes to **different** keys are not ordered; providers may execute them in any order.

## Failure handling

A failed applied outcome raises `BatchWriteException`, **poisons** the unit of work against further
staging or commit, and requires rollback.

```csharp
try
{
    var report = work.CommitWithOutcomes();
}
catch (BatchWriteException ex)
{
    // Only attributed APPLIED failures — never aggregate synthetic successes
    // and never superseded declarations.
    foreach (var failure in ex.Outcomes)
        Log(failure);
    work.Rollback();
}
```

## Provider batch paths

Each provider does its honest best and **documents where it falls back**. Capability descriptors say
whether a native returning/output path or a fallback is in use, so you can choose the evidence/cost
you need.

| Provider | Unconditional homogeneous group | Falls back to row-attributed when |
| --- | --- | --- |
| **SQLite** | One multi-row `INSERT … RETURNING` / `INSERT … ON CONFLICT … DO UPDATE … RETURNING`; chunked at the active variable limit | Version-conditional writes, or a declared secondary unique index |
| **PostgreSQL** | One multi-row `INSERT … ON CONFLICT … RETURNING`, including partial-index conflict predicate and generated version | Declared secondary unique index (so one constraint error can't be attributed to every row) |
| **SQL Server** | `MERGE … WITH (HOLDLOCK)` against a durable schema-owned **table-valued parameter** — a 1,000-row group stays one command | No TVP installed (parameter-limited `VALUES` fallback), or row-specific version predicates |
| **MongoDB** | One unordered `BulkWrite` of `UpdateOneModel` upserts for **aggregate** commits | **`CommitWithOutcomes` costs one `FindOneAndUpdate`-equivalent per coalesced row** — bulk acknowledgements cannot distinguish inserted from updated |

> The MongoDB exact-mode cost is the one to plan around. If you are writing thousands of rows and
> don't need per-row evidence, use `Aggregate`.

The capability descriptor does **not** claim that a conditional or generated-column workload is one
round trip.

## Capabilities

```csharp
BatchWriteCapabilities.StagedUnitOfWork   // groundwork.storage.batched-unit-of-work
BatchWriteCapabilities.PerRowOutcomes     // groundwork.storage.batched-outcomes
BatchWriteCapabilities.NativeBatch        // groundwork.storage.batched-native
WellKnownCapabilities.AtomicCommit        // groundwork.operational.atomic-commit
```

All relational providers and the reference provider advertise `atomic-commit`. MongoDB advertises it
**only when the connected deployment reports transaction support**.

## Typed batches

For Records:

```csharp
using var batch = table.BeginUnitOfWork(connection, BatchWriteOptions.Exact);
batch.Upsert(customer);
batch.Delete(other);
var report = batch.CommitWithOutcomes();
```

`RecordTableStoreUnitOfWork<T>` follows exactly the same lifetime rules.

## Mixing units and families

Documents map to plain `RowWrite` values, so a single transaction can span a Records unit, a
Documents unit, and a raw kernel unit:

```csharp
using var work = connection.BeginUnitOfWork(
    StorageAccess.Global, BatchWriteOptions.Exact, claimsUnit, auditUnit);

work.Stage(RowWrite.Insert(auditUnit, auditValues));
work.Stage(RowWrite.CompareAndDelete(
    claimsUnit,
    new StorageKey(new Dictionary<string, object?> { ["id"] = claimId }),
    new Dictionary<string, object?> { ["owner"] = owner, ["fence"] = fence }));

var committed = work.CommitWithOutcomes();
// A comparison mismatch is attributed to its RowWrite and rolls back the whole transaction.
```

## Observability

The write observer fires **once per provider batch command**, not once per staged row. Payloads carry
operation metadata only.

## Benchmark

```bash
dotnet run --project benchmarks/Groundwork.Benchmarks -- roundtrips --workload commit --n 1000
```

Target: **one batch command** for a 1,000-row homogeneous upsert group on SQLite, PostgreSQL, SQL
Server, and MongoDB aggregate commits. Select `OutcomeMode.Aggregate` for this target.

Repository CI runs this measurement only through the manual `Performance evidence` workflow so
ordinary correctness failures remain distinct from benchmark infrastructure and output.

## Next

- **[Writing Data](Writing-Data)** — outcomes in detail
- **[Providers](Providers)** — per-provider notes
