# Concurrency

Optimistic concurrency in Groundwork is **opt-in and system-owned**. If you don't declare it, you get
none of its cost.

## Opting in

```csharp
// Default — no version column, no version machinery at all.
Concurrency = ConcurrencyDeclaration.None

// Opt in.
Concurrency = ConcurrencyDeclaration.Optimistic()            // token column "version"
Concurrency = ConcurrencyDeclaration.Optimistic("revision")  // custom logical name
```

With builders:

```csharp
StorageUnit.Declare("orders", "orders").OptimisticConcurrency()  // or .Optimistic()
RecordTable.For<Customer>("customers").OptimisticConcurrency()
DocumentUnit.For<Note>("note", "notes").OptimisticConcurrency()
```

For a `None` unit, providers **must not** add a version column, version metadata, token projection,
or any version read/increment/CAS work. That is a contract, not an optimization.

## The token is system-owned

You never supply or change the token value.

- Providers may map the logical token to a hidden physical column (normally `__groundwork_version`).
- If a declared token column *is* present in the portable schema, it must be a **non-null `Int64`
  with default `0`**.
- The provider creates the physical token with default `0`; the **first accepted write returns logical
  version `1`**.
- Supplying a token value is rejected with `GW-WRITE-CONCURRENCY-003`.
- The token cannot be part of the storage key, and cannot be part of any declared index.
- In Records, `ToRowValues` **omits** the token even if your CLR type has a member with that name,
  and queries exclude it too. A same-named CLR member materializes as its default value and **must
  not be used as application state**.

## Version lifecycle

| Operation | Result on an optimistic unit |
| --- | --- |
| Successful `Insert` | Version `1` |
| Accepted `Update` / `Upsert` | Token incremented |
| Stale `IfVersion` | `ConcurrencyConflict`, nothing written |

```csharp
var inserted = session.Insert(values);                        // Version == 1
var updated  = session.Update(values, WriteOptions.IfVersion(1)); // Version == 2
var stale    = session.Update(values, WriteOptions.IfVersion(1)); // ConcurrencyConflict
```

An optimistic conditional upsert remains a **single provider write primitive**. `None`-mode
conditional upsert is also a single write, reporting logical `Inserted`/`Updated` with no token
machinery at all.

## Handling a conflict

`ConcurrencyConflict` is a **status, not an exception**. The typical loop:

```csharp
for (var attempt = 0; attempt < maxAttempts; attempt++)
{
    var current = session.Read(key);
    if (current is null) return;

    var next = Apply(current.Values);
    var outcome = session.Update(next, WriteOptions.IfVersion(current.Version!.Value));

    if (outcome.Succeeded) return;
    if (outcome.Status != WriteOutcomeStatus.ConcurrencyConflict) throw Unexpected(outcome);
}
```

If you need to know whether the conflict was "missing" or "stale", inspect `outcome.Detail` — it
performs at most one cached disambiguating read. If you don't need to know, don't touch it and you
don't pay for it.

## Compare-and-delete vs. `IfVersion`

They solve different problems and can be used together:

| | `WriteOptions.IfVersion(n)` | `CompareAndDelete(key, values)` |
| --- | --- | --- |
| Compares | The system-owned revision token | Your declared column values |
| Survives an unrelated update? | No — revision moves | **Yes** — owner/fence still match |
| Typical use | Lost-update prevention | Distributed claim/lease release |

A claim can be renewed to a new revision and still be released when its owner and fence match. See
**[Writing Data](Writing-Data)**.

## Staged writes

`RowWrite` applies the same precondition and token validation as direct writes. A staged
**non-unconditional** precondition uses the provider's row-attributed fallback when a native
multi-row command cannot preserve its semantics; unconditional homogeneous groups keep the fast
native batch paths.

`RowWrite.Upsert` with an explicit `IfVersion`, and the explicit `RowWrite.ConditionalUpsert`, always
use the provider's atomic conditional-upsert primitive.

## Provider behavior

All five conformance providers (InMemory, SQLite, PostgreSQL, SQL Server, MongoDB) implement both
`None` and `Optimistic`, verified by the shared suite: catalog/index checks, CRUD outcomes, optimistic
conflict behavior, scope isolation, and unit-of-work commit/rollback.

- **SQL Server** uses one database-scoped `sp_getapplock` plus per-target durable fences/history for
  schema coordination, preventing different declarations from deadlocking through the shared server
  catalog. Pre-database-lock previews do not coordinate with the new resource, so rolling upgrades
  must pre-apply schema or restrict schema application to one release line. It uses serializable
  write transactions for optimistic concurrency.
- **SQLite** uses `BeginTransaction(IsolationLevel.Serializable, deferred: false)` — an immediate
  write transaction — which avoids upgrading a read transaction and the resulting `BUSY_SNAPSHOT`.
- **MongoDB** transactional same-identity races return portable deterministic outcomes;
  wrapper-owned transactions retry transient write-conflict bodies.

The deterministic **W2 concurrency harness** (`ConcurrencyHarness` in `Groundwork.Testing`) runs both
modes and asserts the invariants. SQLite and in-memory runs need no external services; live
PostgreSQL, SQL Server, and MongoDB activate through their connection-string environment variables.
See **[Testing](Testing)**.

## Next

- **[Writing Data](Writing-Data)** — outcomes and preconditions
- **[Unit of Work & Batching](Unit-of-Work-and-Batching)** — transactional grouping
