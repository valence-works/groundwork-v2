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

Writes are first coalesced by storage unit and key in declaration order. The
last write wins even when earlier inputs used another mode or supplied column
set; only then are final writes grouped for a provider batch. The final result
is associated with every coalesced input row. Writes to different keys are not
ordered and providers may execute them in any order. A staged-key read is a
synchronization point: all matching staged writes are flushed before the read
is delegated, while a query flushes the whole staged set.

The row cap is a memory and parameter safety boundary, not a transaction
boundary. A cap-triggered flush remains part of the enclosing unit-of-work
transaction and is rolled back with it.

## Outcomes and capabilities

`Commit` and `CommitAsync` select the aggregate-cost provider path and return
the submitted/succeeded/failed `BatchWriteSummary`. `CommitWithOutcomes` and
`CommitWithOutcomesAsync` select exact evidence and expose one
`RowWriteOutcome` for every staged input, including inputs coalesced into a
final write. `RowWrite.Upsert` with an expected version, or the explicit
`RowWrite.ConditionalUpsert`, always uses the provider's atomic conditional
upsert primitive. A failed outcome raises `BatchWriteException`, poisons the
unit of work against further staging/commit, and requires rollback.

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
  predicate when one is declared. A declared secondary unique index selects the
  row-attributed fallback so one constraint error cannot be reported for every
  row in the native statement.
- SQL Server uses a durable schema-owned table-valued parameter as the
  `MERGE ... WITH (HOLDLOCK)` source, so a 1,000-row homogeneous group remains
  one provider command. Existing installations without the type use the
  parameter-limited `VALUES` compatibility fallback; writes needing
  row-specific version predicates retain the single-row path. A declared
  secondary unique index likewise selects the row-attributed fallback.
- MongoDB uses one unordered `BulkWrite` of `UpdateOneModel` upserts for
  aggregate commits. `CommitWithOutcomes` switches to one
  `FindOneAndUpdate`-equivalent conditional write per row because bulk
  acknowledgements cannot distinguish inserted from updated outcomes.

Every provider keeps this fallback visible through capability documentation; the
capability does not claim that a conditional or generated-column workload is one
round trip.

The local proof workload is:

```bash
dotnet run --project benchmarks/Groundwork.Benchmarks -- \
  roundtrips --workload commit --n 1000
```

The target is one batch command for a 1,000-row homogeneous upsert group on
SQLite, PostgreSQL, SQL Server, and MongoDB aggregate commits; exact MongoDB
outcomes intentionally cost one command per coalesced row. The transaction
boundary is owned by the unit of work.
