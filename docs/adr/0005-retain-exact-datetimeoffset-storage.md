# ADR 0005: Retain exact `DateTimeOffset` storage

- Status: Accepted
- Date: 2026-08-30

## Context

Groundwork's portable `DateTimeOffset` contract compares instants as UTC ticks and preserves all
seven fractional-second digits. A .NET tick is 100 nanoseconds, so a value need not align to a
microsecond boundary. PostgreSQL and MySQL/MariaDB currently store those UTC ticks in `bigint`,
SQLite stores a normalized round-trip string, SQL Server uses `datetimeoffset(7)`, MongoDB stores
UTC ticks in an integer, and InMemory preserves the submitted .NET value while applying UTC-tick
comparison semantics.

Issue #182 investigated whether a declaration such as `DateTimeOffset(6)` could instead select
native temporal types and remove the integer or text encodings. The question is about the base
storage contract, not the read-side reporting views accepted in ADR 0004.

The provider limits are not uniform:

| Provider | Candidate physical type | Precision and range consequence |
| --- | --- | --- |
| PostgreSQL | `timestamptz(6)` | Native and microsecond-precise; fractional precision is limited to 0-6 digits. |
| SQL Server | `datetimeoffset(6)` | Native and microsecond-precise; `datetimeoffset` supports 0-7 digits and the full years 0001-9999 range. |
| MySQL/MariaDB | UTC-normalized `DATETIME(6)` | Native and microsecond-precise, but the supported `DATETIME` range begins at year 1000. MySQL rounds finer input by default; MariaDB truncates it by default. |
| SQLite | canonical `TEXT` or numeric encoding | SQLite has no date/time storage class; an application chooses TEXT, REAL, or INTEGER. Its built-in date/time functions only preserve milliseconds. |
| MongoDB | integer microseconds or ticks | BSON Date is milliseconds since the Unix epoch, so it cannot represent six fractional digits. |
| InMemory | submitted .NET value | No external native storage type exists; query semantics compare its UTC ticks. |

The current public declaration already has `Precision` and `Scale`, but they describe decimal
columns. They participate in canonical schema serialization, fingerprints, physical snapshots,
and evolution rules. Reusing those fields for temporal precision would give one property two
different meanings and would make decimal widening rules accidentally applicable to time.

## Decision

Groundwork will retain the current exact `DateTimeOffset` contract and physical mappings. It will
not add `DateTimeOffset(6)` or silently remap existing declarations before 1.0.

Declaration-level microsecond precision does not eliminate provider-specific encodings: MongoDB
would still need an integer representation and SQLite would still need an application-selected
storage format. It would also narrow the portable value set on MySQL/MariaDB and discard one digit
of values that Groundwork currently promises to round-trip. Those costs outweigh the benefit of
making the relational base tables more idiomatic, especially now that ADR 0004 provides opt-in
native reporting projections without weakening runtime storage.

If demand justifies a precision-limited temporal contract later, it must be a new explicit schema
semantic rather than a reinterpretation of decimal `Precision`/`Scale`. Its contract must define:

- a UTC instant rather than preservation of the submitted offset;
- a declared fractional precision independent of decimal precision;
- the portable range, including whether years before 1000 are refused;
- write validation that rejects values not aligned to the declared precision instead of silently
  rounding or truncating them; and
- provider mappings that remain allowed to use integer or text storage where no lossless native
  temporal type exists.

Rejecting non-aligned values is the only policy that keeps a declared precision observable and
provider-neutral. Letting each engine round or truncate would make equality, ordering, uniqueness,
and index boundaries depend on the selected provider.

## Migration implications

Changing an existing `DateTimeOffset` column to a precision-limited temporal contract would be a
data and API migration, not a compatible precision adjustment:

- PostgreSQL and MySQL/MariaDB would change from `bigint` UTC ticks to temporal columns.
- SQLite would need a new canonical representation or an explicit decision to retain its text
  representation.
- MongoDB could not use BSON Date for microseconds. It could retain UTC ticks and add alignment
  validation; choosing integer microseconds instead would require a data rewrite.
- SQL Server would narrow `datetimeoffset(7)` to `datetimeoffset(6)` and must first prove that all
  values are microsecond-aligned.
- Existing defaults, indexes, predicates, fingerprints, applied snapshots, drift validation,
  generators, and schema-evolution rules would all need to understand the new semantic.

A future implementation should therefore use expand/contract: add a new column or storage unit,
validate every existing value against the new range and alignment contract, backfill explicitly,
move readers and writers, and only then retire the exact column. It must never reinterpret an
existing applied declaration in place.

## Evidence

- [.NET `DateTimeOffset` uses 100-nanosecond ticks](https://learn.microsoft.com/en-us/dotnet/api/system.datetimeoffset.addticks?view=net-10.0).
- [PostgreSQL timestamps have microsecond resolution and accept precision 0-6](https://www.postgresql.org/docs/current/datatype-datetime.html).
- [SQL Server `datetimeoffset` supports precision 0-7 and years 0001-9999](https://learn.microsoft.com/en-us/sql/t-sql/data-types/datetimeoffset-transact-sql?view=sql-server-ver17).
- [MySQL temporal fractional precision is 0-6 and lower precision rounds by default](https://dev.mysql.com/doc/refman/8.4/en/fractional-seconds.html).
- [MariaDB temporal microseconds and precision coercion are separately configurable](https://mariadb.com/docs/server/reference/sql-functions/date-time-functions/microseconds-in-mariadb).
- [MySQL `DATETIME` has a supported range beginning at year 1000](https://dev.mysql.com/doc/refman/8.4/en/datetime.html).
- [SQLite has no date/time storage class](https://www.sqlite.org/datatype3.html#date_and_time_datatype), and [its date/time functions retain only the first three fractional digits](https://www.sqlite.org/lang_datefunc.html#time_values).
- [BSON Date stores milliseconds since the Unix epoch](https://www.mongodb.com/docs/manual/reference/bson-types/#date).

## Alternatives considered

### Remap every `DateTimeOffset` declaration to native temporal storage

Rejected: PostgreSQL and MySQL/MariaDB cannot retain the seventh fractional digit, MongoDB cannot
retain microseconds in BSON Date, SQLite has no native type, and MySQL/MariaDB do not support the
full existing range. SQL Server's current `datetimeoffset(7)` mapping is already native and exact,
so remapping it provides no benefit.

### Make microsecond precision opt-in on the existing portable type

Rejected for now: it creates a second semantic for one type and a cross-provider data migration,
while still failing to deliver a native type on every provider. A future opt-in must use a distinct
temporal-precision declaration and migration boundary.

### Round or truncate values at write time

Rejected: provider-specific coercion would change observable equality and ordering. A future
precision-limited contract must reject non-aligned values before provider mutation.

## Consequences

- Existing catalogs and applications keep exact 100-nanosecond UTC-instant behavior.
- Provider-specific integer and text encodings remain an intentional portability mechanism.
- ADR 0004 interop views remain the supported way to expose idiomatic relational reporting types.
- No runtime, schema, generator, or migration behavior changes as a result of this spike.
- A future precision-limited temporal feature requires a separate contract and explicitly planned
  expand/contract migration work.
