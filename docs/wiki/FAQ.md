# FAQ

### Is Groundwork an ORM?

No. There is no change tracking, lazy loading, or implicit navigation discovery. Groundwork is a
provider-neutral storage kernel: you declare typed storage and references, then read and write them
explicitly. The query model supports one explicitly activated schema-declared reference join.

### Why is `IGwQueryable<T>` not `IQueryable`?

Because `IQueryable` accepts *any* expression tree and only decides at runtime whether it can
translate it — leading to client-side evaluation, silent full scans, or a runtime exception in
production. A closed surface accepts only what it can translate and tells you **at build time**, with
a `GW-LINQ-*` code and a named fix.

### Why can't I use an arbitrary LINQ join?

A portable join across four very different stores must have one schema-owned shape. Bind a direct
navigation with `table.Reference(...)` and activate it with `.Join(reference)`. Arbitrary
`Join`/`GroupJoin`, undeclared navigations, deeper chains, and multiple joins remain refused as
`GW-LINQ-104`. Provider execution also stays fail-closed until that provider advertises the
corresponding rendering capability.

### Can I store a `double`?

Yes — `PortableType.Double` is storable on all four providers and round-trips bit-for-bit. You cannot
*compare* one. Binary floating-point comparison, rounding, and index behavior differ across stores, so
an index seek and a scan can disagree about the same row; predicates and ordering are refused
(`GW-SEM-TYPE-006`), and a key, index, or grouping column is refused at declaration
(`GW-PORT-012`). Use `Int32`, `Int64`, or declared `Decimal(18,4)` for a value you query on.

### Why was my `double` write refused?

Because it was NaN, an infinity, or negative zero (`GW-VALUE-DOUBLE-001`). Those are the values the
four stores do not agree on: SQL Server refuses all three of the first kind, SQLite refuses NaN, and
SQLite and MongoDB both return positive zero for a stored negative zero. Groundwork refuses them at
the write rather than storing a value a different provider would hand back differently.

### Why is there no `Single`?

SQLite `REAL` and BSON `double` are both IEEE-754 binary64. A `Single` column would be a widened
binary64 column on half the supported providers, which is a different bargain from the one `Double`
makes. Declare `Double`.

### Why do I have to name a null order on every sort term?

Because `ProviderDefault` is exactly the setting that makes PostgreSQL and SQL Server return the same
query in a different order. Naming it makes paging deterministic (`GW-SEM-ORDER-004`).

### Why is my case-insensitive query refused?

Groundwork emits no database-side case folding. Case-insensitive matching requires a declared,
versioned **persisted search key**, and the query's comparison policy must match it exactly
(`GW-SEM-TEXT-001`, `GW-QUERY-031`). Ordinal `StartsWith` needs none of this — it lowers to an exact
range on the base column.

### Can I just make Groundwork do a table scan?

Yes, explicitly:

```csharp
[assembly: GwAllowAcceptedScans]

query.AcceptScan("GW-SCAN-0007", "admin export, <10k rows", "platform-team", "2026-12-31");
```

Four mandatory arguments: id, reason, owner, expiry. Markers expire and are inventoried
(`GW-COVER-905`), so scan debt stays visible.

### Why doesn't `#pragma warning disable` silence a coverage error?

Deliberately. The pragma silences the analyzer but leaves no `ScanAcceptance` on the request, so the
runtime gate still refuses. The acceptance has to be a value in the query, not a comment in the source.

### Do I need `Groundwork.Records` or `Groundwork.Records.Store`?

**`Groundwork.Records.Store`.** It adds `table.Open(connection)` and the typed unit of work.
`Groundwork.Records` is deliberately provider-free, which is useful when a *library* declares storage
without picking a provider.

### Do I need the analyzer?

Not to run, but strongly recommended. It moves coverage and portability failures from runtime to your
editor.

### Can I use Groundwork with an existing database?

Only if the physical schema matches a Groundwork declaration exactly. Column drift is startup-fatal
(`GW-RUNTIME-001`), and there is no mapping layer to bridge a mismatch. `groundwork adopt` can record
an exactly matching catalog without executing DDL; otherwise create Groundwork-owned storage and
backfill it rather than weakening the declaration. See **[Migrate from EF Core](EF-Core-Migration)**
for the inventory, adoption, dual-write, and cutover path.

