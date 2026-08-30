# P3.3 data migrations

`SchemaEvolutionMetadata.SemanticMigrationId` used to be a bare label: it suppressed `GW-SCHEMA-005`
for a non-nullable column with no portable default and forced `--allow-semantic`, but carried no
transform. It now names a *data migration*: a host-process transform executed under the same
authorization that admits the semantic schema change.

## The transform

```csharp
public interface IDataMigrationTransform
{
    string Identity { get; }                       // part of the request fingerprint
    ImmutableArray<string> SourceColumns { get; }  // columns the scan projects
    ImmutableArray<string> TargetColumns { get; }  // columns it may write
    DataMigrationValues Transform(DataMigrationRow row);
}
```

Row in, values out. The member returns a value rather than a task on purpose: a transform runs once
per row inside a provider chunk and runs again on a row that a rolled-back chunk did not commit, so
it must be a pure function of the row and never a place to do I/O.

The row carries the **portable** CLR type each column declares — `RelationalDialect.ReadValue` maps
the driver's storage representation back before the transform sees it — so one transform behaves
the same on SQLite, PostgreSQL, SQL Server, and MongoDB rather than seeing `long` on one and `int`
on the next.

A transform is attached by identity:

```csharp
var catalog = new DataMigrationCatalog([
    new DataMigration("2026-08-slugify", new StorageUnitId("orders"), new SlugTransform())
]);
PhysicalSchemaApplication.Apply(target, executor, dataMigrations: catalog);
await PhysicalSchemaApplication.ApplyAsync(target, executor, dataMigrations: catalog, cancellationToken: token);
```

The migration runs after the plan's operations are acknowledged and after applied state is
published, because replaying `CREATE TABLE`/`ADD COLUMN` is not idempotent while the data-migration
ledger is. It runs inside the same application lock and on the same relational connection and
fence, so a schema change cannot interleave with a migration of the same target — and a long
migration therefore holds that lock for its duration. Bound a pass with a budget when that matters. When it does not finish, the application outcome is `DataMigrationIncomplete` and the
schema tool exits with `PendingChanges` — the schema is applied, the target is not migrated, and
neither fact is hidden behind the other.

`DerivedColumnTransform` is the folded search-key backfill expressed as an ordinary transform. The
in-transaction derived-column backfill of a schema apply and the chunked runner both drive it, so
there is one definition of what a search key is rather than one per provider.

## Chunked, resumable execution

`DataMigrationRunner.Run` / `RunAsync` owns budgets, ledger transitions, and refusals; the provider
owns reading, writing, and committing one chunk.

```csharp
new DataMigrationBudget { MaxRowsPerBatch = 512, MaxBatches = 4, MaxRows = 10_000 }
```

`MaxBatches`/`MaxRows` stop a pass deliberately and return `DataMigrationStatus.Interrupted` with
the resume cursor. Cancellation is an abort, not a plan: it is observed before each chunk and
throws, exactly as retention does. Either way the chunk boundary is the recovery point.

