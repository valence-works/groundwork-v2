# Providers

One declaration, five databases. This page is what you need to know about each before choosing.

## Support matrix

“**Conformance**” means the provider passes the provider-neutral contract suites.
“**Production-supported**” additionally requires a supported deployment topology, operational
guidance, and an owner for provider-specific incidents. In the first preview, **nothing is
production-supported yet** — conformance is evidence of contract behavior, not a support promise.

| Provider | Status | Required topology |
| --- | --- | --- |
| **SQLite** | Conformance-passing / preview | File-backed or in-memory, with the documented connection lifetime. **SQLite 3.35.0+**. |
| **MySQL/MariaDB** | Conformance-passing / preview | MySQL 8.0.17+ or MariaDB 11.4.13+ with InnoDB and NO PAD `utf8mb4_0900_bin` |
| **PostgreSQL** | Conformance-passing / preview | PostgreSQL 17-compatible |
| **SQL Server** | Conformance-passing / preview | SQL Server 2022-compatible |
| **MongoDB** | Conformance-passing / preview | **Replica set or sharded** for transactional and exact-append behavior |
| `Groundwork.Testing` | Public provider-author package | Deterministic reference provider — **not an application database** |
| `Groundwork.Tool` | Preview | Deployment-time planning + explicit authorization only |

> **MongoDB standalone is intentionally not production-supported.** It cannot provide the
> transaction/session guarantees required by exact append and durable idempotency, so it simply does
> not advertise those capabilities.

## Capability differences at a glance

| Capability | SQLite | MySQL/MariaDB | PostgreSQL | SQL Server | MongoDB (replica set) | MongoDB (standalone) |
| --- | :-: | :-: | :-: | :-: | :-: | :-: |
| Atomic commit | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ |
| Provider sequence | ✅ | ✅ | ✅ | ✅ | ✅ (+1 command) | ❌ |
| Append idempotency | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ |
| Exact append outcomes | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ |
| Durable high-water inspection | ✅ | ❌ | ✅ | ✅ | ✅ | ❌ |
| Exact retention | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ |
| Compare-and-delete | ✅ | ❌ | ✅ | ✅ | ✅ | ❌ |
| Native multi-row batch | ✅ | ❌ (row fallback) | ✅ | ✅ | ✅ (aggregate mode) | — |
| Cross-scope query | ✅ | ✅ | ✅ | ✅ | ✅ (unpinned only) | — |
| Native index hints | ❌ (no syntax) | ❌ (no syntax) | ❌ (no syntax) | ✅ | ✅ | — |

