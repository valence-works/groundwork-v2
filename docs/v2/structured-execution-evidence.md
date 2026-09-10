# Structured execution evidence

Status: released in `0.4.0-preview.17`. The package publication and exact-feed
verification gates passed in canonical release run `34017785123` for merge
`59290973218eb0476a39ea3fd9bd740aa182560e`; see [ADR 0004](../adr/0004-structured-provider-execution-evidence.md).

## Migrating a command-text consumer

Implement `IProviderExecutionObserver` on the observer supplied through the
existing `IProviderCommandObserver` registration. Keep `Observe` for existing
command diagnostics and counts. Handle structured terminal observations in
`ObserveExecution`; do not count that callback as another command.

Select shape-only evidence when native plans are unnecessary. Request
`ProviderExecutionEvidenceOptions.ShapeAndPlans` only for diagnostic capture:
plan collection adds provider work and must stay outside ordinary timing
measurements. Registration alone does not make an unsupported operation mapped.

Migrate each acceptance rule to the facts it actually requires:

- Require a successful actual-command outcome and collected semantic shape.
  A rendered request or a pre-execution legacy event does not prove execution.
- Check the complete predicate conjunction, effective ordering and transforms,
  scope binding, projection and native paging facts. Do not reconstruct missing
  facts from the input query or a schema declaration.
- Treat unknown, absent and explicit native bounds as different states.
  A client materialization bound is not a native limit or proof of an enforced
  unique key.
- Validate opaque command, statement and target identities when correlating
  facts. Identities are capture-local; use declared logical identities for
  comparisons across captures. Never derive opaque IDs from tenant values.
- Require the plan facts needed by the rule independently of shape facts.
  A successful read may have failed or unsupported plan collection.

Do not fall back to SQL/BSON parsing when required structured facts are missing.
Report that the rule cannot yet be proved and complete the provider mapping.
Consumers still own workload definitions, representative data, result checks,
performance budgets and acceptance decisions. Groundwork reports provider facts,
not application-specific verdicts.

## Interpreting native plans

Plan provenance distinguishes estimated explain output, diagnostic replay and
original-execution telemetry. Estimates and replay statistics cannot stand in
for measurements of the original command. Plan collection command counts are
separate from legacy observed command counts.

Requested plan collection can throw even after the application read succeeded.
Retain its terminal callback: it records the successful actual-command outcome
and failed plan collection independently. The original diagnostic exception
propagates after observation; a callback exception cannot mask a pending
diagnostic or actual-command failure. Unsupported plan grammar alone does not
throw. Consumers must inspect availability instead of assuming that a normally
returning query necessarily has usable plan evidence.

A chosen-index fact does not prove a complete access path. A mapped
`ProviderPlanForest` preserves native operator relationships; a missing forest
does not mean no sort. Even a complete operator structure does not establish
sort-key semantics, spill behavior, row counts or enforced uniqueness. Require
explicit evidence for each fact your policy needs.

Treat `TopNSort` as both sorting and row-limiting work. It represents one fused
native operator, not two separate nodes, and does not establish a numeric bound
or prove that the sort stayed in memory. Standalone operations remain `Sort`
and `Limit`.

`Filter` retains a native predicate-filtering stage, not its expression or
values. Its presence alone does not prove predicate identity, selectivity or
index coverage; it carries no storage target identity.

### Native node details

From `0.4.0-preview.28`, `ProviderPlanNode.Details` may carry what the provider actually
exposed on a sort, top-N sort or limit node: `NativeSortKeys` (logical columns, direction,
null placement, supported transforms), `NativeLimit` (`Unknown`, `Absent` or `Explicit`
with a literal value) and `Spill` (observed spilled or not, with provider-reported metrics
when present). Treat every absent value as not observed: a consumer policy that requires a
fact must fail closed when it is missing, and must not read a missing spill as no spill.
Provider coverage differs and is documented per provider: PostgreSQL estimated explain
yields sort keys only; SQL Server replay yields order columns, literal `TopSort`/`Top`
bounds and a spill observation; MongoDB yields sort keys and literal limits from either
explain form and `usedDisk` from `executionStats`; SQLite yields sort purpose only.
`ProviderPlanForest.ObservedRootOrder` records the observed order of sibling roots
(MongoDB pipeline stages) without claiming parentage.

