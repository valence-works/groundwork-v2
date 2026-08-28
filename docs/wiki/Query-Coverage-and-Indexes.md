# Query Coverage & Indexes

Groundwork does not let a query become an accidental table scan. A read that no declared index can
serve is **refused** — in the editor, at build, and at runtime.

## Why

Table scans are usually not a decision anyone made. They are a decision nobody noticed: a predicate
changes, an index is dropped in a rolling deploy, a new filter is added on a Friday. By the time it
shows up it is a production incident.

Groundwork makes the scan an explicit, attributed, expiring decision instead.

## The three gates

| Gate | Where | Component |
| --- | --- | --- |
| **Editor / build** | Roslyn | `Groundwork.Analyzers` |
| **Build (schema)** | MSBuild | `Groundwork.SchemaTool.MSBuild` |
| **Runtime** | Before execution | `RuntimeCoverageGate` / `QueryCoverageEnforcer` |

They share **one** provider-neutral implementation: `QueryCoverageChecker`. There is no second
approximation that could disagree with the first.

## What counts as a covering index

Two things: a **declared index**, and the **declared key**.

The key counts because it is already an index. Every relational coordinator emits it as the table's
`PRIMARY KEY`, and PostgreSQL, SQL Server, and SQLite each back that with a unique index the planner
seeks on. So this needs no `[GwIndex]`:

```csharp
await db.Table<Customer>().Query.Where(c => c.Id == id).ToListAsync(executor);
```

Both kinds of candidate come from one derivation, `CoverageCandidates.Derive`, which all three gates
call — so there is no place for the analyzer, the build gate, and the runtime gate to disagree about
what a unit offers.

Three details worth knowing:

- **The key is ordered, like any compound index.** A key of `(tenant, id)` covers a filter on
  `tenant`, and one on `tenant` and `id` together. It does **not** cover a filter on `id` alone —
  that is a trailing column, not a leading one, and it needs its own index.
- **The key is exempt from the deployed-catalog intersection.** A declared index can be missing from
  the catalog part way through a rolling deploy, which is why the runtime gate intersects. The key
  cannot: it is created with the table.
- **A refusal that pins your whole key points you at the point read, not at a duplicate index.** When
  the predicate fixes every key column with a single-value equality, at most one row can match, so no
  index would help: the suggestion is withheld and the message names `session.Read(key)`, or the typed
  `Records` read. Only that shape gets that direct-read remedy. A `GW-COVER-016` refusal also
  withholds an index suggestion because the shape itself is not representable by an ordered index;
  rewrite it into a portable shape where possible, or accept the scan. Refusal sets containing only
  actionable codes such as `GW-COVER-005`, `GW-COVER-006`, and `GW-COVER-009` keep the ordinary
  `[GwIndex(...)]` suggestion.

