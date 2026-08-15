# W3: batched unit of work

`IUnitOfWork.Stage` accepts provider-neutral `RowWrite` values. Staged writes are
flushed when the unit of work commits, when a session reads a key that has staged
writes, or when `BatchWriteOptions.MaxRowsPerFlush` is reached.

```csharp
using var work = connection.BeginUnitOfWork(
    StorageAccess.Global,
    new BatchWriteOptions { MaxRowsPerFlush = 1_000 },
    unit);

work.Stage(RowWrite.Upsert(unit, values));
var summary = await work.CommitWithOutcomesAsync();
```

## Flush semantics

Writes are grouped by storage unit, operation mode, and supplied column set.
Writes to the same key within one group are coalesced in declaration order; the
last write is sent to the provider and its result is associated with every
coalesced input row. Writes to different keys are not ordered and providers may
execute them in any order. A staged-key read is a synchronization point: all
matching staged writes are flushed before the read is delegated.

The row cap is a memory and parameter safety boundary, not a transaction
boundary. A cap-triggered flush remains part of the enclosing unit-of-work
transaction and is rolled back with it.

## Outcomes and capabilities

`Commit` and `CommitAsync` commit the batch and expose aggregate success through
the same summary implementation. `CommitWithOutcomes` and
`CommitWithOutcomesAsync` expose one `RowWriteOutcome` for every staged input,
including inputs coalesced into a final write. A failed outcome raises
`BatchWriteException`; the provider unit of work rolls back the transaction.

Providers advertise `BatchWriteCapabilities.StagedUnitOfWork` and
`BatchWriteCapabilities.PerRowOutcomes` through `IStorageProviderConnection.Capabilities`.
The descriptor deliberately says when a provider uses a native returning/output
path or a fallback, so callers can choose the evidence/cost they need.

The write observer is invoked once per provider batch command, not once per
staged row. Observer payloads contain operation metadata only and must not carry
row values or keys.

## Provider paths

- SQLite uses one multi-row `INSERT ... RETURNING` or
  `INSERT ... ON CONFLICT ... DO UPDATE ... RETURNING` command for unconditional
  insert/upsert groups. Version-conditional writes and operations whose exact
  outcome requires row-specific predicates retain the single-row path.
- PostgreSQL uses one multi-row `INSERT ... ON CONFLICT ... RETURNING` command
  per unconditional insert/upsert group, including its partial-index conflict
  predicate when one is declared.
- SQL Server uses a `MERGE ... WITH (HOLDLOCK)` source batch and splits the
  source below the 2,100-parameter limit. This is the compatibility form until
  the schema executor grows a durable table-valued-parameter operation; writes
  needing row-specific version predicates retain the single-row path.
- MongoDB uses one unordered `BulkWrite` of `UpdateOneModel` upserts. Provider
  sequence allocation and conditional/versioned writes retain the single-row
  path because they need separate allocation or CAS evidence.

Every provider keeps this fallback visible through capability documentation; the
capability does not claim that a conditional or generated-column workload is one
round trip.

The local proof workload is:

```bash
dotnet run --project benchmarks/Groundwork.Benchmarks -- \
  roundtrips --workload commit --n 1000
```

For SQLite this reports one batch command for a 1,000-row upsert group (the
transaction boundary is owned by the unit of work).
