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

    IStorageSession OpenSession(
        StorageUnit unit,
        StorageAccess access,
        IProviderCommandObserver? observer = null);
    IOwnedStorageSession OpenOwnedSession(
        StorageUnit unit,
        StorageAccess access,
        IProviderCommandObserver? observer = null);
    IUnitOfWork BeginUnitOfWork(StorageAccess access, params StorageUnit[] units);
    IUnitOfWork BeginUnitOfWork(StorageAccess access, BatchWriteOptions options, params StorageUnit[] units);
    IUnitOfWork BeginUnitOfWork(
        StorageAccess access,
        BatchWriteOptions options,
        IProviderCommandObserver? observer,
        params StorageUnit[] units);
}
```

`OpenSession` returns a non-owning view tied to the connection lifetime. `OpenOwnedSession` returns an
`IOwnedStorageSession`; providers must release its per-session resources from both `Dispose` and
`DisposeAsync`, report that state through `IsReleased`, and reject operations after release.

The Store package currently has eleven public session interfaces. Only `IStorageSession` is the
required operation surface, and only the object returned by `OpenOwnedSession` implements
`IOwnedStorageSession`. The other nine are capability contracts, not a checklist to implement:

| Optional interface | Guarantee it adds |
| --- | --- |
| `IStorageInspectionSession` | Durable generated-sequence high-water inspection |
| `IExactAppendStorageSession` | Replay-stable per-row append outcomes |
| `IPrivilegedCrossScopeQuerySession` | Explicit privileged queries across scopes |
| `IBatchedStorageSession` | Native batched write execution |
| `IConcurrencyStorageSession` | Atomic optimistic conditional upsert |
| `IRetentionStorageSession` | Retention execution |
| `IExactRetentionStorageSession` | Exact compare-and-delete retention |
| `ISetMutationStorageSession` | Set-based update/delete |
| `ICompareAndDeleteStorageSession` | Atomic compare-and-delete |

Implement an optional interface only when the connected deployment advertises and honors the
matching capability. A basic external stub should implement none of them.

The compile-only
[`Groundwork.Samples.ExternalProviderStub`](https://github.com/valence-works/groundwork-v2/tree/main/samples/Groundwork.Samples.ExternalProviderStub)
implements this complete boundary, including a non-owning session, an owned session, a unit of work,
catalog, schema coordinator, and relational dialect. Its `DriverWork` members deliberately mark the
native work a real provider still owns; it is not an executable database provider.

## Two routes

### 1. Relational — reuse the public substrate and implement the Store boundary

`Groundwork.Substrate.Relational` has a public, reusable schema and rendering seam:

- `RelationalDialect` owns provider SQL, mapping, locking, fencing, history, and catalog hooks.
- `RelationalSchemaExecutor` owns schema-operation dispatch, its connection lifetime,
  application-lock cleanup, fencing checks, and operation-batch transactions.
- `RelationalRuntimeAdmission` owns cached startup drift admission.
- `RelationalSchemaToolSession` adapts the same executor to the CLI plug-in contract.
- `RelationalStorageSessionBase` owns required read, query, aggregation, and CRUD lifecycle and
  delegates native commands through one `RelationalStorageSessionAdapter`.
- `RelationalAppendAdapter` and `RelationalRetentionAdapter` expose driver-shaped commands while
  the base owns validation, durable claim/replay protocols, transaction admission, and `OnAppend`
  cleanup. Protected runners let an opted-in session reuse exact append/retention, cross-scope query,
  and set-mutation state machines.
- `RelationalUnitOfWork`, `RelationalUnitOfWorkSession`, and `RelationalUnitOfWorkLifetime` own
  staging, exact-outcome reporting, terminal-state enforcement, and transaction cleanup.
- `RelationalQueryRenderer`, `RelationalAggregationRenderer`, `RelationalQueryResultReader`, and
  `RelationalExecution` remove common query rendering, materialization, and sync/async ADO.NET
  dispatch from the driver-specific code.

That is not the whole provider. Derive the concrete session from `RelationalStorageSessionBase`,
derive one driver adapter from `RelationalStorageSessionAdapter`, and construct units of work with
the three public unit-of-work types. The individual provider-neutral state-machine classes remain
internal implementation; an external provider must not depend on them through reflection or
`InternalsVisibleTo`. You still own:

- native connection creation, pooling/ownership, shared-session serialization, and transaction setup
- one session adapter implementing native parameter binding and the four keyed mutation commands;
  the base supplies their synchronous/asynchronous public surface
- native append-ledger/payload commands through `RelationalAppendAdapter`; when retention is
  declared, native retention/ledger commands through `RelationalRetentionAdapter`
- `IOwnedStorageSession` cleanup and use-after-release enforcement
- one serialization gate shared by every non-owning session on the same connection
- one native connection/transaction supplied to `RelationalUnitOfWorkLifetime`, plus construction of
  transaction-bound sessions returned as `RelationalUnitOfWorkSession`
- parameter creation/binding, value decoding, provider error mapping, command observation, and
  generated values
- optional session interfaces only for capabilities the connected deployment can actually honor

Within `RelationalDialect`, supply the provider-shaped schema and SQL behavior:

At this release the dialect has **27 abstract members**, plus virtual hooks for provider-specific
DDL, constraints, aggregation expressions, transactions, and catalog comparison. The external stub
implements every abstract member so a signature change breaks its build instead of silently making
this guide stale.

- `ProviderName`, identifier quoting, portable type/collation/default mapping, column validation
- ordinary query-renderer creation, aggregation SQL hooks, transaction isolation, read conversion,
  and parameter-budget reporting
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

Your provider project normally references `Groundwork.Store`, `Groundwork.Kernel`,
`Groundwork.Query.Planning`, and the appropriate substrate. It **must not** rely on:

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
| **Query** | Render the normalized `QueryRequest`; honor null ranks, tie-breaks, and parameter budgets. Use `Groundwork.Query.Planning.RuntimeCoverageGate` before execution |
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
- [ ] `RelationalDialect`, `RelationalStorageSessionBase` + adapter, and the shared unit-of-work runtime (relational), or a direct non-relational implementation
- [ ] Both sync and async session members use the matching native driver surface
- [ ] Owned sessions release their own resources; ordinary and unit-of-work sessions remain non-owning
- [ ] One native transaction owns every unit-of-work session until commit, rollback, or disposal
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