### Can I mix Records and Documents in one transaction?

Yes. Documents map to plain `RowWrite` values, so a single `IUnitOfWork` can stage writes for a
Records unit, a Documents unit, and a raw kernel unit.

### How do I do a "get or create"?

`session.Insert(values, WriteOptions.CreateOnly)` — on an optimistic unit, a conflict returns a status
rather than throwing. Or `Upsert` if you don't need to distinguish.

### Is `ConcurrencyConflict` an exception?

No — a `WriteOutcomeStatus`. Branch on it. Exceptions are reserved for genuinely exceptional
conditions.

### Why is `WriteOutcome.Detail` lazy?

A zero-row conditional write cannot always distinguish "missing" from "stale version" without another
read. `Status` is returned immediately (conservatively); `Detail` performs that disambiguation once
and caches it. If you don't need to know, you don't pay.

### Can I trust the provider sequence as an ordering of commits?

No. Allocation is **strictly increasing**, but concurrent transactions can commit or become visible in
a different order — so the value is not a commit-order timestamp. **Gaps are valid.** It is an
identity, not a clock.

### Why does MongoDB need a replica set?

Transactions. Sequences, idempotent append, exact append, durable high-water inspection, exact
retention, compare-and-delete, and atomic commit all require them. Standalone MongoDB does not
advertise those capabilities and refuses before doing I/O.

### Why is SQLite rejecting a second connection?

The store holds one `${database}.schema.lock` file handle for its lifetime, so the second opener —
another process or this one — is refused with `GW-SQLITE-LIFETIME-001`. Use **one
`IStorageProviderConnection` per database file, for the life of the process** — not one per request.
Under a host, `AddGroundwork().AddConnection(...)` does that for you; see
**[Hosting & Dependency Injection](Hosting-and-Dependency-Injection)**.

### Can I run migrations between preview versions?

No. Groundwork v2 is a **clean-break** pre-1.0 product. When a release note marks a persisted schema
boundary, discard the earlier catalog and create a fresh one. There is deliberately no in-place
migration, alias, dual-write, or fallback.

### Is it production-ready?

Not yet declared so. All providers are **conformance-passing / preview**. Production support
additionally requires a supported topology, operational guidance, and an owner for provider-specific
incidents — see **[Versioning & Support](Versioning-and-Support)**.

### Where's the async API?

On every operation. `IStorageSession` declares each operation twice — `Read`/`ReadAsync`,
`Insert`/`InsertAsync`, and so on through query, aggregate, write, and append — and every optional
capability does the same, alongside `CommitAsync`/`CommitWithOutcomesAsync` and the LINQ terminals.
Both surfaces are supported: the async one is what a server-side host should use, and the sync one
stays because removing it would only move the blocking call into your code.

Whether a call actually yields its thread depends on the driver underneath. PostgreSQL, SQL Server,
and MongoDB do. SQLite does not — Microsoft.Data.Sqlite completes its async surface synchronously —
and neither does the in-memory reference provider. See
**[the async surface reference](https://github.com/valence-works/groundwork-v2/blob/main/docs/v2/async-surface.md)**.

### Can I write my own provider or storage family?

Yes — both are first-class. See **[Extending: Writing a Provider](Extending-Writing-a-Provider)**. The
event-log sample is a complete family in 20 lines against the public kernel API, with no Records or
Documents dependency.

### How do I know what a provider actually supports at runtime?

```csharp
connection.Capabilities.Any(c => c.Id == BatchWriteCapabilities.ExactRetention)
```

Check capabilities at **startup**, not at first use. See
**[Capabilities Reference](Capabilities-Reference)**.

### Why so many refusals?

Every refusal exists because the alternative is a query or write that behaves differently on a
different database — usually discovered in production, months later, by a customer. Groundwork moves
that discovery to compile time or admission time, with a code and a named fix.

### Where do I report a bug?

[valence-works/Groundwork/issues](https://github.com/valence-works/Groundwork/issues). Include the
`GW-*` code, the declaration, and the provider.