## Continuation pages

Since 0.4.0-preview.28 a keyset continuation page is a supported bounded shape.
`ProviderBoundedQueryEvidence.Continuation` is the value-free predicate the
provider emitted for the page: `Lexicographic` (one branch per order term, each
with the prefix equalities or null tests, then an exclusive bound, a non-null
test, or a contradiction, and whether the null alternative was emitted beside
the bound) on SQLite, SQL Server, MongoDB and PostgreSQL's fallback, or `Tuple`
(one exclusive bound per order term) when PostgreSQL uses its row-value fast
path; since 0.4.0-preview.29 that path no longer needs a nominated index and
takes the first declared index exposing the ordered segment. Null tests and
contradictions carry no binding identity. A consumer
comparing pages checks the branches against the ordering it asked for and the
binding roles, never the command text.

## Point reads

Since 0.4.0-preview.28 a point read carries a collected native plan when plans
are requested (the same explain seam bounded queries use, so SQLite reports a
primary-key or index search, PostgreSQL and SQL Server the seek on the
enforcing index, MongoDB the `_id` lookup, which since 0.4.0-preview.29 maps
its `IDHACK`/express fast path to `PrimaryKeySearch`) and a uniqueness witness that is
`Observed` only when the provider catalog holds a valid, unfiltered unique
index or primary key over exactly the read's key columns (scope included); the
witness names the enforced columns and whether scope participates. `NotObserved`
means the catalog did not prove it, never that the declaration was trusted.

## Rewritten search keys

A bounded query ordered by a persisted ordinal identity key (a
`QuerySearchKeyColumn` with the `Ordinal` policy and `PreservesOrdinalIdentity`)
reports the source column with the `PhysicalSearchKey` transform and the
`Ordinal` comparison on every provider, MongoDB included (0.4.0-preview.26 and
later). Since 0.4.0-preview.27 a query that selects no index takes that route
whenever any declared index that uses ordinal identities contains the persisted
key, so the ordering is index-servable without a nomination; a selected ordinary
index keeps the logical column it covers. A unit may declare folded or element search keys for other columns
without withholding the shapes of queries that do not emit them; a query that
does emit such a provider-owned physical column fails closed. MongoDB plan sort
keys over renderer-computed fields (`_groundwork_null_rank_N`,
`_groundwork_ordinal_key_N`) resolve to the source column with the `NullRank`
or `OrdinalStringKey` transform; a computed field whose stage is not in the
explain output leaves the sort unobserved.

## Provider identity

`ProviderExecutionEvidence.Provider` names the provider and the connected
server's version on every provider: the relational providers stamp
`DbConnection.ServerVersion`, and MongoDB stamps the server version resolved once
from `buildInfo` (0.4.0-preview.20 and later;
earlier previews stamped the fixed schema identity `1.0`). A consumer that
records the server version it probed can require the two to match before
admitting a capture.

## Retaining observations

The immutable runtime types are not a default JSON wire contract. Map the actual
callback to a consumer-owned, versioned artifact schema. Preserve availability,
provenance, opaque identities and all bound states; validate the reloaded
artifact before admission. Test a real provider observation through capture,
save, reload and admission, plus mutations that must be refused. Synthetic DTO
round-trip tests alone do not prove integration.

Structured evidence omits parameter values, scope plaintext, credentials and
raw commands/plans. Existing raw diagnostic artifacts remain a separate,
potentially sensitive surface; do not copy them into the structured payload.
Legacy observed command totals also do not imply that all provider metadata
operations or diagnostic probes were observed.

## Release adoption

For `0.4.0-preview.17`, pin the complete Groundwork package family to that exact
release and restore from the published Feedz source. Repeat the real artifact
lifecycle test against that exact package closure before removing the consumer's
parser. A source reference or local proof package is development evidence, not
proof of published-package adoption.
