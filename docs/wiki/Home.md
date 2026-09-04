# Groundwork v2

Groundwork is a **provider-neutral persistence kernel for .NET**. You declare typed storage
once — columns, a key, indexes, scope, concurrency, lifecycle — and map that single declaration
to SQLite, PostgreSQL, SQL Server, MongoDB, or MySQL without provider concerns leaking into your
model.

```csharp
var table = RecordTable.For<Customer>("customers")
    .Key(customer => customer.Id)
    .OptimisticConcurrency()
    .Column(customer => customer.Email, column => column.MaxLength(320).Required())
    .Index("by_email", customer => customer.Email)
    .Build();

using var connection = new SqliteProviderFactory().Create("Data Source=customers.db");
connection.Schema.Apply(table.Definition);

var records = table.Open(connection);
records.Insert(new Customer(Guid.NewGuid(), "ada@example.test", "Ada"));

var matches = records.Query(
    table.Query.Where(customer => customer.Email == "ada@example.test"),
    RecordQueryOptions.UsingIndex("by_email"));
```

Swap `SqliteProviderFactory` for `PostgreSqlProviderFactory`, `SqlServerProviderFactory`,
`MongoProviderFactory`, or `MySqlProviderFactory` and nothing above changes. Tests can use the same
declaration with `InMemoryProviderFactory`.

> **Status: pre-1.0 preview.** The current published release is **`0.4.0-preview.11`**. Packages are
> published to a public Feedz source, not nuget.org. Read
> [Versioning & Support](Versioning-and-Support) and [Installation](Installation) before adopting.

Use the wiki **Search** box to find a symbol or diagnostic. On a small screen, where the sidebar can
collapse, the complete **All pages** inventory below is the mobile navigation.

---

## What makes Groundwork different

Most .NET data-access libraries let the database's semantics leak through and hope you notice.
Groundwork's design position is the opposite: **anything that cannot mean the same thing on every
supported provider is refused, loudly, with a stable diagnostic code and a named alternative.**

| Principle | What it means in practice |
| --- | --- |
| **Refuse, don't approximate** | `NULL` three-valued logic, native culture-sensitive collation, `float` predicates, and undeclared or arbitrary cross-table joins are refused rather than silently degraded per provider. One schema-declared reference can be activated explicitly. Locale ordering requires a declared, versioned ICU sort-key projection. |
| **Opt in to machinery** | No version column unless you declare `OptimisticConcurrency()`. No idempotency ledger unless you declare `AppendIdempotency(...)`. You never pay for what you didn't ask for. |
| **Capabilities are advertised, not assumed** | A connection tells you what the *deployment* can do. Standalone MongoDB simply does not advertise compare-and-delete, and the call refuses before doing I/O. |
| **Coverage is enforced** | A query that no declared index can serve is refused at build time (analyzer) *and* at runtime (gate) unless you explicitly accept a scan with an owner and expiry. |
| **Schema is deployment-time work** | Runtime is inspect-only by default. Applying physical schema is an explicit, authorized `groundwork` CLI operation. |

If you want an ORM that "just works" against whatever's in the connection string, this is not that.
If you want a storage contract you can reason about and move between databases, it is.

---

## Where to start

**New to Groundwork?** Read these three in order:

1. **[Installation](Installation)** — package feed, package selection, first project.
2. **[Core Concepts](Core-Concepts)** — storage units, sessions, access, lifetimes. The mental model.
3. **[Records: Typed Rows](Records-Typed-Rows)** — the ordinary path for typed CRUD.

**Coming from EF Core?** Start with **[Migrate from EF Core](EF-Core-Migration)** for the concept
map, model-import report, catalog adoption boundary, and a complete customer/order cutover.

**Evaluating it?** [Versioning & Support](Versioning-and-Support), [Providers](Providers), and
[Portable Semantics](Portable-Semantics) tell you what you'd be committing to.

---

## All pages

