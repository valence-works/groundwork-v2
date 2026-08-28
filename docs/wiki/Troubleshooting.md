# Troubleshooting

Symptom → cause → fix. Codes link to **[Diagnostics Reference](Diagnostics-Reference)**.

---

## Install and restore

### `Groundwork.*` package not found

Groundwork is **not on nuget.org**. Configure the Feedz source and package source mapping — see
**[Installation](Installation)**.

### Restore resolves a different version on CI than locally

You are missing `<packageSourceMapping>`. Without it, restore order decides which feed wins.

### `TypeLoadException` / `MissingMethodException` between Groundwork assemblies

Mixed preview versions. **Every `Groundwork.*` package must be on the same exact version.**

```bash
dotnet list package --include-transitive | grep Groundwork
```

### `table.Open(connection)` does not compile

You referenced `Groundwork.Records` instead of **`Groundwork.Records.Store`**. The bridge lives in the
`.Store` package.

---

## Declaration

### `GW-PORT-010` — invalid physical identifier

Physical names must be ASCII letters/digits/underscores, start with a letter or underscore, be
**≤ 63 ASCII bytes**, and not use the `__groundwork_` prefix. The message names the offending
identifier.

### `GW-PORT-002` — decimal missing precision/scale

```csharp
.Decimal("total", 18, 4)                            // ✅
.Decimal("total", c => c.Precision(18, 4))          // ✅
.Decimal("total")                                    // ❌
```

Portable *predicates* additionally require exactly `decimal(18,4)` (`GW-SEM-DECIMAL-001`).

### `GW-PORT-003` — index key column needs `MaxLength`

Variable-length index key columns must be bounded so providers can validate native key widths from
the declaration alone.

```csharp
.String("email", 320, c => c.Required())
.Index("by_email", "email")
```

### `GW-PORT-001` — unique index over nullable columns

Multiple nulls in a unique index mean different things on different providers, so Groundwork will not
guess. Either make the columns required, or use `MissingValueBehavior.Excluded`.

### `GW-PORT-005` — `ProviderSequence` misdeclared

Must be **non-nullable `Int64` and the sole primary-key column** of its unit.

### `GW-PORT-009` — duplicate physical index signature

Two indexes produce the same physical index. Consolidate their query purposes onto one.

### `GW-DECL-INDEX-003` — index over a JSON column

JSON columns cannot be index keys. Leave it unindexed, or project a scalar (in Documents, use
`Project(...)`).

---

## Writes

### `GW-WRITE-CONCURRENCY-001` — `IfVersion`/`CreateOnly` rejected

The unit does not declare optimistic concurrency:

```csharp
RecordTable.For<Customer>("customers").Key(c => c.Id).OptimisticConcurrency()
```

### `GW-WRITE-CONCURRENCY-003` — token supplied by the application

The optimistic token is **system-owned**. Remove it from your payload; supply the expectation via
`WriteOptions.IfVersion(...)` / `RecordWriteOptions.IfVersion(...)`.

In Records, a CLR member with the same name as the token is deliberately excluded from mapping and
from queries — it materializes as its default and **must not be used as application state**.

### `ConcurrencyConflict` you didn't expect

Someone else wrote first. Re-read, re-apply, retry — see **[Concurrency](Concurrency)**. Inspect
`outcome.Detail` if you need to distinguish "missing" from "stale" (one cached extra read).

### `Upsert` returned `NotFound`

With a `ProviderSequence` key, a **supplied** key is an immutable locator: it updates an existing row
or returns `NotFound`. It never inserts. Omit the key to request a new generated row.

### A value I expected to be carried forward is missing

The native conditional write path does **not** pre-read the stored row, so the existing row is not a
free source of values. `createdAt`-style values must be preserved by insert-only write construction
(`$setOnInsert` and equivalents), which means declaring them properly rather than relying on
read-then-write.

### MongoDB refuses `ConditionalUpsert` with a generated column

Sequence allocation needs a separate `FindOneAndUpdate` and a transaction, which would violate the
one-command contract. Use ordinary `Insert`/`Upsert`, or drop the generated column.

### Reusing a `RowWrite` instance is rejected

