# Extending: Writing a Provider

Groundwork is designed so a provider can be **maintained outside this repository** and still implement
the complete contract. This page is the map.

## The discovery seam

```csharp
public interface IStorageProviderFactory
{
    IStorageProviderConnection Create(string connectionString);
}
```

That is the whole entry point. Your factory returns a connection implementing:

```csharp
public interface IStorageProviderConnection : IDisposable
{
    IProviderCatalog Catalog { get; }
    ISchemaCoordinator Schema { get; }
    IReadOnlyList<CapabilityDescriptor> Capabilities { get; }

    IStorageSession OpenSession(StorageUnit unit, StorageAccess access);
    IOwnedStorageSession OpenOwnedSession(StorageUnit unit, StorageAccess access);
    IUnitOfWork BeginUnitOfWork(StorageAccess access, params StorageUnit[] units);
    IUnitOfWork BeginUnitOfWork(StorageAccess access, BatchWriteOptions options, params StorageUnit[] units);
}
```

`OpenSession` returns a non-owning view tied to the connection lifetime. `OpenOwnedSession` returns an
`IOwnedStorageSession`; providers must release its per-session resources from both `Dispose` and
`DisposeAsync`, report that state through `IsReleased`, and reject operations after release.

## Two routes

### 1. Relational — implement `RelationalDialect`

`Groundwork.Substrate.Relational` already owns connection ownership, schema-operation dispatch,
application-lock cleanup, and fencing in `RelationalSchemaExecutor`. You supply **only
provider-specific behavior**:

- `ProviderName`, identifier quoting, portable type/collation/default mapping, column validation
- DDL emission: table creation, column addition/finalization, index creation/removal. Column
  finalization receives both the column name and the complete `ColumnDefinition`, so you can emit your
  type-specific `ALTER COLUMN` form
- Conditional-upsert and bounded batch-insert SQL emission
- Value conversion and `TryMapUniqueViolation(DbException, out string indexName)`
- Application-lock acquire/release/verify, server-session identity, fence acquisition/assertion,
  infrastructure setup
- History read and **transactional publish** — `PublishHistory` receives the active transaction,
  target, owner/fence, and the previously applied target fingerprint. It **must compare-and-swap that
  old value** before recording the new state; a null expected value means no history row may exist
- Catalog inspection: `TableExists`, `ReadColumns`, `ReadIndex`

Optional virtual hooks cover column backfill, provider schema definitions, and target validation.
Returning `null` from `BackfillColumnSql` makes an unsupported backfill **explicit**, and the shared
executor refuses that operation rather than guessing.

Operation batches execute in **one durable transaction with fencing before and after**. A failed
operation rolls back the complete batch. **Dialect callbacks must not commit or roll back** the
transaction owned by the shared executor.

### 2. Non-relational — implement the contracts directly

`Groundwork.Substrate.Mongo` is the equivalent seam for document stores. `Groundwork.MongoDb` is the
worked example, including how to expose a native connection type (`IMongoProviderConnection`) while
adapting to the provider-neutral contract.

## Rules

Your provider project references the substrate and `Groundwork.Kernel` normally, and **must not** rely
on:

- `InternalsVisibleTo`
- internal helper types
- contract-family assemblies (`Records`, `Documents`)
- provider assumptions baked into the substrate

Nothing in the contract requires `Groundwork.Testing` at runtime. If your provider needs it to open a
connection, the boundary has been crossed.

## Advertise capabilities honestly

```csharp
public IReadOnlyList<CapabilityDescriptor> Capabilities => BatchWriteCapabilities.ForProvider(
    "Acme",
    nativeBatch: true,
    exactOutcomeCost: "one round trip per row",
    batchCost: "uses a native multi-row statement",
    exactAppendOutcomes: supportsTransactions,
    durableHighWaterInspection: supportsTransactions,
    exactRetention: supportsTransactions,
    atomicCommit: supportsTransactions,
    compareAndDelete: supportsTransactions);
```

Two non-negotiables:

1. **Advertise only what the connected deployment can actually do.** Follow MongoDB's example: gate
   the transactional capabilities on the deployment reporting transaction support, and omit them
   otherwise.
2. **Do not implement the optional interface when the capability is absent.** The public extension
   methods (`AppendWithOutcomes`, `Inspect`, `QueryAcrossScopes`) check the interface and refuse with
   a stable code before doing provider work. Implementing the interface without the guarantee turns a
   clean refusal into a silent correctness bug.

Descriptors should state honest cost — `AdditionalProviderCommandsPerWrite` and the
`exactOutcomeCost` / `batchCost` strings are read by consumers sizing workloads.

## Required behaviors

