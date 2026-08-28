# Diagnostics Reference

Every Groundwork refusal carries a **stable code**, a **path** to the offending element, and a
**message naming the corrective action**. Codes are part of the public contract: they change only
with an explicit release note and regression proof.

Use them. Branch on `exception.Message.Contains("GW-…")` or on typed `Code` properties rather than on
message text.

## Where codes surface

| Type | Carries |
| --- | --- |
| `PortabilityRefusal` | `Code`, `Message`, `Path` |
| `PortableSemanticRefusal` | `Code`, `Message`, `Path` |
| `GroundworkDiagnostic` (via `StorageDeclarationException.Diagnostics`) | `Code`, `Message`, `Path` |
| `DocumentDiagnostic` (via `DocumentDeclarationException.Diagnostics`) | `Code`, `Message`, `Path` |
| `LinqDiagnostic` (via `LinqTranslationException.Diagnostics`) | `Code`, `Message`, `Span`, `Path` |
| `CoverageRefusal` (via `QueryCoverageException`) | `Code`, `Message`, `NearestIndex`, `SuggestedIndex`, `SuggestedDeclaration` |
| `AggregationValidationError` (via `AggregationValidationException`) | `Code`, `Message`, `Path` |
| `CapabilityValidationIssue` | `Code`, `Message`, `Target`, `IsError` |
| `QueryRenderException` | `Code`, `Message` |
| Runtime `InvalidOperationException` / `NotSupportedException` | Code embedded in the message |

---

## `GW-PORT-*` — declaration portability

Raised by `PortabilityValidator`, builders, and providers **before schema I/O**.

| Code | Meaning | Fix |
| --- | --- | --- |
| `GW-PORT-000` | No storage unit supplied | Pass a unit |
| `GW-PORT-001` | Unique index includes nullable columns with `MissingValues.Included` | Use `Excluded`, or make the columns required |
| `GW-PORT-002` | Decimal column missing `Precision`/`Scale` | Declare both |
| `GW-PORT-003` | Index key column lacks a positive `MaxLength` | Declare `MaxLength` |
| `GW-PORT-004` | Decimal index key width uncomputable (precision outside supported range) | Use a supported precision |
| `GW-PORT-005` | `ProviderSequence` is not the sole non-nullable `Int64` primary key | Make it so |
| `GW-PORT-006` | Collation outside the portable set | Use `Ordinal`, `OrdinalIgnoreCase`, or `UnicodeOrdinalIgnoreCase` |
| `GW-PORT-007` | Invalid retention declaration | Non-negative `KeepNewest`, declared non-nullable orderable order column, declared partition columns |
| `GW-PORT-008` | MongoDB composite key **order** changed after apply | Restore the order, or rebuild the catalog |
| `GW-PORT-009` | Two indexes share a physical signature | Consolidate onto one index |
| `GW-PORT-010` | Invalid physical identifier | ASCII letters/digits/underscores, starts with letter/underscore, ≤ 63 bytes, no `__groundwork_` prefix |
| `GW-PORT-011` | Duplicate physical index name | Use unique names |
| `GW-PORT-012` | A `Double` column used as a key, index, or aggregation group-by column | Declare `Decimal` or `Int64` for a value you compare; keep `Double` for one you only store |
| `GW-PORT-013` | A declared default's CLR type does not match its `PortableType`, or a `Double` default is outside the **defaultable** domain — non-finite, negative zero, or subnormal | Supply a default of the CLR type named by the column; for `Double`, use a finite, normal value other than negative zero; a subnormal can still be written as a value |
| `GW-PORT-014` | Locale sort-key declaration is invalid, or the host cannot provide real ICU collation | Declare a bounded String and positive `MaximumExpansionFactor`; disable invariant globalization and Windows NLS; pin ICU consistently across hosts |

---

## `GW-SEM-*` — portable query semantics

Raised by `PortableQuerySemantics.Validate`. See **[Portable Semantics](Portable-Semantics)**.

