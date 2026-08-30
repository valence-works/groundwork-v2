# Core Concepts

Read this once and most of Groundwork's API stops being surprising.

---

## Storage unit

A **storage unit** is one logical typed table or collection. It is a plain immutable record —
`Groundwork.Kernel.StorageUnit` — with columns, a key, optional indexes, and optional policies.

```csharp
var unit = new StorageUnit
{
    Id      = new StorageUnitId("orders"),   // stable logical identity
    Name    = "orders",                       // physical table/collection name
    Columns =
    [
        new() { Name = "id",    Type = PortableType.String, MaxLength = 64, IsNullable = false },
        new() { Name = "total", Type = PortableType.Decimal, Precision = 18, Scale = 4 }
    ],
    Key = new KeyDefinition { Columns = ["id"] }
};
```

Or fluently:

```csharp
var unit = StorageUnit.Declare("orders", "orders")
    .String("id", 64, c => c.Required())
    .Decimal("total", 18, 4)
    .Key("id")
    .Build();
```

`Id` is the **logical** identity — it can contain dots and other characters that the physical name
cannot. `Name` is the **physical** identifier and is held to strict portable rules (ASCII, ≤ 63
bytes, no `__groundwork_` prefix). See **[Declaring Storage](Declaring-Storage)**.

The logical id is what **carries identity across a rename**. Schema planning keys its slots on it,
so changing `Name` while keeping `Id` plans as a rename that brings the rows with it, rather than as
a drop and a create. Columns have the same pair: `ColumnDefinition.Id` defaults to the column's
`Name` and only has to be spelled once the physical name changes.

```csharp
// Was "customer"; keeps its rows because it keeps its logical id.
new() { Name = "buyer", Id = "customer", Type = PortableType.String, MaxLength = 64 }
```

Renames are **authorized** work, not automatic: the deployment tool needs the plan fingerprint plus
`--allow-semantic rename-column:orders.buyer`. All four providers honor them there; MongoDB's
in-process `Schema.Apply` now reads the same applied ledger and uses the same rename plan.
See **[Schema Management](Schema-Management)**.

A storage unit has **no provider knowledge whatsoever**. The same object is handed to SQLite and to
MongoDB.

---

## Contract families

A *contract family* is a typed façade that produces a `StorageUnit` and maps your CLR types onto it.
Groundwork ships two, and you can write your own:

- **[Records](Records-Typed-Rows)** — `RecordTable<T>` for ordinary typed rows.
- **[Documents](Documents)** — `DocumentUnit<T>` for typed JSON documents with schema versioning.
- **Your own** — the `samples/Groundwork.Samples.EventLog` project declares a complete event-log
  family in a 20-line fluent chain against the public kernel API only, with **no** reference to
  Records or Documents. That is the proof the kernel is genuinely family-neutral.

Providers never see the family. They see a `StorageUnit` and a dictionary of values.

---

## Connection, session, unit of work

Three objects with three very different lifetimes. Getting these wrong is the most common source of
`ObjectDisposedException` and "why is my session dead?" confusion, so they are worth a table.

| Object | Owns resources? | Disposable? | Lifetime |
| --- | --- | --- | --- |
| `IStorageProviderConnection` | **Yes** — the provider connection, pools, schema locks | **Yes** — dispose it | You control it. Everything below dies with it. |
| `IStorageSession` | Depends on how it was opened | Direct connection sessions are non-owning; `IOwnedStorageSession` sessions are disposable | While its owner is alive and its captured declaration remains current |
| `IOwnedStorageSession` | **Yes** — its provider session resources | **Yes** — dispose it when the operation ends | Until disposed or its captured declaration becomes stale |
| `IUnitOfWork` | **Yes** — its transaction and staged sessions | **Yes** — dispose it | Until commit / rollback / dispose |

