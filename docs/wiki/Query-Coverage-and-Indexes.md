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

## The analyzer

`Groundwork.Analyzers` reads your schema from the current assembly's generated `GroundworkSchema`
attribute, from referenced assemblies via `GroundworkSchemaMetadata`, or from a `.json`
`AdditionalFile` selected by `gw_schema_file`.

The closed query surface it understands: `Table<T>()`, `Where`, `WhereIf`, ordering, `Skip`/`Take`,
and the terminals `ToList`, `ToListAsync`, `Count`, `CountAsync`, `Any`, `AnyAsync`.

- `WhereIf` is enumerated as **every 2ⁿ shape for n ≤ 6**.
- The reassignment form `if (condition) q = q.Where(...)` is enumerated **up to 32 shapes**.
- Loops, escapes, unknown helpers, and larger compositions are reported as **unresolved**.

Roslyn requires compiler-valid diagnostic ids, so emitted ids use underscores
(`GW_COVER_006`) while each message retains the published `GW-COVER-006`.

| Roslyn id | Meaning |
| --- | --- |
| `GW_COVER_005` / `GW_COVER_006` | Uncovered query — the message includes the suggested `[GwIndex(...)]` |
| `GW_COVER_009`, `GW_COVER_016` | Additional coverage refusals |
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
    ex.Message;                               // explains the corrective index action
    // Refusals carry NearestIndex, SuggestedIndex, and SuggestedDeclaration:
    //   [GwIndex("ix_email_createdat", "email ASC, createdAt DESC")]
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

> **Known provider gap:** MongoDB currently has no query executor wired to the runtime coverage gate.
> Mongo query endpoints must call the shared `RuntimeCoverageGate` before execution to obtain
> dependent-shape refusal. Extra native indexes are never used to satisfy a declared index.

## Designing indexes that cover

- An index covers a query when its **ordered** columns can serve the predicate **and** the ordering.
- Equality columns come first, then range, then order columns.
- Refusals include a `SuggestedIndex` and a ready-to-paste `SuggestedDeclaration`. Use it.
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