> **MongoDB caveat.** MongoDB stores the key in `_id` but filters on the declared field names, and
> creates no index over them, so a key-bounded read is admitted by the gate and then scans. The
> verdict is portable; the plan is not yet. See [#238](https://github.com/valence-works/groundwork-v2/issues/238).

## The analyzer

`Groundwork.Analyzers` reads your schema from the current assembly's generated `GroundworkSchema`
attribute, from referenced assemblies via `GroundworkSchemaMetadata`, or from a `.json`
`AdditionalFile` selected by `gw_schema_file`.

The closed query surface it understands: `Table<T>()`, `Where`, `WhereIf`, ordering, `Skip`/`Take`,
mapped-column `Select`, `Distinct`, and the terminals `ToList`, `ToListAsync`, `Count`,
`CountAsync`, `Any`, `AnyAsync`, `First`, `FirstOrDefault`, `Single`, `SingleOrDefault`, `Sum`,
`Min`, and `Max`. Async reduction adapters are deferred to #150. `First` and `FirstOrDefault` require an explicit
deterministic order. A distinct projection is covered only when every projected column is present in
the candidate index
and an equality/range predicate bounds the unpaged provider read. Otherwise the query must carry an
explicit live accepted scan; a bounded `Take` or cardinality terminal alone is not coverage.
Reduction terminals likewise require the selected numeric/orderable column to be present in the
candidate index; the reduction target is part of the query shape.

- `WhereIf` is enumerated as **every 2ⁿ shape for n ≤ 6**.
- The reassignment form `if (condition) q = q.Where(...)` is enumerated **up to 32 shapes**.
- Loops, escapes, unknown helpers, and larger compositions are reported as **unresolved**.

Roslyn requires compiler-valid diagnostic ids, so emitted ids use underscores
(`GW_COVER_006`) while each message retains the published `GW-COVER-006`.

| Roslyn id | Meaning |
| --- | --- |
| `GW_COVER_005` / `GW_COVER_006` / `GW_COVER_009` | Actionable coverage refusal — includes a suggested `[GwIndex(...)]` when no `GW_COVER_016` also applies |
| `GW_COVER_016` | Nonportable query shape — rewrite it or accept the scan; no index suggestion is emitted |
| `GW_COVER_900` | Unresolved composition. **Error by default**, downgradeable via `.editorconfig` |
| `GW_COVER_901` | Scan marker on an already-covered query |
| `GW_COVER_902` | Accepted scan without `[assembly: GwAllowAcceptedScans]` |
| `GW_COVER_903` | Scan marker on/after its expiry |
| `GW_COVER_904` | Scan marker within its final 30 days |
| `GW_COVER_905` | Inventory of accepted scans (id, reason, owner, expiry) |

```ini
# .editorconfig — accept unresolved compositions as warnings during a migration
dotnet_diagnostic.GW_COVER_900.severity = warning
```

The analyzer has **no provider, ADO.NET, or runtime storage dependency**. Its package places the
analyzer and its model/planning/schema dependencies under `analyzers/dotnet/cs`, so any project can
consume it without pulling in a provider.

There is also a code fix that rewrites supported conditional reassignment into `WhereIf`.

## The runtime gate

```csharp
var gate = new RuntimeCoverageGate(
    declaredIndexes: [new CoverageIndex("by_email", [new CoverageIndexColumn("email")])],
    deployedIndexes: catalogIndexes);

var decision = gate.Check(request);
if (decision.Coverage.Decision == CoverageDecision.Refuse)
    throw new QueryCoverageException(decision.Coverage);
```

Or the enforcing form, which providers should call immediately before executing a resolved request:

```csharp
QueryCoverageEnforcer.EnsureCovered(request, DateTimeOffset.UtcNow);
```

```csharp
catch (QueryCoverageException ex)
{
    ex.Code;                                  // e.g. "GW-COVER-006"
    ex.Message;                               // explains the corrective action
    // Actionable refusals carry NearestIndex, SuggestedIndex, and SuggestedDeclaration:
    //   [GwIndex("ix_email_createdat", "email ASC, createdAt DESC")]
    // GW-COVER-016 refusals carry no suggested index: rewrite the shape or accept the scan.
}
```

Behavior that matters operationally:

- **Runtime coverage intersects your declared indexes with the deployed catalog.** An undeclared
  index that exists only in the database **cannot rescue a query** during a rolling deploy. This is
  intentional — otherwise the gate would pass in production and fail after the next clean deploy.
- Generated/recognized shapes use their verified plan **while its index is present**; other shapes go
  through the same `QueryCoverageChecker` and are cached by `ShapeFingerprint` in a bounded cache.
- Cache eviction emits the `groundwork.runtime.coverage.cache.eviction` metric. Watch it: sustained
  eviction means your shape space is larger than `MaximumCachedShapes` (default 1024).

## The LINQ executor

`GwLinqExecutor` (package `Groundwork.Query.Linq.Execution`) is the one adapter behind the LINQ
terminals, for **every** provider. There is deliberately no per-provider executor: admission, scan
acceptance, paging, materialization, and the async terminals are provider-neutral, and a second copy
per provider would be a second place for coverage to drift.

```csharp
var executor = new GwLinqExecutor(session, connection);
var rows = await db.Table<Customer>().Query
    .Where(c => c.Email == "ada@example.test")
    .ToListAsync(executor);
```

Every terminal admits the request through `RuntimeCoverageGate` **before** the provider is asked to
render anything. The request that is admitted is the one you wrote — not the narrowed count or
existence probe derived from it — so a runtime refusal carries the same code and the same named fix
the analyzer reported at build time.

- **Pass the connection.** It supplies both things admission needs beyond the query itself: the
  catalog, so declared indexes are intersected with the deployed ones, and the budgets. Without it
  the gate admits against the declaration alone — an index a rolling deploy has not created yet can
  still satisfy it — and the fence falls back to portable defaults.
- Each provider supplies only its native budgets, advertised by the **connection** as a
  `QueryAdmissionProfile`, so the pre-execution value fence uses the provider's real limit instead of
  a portable guess — SQLite 999, SQL Server 2,100, PostgreSQL 65,535. MongoDB has no bound-parameter
  budget of its own (its bound is the 16 MB command document), so it advertises no parameter
  ceiling while ordinary membership retains the portable 1,000-value renderer limit. Keyed batch reads use the separate
  `MaximumBatchReadKeys` budget (999 by default; SQLite 999, SQL Server 2,098, and PostgreSQL
  65,535), with MongoDB using an effectively unbounded count plus a conservative 15 MiB payload
  budget; the same 15 MiB payload fence applies when a caller omits its connection. An explicit
  profile can advertise a different deployment budget. A scoped session reserves one key slot for its
  provider-injected scope parameter. A budget
  is a deployment property — SQLite's ceiling is a compile-time option of the library you loaded —
  which is why it is advertised rather than assumed, and why it lives on the connection where a
  session decorator cannot drop it.

## Accepted scans

Some reads genuinely should scan — a small admin export, a one-off migration. Say so explicitly:

```csharp
[assembly: GwAllowAcceptedScans]   // required, or GW-COVER-902

var query = table.Query
    .Where(c => c.Notes.Contains("refund"))
    .AcceptScan("GW-SCAN-0007", "admin export, <10k rows", "platform-team", "2026-12-31");
```

Four arguments, all mandatory: **id, reason, owner, expiry.** The marker becomes a
`ScanAcceptance` value on the request AST.

> **A `#pragma warning disable GW_COVER_006` does not work.** It silences the analyzer but leaves no
> `ScanAcceptance` on the request, so `QueryCoverageEnforcer` still refuses at runtime. This is
> deliberate: the acceptance must be a value in the query, not a comment in the source.

Markers **expire**. You are warned in the final 30 days (`GW-COVER-904`) and refused on or after the
date (`GW-COVER-903`). `GW-COVER-905` gives you the full inventory, so scan debt is visible rather
than accumulating silently.

## MSBuild verification

```xml
<PropertyGroup>
  <GroundworkSchemaFile>$(MSBuildProjectDirectory)/groundwork.schema.json</GroundworkSchemaFile>
  <GroundworkCoverageFile>$(MSBuildProjectDirectory)/groundwork.coverage.json</GroundworkCoverageFile>
</PropertyGroup>
```

The task runs **before compilation**. Portability refusals and uncovered queries are MSBuild errors
carrying their Groundwork diagnostic codes.

The coverage inventory is a `queries` array. Each query names a `table` and may declare `equal`,
`range`, `order`, `skip`, `take`, and `totalCount`. Order entries use `column`, `direction`
(`asc`/`desc`), and `nulls` (`first`/`last`).

```json
{
  "queries": [
    {
      "table": "customers",
      "equal": ["email"],
      "order": [{ "column": "createdAt", "direction": "desc", "nulls": "last" }],
      "take": 50
    }
  ]
}
```

The inventory is checked against the indexes in the **canonical schema**, not a provider-specific
approximation. `samples/Groundwork.Samples.CoverageNegative` is intentionally unbuildable and exists
to prove the gate actually fails a build.

## Runtime schema admission

At startup, providers compare the deployed catalog against the compiled physical target:

| Drift | Code | Effect |
| --- | --- | --- |
| Missing/changed **columns** (including collation and persisted search-key algorithm) | `GW-RUNTIME-001` | **Startup-fatal**, names the column |
| Missing/changed declared **indexes** | `GW-RUNTIME-002` | Reported separately; only makes **dependent query shapes** refuse |

MongoDB performs the same inspect-only split at `OpenSession` via its public `InspectSchema` report.

All four providers execute LINQ terminals through the same gate — see **[The LINQ executor](#the-linq-executor)**.
Extra native indexes are never used to satisfy a declared index, on any provider.

## Designing indexes that cover

- An index covers a query when its **ordered** columns can serve the predicate **and** the ordering.
- Equality columns come first, then range, then order columns.
- Actionable coverage refusals include a `SuggestedIndex` and ready-to-paste `SuggestedDeclaration`.
  `GW-COVER-016` refusals do not: no ordered index can clear a nonportable shape, so rewrite it or
  explicitly accept the scan.
- A **sparse** index (`MissingValueBehavior.Excluded`) cannot serve a predicate that could match an
  excluded null.
- Two indexes with the same physical signature are refused (`GW-PORT-009`) — consolidate.

## Editor performance

The analyzer's regression test analyzes **500 covered call sites in one compilation** with a
15-second ceiling on the shared CI runner. A Release run completed the full analyzer test assembly,
including that workload, in two seconds. The generous ceiling catches accidental combinatorial
regressions without turning runner variance into a flaky build.

## Next

- **[Querying](Querying)** — building the queries being checked
- **[Schema Management](Schema-Management)** — getting indexes deployed