| Code | Meaning |
| --- | --- |
| `GW-SEM-NULL-001` | Range with a null operand |
| `GW-SEM-NULL-002` | Null value for a non-nullable column |
| `GW-SEM-NOT-001` | Non-portable negation |
| `GW-SEM-TYPE-001` | Column comparison requires exact matching types |
| `GW-SEM-TYPE-002` | Double membership |
| `GW-SEM-TYPE-004` | Untyped or null constant reference |
| `GW-SEM-TYPE-005` | Constant type must exactly match column/set type |
| `GW-SEM-TYPE-006` | Binary floating point in a predicate or index — `Double` is declarable and storable, only uncomparable |
| `GW-SEM-TYPE-007` | Element set without an exact declared element type |
| `GW-SEM-TEXT-001` | Non-portable text comparison policy |
| `GW-SEM-TEXT-003` | Non-portable substring anchor |
| `GW-SEM-DECIMAL-001` | Decimal is not `decimal(18,4)` |
| `GW-SEM-ORDER-001` | Ordering a non-orderable type |
| `GW-SEM-ORDER-002` | Ordering comparison on a non-orderable type |
| `GW-SEM-ORDER-003` | Range ordering on a non-orderable type |
| `GW-SEM-ORDER-004` | `NullOrder.ProviderDefault` |
| `GW-SEM-ORDER-005` | Boolean ordering |
| `GW-SEM-ORDER-006` | First/FirstOrDefault without an explicit deterministic order |
| `GW-SEM-AGG-001` | Sum requires an Int32, Int64, or Decimal column |
| `GW-SEM-AGG-002` | Min and Max require an orderable column |
| `GW-SEM-LATEST-001` | Latest-per-key needs a non-null `DateTimeOffset` |
| `GW-SEM-UNKNOWN-001` | Unrecognised predicate node |

---

## `GW-LINQ-*` — LINQ translation

| Code | Problem | Fix |
| --- | --- | --- |
| `GW-LINQ-101` | Computed/member expression over columns | Declare a computed column |
| `GW-LINQ-102` | Arithmetic expression over columns | Declare a computed column |
| `GW-LINQ-103` | Column-to-column comparison | Add `.AcceptScan(...)` |
| `GW-LINQ-104` | Cross-table expression | v2 has no joins — element set or two queries |
| `GW-LINQ-105` | Grouped top-one | Use `.LatestPer(...)` |
| `GW-LINQ-106` | Unsupported element-set predicate | Declare the element set |
| `GW-LINQ-107` | Opaque helper | Mark it `[GwQueryFragment]` |
| `GW-LINQ-108` | Unpinned string comparison | Use `Ordinal`/`OrdinalIgnoreCase` matching the column's folding |
| `GW-LINQ-109` | Non-UTC clock | Use `DateTimeOffset.UtcNow` |
| `GW-LINQ-110` | Decimal precision/scale | Value exceeds the declared decimal |
| `GW-LINQ-111` | First/FirstOrDefault without deterministic order | Add an explicit `OrderBy` before `First` or `FirstOrDefault` |
| `GW-LINQ-112` | Sum/Min/Max selector is not a mapped portable column | Select a mapped numeric or orderable column |
| `GW-LINQ-113` | Skip without Take | Add a bounded Take; offset-only pages are not portable |

---

## `GW-COVER-*` — query coverage

Published codes appear in messages; Roslyn ids use underscores.

| Code | Roslyn id | Meaning |
| --- | --- | --- |
| `GW-COVER-005` | `GW_COVER_005` | Coverage refusal |
| `GW-COVER-006` | `GW_COVER_006` | Uncovered query — includes suggested `[GwIndex(...)]` |
| `GW-COVER-009` | `GW_COVER_009` | Coverage refusal |
| `GW-COVER-016` | `GW_COVER_016` | Coverage refusal |
| `GW-COVER-900` | `GW_COVER_900` | Unresolved composition. Error by default; downgradeable in `.editorconfig` |
| `GW-COVER-901` | `GW_COVER_901` | Scan marker on an index-covered query |
| `GW-COVER-902` | `GW_COVER_902` | Accepted scan without `[assembly: GwAllowAcceptedScans]` |
| `GW-COVER-903` | `GW_COVER_903` | Scan marker on/after expiry |
| `GW-COVER-904` | `GW_COVER_904` | Scan marker in its final 30 days |
| `GW-COVER-905` | `GW_COVER_905` | Accepted-scan inventory |