```csharp
using var connection = new SqliteProviderFactory().Create("Data Source=app.db");

// A session is a cheap non-owning view. Don't dispose it, don't cache it past the connection.
var session = connection.OpenSession(unit, StorageAccess.Global);

using var work = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, unit);
var staged = work.OpenSession(unit);   // valid only until `work` becomes terminal
work.Stage(RowWrite.Upsert(unit, values));
var report = work.CommitWithOutcomes();
// `staged` is now invalid. Do not touch it.
```

Rules worth memorising:

1. **Keep the connection alive** for every session, schema, catalog, or query operation on it.
2. **Commit and rollback are terminal.** Disposing a non-terminal unit of work rolls it back.
3. **Never retain a session obtained from a unit of work** after that unit is terminal.
4. **Dispose an owned session when its caller-owned operation ends.**
5. **Reopen sessions after applying a changed declaration on their provider connection.** Earlier
   direct, owned, and unit-of-work sessions refuse before provider I/O with `GW-RUNTIME-005`.
6. `RecordTableStoreUnitOfWork<T>` follows exactly the same rules.

Under a host, `Groundwork.Extensions.DependencyInjection` encodes those lifetimes for you — the
connection is a process singleton, sessions and units of work come from a scoped `IGroundworkStorage`,
and scoped sessions are released when that storage scope is disposed. A connection registered any
other way is refused with `GW-HOST-001`. See **[Hosting & Dependency Injection](Hosting-and-Dependency-Injection)**.

---

## Storage access

Every session is opened with an explicit **access context**. There is no ambient/default tenant.

```csharp
StorageAccess.Global                          // for units declared ScopePolicy.Global
StorageAccess.Scoped(new StorageScope("t-1")) // for units declared ScopePolicy.Scoped
StorageAccess.PrivilegedAcrossScopes(audit)   // audited, query-only, across every scope
```

The access kind must match the unit's declared `ScopePolicy` — opening a scoped unit with
`Global` (or vice versa) throws before any I/O. Privileged access requires a
`StorageAccessAudit` carrying a non-blank identity and purpose, and is **query-only**: reads,
writes, append, retention, aggregation, inspection, and `BeginUnitOfWork` all refuse.

See **[Multi-Tenancy & Scopes](Multi-Tenancy-and-Scopes)**.

---

## Capabilities

A connection advertises what the **deployed** database can actually do:

```csharp
var canCompareAndDelete = connection.Capabilities
    .Any(c => c.Id == BatchWriteCapabilities.CompareAndDelete);
```

This is not a static per-provider list. Transactional MongoDB advertises compare-and-delete,
exact append, durable high-water inspection, and provider sequences; **standalone MongoDB
advertises none of them** because it cannot provide the required transaction semantics.

The corresponding optional interfaces (`IExactAppendStorageSession`, `IStorageInspectionSession`,
`ICompareAndDeleteStorageSession`, `IPrivilegedCrossScopeQuerySession`) are implemented only when the
capability is real. The public extension methods check the capability and refuse with a stable code
(`GW-APPEND-003`, `GW-INSPECT-001`, `GW-ACCESS-002`) **before** attempting provider work.

Descriptors also carry honest cost information — e.g. MongoDB's exact-outcome descriptor says
"one `FindOneAndUpdate` per coalesced row" rather than pretending it is one round trip.

See **[Capabilities Reference](Capabilities-Reference)**.

---

## Opt-in machinery

Nothing is added to your schema that you did not declare.

| Feature | Opt in with | If you don't |
| --- | --- | --- |
| Optimistic concurrency | `ConcurrencyDeclaration.Optimistic()` / `.OptimisticConcurrency()` | No version column, no CAS work, and `IfVersion`/`CreateOnly` are **rejected** (`GW-WRITE-CONCURRENCY-001`) |
| Idempotent append | `AppendIdempotency(window)` | `Append` is unavailable on the unit |
| Retention | `Retention(...)` / `KeepNewest(...)` | No cleanup happens |
| Exact retention replay | `RetentionIdempotency(window)` (requires `Retention`) | Retention runs status-only, still resumable |
| Multi-tenancy | `.Scoped()` | Unit is global |
| Aggregation | `AggregationProfiles` / `.Aggregate(...)` | `session.Aggregate(...)` has no profile to name; typed Records bindings require a declared profile |
| Provider sequence | `ColumnGeneration.ProviderSequence` | You supply keys yourself |

