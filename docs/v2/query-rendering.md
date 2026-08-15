# Native query rendering (v2)

`QueryRequest` is normalized and validated before it reaches a provider renderer. Each provider
has one renderer over the public substrate contract: SQLite, PostgreSQL, and SQL Server emit a
`RelationalQueryCommand`; MongoDB emits a `MongoQueryCommand` containing native BSON filter and
sort documents, and an aggregation pipeline when explicit null ranks or a count are required. The
renderers are synchronous and do not change the query model.

`Paging.Keyset(limit)` is the first keyset page. `Paging.Continuation` carries a typed tuple made
with `QueryContinuationToken.Encode`; the tuple contains every requested order term followed by
the explicitly supplied `QueryRenderOptions.TieBreakColumns`. Applications must supply their
declared identity columns as tie-breaks so pages remain deterministic. Every order term must name
its null rank. Offset paging remains available through `Paging.OffsetLimit` and is rendered only
when requested.

The default index policy is provider-default and emits no native hint. A declaration must use
`QueryIndexPinning.Pinned` before SQL Server or MongoDB can receive a hint. PostgreSQL and SQLite
retain the declaration for diagnostics but have no hint syntax and therefore remain unhinted.
An empty `In` normalizes to match-none; a pinned declaration is still carried on the native
command. A pinned index that excludes null values is refused when the predicate could match an
excluded null, except for match-none. This preserves the v1 sparse-index safety rule.

`ResultShape.Rows` never adds a count expression. `ResultShape.TotalCount` adds the provider
window-count projection. Provider sessions expose this through the public `IStorageSession.Query`
operation, returning immutable rows, an optional `TotalCount`, and a bound next continuation token.
The execution layer may project identity columns and null-rank fields internally; they are removed
before materialization. `In` values are capped at 1,000 by default (`GW-QUERY-015`), and every
rendered parameter, including cursor and page parameters, is checked against the provider budget
(SQLite 999, SQL Server 2,100, PostgreSQL 65,535). No renderer emits database-side case folding;
non-ordinal text policies are refused by the normalized semantic contract.