---

## `GW-QUERY-*` — rendering and execution

| Code | Meaning |
| --- | --- |
| `GW-QUERY-009` | Query rendering refusal |
| `GW-QUERY-013` | Query rendering refusal |
| `GW-QUERY-015` | `In` value count exceeds the cap (default 1,000) |
| `GW-QUERY-018` | Index column uses a non-queryable portable type |
| `GW-QUERY-020` | Query rendering refusal |
| `GW-QUERY-030` | Query rendering refusal |
| `GW-QUERY-031` | `ColumnRef` policy does not match the schema's persisted search-key mapping |

---

## `GW-RUNTIME-*` — runtime schema admission

| Code | Meaning | Severity |
| --- | --- | --- |
| `GW-RUNTIME-001` | Column drift — missing/changed column, collation, or search-key algorithm | **Startup-fatal** |
| `GW-RUNTIME-002` | Index drift — missing/changed declared index | Dependent query shapes refuse |
| `GW-RUNTIME-003` | A deployed column the declaration does not describe, downgraded from `GW-RUNTIME-001` by the unit's opt-in foreign-column policy | Warning |
| `GW-RUNTIME-010`…`013` | Additional runtime admission refusals | |

A deployed column the declaration does not describe is `GW-RUNTIME-001` by default. Declaring
`ForeignColumns = ForeignColumnPolicy.TolerateDatabaseSupplied` (fluent: `.TolerateForeignColumns()`;
schema document: `"foreignColumns": "TolerateDatabaseSupplied"`) downgrades it to `GW-RUNTIME-003`
**only** where the database supplies a value for the column on its own — nullable, defaulted, or
generated. A foreign column a write that omits it would fail on stays `GW-RUNTIME-001`, because no
policy makes it writable. Nothing else about drift changes: a declared column that differs, a
missing column, and index drift are unaffected.

---

## `GW-WRITE-CONCURRENCY-*`

| Code | Meaning |
| --- | --- |
| `GW-WRITE-CONCURRENCY-001` | `CreateOnly`/`IfVersion` on a `None` unit |
| `GW-WRITE-CONCURRENCY-002` | Invalid operation/precondition pairing |
| `GW-WRITE-CONCURRENCY-003` | Application supplied a system-owned token value |

---

## `GW-VALUE-*` — written value domains

| Code | Meaning |
| --- | --- |
| `GW-VALUE-DOUBLE-001` | A `Double` write outside the storable domain: NaN, an infinity, or negative zero. SQL Server refuses NaN and both infinities at the wire protocol and SQLite refuses NaN, while SQLite and MongoDB both return positive zero for a stored negative zero — so the value a reader gets would depend on the provider. Write a finite value other than negative zero. |

---

## `GW-WRITE-NESTED-*`

| Code | Meaning |
| --- | --- |
| `GW-WRITE-NESTED-001` | A write re-entered a session already inside its own provider write transaction. A provider connection carries one transaction at a time, so the nested write can neither join nor isolate itself from the outer one; open a unit of work and stage the writes instead. |

---

## `GW-SET-*` — set-based mutation

`UpdateWhere` and `DeleteWhere` are admitted by the coverage rule that admits an equivalent read,
so an uncovered set-based mutation is refused with a `GW-COVER-*` code and is accepted only by the
same `AcceptScan` a read would need. The codes below cover what is specific to mutating a set.

| Code | Meaning |
| --- | --- |
| `GW-SET-001` | Provider does not advertise set-based mutation. Check `ISetMutationStorageSession` / the `groundwork.storage.set-mutation` capability first. |
| `GW-SET-002` | Assignment column is not an application-declared column of the unit, is provider-owned, or is a declared key column. A set-based update never moves rows between identities. |
| `GW-SET-003` | A set-based update was issued with no column assignments. |
| `GW-SET-004` | Assignment to a `PortableType.Json` column. Assign a portable scalar or binary column instead. |

Assignments are values, never column-relative expressions. Repeating an update stores the same
application values, but set mutation does not participate in the append idempotency ledger and a
repeat on an optimistic-concurrency unit bumps every matched token again. Resolve an unknown
acknowledgement before retrying when token stability matters. Assigning the optimistic token is
refused as `GW-WRITE-CONCURRENCY-003`, by the same rule that refuses supplying it to a keyed write;
a set-based update bumps the token itself.

