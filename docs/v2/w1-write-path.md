# W1 native conditional write path

`IConcurrencyStorageSession.ConditionalUpsert` is a provider-native, conditional
write primitive. SQLite, PostgreSQL, SQL Server, and MongoDB issue their native
single statement/command without a shared pre-read. The `WritePathObserver` is a
proof seam for counting that command; observer descriptions are intentionally
redacted diagnostic labels, not replayable command logs.

## Outcome and detail

`WriteOutcome.Status` is the immediate result. A provider can return the
conservative `ConcurrencyConflict` status when a zero-row conditional write does
not distinguish “missing” from “stale version” without another read. Accessing
`WriteOutcome.Detail` performs that disambiguation at most once and caches it. A
successful outcome carries its version immediately; unique violations carry the
logical declared index name where the provider exposes one.

The stored row is no longer fetched as part of every write, so it is not a free
source of values for the caller or write bridge. In particular, `createdAt` is
preserved by insert-only write construction (`$setOnInsert`/equivalent), rather
than by reading and copying the existing row.

MongoDB `ProviderSequence` columns are intentionally refused by this primitive:
sequence allocation requires a separate `FindOneAndUpdate` command and a
transaction, which would violate the one-command contract. Use ordinary
`Insert`/`Upsert` or remove the generated column for a conditional write.

## Public API note

`WriteOutcome` remains a record and keeps the two-field property surface
(`Status`, `Version`), but its implementation is no longer a positional record so
that lazy detail can be represented without eagerly probing. Consumers should use
named properties; code depending on the generated positional constructor or
deconstruction shape must migrate to the named properties. `UniqueIndexName` is
optional metadata and is not populated by the conditional write for a CAS
conflict until detail is requested.