### Getting started
- **[Installation](Installation)** — feed configuration, package source mapping, versions
- **[Package Map](Package-Map)** — which package to reference, and why the layering matters
- **[Core Concepts](Core-Concepts)** — the mental model: units, sessions, access, capabilities, lifetimes
- **[Migrate from EF Core](EF-Core-Migration)** — scaffold, reconcile, deploy, and cut over an EF application

### Declaring your storage
- **[Declaring Storage](Declaring-Storage)** — the kernel declaration builder, columns, keys, indexes
- **[Portable Semantics](Portable-Semantics)** — the type system, text/collation rules, what is refused
- **[Schema Management](Schema-Management)** — apply/diff, the `groundwork` CLI, MSBuild verification, drift

### Reading and writing
- **[Records: Typed Rows](Records-Typed-Rows)** — `RecordTable<T>`, typed CRUD, typed projections
- **[Documents](Documents)** — typed JSON documents with versioning and upcasters
- **[Writing Data](Writing-Data)** — preconditions, outcomes, generated values
- **[Concurrency](Concurrency)** — opt-in optimistic tokens, conditional writes, compare-and-delete
- **[Unit of Work & Batching](Unit-of-Work-and-Batching)** — staged writes, coalescing, exact vs aggregate outcomes
- **[Querying](Querying)** — the query model, the closed LINQ front-end, paging, keyset continuation
- **[Query Coverage & Indexes](Query-Coverage-and-Indexes)** — the analyzer, the runtime gate, accepted scans

### Advanced scenarios
- **[Streams: Sequences, Append & Retention](Streams-Append-and-Retention)** — provider sequences, idempotent append, retention, durable high-water
- **[Aggregation](Aggregation)** — declared aggregation profiles, time buckets, reducers
- **[Multi-Tenancy & Scopes](Multi-Tenancy-and-Scopes)** — scoped units and audited cross-scope recovery

### Operating
- **[Hosting & Dependency Injection](Hosting-and-Dependency-Injection)** — `AddGroundwork()`, named connections, the connection lifetime, startup admission, health checks
- **[Providers](Providers)** — per-provider behavior, connection strings, deployment requirements
- **[Testing](Testing)** — the in-memory provider, conformance suites, concurrency harness
- **[Troubleshooting](Troubleshooting)** — symptom → cause → fix
- **[Diagnostics Reference](Diagnostics-Reference)** — every `GW-*` code, grouped
- **[Capabilities Reference](Capabilities-Reference)** — every capability id and what advertising it promises

### Reference
- **[Versioning & Support](Versioning-and-Support)** — the frozen 1.0 contract, 1.x evolution rules, final preview transition, and support matrix
- **[Extending: Writing a Provider](Extending-Writing-a-Provider)** — the complete provider boundary, reusable substrate, and conformance proof
- **[FAQ](FAQ)**

---

## The 60-second mental model

```text
                        your code
                            │
      ┌─────────────────────┼─────────────────────┐
      │                     │                     │
 Groundwork.Records   Groundwork.Documents   (your own family)
  typed rows           typed JSON docs        e.g. an event log
      │                     │                     │
      └─────────────────────┼─────────────────────┘
                            │
                    Groundwork.Store          ← the runtime contract you actually call
                 IStorageProviderConnection
                 IStorageSession / IUnitOfWork
                            │
      ┌──────────┬──────────┼──────────┬──────────┬──────────┐
   Sqlite   PostgreSql  SqlServer   MongoDb    MySql     Testing
                            │
                    Groundwork.Kernel         ← declarations, portability rules
                 Groundwork.Query.Model       ← the predicate AST, BCL-only
```

Everything depends **inward**. A declaration never knows about a provider. A provider never knows
about `Records` or `Documents`. That is what makes the same declaration runnable on five databases —
and what makes it possible for you to add another family or provider without forking.

---

*Source and issues: [valence-works/groundwork-v2](https://github.com/valence-works/groundwork-v2). Licensed MIT.*
