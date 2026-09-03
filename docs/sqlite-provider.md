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
Microsoft.Data.Sqlite completes its asynchronous ADO.NET surface synchronously, and the provider
serializes every non-unit-of-work session command on a connection gate, and holds that same gate for
the complete unit-of-work transaction lifetime. A caller that awaits while keeping a unit of work
open intentionally keeps other provider work queued. An asynchronous read, write, append, or
unit-of-work commit checks cancellation before joining the synchronous gate and returns an
already-completed task once admitted — it never yields the thread. Cancellation cannot interrupt a
caller already blocked on that synchronous gate. `QueryAsync` checks the token again before issuing
its reader on the asynchronous ADO.NET surface, where the token can still interrupt the native
statement mid-execution. Use this provider's asynchronous surface for source compatibility with
hosts written against it, not for thread relief.

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