---

## `GW-APPEND-*` / `GW-RETENTION-*` / `GW-INSPECT-*`

| Code | Meaning |
| --- | --- |
| `GW-APPEND-001` | Same unit/scope/nonce with a **different payload**. Nothing written. |
| `GW-APPEND-002` | Legacy ledger entry has no persisted generated values |
| `GW-APPEND-003` | Provider does not advertise exact append |
| `GW-RETENTION-001` | Same nonce, changed retention request (including effective keep value) |
| `GW-RETENTION-002` | Malformed/legacy exact retention result — use a new nonce |
| `GW-RETENTION-003` | Provider does not advertise exact retention |
| `GW-RETENTION-004` | `RetentionIdempotency` declared without `Retention` |
| `GW-INSPECT-001` | Provider does not advertise durable inspection |
| `GW-INSPECT-002` | Unit has no `ProviderSequence` column |

---

## `GW-HOST-*` — dependency injection and hosting

Raised by `Groundwork.Extensions.DependencyInjection`, carried on
`GroundworkHostingException.Code`. See **[Hosting & Dependency Injection](Hosting-and-Dependency-Injection)**.

| Code | Meaning | Fix |
| --- | --- | --- |
| `GW-HOST-001` | A storage connection is registered with a non-singleton lifetime | Register through `AddGroundwork().AddConnection(...)`, which registers connections as process singletons; inject the scoped `IGroundworkStorage` for per-request sessions and units of work |
| `GW-HOST-002` | Two connections registered under the same name | Name the second one differently, or reconfigure the first with `services.Configure<GroundworkConnectionOptions>(name, …)` |
| `GW-HOST-003` | A connection name was requested that was never registered | Register it; the message lists the names that exist |
| `GW-HOST-004` | A registered connection has no provider factory or no connection string | Call `options.UseProvider(factory, connectionString)` |
| `GW-HOST-005` | Startup admission found pending physical schema work, or could not run | Apply the declaration from the deployment step with `groundwork apply --safe`; runtime is inspect-only |
| `GW-HOST-006` | The deployed database does not advertise a required capability | Deploy a topology that provides it, or drop the requirement and degrade gracefully |

---

## `GW-ACCESS-*` — access context

| Code | Meaning |
| --- | --- |
| `GW-ACCESS-001` | Cross-scope query without privileged access |
| `GW-ACCESS-002` | Provider session does not advertise privileged cross-scope queries |
| `GW-ACCESS-003` | Point operation under privileged access (ambiguous) |
| `GW-ACCESS-004`…`006` | Additional access-context refusals |

---

## `GW-DECL-*` — Records declarations

| Code | Meaning |
| --- | --- |
| `GW-DECL-COLUMN-001` | Invalid column declaration |
| `GW-DECL-CONCURRENCY-001` | Invalid concurrency declaration |
| `GW-DECL-KEY-001`…`003` | Invalid key declaration |
| `GW-DECL-INDEX-001`, `-002` | Invalid index declaration |
| `GW-DECL-INDEX-003` | Index over a JSON column — *"Leave the JSON column unindexed"* |

---

## `GW-DOC-*` — Documents

| Code | Meaning |
| --- | --- |
| `GW-DOC-DECL-001` | Missing `Id` selector |
| `GW-DOC-DECL-002` | JSON path projected more than once |
| `GW-DOC-DECL-003` | Column collides with a reserved/declared column |
| `GW-DOC-DECL-004` | Duplicate index name |
| `GW-DOC-DECL-005` | Index targets an unprojected path |
| `GW-DOC-DECL-006` | Index targets a JSON projection |
| `GW-DOC-DECL-007` | Enum with unsupported unsigned underlying type |
| `GW-DOC-DECL-008` | Enum JSON converter unusable or emits an unsupported kind |
| `GW-DOC-DECL-009` | Projected member can be omitted by the JSON contract |
| `GW-DOC-MAT-001`…`004` | Materialization failures |

---

## `GW-AGG-*` — aggregation

Grouped by concern:

