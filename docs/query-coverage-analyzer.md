# Query coverage analyzer

`Groundwork.Analyzers` is the editor/build front end for the single provider-neutral Q3
`QueryCoverageChecker`. It reads the current assembly's generated `GroundworkSchema` attribute,
referenced assembly attributes through `GroundworkSchemaMetadata`, or a `.json` AdditionalFile
selected by `gw_schema_file`.

The closed query surface is deliberately small: `Table<T>()`, `Where`, `WhereIf`, ordering,
`Skip`/`Take`, and the `QueryAsync`, `CountAsync`, `FirstOrDefaultAsync`, and `ToListAsync` terminal
methods. `WhereIf` is enumerated as every 2^n shape for n <= 6. The reassignment form
`if (condition) q = q.Where(...)` is enumerated up to 32 shapes; loops, escapes, unknown helpers,
and larger compositions are reported as unresolved.

An uncovered read may opt into a visible, attributed scan with the runtime AST value
`.AcceptScan("GW-SCAN-0007", "reason", "owner", "yyyy-MM-dd")`. The marker is not a pragma
suppression: a query suppressed with `#pragma warning disable GW_COVER_006` still has no
`ScanAcceptance` and is refused by `QueryCoverageEnforcer`. Accepted scans require
`[assembly: GwAllowAcceptedScans]`; the analyzer reports `GW-COVER-902` otherwise. A marker on an
index-covered query reports `GW-COVER-901`, expiry reports `GW-COVER-904` during the final 30 days
and `GW-COVER-903` on or after the expiry date, and `GW-COVER-905` inventories the id, reason,
owner, and expiry. Providers should call `QueryCoverageEnforcer.EnsureCovered` immediately before
executing a resolved request so analyzer suppression cannot bypass the runtime gate.

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
