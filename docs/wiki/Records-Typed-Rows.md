# Records: Typed Rows

`Groundwork.Records` maps a CLR type to a `StorageUnit` and gives you typed CRUD and queries.
This is the ordinary path for most applications.

> Reference **`Groundwork.Records.Store`**, not `Groundwork.Records`. The `.Store` package adds
> `table.Open(connection)` and the typed unit of work; `Groundwork.Records` comes transitively.

## Declaring a table

```csharp
using Groundwork.Kernel;
using Groundwork.Records;
using Groundwork.Sqlite;
using Groundwork.Store;

public sealed record Customer(Guid Id, string Email, string Name);

var table = RecordTable.For<Customer>("customers")
    .Key(customer => customer.Id)
    .OptimisticConcurrency()
    .Column(customer => customer.Email, column => column.MaxLength(320).Required())
    .Column(customer => customer.Name,  column => column.MaxLength(200).Required())
    .Index("by_email", customer => customer.Email)
    .Build();
```

Builder methods: `Key`, `OptimisticConcurrency` (alias `Optimistic`), `Column`, `Index`,
`UniqueIndex`, `Build`. Selectors must select a **public property or field directly** —
`c => c.Email` is fine, `c => c.Email.Trim()` is not.

`table.Definition` is the plain `StorageUnit`. Providers only ever see that.

## Applying schema and opening a session

```csharp
using var connection = new SqliteProviderFactory().Create("Data Source=customers.db");

connection.Schema.Apply(table.Definition);
if (!connection.Schema.Diff(table.Definition).IsEmpty)
    throw new InvalidOperationException("Schema drift after apply.");

var records = table.Open(connection);                                   // global access
var scoped  = table.Open(connection, StorageAccess.Scoped(new StorageScope("tenant-a")));
```

`RecordTableSession<T>` is a non-owning view. Don't dispose it; keep the connection alive.

## CRUD

```csharp
var customer = new Customer(Guid.NewGuid(), "ada@example.test", "Ada");

var inserted = records.Insert(customer);
// inserted.Status  == RecordWriteStatus.Inserted
// inserted.Version == 1   (because the unit declared OptimisticConcurrency)

var changed = customer with { Name = "Ada Lovelace" };
var updated = records.Update(changed, RecordWriteOptions.IfVersion(inserted.Version!.Value));
// Status == Updated, Version == 2

var upserted = records.Upsert(changed with { Name = "Ada Byron" },
                              RecordWriteOptions.IfVersion(updated.Version!.Value));
// Status is Updated or Upserted, Version == 3

var conflict = records.Upsert(changed with { Name = "stale" },
                              RecordWriteOptions.IfVersion(1));
// Status == RecordWriteStatus.ConcurrencyConflict — nothing was written

records.Delete(customer);
```

### `RecordWriteResult`

| Member | Meaning |
| --- | --- |
| `Status` | `Inserted`, `Updated`, `Upserted`, `Deleted`, `NotFound`, `UniqueViolation`, `ConcurrencyConflict` |
| `Version` | New logical version, when the unit is optimistic |
| `GeneratedValues` | Provider-assigned values (e.g. a sequence key), by column name |
| `UniqueIndexName` | The **logical declared** index name for a unique violation, where the provider exposes one |
| `Succeeded` | True for `Inserted` / `Updated` / `Upserted` / `Deleted` |

**These are results, not exceptions.** `ConcurrencyConflict` and `UniqueViolation` are ordinary
statuses you branch on. Reserve exceptions for genuinely exceptional conditions.

### `RecordWriteOptions`

```csharp
RecordWriteOptions.Unconditional          // default
RecordWriteOptions.IfVersion(expected)    // requires .OptimisticConcurrency()
```

Using `IfVersion` on a unit without optimistic concurrency throws with an explicit instruction:
*"Declare `.OptimisticConcurrency()` before using `RecordWriteOptions.IfVersion(...)`."*

## Querying

```csharp
var query = table.Query.Where(customer => customer.Email == "ada@example.test");

var matches = records.Query(query, RecordQueryOptions.UsingIndex("by_email"));
var count   = records.Count(query);
var exists  = records.Any(query);

// Or inline:
var recent = records.Query(q => q
    .Where(c => c.Name.StartsWith("A"))
    .OrderBy(c => c.Email)
    .Take(50));
```

`table.Query` is an `IGwQueryable<T>` — a **closed** LINQ surface, not `IQueryable`. Unsupported
expressions are refused at build time with a `GW-LINQ-*` code and a named fix, never evaluated
client-side. See **[Querying](Querying)**.

`RecordQueryOptions.UsingIndex(name)` carries a **declared logical** index name to the provider for
native selection/plan verification. It is *not* an optimizer hint — see
**[Query Coverage & Indexes](Query-Coverage-and-Indexes)**.