| Area | Requirement |
| --- | --- |
| **Schema** | `Apply` is idempotent — reapplying an unchanged declaration is a **no-op**, and `Diff` is empty afterwards |
| **Naming** | Refuse invalid physical identifiers (`GW-PORT-010`) **before** schema I/O; reuse `PortabilityValidator.EnsurePhysicalIdentifiers` |
| **Logical declaration** | Refuse invalid key/index references and application declarations using `__groundwork_` (`ProviderOwnedColumns.ValidateLogicalDeclaration`) |
| **Scope** | Enforce scope as a **provider-owned physical restriction** applied before caller predicates; never expose it as a caller-visible column |
| **Access mismatch** | Refuse global access on a scoped unit and vice versa, before I/O |
| **Concurrency** | Support both `None` and `Optimistic`. For `None`, add **no** version column or version work |
| **Write outcomes** | Return portable `WriteOutcomeStatus` values, including `UniqueViolation` with the **logical declared** index name where available |
| **Generated values** | Never synthesize from the caller payload or a read-after-write |
| **Query** | Render the normalized `QueryRequest`; honor null ranks, tie-breaks, and parameter budgets. Call `RuntimeCoverageGate` before execution |
| **Errors** | Use the published `GW-*` codes |

## Prove it with the conformance suite

```csharp
var report = ConformanceSuite.Run(new AcmeProviderFactory(), connectionString);

Assert.True(report.Passed,
    string.Join("\n", report.Failures.Select(f => $"{f.Name}: {f.Failure}")));
```

Covers schema apply/no-op/diff, catalog and index verification, scope isolation, audited cross-scope
queries, CRUD outcomes, optimistic conflicts, unique violations, and unit-of-work commit/rollback.

Treat the suite as your **specification**, not as an afterthought. It is the same suite the four
shipped providers pass.

Capability-specific proofs live in `tests/Groundwork.StreamCapabilities.Tests` (sequences,
idempotency, retention, lifecycle, aggregation) and `tests/Groundwork.Differential.Tests` (four-way
query differential and explain plans).

The concurrency harness:

```csharp
var report = ConcurrencyHarness.Run(
    new StorageProviderConcurrencyFactory("acme", new AcmeProviderFactory()),
    connectionString,
    new ConcurrencyProbeOptions { Concurrency = ConcurrencyKind.Optimistic });
```

See **[Testing](Testing)**.

## Schema-tool integration

Implement `ISchemaToolProviderSessionFactory` so the `groundwork` CLI can discover your provider.
Users load an external plug-in with `--provider-assembly`. `--connection` and `--database` are passed
to your factory and must **not** be echoed in reports.

Hosts can alternatively inject an `ISchemaToolProviderSession` resolver directly.

---

## Writing a contract family instead

A *family* is a typed façade producing a `StorageUnit`. It needs **no** provider dependency at all —
which is exactly what makes it portable.

The kernel gives you everything required:

- `StorageUnit.Declare` and `StorageDeclarationBuilder`
- Typed column helpers plus `Column(name, type, …)` as the runtime-type alias; `ColumnBuilder` carries
  required/nullable, sizing, precision, defaults, collation, and provider-sequence policies
- `IndexBuilder.Column` alongside `Ascending` / `Descending`
- `Scoped()`, `AppendIdempotency(window, ledgerName)`, `Retention(...)` / `KeepNewest(...)`
- `Aggregate(...)` — the closed aggregation DSL for `GroupBy`, `Min`, `Max`, `Sum`, `SetUnion`,
  `FirstBy`
- `ConformanceScenario`, which makes the shipped `ConformanceSuite` reusable by your family

`samples/Groundwork.Samples.EventLog` is the proof. Its declaration references **only**
`Groundwork.Kernel`, and an architecture test checks the compiled assembly references — the sample
build fails if the declaration surface regresses into a family-specific dependency.

The headline: an event-log family plus provider implementation that cost **11,880 lines in v1** is
**0 lines in v2**, replaced by a **20-line public declaration chain**. `Groundwork.Records` keeps only
a compatibility forwarding wrapper; it does not maintain a second declaration implementation.

If you need a custom execution boundary rather than a custom declaration, implement `IRecordStore` —
see **[Records: Typed Rows](Records-Typed-Rows)**.

---

## Checklist

- [ ] `IStorageProviderFactory` + `IStorageProviderConnection`
- [ ] `RelationalDialect` (relational) or direct contract implementation
- [ ] Capabilities reflect the **connected deployment**, not the package
- [ ] Optional interfaces implemented **only** when the guarantee holds
- [ ] `Apply` idempotent; `Diff` empty after apply
- [ ] Physical identifier and reserved-name validation before I/O
- [ ] Scope enforced physically, before caller predicates
- [ ] Both concurrency modes; **no** version machinery for `None`
- [ ] Portable outcome statuses, with logical index names on unique violations
- [ ] `ConformanceSuite` green
- [ ] Concurrency harness green
- [ ] `ISchemaToolProviderSessionFactory` for CLI integration
- [ ] No `InternalsVisibleTo`, no internal types, no contract-family references, no `Groundwork.Testing`
      in the runtime path

## Next

- **[Capabilities Reference](Capabilities-Reference)** — what each id promises
- **[Testing](Testing)** — the suites in detail
