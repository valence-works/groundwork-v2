# ADR 0004: Make interop reporting views explicit and relational-only

- Status: Accepted
- Date: 2026-08-30

## Context

Groundwork's portable schema deliberately chooses physical representations that preserve its
runtime contract. PostgreSQL and MySQL/MariaDB store `DateTimeOffset` as UTC ticks, and SQLite
stores decimal values as canonical text. Reporting and integration tools often need a provider's
idiomatic type instead. A view can provide that read-side projection without changing the table
that the Groundwork runtime writes.

The projection is nevertheless schema work: it creates a named database object, can expose rows
across tenants, and may need to be replaced when a table evolves. It must therefore be planned,
authorized, recorded, and reconciled with the same care as other physical schema work.

## Decision

An interop view is opt-in per storage unit. Its name is part of the canonical declaration and
physical target. The schema tool emits, applies, validates, and records one provider-owned view
definition for each opted-in relational unit. View creation, replacement, and removal are
deployment-tool operations and require the exact current plan authorization; in-process
`connection.Schema.Apply` refuses them with the normal protected-schema refusal.

The first implementation is relational-only: SQLite, PostgreSQL, SQL Server, and MySQL/MariaDB
use the shared relational executor and dialect projection hook. MongoDB is refused because a
scoped unit is a primary collection plus dynamically materialized per-scope collections, not one
stable relation. The in-memory provider has no native catalog and is also refused. Providers must
fail before provider mutation when a declared view is unsupported.

The view selects declared application columns and omits Groundwork's internal columns. For a
scoped unit it additionally exposes `__groundwork_scope` and therefore contains every scope's
rows. The view is not an authorization boundary: database grants and the consumer's query policy
must protect cross-scope access. A view name may not collide with its source table, another
storage object, or another declared interop view; `GW-PORT-015` reports that portability refusal.

Each dialect owns its conversion expression. SQLite casts decimal text to `NUMERIC`; PostgreSQL
converts UTC ticks to `timestamptz`; MySQL/MariaDB converts ticks to `DATETIME(6)`; SQL Server
selects its already-native `datetimeoffset(7)` and decimal values. PostgreSQL and MySQL view
timestamps have provider microsecond precision, so sub-microsecond tick precision is not promised
by the reporting surface. The portable table remains the source of exact runtime values.

Relational view DDL runs through the schema operation transaction and is published in the applied
ledger only after the operation succeeds. MySQL/MariaDB may implicitly commit DDL, so a failed
multi-operation batch can leave physical work that must be reconciled by the next plan; the
schema tool must not promise rollback equivalent to SQLite, PostgreSQL, or SQL Server.

The emitted definition carries a tautological fingerprint marker tied to the canonical provider
definition. Validation reads the live catalog text and requires that marker as well as the declared
output columns, so replacing a view with a same-shaped but different projection is reported as
drift. The marker is drift evidence, not an authorization or tamper-resistance mechanism.

## Alternatives considered

### Implicit views for every storage unit

Rejected: it silently creates public read surfaces, complicates grants and naming, and makes a
portable schema change an externally visible database change without an operator choosing it.

### Store reporting types in the base table

Rejected: it would weaken the portable tick and decimal representations that preserve Groundwork
runtime equality, ordering, and round-trip behavior.

### Treat MongoDB and InMemory as silent no-ops

Rejected: a successful deployment report would claim a view that does not exist. Explicit refusal
keeps provider capability and operational state honest.

## Consequences

- View names, definitions, and provider conversions participate in plan fingerprints and applied
  history.
- A view replacement is visible and protected in `plan --output json`; callers must authorize the
  exact deployment plan rather than granting a broad view permission.
- Scoped consumers must receive database-level grants deliberately because the view contains all
  scope rows and the raw scope discriminator.
- Provider E2E coverage must verify both the native output type and precision caveat, while
  unsupported providers must prove fail-closed, no-mutation behavior.