### Typed projections

Read only the columns you need:

```csharp
var projection = table.Select(
    table.Query.Where(c => c.Name.StartsWith("A")),
    c => new { c.Id, c.Email });

var rows = records.Query(projection);
```

The retained selector compiles a result materializer for direct members, anonymous shapes, and
intentionally partial same-type constructors/initializers. **Omitted columns are never read** from
the database.

## Batched writes

```csharp
using var batch = table.BeginUnitOfWork(connection, BatchWriteOptions.Exact);

batch.Upsert(new Customer(Guid.NewGuid(), "grace@example.test", "Grace"));
batch.Upsert(new Customer(Guid.NewGuid(), "mary@example.test",  "Mary"));

var report = batch.CommitWithOutcomes();
// report.Summary.IsSuccessful, report.Outcomes.Count == 2
```

`RecordTableStoreUnitOfWork<T>` supports `Insert`, `Update`, `Upsert`, `Delete`, `Commit`,
`CommitWithOutcomes`, `Rollback`, `Dispose`. Commit and rollback are terminal; disposing a
non-terminal unit rolls it back. See **[Unit of Work & Batching](Unit-of-Work-and-Batching)**.

## The version token is system-owned

This trips people up, so it is worth being explicit.

- `ToRowValues` **omits** the optimistic token even if your CLR type happens to have a member with
  the same name.
- You supply the expected version through `RecordWriteOptions`; the provider returns the next
  version in `RecordWriteResult`.
- The token is **excluded from queries** too. A same-named CLR member materializes as its default
  value and **must not be used as application state**.
- Supplying a token value in your payload is rejected with `GW-WRITE-CONCURRENCY-003`.
- The declaration records the logical token (normally `version`); providers normalize it to a hidden
  physical `__groundwork_version` column or field. It is neither an envelope nor an extra
  application column.

## Mapping performance

For a `[GwTable]` type, the schema generator emits direct getters and constructor/member
materialization and registers them when the assembly loads. `ToRowValues` and `FromRowValues` invoke
those ordinary delegates with **zero runtime dynamic-code generation**. Fluent-only ungenerated
types retain the preview compatibility fallback; source-generated accessors are the AOT path.

Bind the generated accessor to the generator's declaration without reflection:

```csharp
var table = RecordTable.FromGenerated<Customer>(CustomerStorageUnit.Definition);
```

`FromGenerated` fails closed when the row has no registered generated metadata. Use this entry
point, rather than `RecordTable.For<T>`, for a trimmed or Native AOT application.

`FromRowValues` chooses a public constructor that can account for every read-only member, then
applies compiled assignments to remaining writable members. Shapes that cannot initialize every
declared member are refused.

You can assert this in your own tests:

```csharp
RecordTable<Customer>.AccessorDynamicCodeGenerationCount // stays zero for generated rows
```

The repository benchmark that enforces it:

```bash
dotnet run --project benchmarks/Groundwork.Benchmarks -- records --n 1000
```

The manual `Performance evidence` workflow captures the release/milestone result; ordinary pull
requests use deterministic correctness tests instead.

## Using a custom execution boundary

`IRecordStore` is the provider-neutral seam. `Groundwork.Records.Store` implements it over
`IStorageProviderConnection`, but you can implement it yourself if you have another boundary
(an RPC layer, a caching decorator, a test double):

```csharp
public interface IRecordStore
{
    RecordWriteResult Insert(StorageUnit unit, RowValues values, RecordWriteOptions? options = null);
    RecordWriteResult Update(StorageUnit unit, RowValues values, RecordWriteOptions? options = null);
    RecordWriteResult Upsert(StorageUnit unit, RowValues values, RecordWriteOptions? options = null);
    RecordWriteResult Delete(StorageUnit unit, RowValues key,    RecordWriteOptions? options = null);
    RecordQueryResult Query(QueryRequest request, QueryRenderOptions? options = null);
}

var records = table.Open(myCustomStore);
```

This is why `Groundwork.Records` has no provider dependency.

## Common declaration refusals

| Situation | Diagnostic |
| --- | --- |
| Index over a `Json` column | `GW-DECL-INDEX-003` — *"Leave the JSON column unindexed"* |
| `IfVersion` without `.OptimisticConcurrency()` | Explicit `InvalidOperationException` naming the fix |
| Selector that is not a direct member access | `ArgumentException` |
| Type with no public instance columns | `ArgumentException` |

## Next

- **[Writing Data](Writing-Data)** — the underlying write path and outcomes
- **[Querying](Querying)** — the full query surface
- **[Documents](Documents)** — when a row is not the right shape