Each `Stage` call is a declaration position. Create a new `RowWrite` per staged occurrence.

### `BatchWriteException`

A failed applied outcome **poisons** the unit of work. Inspect `ex.Outcomes` (attributed applied
failures only — never synthetic successes or superseded declarations), then `Rollback()`.

### `CommitWithOutcomes` throws about aggregate mode

Outcome mode is chosen when the unit begins. Use `BatchWriteOptions.Exact`.

---

## Queries

### `QueryCoverageException` (`GW-COVER-006`)

No candidate covers the query. The candidates are the unit's declared key and its declared indexes,
so a filter on the key — or on the leading columns of a composite key — does not raise this. The
exception usually carries `SuggestedIndex` and a ready-to-paste `SuggestedDeclaration`. A
`GW-COVER-016` refusal has neither, because no ordered index can clear a nonportable shape; rewrite
the query into a portable shape where possible, or accept the scan explicitly. When the predicate
pins every key column with a single-value equality, the message names `session.Read(key)` instead.
For actionable coverage refusals, declare and deploy the suggested index, or accept the scan:

```csharp
query.AcceptScan("GW-SCAN-0007", "reason", "owner", "2026-12-31")
```

with `[assembly: GwAllowAcceptedScans]`.

### `#pragma warning disable GW_COVER_006` didn't help

By design. It silences the analyzer but leaves no `ScanAcceptance` on the request, so the runtime gate
still refuses. Use `.AcceptScan(...)`.

### `GW-COVER-903` — expired scan marker

Scan acceptances expire. Renew it deliberately with a new expiry (and reconsider whether an index is
now the better answer), or add the index.

### `GW-COVER-900` — unresolved composition

The analyzer could not enumerate your query shapes: a loop, an escape, an unknown helper, or a
composition larger than the enumeration limits (2ⁿ for `WhereIf` with n ≤ 6; 32 shapes for conditional
reassignment). Simplify, or downgrade during a migration:

```ini
dotnet_diagnostic.GW_COVER_900.severity = warning
```

### `GW-SEM-ORDER-004` — provider-default null ordering

Name the null rank on every order term. `ProviderDefault` is precisely the setting that makes two
databases disagree.

### `GW-QUERY-031` — search-key policy mismatch

The `ColumnRef` string comparison policy must exactly match the schema's persisted search-key mapping.
Check the declared collation and the query's policy.

### `GW-QUERY-015` — too many `In` values

Default cap is 1,000. Chunk the query, or reshape it as a range/join-free lookup.

### Parameter budget exceeded

SQLite 999, SQL Server 2,100, PostgreSQL 65,535 — including cursor and page parameters. Reduce
membership size or page width.

### Pages repeat or skip rows

Supply your declared identity columns as `QueryRenderOptions.TieBreakColumns`, and name an explicit
null rank on every order term. Prefer `Paging.Keyset` over `Paging.OffsetLimit`.

### `GW-LINQ-107` — opaque helper

Mark the helper `[GwQueryFragment]`.

### `GW-LINQ-104` — cross-table expression

v2 has **no joins**. Use a declared element set, or two queries.

---

## Schema

### `GW-RUNTIME-001` — column drift, startup fatal

The deployed catalog does not match the compiled target. Run `groundwork status` / `plan`, then apply.

### `GW-RUNTIME-002` — index drift

Only dependent query shapes refuse; the app stays up. Deploy the missing index.

> An index that exists in the database but is **not declared** cannot rescue a query. Runtime coverage
> intersects declared with deployed on purpose, so a rolling deploy cannot pass by accident.

### "The derived search key must be rebuilt"

You changed a folded collation or prefix-boundary encoding on an already-deployed column. That is a
**rebuild**, not an additive edit. Plan it as one.

### `groundwork apply` exits `4`

Authorization required. `--safe` for safe work; destructive plans need the current fingerprint plus
every exact operation identity via `--allow-destructive`; semantic migrations need `--allow-semantic`.

### `groundwork status` exits `2`

Not an error — **pending changes**. Treat exit `2` as "work to do" in CI.

### `GW-RETENTION-004`

`RetentionIdempotency` requires `Retention` on the same unit. Declare `Retention(...)` first, or drop
`RetentionIdempotency` for status-only retention.

