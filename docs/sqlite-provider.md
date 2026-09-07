# SQLite provider

`Groundwork.Sqlite` is the SQLite implementation of the production, provider-neutral
`Groundwork.Store` contracts and relational schema behaviors. It uses `Microsoft.Data.Sqlite` and
keeps the provider-specific surface in the `Groundwork.Sqlite` assembly; relationship-transition
execution is intentionally not part of this provider slice.

The provider enables WAL and a busy timeout when a store opens. Schema and unit-of-work writes use
`BeginTransaction(IsolationLevel.Serializable, deferred: false)`, which Microsoft.Data.Sqlite maps
to an immediate write transaction. This avoids upgrading a read transaction and the resulting
`BUSY_SNAPSHOT` failure. SQLite 3.35.0 or newer is required because the Groundwork write contract
depends on modern upsert/returning behavior; opening an older native library fails with the version
in the exception message.

`IStorageSession`'s asynchronous members are honest about what this provider can do:
Microsoft.Data.Sqlite completes its asynchronous ADO.NET surface synchronously. The provider gate
serializes units of work, schema and catalog work, provider disposal, and every command on a shared
in-memory connection, and a unit of work holds that gate for its complete transaction lifetime.
A session on a file-backed store runs on its own connection and does not join that gate: it opens
while another caller's unit of work is active, its reads proceed under WAL, and its writes wait on
SQLite's own writer lock through the connection's busy timeout (five seconds by default) rather
than on the provider gate. Concurrent callers sharing one such session view are serialized on a
per-session gate, as the PostgreSQL and SQL Server views are; an owned session has no gate at all,
because it has one owner. An asynchronous read, write, append, or unit-of-work commit checks
cancellation before joining a gate and returns an already-completed task once admitted — it never
yields the thread. Cancellation cannot interrupt a caller already blocked on a synchronous gate or
on SQLite's busy handler. `QueryAsync` checks the token again before issuing its reader on the
asynchronous ADO.NET surface, where the token can still interrupt the native statement
mid-execution. Use this provider's asynchronous surface for source compatibility with hosts written
against it, not for thread relief.

Disposing a provider while one of its units of work is still active never waits on that unit of
work's gate. The provider refuses new work and releases its remaining handles when the unit of work
commits, rolls back, or is disposed.

The store owns one `${database}.schema.lock` file handle for its lifetime. A second process or
connection to the same file is rejected before schema work begins. The handle is not opened and
closed around individual writes. SQLite index names are database-global, so logical index names are
stored with a provider prefix while the catalog exposes the declared logical names.

SQLite cannot alter a column's nullability in place. Adding a required column is staged as nullable,
backfilled from its portable default, and finalized by a transactionally rebuilt table. The rebuild
uses SQLite's recorded CREATE TABLE and index SQL, copies all existing columns, renames the rebuilt
table, and restores the native indexes; this preserves rows, keys, constraints, and indexes.
