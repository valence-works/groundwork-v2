# FAQ

### Is Groundwork an ORM?

No. There is no change tracking, no lazy loading, no navigation properties, and **no joins**. It is a
provider-neutral storage kernel: you declare typed storage, and you read and write it explicitly.

### Why is `IGwQueryable<T>` not `IQueryable`?

Because `IQueryable` accepts *any* expression tree and only decides at runtime whether it can
translate it — leading to client-side evaluation, silent full scans, or a runtime exception in
production. A closed surface accepts only what it can translate and tells you **at build time**, with
a `GW-LINQ-*` code and a named fix.

### Why no joins?

A portable join across four very different stores would either constrain the model to the weakest
provider or produce a different plan on each. v2 instead offers **declared element sets** and
**latest-per-key**, and expects you to issue two queries where a join would have been used. This is
`GW-LINQ-104`.

### Why is `double`/`float` unusable?

Binary floating-point comparison, rounding, and index behavior differ across stores, so an index seek
and a scan can disagree about the same row. Use `Int32`, `Int64`, or declared `Decimal(18,4)`
(`GW-SEM-TYPE-006`).

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
(`GW-RUNTIME-001`), and there is no mapping layer to bridge a mismatch. Greenfield or
recreate-and-reload is the intended path today.

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

Async is available where it matters most today: unit-of-work commits (`CommitAsync`,
`CommitWithOutcomesAsync`) and the LINQ terminals (`ToListAsync`, `CountAsync`, `AnyAsync`). Direct
session reads and writes are currently synchronous.

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