This is deliberate. A unit with `ConcurrencyDeclaration.None` produces exactly the columns you
declared and no version read/increment/CAS work on the write path.

---

## Refusal over approximation

The single most important thing to internalise. Groundwork **refuses** anything whose meaning would
differ across providers, rather than delegating to each database's dialect:

- Predicates are **two-valued**. There is no `UNKNOWN`. Missing is exactly `null`, and `Not(p)` is
  the exact complement of `p`.
- Text predicates accepted for provider planning are **`Ordinal`**. Culture, ICU, and accent-sensitive
  predicates are refused. Case-insensitive prefix matching requires a declared, versioned persisted
  search key. Locale ordering requires a declared, versioned ICU sort key; providers order its portable
  text encoding ordinally.
- Only exact `Int32`, `Int64`, and declared `Decimal(18,4)` are portable for **comparison**. Binary
  floating point is **storable but not comparable**: a `Double` column can be declared, written, and
  read back bit-for-bit, and is refused in predicates, ordering, and indexes.
- Date/times are compared as **UTC ticks**; `DateTime` and unspecified/local kinds are not accepted.
- GUID equality and ordering use an **RFC-4122 network-byte key**, so ordering is stable across
  providers rather than following each store's internal byte layout.
- One schema-declared reference can be activated explicitly with `.Join(reference)`; arbitrary,
  undeclared, deeper, or multiple joins remain refused.

Every refusal carries a stable `GW-*` code, the offending path, and a named alternative. See
**[Portable Semantics](Portable-Semantics)** and **[Diagnostics Reference](Diagnostics-Reference)**.

---

## Coverage

A query that no declared index can serve is **refused**, not silently turned into a table scan:

- at **build time** by `Groundwork.Analyzers`, and
- at **runtime** by `RuntimeCoverageGate` / `QueryCoverageEnforcer` (`QueryCoverageException`).

Runtime coverage intersects your *declared* indexes with the *deployed* catalog, so an ad-hoc index
someone added in the database cannot rescue a query during a rolling deploy.

If you genuinely need a scan, say so explicitly and take ownership of it:

```csharp
query.AcceptScan("GW-SCAN-0007", "admin export, <10k rows", "platform-team", "2026-12-31")
```

A `#pragma warning disable` does **not** work: it silences the analyzer but leaves no
`ScanAcceptance` on the request, so the runtime gate still refuses. See
**[Query Coverage & Indexes](Query-Coverage-and-Indexes)**.

---

## Schema is deployment-time work

Runtime admission is **inspect-only** by default. `connection.Schema.Apply(unit)` exists and is used
freely in tests and local development. It takes no authorization callback, so it performs only what
re-applying the same declaration could put back — creates, adds, renames, widenings, index rebuilds —
and **refuses to destroy what it could not restore** (`GW-SCHEMA-010`): dropping a column or its
storage, or narrowing a column past the values in it. Production physical schema changes belong to
the `groundwork` CLI, which requires explicit authorization (`--safe`, or an exact plan fingerprint
plus per-operation ids for destructive/semantic work). Startup admission is the gate that protects a
running application: it refuses unauthorized destructive (`GW-SCHEMA-007`) and semantic
(`GW-SCHEMA-008`) work.

At startup, providers compare the deployed catalog against the compiled target:
- **Column drift** (missing/changed columns, collation, search-key algorithm) is **startup-fatal** (`GW-RUNTIME-001`).
- **Index drift** is reported separately (`GW-RUNTIME-002`) and only makes *dependent query shapes* refuse.

See **[Schema Management](Schema-Management)**.

---

## Next

- **[Declaring Storage](Declaring-Storage)** — build a real declaration
- **[Records: Typed Rows](Records-Typed-Rows)** — the ordinary typed path
