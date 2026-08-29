# Native query rendering (v2)

`QueryRequest` is normalized and validated before it reaches a provider renderer. Each provider
has one renderer over the public substrate contract: SQLite, PostgreSQL, and SQL Server emit a
`RelationalQueryCommand`; MongoDB emits a `MongoQueryCommand` containing native BSON filter and
sort documents, and an aggregation pipeline when explicit null ranks or a count are required. The
renderers are synchronous and do not change the query model.

Relational aggregation source predicates use the same ordinary renderer fragment as `QueryRequest`:
provider hooks, portable ordering (including SQL Server GUID ordering), substring semantics, and
adapted bound parameters therefore cannot drift between a normal query and pre-reduction input.
The aggregate command and its budget probe bind the same fragment parameters before execution.

`Paging.Keyset(limit)` is the first keyset page. `Paging.Continuation` carries a typed tuple made
with `QueryContinuationToken.Encode`; the tuple contains every requested order term followed by
the explicitly supplied `QueryRenderOptions.TieBreakColumns`. For joined continuations, applications
must supply their complete driving identity through `QueryRenderOptions.DrivingIdentityColumns`; the
declaration must exactly match the provider-resolved source key, including portable type metadata and
facets. The joined order includes that identity in declaration order even when the additional tie-break list is
partial or a requested order term already names one of its columns. This preserves caller-selected
sort priority while retaining a complete declaration-order identity suffix. Joined renderers alias
each effective-order value with
`QueryRequestExecution.ContinuationFieldName(index)` so qualified columns that share a logical name
remain distinct inside the provider result. These fields are internal and never enter the public row.
Every order term must name its null rank. Offset paging remains available through `Paging.OffsetLimit`
and is rendered only when requested.

The default index policy is provider-default and emits no native hint. A declaration must use
`QueryIndexPinning.Pinned` before SQL Server or MongoDB can receive a hint. PostgreSQL and SQLite
retain the declaration for diagnostics but have no hint syntax and therefore remain unhinted.
Consumers querying an ordinary `IStorageSession` can call
`storageUnit.CreateQueryRenderOptions(selectedIndex)` to translate the admitted unit's index
names, column types, nullability, and missing-value policy without restating schema metadata. The
optional selection remains provider-default: it enables coverage/explain evidence but never silently
turns into an optimizer hint.

### Declared-reference joins

SQLite, PostgreSQL, and SQL Server render one declared-reference node as a qualified native
`INNER JOIN`. Stable driving and target aliases qualify the join key, predicates, projections,
ordering, latest-per-key windows, distinct keys, reductions, and continuation comparisons. The
renderer emits `QueryRequestExecution.ContinuationFieldName(index)` for every effective joined
order value so equal logical names on opposite sides cannot collapse a cursor tuple.

The existing `QueryRenderOptions` index selection describes the driving relation. A pinned SQL
Server index is attached only to that driving alias; it is never copied to the target. SQLite and
PostgreSQL keep both relations optimizer-selected. The join equality itself binds no values, and
both sides' predicate values, continuation values, and page values consume one shared provider
parameter budget. Mongo `$lookup` is a separate renderer capability, and composite public
source/target row materialization is owned by the Records join materializer rather than the SQL
renderer. Provider execution through the ordinary dictionary result reader therefore remains
fail-closed before provider I/O until that composite materializer lands, preventing duplicate
source/target field labels from being collapsed into one dictionary slot.

An empty `In` normalizes to match-none; a pinned declaration is still carried on the native
command. A pinned index that excludes null values is refused when the predicate could match an
excluded null, except for match-none. This preserves the v1 sparse-index safety rule.

`ResultShape.Rows` never adds a count expression. `ResultShape.TotalCount` adds the provider
window-count projection. `ResultShape.First` and `ResultShape.FirstOrDefault` execute with a
one-row bound, while `ResultShape.Single` and `ResultShape.SingleOrDefault` use a two-row bound
so an over-one result can be detected. `First` and `FirstOrDefault` require an explicit order;
`Single` and `SingleOrDefault` use their bounded probe to detect an over-one result without
imposing an order.
`QueryRequest.Distinct` deduplicates the public projection before paging and cardinality checks.
Provider sessions expose this through the public `IStorageSession.Query`
operation, returning immutable rows, an optional `TotalCount`, and a bound next continuation token.
The execution layer may project identity columns and null-rank fields internally; they are removed
before materialization. `In` values are capped at 1,000 by default (`GW-QUERY-015`), and every
rendered parameter, including cursor and page parameters, is checked against the provider budget
(SQLite 999, SQL Server 2,098 caller-owned parameters, PostgreSQL 65,535). SQL Server's 2,100
statement limit reserves two slots for the `Microsoft.Data.SqlClient` `sp_executesql` wrapper. No
renderer emits database-side case folding;
non-ordinal text policies are refused by the normalized semantic contract.

## Explain-plan assertion diagnostics

Set `GW_EXPLAIN_ASSERT=1` (or `true`) when running the differential suite to verify that every
query carrying a coverage-proven selected index actually uses its deployed physical index:

```bash
GW_EXPLAIN_ASSERT=1 \
GW_EXPLAIN_ARTIFACT_DIR="$PWD/TestResults/groundwork-explain" \
dotnet test tests/Groundwork.Differential.Tests
```

The diagnostic mode is off by default and adds no plan command to normal query execution. When enabled, the
provider executes the query normally and then obtains its native plan: PostgreSQL uses
`EXPLAIN (FORMAT JSON)`, SQL Server uses showplan XML, SQLite uses `EXPLAIN QUERY PLAN`, and
MongoDB uses `explain` with `executionStats`. The assertion requires the exact resolved physical
index name on an `Index Scan`/`Index Only Scan`, `Index Seek`, `USING INDEX` (including SQLite's
equivalent `USING COVERING INDEX`), or winning-plan `IXSCAN`, respectively. Match-none queries do
not perform a provider read and therefore have no chosen index to assert.

Each assertion writes the unmodified JSON, XML, or text plan to `GW_EXPLAIN_ARTIFACT_DIR`; when the
variable is omitted, artifacts go to `TestResults/groundwork-explain`. Test output labels the proof
as `optimizer-selected` for unhinted PostgreSQL/SQLite plans and `hinted` for SQL Server/MongoDB.
The latter proves that the deployed index exists and is usable, not that the optimizer selected it
freely. A failure includes the artifact path. Plans can contain identifiers and query values, so CI
should handle the artifact directory as potentially sensitive test output.

The differential harness computes its plan claim from the effective provider request, after the
automatic identity tie-break has been appended. It does not claim `ix_number` for the nullable,
order-only page shape: the rendered null-rank expressions and identity suffix are not served by
that single-column index on every provider. Its positive plan proof instead uses a selective
`(numberValue, id)` compound index, 2,000 rows, and current PostgreSQL statistics. Predicate shapes
that the effective coverage checker proves against the compound index remain asserted; unrelated
shapes are left at provider-default selection rather than carrying a false pinned-index claim.
