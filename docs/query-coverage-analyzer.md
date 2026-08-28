# Query coverage analyzer

`Groundwork.Analyzers` is the editor/build front end for the single provider-neutral Q3
`QueryCoverageChecker`. It reads the current assembly's generated `GroundworkSchema` attribute,
referenced assembly attributes through `GroundworkSchemaMetadata`, or a `.json` AdditionalFile
selected by `gw_schema_file`.

The closed query surface is deliberately small: `Table<T>()`, `Where`, `WhereIf`, ordering,
`Skip`/`Take`, and the `ToList`, `ToListAsync`, `Count`, `CountAsync`, `Any`, and `AnyAsync` terminal
methods. `WhereIf` is enumerated as every 2^n shape for n <= 6. The reassignment form
`if (condition) q = q.Where(...)` is enumerated up to 32 shapes; loops, escapes, unknown helpers,
and larger compositions are reported as unresolved.

Coverage candidates come from one derivation, `CoverageCandidates.Derive`, shared by this analyzer,
`SchemaVerifier`, and the runtime gate. It contributes the declared key ahead of the declared
indexes: every relational coordinator emits the key as the table's `PRIMARY KEY`, which the engine
backs with a unique index, so a key-bounded read is a seek rather than a scan. The key is an ordered
candidate, so a composite key `(tenant, id)` bounds a predicate on `tenant` and on `tenant` and `id`
together, but not on `id` alone. Where a refused predicate pins every key column with a single-value
equality, at most one row can match and no index would improve on that, so the suggestion is withheld
and the point-read path is named instead. Every other shape — a disjunction, a range, an equality
over part of a composite key — keeps the ordinary suggestion, even when it names the key's own
columns.

An uncovered read may opt into a visible, attributed scan with the runtime AST value
`.AcceptScan("GW-SCAN-0007", "reason", "owner", "yyyy-MM-dd")`. The marker is not a pragma
suppression: a query suppressed with `#pragma warning disable GW_COVER_006` still has no
`ScanAcceptance` and is refused by `QueryCoverageEnforcer`. Accepted scans require
`[assembly: GwAllowAcceptedScans]`; the analyzer reports `GW-COVER-902` otherwise. A marker on an
index-covered query reports `GW-COVER-901`, expiry reports `GW-COVER-904` during the final 30 days
and `GW-COVER-903` on or after the expiry date, and `GW-COVER-905` inventories the id, reason,
owner, and expiry. Providers should call `QueryCoverageEnforcer.EnsureCovered` immediately before
executing a resolved request so analyzer suppression cannot bypass the runtime gate.

Q10 runtime admission compares the deployed catalog with the compiled physical target. Missing or
changed columns (including collation and persisted search-key algorithm) are startup-fatal and name
the column; missing or changed declared indexes are reported separately and only make dependent
query shapes refuse. Runtime coverage intersects declared indexes with the deployed set, so an
undeclared database index cannot rescue a query during a rolling deploy. Generated/recognized shapes
use their verified plan when its index is present; other shapes use the same Q3 checker and are
cached by `ShapeFingerprint` in a bounded `RuntimeCoverageGate`. Cache eviction emits the
`groundwork.runtime.coverage.cache.eviction` metric. `RuntimeValueFence` rechecks membership
cardinality, provider parameter count, and continuation order/plan binding before execution; the
typed query model enforces value length, decimal precision/scale, and well-formed UTF-16 at
construction.

The MongoDB provider performs the same inspect-only admission split at `OpenSession`: its public
`InspectSchema` report classifies missing/invalid BSON fields and persisted derived-column
algorithm metadata as `GW-RUNTIME-001` column drift, and missing/changed declared native indexes
as `GW-RUNTIME-002` index drift. Column drift blocks the store; index drift is retained in the
report and does not block opening. Its LINQ query executor is wired through the shared
`RuntimeCoverageGate` before native aggregation/find execution; extra native indexes are never
used by the admission report to satisfy a declared index.

Q3 refusal codes are preserved in the diagnostic message and include the suggested `[GwIndex(...)]`
declaration. Roslyn requires compiler-valid diagnostic identifiers, so the emitted IDs use
`GW_COVER_005`, `GW_COVER_006`, `GW_COVER_009`, `GW_COVER_016`, `GW_COVER_900`, and
`GW_COVER_901` through `GW_COVER_905`, while each
message retains the published `GW-COVER-*` code. `GW_COVER_900` is an error by default and is
downgradeable with the normal `dotnet_diagnostic.GW_COVER_900.severity` `.editorconfig` setting.
The code fix rewrites supported conditional reassignment into `WhereIf`.

The analyzer has no provider, ADO.NET, or runtime storage dependency. Its package places the
analyzer and provider-neutral model/planning/schema dependencies under `analyzers/dotnet/cs` so an
external project can consume the analyzer without referencing this repository's source.

The editor-budget regression test analyzes 500 covered call sites in one compilation and enforces a
15-second ceiling on the shared CI runner. A Release test run during delivery completed the full
analyzer test assembly, including that workload, in two seconds; the deliberately generous ceiling
detects accidental combinatorial regressions
without turning ordinary runner variance into a flaky build.