| Family | Concern |
| --- | --- |
| `GW-AGG-DECL-001`…`010` | Profile declaration |
| `GW-AGG-QUERY-001`…`017` | Query admission |
| `GW-AGG-SOURCE-001`…`007` | Source predicate binding |
| `GW-AGG-PRED-001`…`012` | Post predicate / allowances |
| `GW-AGG-GROUP-001`…`012` | Grouping and time buckets |
| `GW-AGG-BOUND-001`…`008` | `MaxInputRows` / `MaxGroups` budgets |
| `GW-AGG-TYPE-001`…`004`, `GW-AGG-COLUMN-001`/`002` | Reducer types and columns |
| `GW-AGG-SUM-001` | `Sum` accepts only `Int32`/`Int64`/`Decimal` |
| `GW-AGG-FIRST-001` | `FirstBy` requires a non-null orderable order column |
| `GW-AGG-ADHOC-001`…`004` | Missing, expired, or mismatched ad-hoc acceptance |
| `GW-AGG-ADHOC-902` (`GW_AGG_ADHOC_902`) | Accepted aggregation without `[assembly: GwAllowAcceptedAggregations]` |
| `GW-AGG-ADHOC-903` (`GW_AGG_ADHOC_903`) | Accepted aggregation on or after its expiry date |
| `GW-AGG-ADHOC-904` (`GW_AGG_ADHOC_904`) | Accepted aggregation within its final 30 days |
| `GW-AGG-ADHOC-905` (`GW_AGG_ADHOC_905`) | Inventory of accepted aggregation metadata (id, reason, owner, expiry, and budgets) |
| `GW-AGG-ADHOC-906` (`GW_AGG_ADHOC_906`) | Required accepted aggregation metadata is not statically resolvable; fails closed |

---

## `GW-CAP-*` — capability validation

| Code | Meaning |
| --- | --- |
| `GW-CAP-002` | Provider warning (non-fatal) |
| `GW-CAP-004` | Provider does not support required capabilities |
| `GW-CAP-005` | Provider does not support the declared concurrency mode |
| `GW-CAP-013` | Capability is evidence-gated and lacks evidence |
| `GW-CAP-014` | Required capability is not registered — register it via an `IGroundworkModule` |

---

## `GW-CLI-*` / `GW-SCHEMA-*` — tooling

| Code | Meaning |
| --- | --- |
| `GW-CLI-001`, `-005`…`-012` | CLI invocation and authorization refusals |
| `GW-CLI-007` | Schema changes require explicit `--safe` authorization |
| `GW-CLI-008` | A destructive operation was not named through `--allow-destructive` |
| `GW-CLI-012` | A semantic migration was not named through `--allow-semantic` |
| `GW-CLI-013` | `adopt` was invoked against a provider that cannot compare a deployed catalog to a compiled target, so it cannot prove a match. Every provider Groundwork ships is such an inspector, MongoDB included, so this names a third-party plug-in |
| `GW-SCHEMA-TOOL-001` | Schema tool refusal |

### `GW-SCHEMA-*` — schema planning and evolution

Planning refusals say the evolution is **invalid**; the authorization codes say it is valid but
**not yet authorized**. The two are deliberately distinct: an invalid evolution has no approval that
would make it work, while an unauthorized one is waiting on an operator naming it.

