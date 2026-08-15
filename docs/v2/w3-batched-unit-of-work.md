# W3: batched unit of work

`IUnitOfWork.Stage` accepts provider-neutral `RowWrite` values. Staged writes are
flushed when the unit of work commits, when a session reads a key that has staged
writes, or when `BatchWriteOptions.MaxRowsPerFlush` is reached.

```csharp
using var work = connection.BeginUnitOfWork(
    StorageAccess.Global,
    new BatchWriteOptions
    {
        MaxRowsPerFlush = 1_000,
        OutcomeMode = BatchOutcomeMode.Exact
    },
    unit);

work.Stage(RowWrite.Upsert(unit, values));
var summary = await work.CommitWithOutcomesAsync();
```

## Flush semantics

Writes are first coalesced by storage unit and key in declaration order. The
last write wins even when earlier inputs used another mode or supplied column
set; only then are final writes grouped for a provider batch. Earlier inputs
are returned as `Superseded` with the zero-based ordinal and provider evidence
of their winning input; they are not reported as provider successes. Writes to
different keys are not ordered and providers may execute them in any order. A
staged-key read is a synchronization point: all matching staged writes are
flushed before the read is delegated, while a query flushes the whole staged
set.

The row cap is a memory and parameter safety boundary, not a transaction
boundary. A cap-triggered flush remains part of the enclosing unit-of-work
transaction and is rolled back with it.

## Outcomes and capabilities

`BatchWriteOptions.OutcomeMode` is selected when the unit of work begins and
applies to automatic cap/read/query flushes as well as commit. `Aggregate` is
the default low-cost path; use `BatchWriteOptions.Exact` (or set
`OutcomeMode = BatchOutcomeMode.Exact`) when provider evidence is required.
`Commit` and `CommitAsync` return the submitted/applied/succeeded/failed/
superseded `BatchWriteSummary`; with `Aggregate`, they use the lowest-cost
provider path. `CommitWithOutcomes` and
`CommitWithOutcomesAsync` require `Exact` and expose one `RowWriteOutcome` for
every staged input. They reject an aggregate-mode unit rather than claiming
exact evidence after an earlier aggregate flush. `RowWrite.Upsert` with an
expected version, or the explicit `RowWrite.ConditionalUpsert`, always uses
the provider's atomic conditional upsert primitive. A failed applied outcome
raises `BatchWriteException`, poisons the unit of work against further
staging/commit, and requires rollback.

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
outcomes intentionally cost one command per coalesced row. Select
`OutcomeMode.Aggregate` for this benchmark target. The transaction boundary is
owned by the unit of work.