Progress is keyset, not offset: the cursor is the last key a chunk committed, in the subject's
declared key order, and the next chunk resumes strictly after it. Every string column is stored
under a binary collation on every relational provider (`Latin1_General_100_BIN2`, `"C"`,
SQLite's ordinal collation), so `ORDER BY` and `>` agree and no row is skipped or repeated.

A relational chunk is **one** `UPDATE` for the whole batch, not one per row:

```sql
UPDATE "orders" SET "slug"=CASE WHEN ("id"=@gwk0_0) THEN @gwv0_0
                                WHEN ("id"=@gwk1_0) THEN @gwv1_0
                                WHEN ("id"=@gwk2_0) THEN NULL
                                ELSE "slug" END
WHERE ("id"=@gwk0_0) OR ("id"=@gwk1_0) OR ("id"=@gwk2_0);
```

The `ELSE` arm is the column itself, so the expression's type never depends on a driver inferring
the type of a null parameter, and a null result is written as the SQL null literal. The chunk is
clamped to `RelationalDialect.ParameterBudget` divided by the parameters each row binds, so a
provider is never handed a statement it cannot bind. This replaced the one-`UPDATE`-per-row loop in
`RelationalSchemaExecutor.BackfillDerivedColumn`, which now drives the same code.

## The ledger

Applied and in-flight migrations live in provider-owned state beside schema history:
`__groundwork_data_migrations` on the relational providers, a collection of the same name on
MongoDB. One row per (subject, provider, migration identity).

`DataMigrationLedgerEntry` is a class with one validating constructor, not a record, because a
record's `with` expression bypasses constructor validation through its init setters. The
constructor refuses:

- `Completed` without a completion instant,
- `Completed` while still carrying a resume cursor,
- `Running` while carrying a completion instant.

So "finished, and here is where to resume from" is unrepresentable, and an interrupted pass cannot
be read back as a finished one.

Completion is a separate durable fact, never inferred from the cursor. `Complete` takes a
`DataMigrationExhaustion` — a value only `DataMigrationChunkOutcome.Exhausted` produces, whose
constructor is internal to the kernel, so no provider assembly can manufacture one. A pass whose
final chunk fills exactly is *not* complete: the source was never observed exhausted, the ledger
still says `Running`, and the next pass runs one more chunk to settle it.

Replay is therefore idempotent. A completed entry short-circuits to
`DataMigrationStatus.Replayed` without touching a row. Reusing a migration identity with a changed
transform, subject, or column set is refused with `GW-MIGRATION-002` rather than silently producing
different values under a name that already means something — the same discipline as append
idempotency.

## Capabilities, advertised not assumed

| Capability | Meaning | Relational | MongoDB |
| --- | --- | --- | --- |
| `KeysetScan` | Rows read in a stable total key order, resuming strictly after a key | yes | yes (`_id`) |
| `AtomicChunkProgress` | A chunk's rows and its progress commit or roll back together | yes | replica set / sharded only |
| `AppliedLedger` | Applied and in-flight migrations recorded durably | yes | yes |
| `SetBasedBatchUpdate` | One statement writes a whole chunk with a different value per row | yes | **no** |

The first three are required; `DataMigrationRunner.EnsureCapabilities` refuses with
`GW-MIGRATION-001`, naming the missing capability, before a row moves. A standalone MongoDB cannot
start a transaction, so it does not advertise `AtomicChunkProgress` and is refused rather than
writing documents whose progress record might not follow. MongoDB has no multi-document update
carrying a different value per document, so it does not claim `SetBasedBatchUpdate`; its chunk is
one `bulkWrite` command of per-document updates, which is a different thing and is named as one.

A relational dialect withholds `AppliedLedger` by leaving `DataMigrationLedgerUpsertSql` null; the
facility then refuses rather than migrating unrecorded.

## Status

`groundwork status` and `groundwork plan` report data migrations per target from provider-owned
state alone — no host transform catalog is needed to read a ledger:

```json
"dataMigrations": [
  { "identity": "2026-08-slugify", "state": "pending", "unit": "orders",
    "rowsScanned": 2, "rowsChanged": 2, "batches": 1, "resumeCursor": "2:i2;" }
]
```

`pending` makes the target pending and the command exit `2`. `applied` carries `completedAt`.
`not-recorded` marks a semantic migration the subject declares that the ledger has never seen.
Plan and status need no transform catalog and do not provision anything. Apply is stricter: the
deployment host exposes its transforms through `ISchemaToolProviderSession.DataMigrationCatalog`,
and all declared identities are resolved before the first target mutates. A missing identity is
refused by name with `GW-MIGRATION-008` rather than silently skipped.

## Expand–contract

A completed data migration is also the gate on the contract half of an expand–contract evolution:
the superseded column cannot be dropped until this ledger records its backfill complete. See
[expand–contract workflows](expand-contract.md).

## Refusal codes

| Code | Meaning |
| --- | --- |
| `GW-MIGRATION-001` | The provider does not advertise a capability the facility requires |
| `GW-MIGRATION-002` | A migration identity was recorded with a different request fingerprint |
| `GW-MIGRATION-003` | The provider session offers no data-migration execution |
| `GW-MIGRATION-004` | The migration cannot be expressed against its subject |
| `GW-MIGRATION-005` | Ledger state is missing, malformed, or self-contradictory |
| `GW-MIGRATION-006` | A transform produced a column it did not declare as a target |
| `GW-MIGRATION-007` | A migration stopped before its source was exhausted and can be resumed |
| `GW-MIGRATION-008` | The declaration names a semantic migration the running host does not supply |