> **A declared key is a coverage candidate on every provider.** A query filtering on the key — or on
> the leading columns of a composite key — is admitted without a separate `[GwIndex]`, because every
> relational coordinator emits the key as the table's `PRIMARY KEY` and the engine backs that with a
> unique index. MongoDB reaches the same verdict but not yet the same plan: it stores the key in
> `_id` while the renderer filters on the declared field names, so the read is admitted and then
> scans. See [Query Coverage & Indexes](Query-Coverage-and-Indexes#what-counts-as-a-covering-index)
> and [#238](https://github.com/valence-works/groundwork-v2/issues/238).

**Always check at runtime rather than reading this table into your code:**

```csharp
if (!connection.Capabilities.Any(c => c.Id == BatchWriteCapabilities.ExactRetention))
    // degrade gracefully
```

---

## SQLite — `Groundwork.Sqlite`

```csharp
using var connection = new SqliteProviderFactory().Create("Data Source=app.db");
using var memory     = new SqliteProviderFactory().Create("Data Source=:memory:");
```

- Uses `Microsoft.Data.Sqlite`. Enables **WAL** and a busy timeout when a store opens.
- Schema and unit-of-work writes use `BeginTransaction(IsolationLevel.Serializable, deferred: false)`,
  which maps to an **immediate write transaction** — avoiding a read-to-write upgrade and the
  resulting `BUSY_SNAPSHOT` failure.
- **Requires SQLite 3.35.0+** for modern upsert/returning behavior. Opening an older native library
  fails with the version in the message.

### The schema lock

The store holds **one `${database}.schema.lock` file handle for its lifetime**. A second process or
connection to the same file is **rejected before schema work begins**. The handle is not opened and
closed around individual writes.

> This is the single most common SQLite surprise. One `IStorageProviderConnection` per database file,
> for the life of the process. Don't create one per request.

The second opener — another process, or this one — is refused with `GW-SQLITE-LIFETIME-001`, whose
message names the file and the fix. Under a host, register the connection with
`AddGroundwork().AddConnection(...)`: see
**[Hosting & Dependency Injection](Hosting-and-Dependency-Injection)**.

### Other notes

- SQLite index names are **database-global**, so logical names are stored with a provider prefix while
  the catalog exposes the declared logical names.
- SQLite cannot alter column nullability in place. Adding a **required** column is staged as nullable,
  backfilled from its portable default, and finalized by a **transactionally rebuilt table** using
  SQLite's recorded `CREATE TABLE`/index SQL — preserving rows, keys, constraints, and indexes.
- Portable ordinal strings use the registered `GROUNDWORK_UTF16_ORDINAL` collation, and ordinary
  indexes inherit it, so predicates and index ordering use true .NET UTF-16 ordinal semantics
  including supplementary characters.

> ⚠️ **Catalogs created by a v2 preview earlier than 0.2.0-preview.1 physically declare those columns
> as `BINARY` and are not compatible.** Delete and recreate them. There is deliberately no migration,
> alias, dual-write, or compatibility mode.

---

## MySQL/MariaDB — `Groundwork.MySql`

```csharp
using var connection = new MySqlProviderFactory().Create(
    "Server=localhost;Database=app;User ID=app;Password=…");
```

- Uses `MySqlConnector` and the shared relational session/unit-of-work substrate.
- Requires InnoDB plus a NO PAD `utf8mb4_0900_bin`; startup refuses incompatible collation
  semantics rather than weakening ordinal key equality.
- Provider sequences use `AUTO_INCREMENT`; schema coordination uses connection-bound
  `GET_LOCK`/`RELEASE_LOCK` leases and durable fences.
- Parameter budget: **65,535**, advertised by the provider.
- No portable native index-hint contract is advertised.

Test/CI connection variable: `GROUNDWORK_MYSQL_CONNECTION`.

---

## PostgreSQL — `Groundwork.PostgreSql`

```csharp
using var connection = new PostgreSqlProviderFactory().Create(
    "Host=localhost;Port=5432;Database=app;Username=app;Password=…");
```

- Uses **Npgsql**. PostgreSQL 17-compatible.
- Provider sequences use `GENERATED BY DEFAULT AS IDENTITY`, read from `RETURNING`.
- Native batches use one multi-row `INSERT … ON CONFLICT … RETURNING`, including the partial-index
  conflict predicate and the generated optimistic version.
- A declared **secondary unique index** selects the row-attributed fallback so one constraint error
  cannot be reported for every row.
- Parameter budget: **65,535** — advertised by the connection to the runtime value fence, not restated by it.
- No index-hint syntax — declarations are retained for diagnostics but never emitted as hints.
- Explain assertions use `EXPLAIN (FORMAT JSON)` and are labeled `optimizer-selected`.

Test/CI connection variable: `GROUNDWORK_POSTGRES_CONNECTION`.

---

## SQL Server — `Groundwork.SqlServer`

```csharp
using var connection = new SqlServerProviderFactory().Create(
    "Server=localhost,1433;Database=app;User Id=sa;Password=…;Encrypt=False;TrustServerCertificate=True");
```

- Uses `Microsoft.Data.SqlClient`. SQL Server 2022+.
- **Native typed tables and nonclustered primary/secondary indexes.** No document envelope, no
  synthetic identity column.
- Schema coordination uses one database-scoped `sp_getapplock` plus per-target durable fences/history,
  so applications of different declarations serialize before reading the shared server catalog;
  optimistic concurrency uses serializable write transactions. Earlier previews used target-specific
  application locks, so rolling upgrades must pre-apply schema or restrict schema application to one
  release line until all schema-applying instances are current.
- Provider sequences use `IDENTITY(1,1)`, read from `OUTPUT INSERTED`.
- Parameter budget: **2,098 caller-owned parameters** — SQL Server's 2,100 statement limit leaves two
  slots for the `Microsoft.Data.SqlClient` `sp_executesql` wrapper; the connection advertises this
  effective ceiling to the runtime value fence, not restating it in the fence.
- Supports native index hints when a declaration is `QueryIndexPinning.Pinned`. Explain assertions use
  showplan XML and are labeled `hinted`.

### Index key budget

SQL Server limits nonclustered index keys to **32 columns and 1,700 bytes**. Groundwork validates
this while applying the declaration using **declared worst-case widths** — `nvarchar(320)` contributes
640 bytes. Consequently:

- Variable-length key columns **must** declare `MaxLength`.
- Unbounded strings, binary values, JSON, and unsupported collations are refused **before opening a
  provider connection**.
- Decimal keys use SQL Server's native precision tiers (5/9/13/17 bytes).
- Folded prefix indexes target provider-owned ASCII search-key columns, validated against the logical
  source width with an expansion factor of **5×** (ASCII ignore-case) or **7×** (Unicode ordinal
  ignore-case).

### Batch path

`MERGE … WITH (HOLDLOCK)` against a **durable schema-owned table-valued parameter**, so a 1,000-row
homogeneous group stays one command. Installations without the type fall back to the
parameter-limited `VALUES` form. Row-specific version predicates and declared secondary unique indexes
retain the single-row path.

### Lifecycle collation

Lifecycle identity columns are created with `Latin1_General_100_BIN2` so unit/scope/nonce values use
ordinal identity. Existing lifecycle tables with a different collation are **refused with migration
guidance** (`GW-SQLSERVER-LIFECYCLE-001`) rather than silently merging case-distinct identities.

Test/CI connection variable: `GROUNDWORK_SQLSERVER_CONNECTION`.

---

## MongoDB — `Groundwork.MongoDb`

```csharp
using var connection = new MongoProviderFactory().Create(
    "mongodb://localhost:27017/app?replicaSet=rs0");
```

> Use **`MongoProviderFactory`** (the provider-neutral `IStorageProviderFactory`).
> `MongoDbProviderFactory` returns the native `IMongoProviderConnection` and is for advanced/adapter
> use.

### Deployment requirement

A **transaction-capable replica set or sharded deployment** is required for provider sequences,
idempotent append, exact append, durable high-water inspection, exact retention, compare-and-delete,
and atomic commit. `IMongoProviderConnection.ProviderSequenceFit` reports `ProviderFit.Unsupported` on
standalone, and those capabilities are omitted from the connection's advertisement.

Standalone MongoDB is fine for basic typed CRUD. It is not fine for streams.

### Behavior notes

- Native typed BSON fields with declared native indexes.
- Provider sequences use `FindOneAndUpdate` on the kernel-owned `__groundwork_sequences` counter, in
  the **same transaction** as the row — **one extra command per inserted/coalesced exact row**.
- `ConditionalUpsert` **refuses** `ProviderSequence` columns (would break the one-command contract).
- **Aggregate** commits use one unordered `BulkWrite` of `UpdateOneModel` upserts.
  **`CommitWithOutcomes` costs one `FindOneAndUpdate`-equivalent per coalesced row**, because bulk
  acknowledgements cannot distinguish inserted from updated.
- Retention walks a disk-spillable bounded-batch partition projection, then `deleteMany`s at most one
  configured batch beyond each partition's watermark. It **never** gathers a whole partition into an
  array. Capped collections are deliberately not used.
- Queries render to `MongoQueryCommand` with native BSON filter and sort; an aggregation pipeline is
  used when explicit null ranks or a count are required.
- Cross-scope queries **cannot** use one pinned physical index across separate scope collections and
  refuse that combination. Unpinned cross-scope queries work.
- Transactional same-identity races return portable deterministic outcomes; wrapper-owned transactions
  retry transient write-conflict bodies. The transaction wrapper closes an already-aborted transaction
  after a duplicate-key outcome so the portable `UniqueViolation` result surfaces.
- Explain assertions use `explain` with `executionStats` (winning-plan `IXSCAN`), labeled `hinted`.

### Query coverage

MongoDB executes LINQ terminals through `GwLinqExecutor` and the shared `RuntimeCoverageGate`, the
same as the relational providers. Coverage is decided from the **declaration** — the declared indexes
intersected with the deployed catalog — not from what the Mongo planner would choose, so an extra
native index nobody declared never satisfies a declared index. A shape the shared checker cannot
prove is refused with the same `GW-COVER-*` code and the same named fix as on SQLite, PostgreSQL, and
SQL Server; it is never silently permitted.

MongoDB has no bound-parameter budget — its real bound is the 16 MB command document — so its
connection advertises no parameter ceiling. Ordinary membership retains the portable 1,000-value
renderer limit. Keyed batch reads advertise an effectively unbounded key count plus a conservative
15 MiB encoded-payload fence, allowing the shared planner to split commands before BSON refusal.

Test/CI connection variables: `GROUNDWORK_MONGO_CONNECTION`,
`GROUNDWORK_MONGO_STANDALONE_CONNECTION`.

---

## In-memory reference provider — `Groundwork.Testing`

```csharp
using var connection = new InMemoryProviderFactory().Create("my-test-store");
```

Deterministic, no external service, implements the same contracts. Use it for unit tests and as the
reference behavior when authoring a provider. **Not an application database.** See
**[Testing](Testing)**.

---

## Switching providers

Only the factory line changes:

```csharp
IStorageProviderFactory factory = providerName switch
{
    "sqlite"     => new SqliteProviderFactory(),
    "postgresql" => new PostgreSqlProviderFactory(),
    "sqlserver"  => new SqlServerProviderFactory(),
    "mongodb"    => new MongoProviderFactory(),
    _ => throw new ArgumentOutOfRangeException(nameof(providerName))
};

using var connection = factory.Create(connectionString);
```

Your declarations, writes, and queries are unchanged. Where a provider genuinely cannot do something,
its capability is absent and the call refuses **before** doing I/O — you find out immediately, not
after a partial write.

## Next

- **[Capabilities Reference](Capabilities-Reference)** — what each id promises
- **[Extending: Writing a Provider](Extending-Writing-a-Provider)** — adding a fifth
