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
