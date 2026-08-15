# L1 Records

`Groundwork.Records` is the typed contract-family surface for ordinary rows. It turns a
`RecordTable<T>` declaration into a plain `Groundwork.Kernel.StorageUnit`; provider assemblies do
not reference the typed declaration or the CLR row type.

## Mapping

`RecordTable<T>.ToRowValues` compiles public property and field accessors once per CLR row type and
caches them in a closed generic cache. `FromRowValues` uses the matching public constructor (or
compiled writable-member assignments) to materialize a row. The hot path only invokes delegates and
does not inspect `MemberInfo`, call `PropertyInfo.GetValue`, or call `Activator`.

`ToRowValues` omits a system-owned optimistic token even if a CLR type happens to expose a member
with the same name. Callers provide the expected version through `RecordWriteOptions`; the provider
returns the next version in `RecordWriteResult`.

## Execution boundary

`IRecordStore` is the provider-neutral adapter seam. It accepts a kernel declaration, `RowValues`,
and the Query.Model `QueryRequest`. The `Groundwork.Records.TestingAdapter` companion package
provides the one-obvious-path `table.Open(connection)` extensions for the existing relational and
Mongo provider connection contracts; application-specific adapters can implement `IRecordStore`
when they have another execution boundary. `RecordTableSession<T>` maps typed insert, update,
upsert, delete, and query operations onto that seam. This keeps provider dependencies out of
`Groundwork.Records` while allowing the shipped adapter to use the existing W1 write path and Q8
closed query surface.

Run the mapping benchmark with:

```bash
dotnet run --project benchmarks/Groundwork.Benchmarks -- records --n 1000
```

The command fails if any accessor compilation happens during the 1,000 mapping operations.