| Code | Meaning | Kind |
| --- | --- | --- |
| `GW-SCHEMA-001` | Legacy schema history has no typed applied snapshot | Invalid |
| `GW-SCHEMA-002` | Applied state belongs to a different target | Invalid |
| `GW-SCHEMA-003` | A changed definition has no portable evolution — a key column's portable type changed, or a rename collides with a name another applied column still holds | Invalid |
| `GW-SCHEMA-004` | A removal has no portable operation — a key column cannot be dropped | Invalid |
| `GW-SCHEMA-005` | A new required column has no portable default or semantic migration for existing rows | Invalid |
| `GW-SCHEMA-006` | Applied state was recorded under a different persisted schema boundary — discard the catalog | Invalid |
| `GW-SCHEMA-007` | A planned **destructive** operation is not authorized. The message names the operation's address, e.g. `drop-column:orders.legacy_total` | Needs authorization |
| `GW-SCHEMA-008` | A planned **semantic** migration is not authorized, e.g. `rename-column:orders.buyer` | Needs authorization |
| `GW-SCHEMA-009` | The schema path cannot honor a declared logical-id rename. MongoDB's in-process `connection.Schema.Apply` plans from the fingerprint in `__groundwork_metadata` rather than from the applied schema ledger, so it cannot tell a renamed field from a new one. `groundwork apply` plans the same rename against that ledger and carries it | Invalid |
| `GW-SCHEMA-010` | `connection.Schema.Apply` was asked to destroy data re-applying cannot restore — drop a column or its storage, or narrow a column past the values in it. Apply it from the `groundwork` CLI, which authorizes the exact operation against the exact plan | Needs authorization |
| `GW-SCHEMA-011` | `groundwork adopt` found applied history already recorded for this target. Adoption records a catalog Groundwork has never applied; apply the pending plan instead | Invalid |
| `GW-SCHEMA-012` | `groundwork adopt` was asked to adopt a subject declared retired, which describes no catalog to verify | Invalid |
| `GW-SCHEMA-013` | The provider reported the deployed catalog invalid without naming what differs, so adoption refused rather than record an unproved claim | Invalid |

`GW-SCHEMA-007` and `-008` replace the earlier use of `GW-RUNTIME-002` for startup auto-apply
refusals. `GW-RUNTIME-002` now means only what its own row says: **index drift**.

---

## `GW-EXPAND-*` — expand–contract workflows

The contract half of an expand–contract evolution refuses until its readiness is **established** from
durable state. See **[expand–contract workflows](../v2/expand-contract.md)**.

| Code | Meaning |
| --- | --- |
| `GW-EXPAND-001` | The applied ledger does not record the column as retained beside its replacement — the expand plan has not been applied |
| `GW-EXPAND-002` | The data migration that populates the replacement column is not recorded complete |
| `GW-EXPAND-003` | The declared dual-presence window has not elapsed |
| `GW-EXPAND-004` | A contract plan was requested without readiness established from durable state |
| `GW-EXPAND-005` | Readiness was established for another target or against another applied state |
| `GW-EXPAND-006` | A declaration withdrew a supersession whose column is still retained |

---

## `GW-MIGRATION-*` — data migrations

| Code | Meaning |
| --- | --- |
| `GW-MIGRATION-001` | Provider does not advertise a required data-migration capability |
| `GW-MIGRATION-002` | A migration identity was recorded with a different request fingerprint |
| `GW-MIGRATION-003` | The provider session offers no data-migration execution |
| `GW-MIGRATION-004` | The migration cannot be expressed against its subject |
| `GW-MIGRATION-005` | Data-migration ledger state is missing, malformed, or self-contradictory |
| `GW-MIGRATION-006` | A transform produced a column it did not declare as a target |
| `GW-MIGRATION-007` | A migration stopped before its source was exhausted and can be resumed |

---

## Other

| Code | Meaning |
| --- | --- |
| `GW-COMPARE-DELETE-001` | Invalid compare-and-delete request (e.g. a JSON column in the equality set) |
| `GW-BATCHREAD-001` | A keyed batch-read's key column does not belong to the requested table |
| `GW-BATCHREAD-002` | A keyed batch-read key cannot be null |
| `GW-BATCHREAD-003` | A provider's query result omitted the batch-read key column, so a matched row could not be attributed to its key |
| `GW-BATCHREAD-004` | A single keyed batch-read value exceeds the provider's conservative encoded-payload budget |
| `GW-BATCH-FINGERPRINT-001` | Batch fingerprint refusal |
| `GW-SQLSERVER-LIFECYCLE-001` | SQL Server lifecycle table has a non-`BIN2` collation — migration required |
| `GW-SQLITE-LIFETIME-001` | A second Groundwork connection to the same SQLite file — the schema lock is held for the life of a connection. One `IStorageProviderConnection` per database file per process; in tests, one file per test or `Data Source=:memory:` |

---

## Stability

Diagnostic codes, public result semantics, and storage contracts change **only with an explicit
release note and regression proof**, even before 1.0. You can safely branch on them.

## Next

- **[Troubleshooting](Troubleshooting)** — symptom → cause → fix
