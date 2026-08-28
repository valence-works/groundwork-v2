# Writing Data

The `Groundwork.Store` write path. Everything — Records, Documents, your own family — ends up here.

## The five operations

```csharp
var session = connection.OpenSession(unit, StorageAccess.Global);

session.Insert(values, options);   // create
session.Update(values, options);   // modify existing
session.Upsert(values, options);   // create or modify
session.Delete(key, options);      // remove
session.Append(operationId, rows); // durable idempotent batch append (opt-in)
```

`StorageValues` and `StorageKey` wrap `IReadOnlyDictionary<string, object?>`:

```csharp
var values = new StorageValues(new Dictionary<string, object?>
{
    ["id"] = "order-1",
    ["total"] = 42.50m
});

var key = new StorageKey(new Dictionary<string, object?> { ["id"] = "order-1" });
```

Values are **snapshotted defensively** on construction, so mutating your source dictionary afterwards
cannot change what gets written.

## Write preconditions

The write API deliberately has **no nullable expected-version field**. You choose a precondition:

```csharp
session.Insert(values, WriteOptions.CreateOnly);
session.Update(values, WriteOptions.IfVersion(3));
session.Upsert(values, WriteOptions.Unconditional);
```

`Unconditional`, `CreateOnly`, and `IfVersion(long)` are distinct values of `WritePrecondition`.

| Refusal | When |
| --- | --- |
| `GW-WRITE-CONCURRENCY-001` | `CreateOnly` or `IfVersion` on a `ConcurrencyDeclaration.None` unit |
| `GW-WRITE-CONCURRENCY-002` | Invalid operation/precondition pairing |
| `GW-WRITE-CONCURRENCY-003` | Application supplied a system-owned token value |

The same validation runs for direct writes **and** `RowWrite` construction — before any provider I/O.

## `WriteOutcome`

```csharp
var outcome = session.Upsert(values);

switch (outcome.Status)
{
    case WriteOutcomeStatus.Inserted:
    case WriteOutcomeStatus.Updated:
    case WriteOutcomeStatus.Upserted:
    case WriteOutcomeStatus.Deleted:            break;  // outcome.Succeeded
    case WriteOutcomeStatus.Replayed:           break;  // idempotent append replay
    case WriteOutcomeStatus.NotFound:           break;
    case WriteOutcomeStatus.UniqueViolation:    break;  // outcome.UniqueIndexName
    case WriteOutcomeStatus.ConcurrencyConflict:break;
    case WriteOutcomeStatus.ComparisonMismatch: break;  // compare-and-delete
    case WriteOutcomeStatus.Superseded:         break;  // batch coalescing
}
```

| Member | Notes |
| --- | --- |
| `Status` | Immediate (possibly conservative) result |
| `Version` | Present immediately on success for optimistic units |
| `GeneratedValues` | Provider-assigned values by column name; `GeneratedValue<T>("seq")` for typed access |
| `Detail` | **Lazily** resolves failure detail; performs at most one disambiguating read, cached |
| `UniqueIndexName` | The **logical declared** index name, via `Detail` |
| `Succeeded` / `Replayed` | Convenience predicates |

### Why `Detail` is lazy

A zero-row conditional write cannot always distinguish "missing" from "stale version" without another
read. Providers return the conservative `ConcurrencyConflict` status immediately; accessing `Detail`
performs that disambiguation **once** and caches it. If you don't need to know which it was, you
never pay for the extra read.

> **API note:** `WriteOutcome` is a record and keeps the `Status` / `Version` property surface, but it
> is no longer a *positional* record — that is what allows lazy detail without eagerly probing. Use
> named properties. Code relying on the generated positional constructor or deconstruction must
> migrate.

## Generated values

For a `ColumnGeneration.ProviderSequence` column:

```csharp
var outcome = session.Insert(new StorageValues(new Dictionary<string, object?>
{
    ["payload"] = "created"     // note: the sequence key is NOT supplied
}));

long seq = outcome.GeneratedValue<long>("sequence");
```

A generated value is **never** synthesized from your payload or from a read-after-write. See
**[Streams: Append & Retention](Streams-Append-and-Retention)**.

## The native conditional write path

`IConcurrencyStorageSession.ConditionalUpsert` is a **provider-native single statement/command with
no shared pre-read**. SQLite, PostgreSQL, SQL Server, and MongoDB each issue their native form.

Two consequences worth knowing:

- **The stored row is no longer fetched as part of every write**, so it is not a free source of
  values for you or for a write bridge. In particular, a `createdAt` value is preserved by
  *insert-only write construction* (`$setOnInsert` or its equivalent), not by reading and copying the
  existing row. If you were relying on read-then-write to carry a value forward, declare it properly.
- SQL Server keeps its serializable range-lock transaction **inside the submitted batch** when you
  have not supplied a transaction, preserving the lock across the conditional update and insert
  without extra client round trips.

**MongoDB refuses `ProviderSequence` columns on this primitive**: sequence allocation needs a separate
`FindOneAndUpdate` and a transaction, which would break the one-command contract. Use ordinary
`Insert`/`Upsert`, or remove the generated column.

## Compare-and-delete

The narrow atomic claim-release operation: delete a row **only if** the key exists and every supplied
declared value equals the current row.

```csharp
if (!connection.Capabilities.Any(c => c.Id == BatchWriteCapabilities.CompareAndDelete))
    throw new NotSupportedException("This deployment cannot release claims atomically.");

var result = session.CompareAndDelete(
    new StorageKey(new Dictionary<string, object?> { ["id"] = claimId }),
    new Dictionary<string, object?> { ["owner"] = owner, ["fence"] = fence });

// Deleted           → released
// NotFound          → already absent
// ComparisonMismatch→ a successor reclaimed it; do NOT delete
```

Details that matter:

- The equality set is a **dictionary of declared scalar or binary columns**, not a general predicate.
- `PortableType.Json` and `PortableType.Double` are refused before I/O — JSON equality and property
  ordering differ across stores, and binary floating point has no comparison semantics that hold
  across them.
- A nullable expected value means the logical null value; an omitted nullable field normalizes to the
  same thing.
- Provider-owned scope, version, action, and derived search-key columns **cannot** be compared.
- `WriteOptions.IfVersion` remains available and is **separate**: a claim can be renewed to a new
  revision and still be released when its owner and fence match.
- Transactional MongoDB is required; standalone MongoDB does not advertise the capability and refuses
  before flushing a staged batch or emitting a write-path event.

It composes into an exact unit of work — see **[Unit of Work & Batching](Unit-of-Work-and-Batching)**.

## Staged writes

`RowWrite` is the provider-neutral staged write value:

```csharp
RowWrite.Insert(unit, values, options)
RowWrite.Update(unit, values, options)
RowWrite.Upsert(unit, values, options)
RowWrite.ConditionalUpsert(unit, values, options)
RowWrite.Delete(unit, key, options)
RowWrite.CompareAndDelete(unit, key, expectedValues)
```

Each `Stage` call is a **declaration position**. Reusing the same `RowWrite` *instance* is rejected,
with guidance to create a new declaration — otherwise coalescing evidence would have ambiguous
occurrence identity.

## Observability

`WriteOptions.Observer` accepts an `IWritePathObserver` invoked **once per provider batch command**,
not once per staged row.

> Observer payloads contain operation metadata only. Descriptions are deliberately **redacted
> diagnostic labels — not replayable command logs**, and must never carry row values or keys.

## Next

- **[Concurrency](Concurrency)** — the optimistic token in detail
- **[Unit of Work & Batching](Unit-of-Work-and-Batching)** — transactions and bulk writes
