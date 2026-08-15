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

Q3 refusal codes are preserved in the diagnostic message and include the suggested `[GwIndex(...)]`
declaration. Roslyn requires compiler-valid diagnostic identifiers, so the emitted IDs use
`GW_COVER_005`, `GW_COVER_006`, `GW_COVER_009`, `GW_COVER_016`, and `GW_COVER_900`, while each
message retains the published `GW-COVER-*` code. `GW_COVER_900` is an error by default and is
downgradeable with the normal `dotnet_diagnostic.GW_COVER_900.severity` `.editorconfig` setting.
The code fix rewrites supported conditional reassignment into `WhereIf`.

The analyzer has no provider, ADO.NET, or runtime storage dependency. Its package places the
analyzer and provider-neutral model/planning/schema dependencies under `analyzers/dotnet/cs` so an
external project can consume the analyzer without referencing this repository's source.
