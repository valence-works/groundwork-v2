# L1 Records

`Groundwork.Records` is the typed contract-family surface for ordinary rows. It turns a
`RecordTable<T>` declaration into a plain `Groundwork.Kernel.StorageUnit`; provider assemblies do
not reference the typed declaration or the CLR row type.

## Mapping

`RecordTable<T>.ToRowValues` compiles public property and field accessors once per CLR row type and
caches them in a closed generic cache. `FromRowValues` chooses a public constructor that can account
for every read-only member, then applies compiled assignments to every remaining writable member.
It refuses shapes that cannot initialize every declared member. The hot path only invokes delegates
and does not inspect `MemberInfo`, call `PropertyInfo.GetValue`, or call `Activator`.

`ToRowValues` omits a system-owned optimistic token even if a CLR type happens to expose a member
with the same name. Callers provide the expected version through `RecordWriteOptions`; the provider
returns the next version in `RecordWriteResult`. The token is likewise excluded from record queries;
a same-named CLR member materializes as its default value and must not be used as application state.
The declaration records the logical token (normally `version`), while providers normalize that
declared machinery to their physical `__groundwork_version` column or field. It is neither an
envelope nor an additional implicit application column.

Typed partial results use `table.Select(query, selector)` and execute through the same Records
session. The retained selector compiles a result materializer for direct members, anonymous shapes,
and intentionally partial same-type constructors/member initializers, so omitted columns are never
read. `RecordQueryOptions.UsingIndex(name)` carries a declared logical index to the provider for
native selection/plan verification.

A navigation-bearing Records type declares its relationship explicitly on the builder. The complex
navigation is not persisted as a column; the named reference binds its ordered source columns to the
target table key:

```csharp
var orders = RecordTable.For<Order>("orders")
    .Key(order => order.Id)
    .Index("by-customer", order => order.CustomerId)
    .Reference("customer", order => order.Customer, customers, order => order.CustomerId)
    .Build();

var customer = orders.Reference<Customer>("customer");
var query = customer.Join(orders.Query)
    .Where(order => order.Customer.Name == "Ada");
var projection = orders.Select(
    query,
    customer,
    (order, target) => new OrderCustomer(order.Id, target.Id, target.Name));
```

The joined selector is terminal and may read direct scalar members, construct typed or anonymous
results, or materialize both complete record parameters. It compiles once and retains the
zero-reflection hot path. Providers expose joined fields as stable `table.column` keys internally;
the compiled Records accessor consumes those qualified values without exposing SQL aliases or
MongoDB's nested lookup document. Generic post-projection query composition is intentionally not
part of this surface.

## Typed declared aggregations

`RecordTable<T>.Aggregate` binds expressions over `AggregationRow` to one profile declared by the
typed table. `row.Get<T>(alias)` is checked against the profile's fixed group or reducer aliases
when the binding is created; it cannot introduce an ad-hoc grouping, reducer, page size, or budget:

```csharp
var byName = table.Aggregate(
    "by-name",
    row => row.Get<string>("name"),
    row => new NameSummary(row.Get<long>("count"), row.Get<long>("total")));
var result = records.Aggregate(byName);
```

`RecordTableSession<T>.Aggregate` and `AggregateAsync` forward the named query through
`IRecordAggregationStore`, which the `Groundwork.Records.Store` adapter implements by calling the
existing provider aggregation session. A custom `IRecordStore` must opt into that capability to
execute bindings; otherwise it fails before provider work.

## Execution boundary

`IRecordStore` is the provider-neutral adapter seam. It accepts a kernel declaration, `RowValues`,
and the Query.Model `QueryRequest`. The `Groundwork.Records.Store` production integration package
provides the one-obvious-path `table.Open(connection)` extensions for the provider-neutral
connection contract; application-specific adapters can implement `IRecordStore` when they have
another execution boundary. `RecordTableSession<T>` maps typed insert, update, upsert, delete,
and query operations onto that seam. This keeps provider dependencies out of `Groundwork.Records`
while allowing the shipped integration to use the existing W1 write path and Q8 closed query
surface.

Run the mapping benchmark with:

```bash
dotnet run --project benchmarks/Groundwork.Benchmarks -- records --n 1000
```

The command exercises both writes and materialization and fails if accessor compilation happens on
either hot path.