---

## Providers

### SQLite: `GW-SQLITE-LIFETIME-001`, a second connection is rejected

The store holds one `${database}.schema.lock` file handle for its lifetime. **Use one
`IStorageProviderConnection` per database file, for the life of the process.** Do not create one per
request. It does not matter whether the second opener is another process or the same one.

Under a host, register the connection with `AddGroundwork().AddConnection(...)`, which registers it
as a process singleton — see **[Hosting & Dependency Injection](Hosting-and-Dependency-Injection)**.
In tests, give each test its own database file or use `Data Source=:memory:`. Do not run
`groundwork apply` against a database an application already has open.

### SQLite: version error on open

SQLite **3.35.0+** is required for modern upsert/returning behavior. The exception message includes
the detected version.

### SQLite: `BUSY_SNAPSHOT`

Groundwork uses immediate write transactions specifically to avoid this. If you see it, something else
is holding a conflicting transaction on the same file — check for a second connection.

### SQLite: columns declared `BINARY` / wrong ordinal behavior

A catalog created by a preview earlier than `0.2.0-preview.1`. **Delete and recreate it.** There is
deliberately no migration path.

### MongoDB: capabilities missing

You are connected to a **standalone** deployment. Sequences, append idempotency, exact append,
durable high-water, exact retention, compare-and-delete, and atomic commit all require a
transaction-capable replica set or sharded deployment.

```bash
docker run -d --name gw-mongo -p 27017:27017 mongo:7.0.24 --replSet rs0 --bind_ip_all
docker exec gw-mongo mongosh --quiet --eval \
  'rs.initiate({_id:"rs0",members:[{_id:0,host:"localhost:27017"}]})'
```

### MongoDB: exact batch commits are slow

`CommitWithOutcomes` costs **one `FindOneAndUpdate`-equivalent per coalesced row** — bulk
acknowledgements cannot distinguish inserted from updated. Use `BatchOutcomeMode.Aggregate` when you
do not need per-row evidence.

### MongoDB: pinned index refused for a cross-scope query

One pinned physical index cannot span separate scope collections. Use an **unpinned** cross-scope
query.

### SQL Server: index refused before connecting

The nonclustered key budget (32 columns / 1,700 bytes) is validated from **declared worst-case
widths** — `nvarchar(320)` costs 640 bytes. Reduce `MaxLength`, or use fewer key columns. Folded
prefix indexes expand **5×** (ASCII ignore-case) or **7×** (Unicode ordinal ignore-case).

### SQL Server: `GW-SQLSERVER-LIFECYCLE-001`

An existing lifecycle table has a collation other than `Latin1_General_100_BIN2`. Follow the migration
guidance in the message — Groundwork refuses rather than silently merging case-distinct identities.

---

## Access

### "Cannot open a scoped unit with global access"

The access kind must match the declared `ScopePolicy`. Use `StorageAccess.Scoped(scope)`.

### `GW-ACCESS-003` — point read under privileged access

Privileged cross-scope access is **query-only**, and a point read across scopes is ambiguous. Open an
ordinary scoped session.

### `GW-ACCESS-001` / `-002`

`QueryAcrossScopes` needs `StorageAccess.PrivilegedAcrossScopes(audit)` **and** a provider session
that advertises the capability.

---

## Lifetimes

### `ObjectDisposedException` from a session

You disposed the connection, or the unit of work reached a terminal state. Sessions are non-owning
views: keep the owner alive, and never retain a unit-of-work session past commit/rollback/dispose.

### My unit of work silently rolled back

Disposing a non-terminal unit rolls it back. Call `Commit()` / `CommitWithOutcomes()` explicitly.

---

## Still stuck?

1. Find the `GW-*` code in **[Diagnostics Reference](Diagnostics-Reference)** — every message names a
   corrective action.
2. Read the friction log at `tests/Groundwork.PublicApi.Acceptance.Tests/friction-log.md`; if it is a
   known ergonomic constraint, it is recorded there with its resolution.
3. Open an issue at
   [valence-works/Groundwork](https://github.com/valence-works/Groundwork/issues) with the code, the
   declaration, and the provider.
