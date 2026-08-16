# ADR 0003: Make cross-scope access explicit, audited, and query-only

- Status: Accepted
- Date: 2026-08-16

## Context

A scoped storage unit normally requires one `StorageScope` for every session.
Recovery, administration, and diagnostics sometimes need to find rows across
all scopes. Treating those callers as global would weaken the unit declaration;
iterating tenant sessions in application code would make paging, counts, and
latest-per-key behavior provider-dependent.

## Decision

Groundwork exposes `StorageAccess.PrivilegedAcrossScopes` for a scoped unit. The
caller must supply a bounded, non-blank audit identity and purpose. The access
is query-only and the session refuses ordinary queries, point reads, writes,
aggregation, inspection, retention, append, and units of work.

`QueryAcrossScopes` returns every row with its `StorageScope`. Providers inject
the physical scope projection and a deterministic SHA-256 scope token. The
token participates in identity ordering and latest-per-key partitioning, but
raw scope and audit strings are not serialized into continuation tokens.
Continuation identity includes the audit identity and purpose, so a token
cannot be replayed under a different privileged invocation.

An optional `IStorageAccessObserver` receives the unit, operation, identity, and
purpose before provider work begins. Provider conformance covers in-memory,
SQLite, PostgreSQL, SQL Server, and MongoDB.

## Consequences

- Ordinary scoped isolation remains unchanged.
- Administrative callers cannot accidentally perform an ambiguous point
  operation or mutation.
- Cross-scope paging, totals, and latest-per-key have one portable contract.
- MongoDB maintains a provider-owned scope registry for its per-scope physical
  collections; relational providers project their hidden scope column.
- The public declaration contract reserves the provider scope and scope-token
  column names so application values cannot be overwritten or hidden.
- Privileged query intent is observable without persisting audit metadata in
  application rows or continuation tokens.
