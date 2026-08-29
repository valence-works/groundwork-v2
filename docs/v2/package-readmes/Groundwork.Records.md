# Groundwork.Records

Typed rows. `RecordTable<T>` maps a CLR type to a kernel `StorageUnit` — key, columns, indexes,
concurrency, scope — and exposes the `IRecordStore` seam. It also binds typed
`AggregationRow` selectors to declared aggregation profiles; the binding preserves the profile's
fixed grouping, reducers, and budgets and never creates an ad-hoc shape.
Navigation-bearing tables can bind one declared reference and compile a terminal typed projection
over both source and target records. Joined materialization keeps provider rows table-qualified and
does not add reflection to the per-row path.
`[GwTable]` row types use the direct accessors and constructor/member materializers emitted by
`Groundwork.Schema.Generator`; `AccessorDynamicCodeGenerationCount` remains zero for that path.
That generated accessor/materializer path is the Native AOT record-mapping contract. The fluent
`RecordTable.For<T>` declaration still infers columns from CLR members, and runtime-typed projection
and aggregation selectors still compile expressions; those boundaries are explicitly annotated as
requiring unreferenced and/or dynamic code. Native AOT applications must add
`Groundwork.Schema.Generator`, preserve members used by fluent declaration inference, and keep row
and projection shapes on generated surfaces. An ungenerated row type is refused with an explicit
generator-oriented error instead of attempting dynamic code.

This package deliberately has **no provider dependency**, which means it also has no
`table.Open(connection)`. That is the point: a library can declare its storage and stay
provider-neutral, leaving the choice of database to whoever consumes it.

## Referencing it

If you are writing an application, you almost certainly want
**[`Groundwork.Records.Store`](https://www.nuget.org/packages/Groundwork.Records.Store)** instead —
it adds the production bridge and brings this package along. Reference `Groundwork.Records` on its
own only from a library that declares storage without picking a provider.

```bash
dotnet add package Groundwork.Records --prerelease
```

## Every Groundwork package

Previews are published to the public
[Groundwork Feedz source](https://f.feedz.io/valence-works/groundwork/nuget/index.json), not yet to
nuget.org, so configure that source for `Groundwork.*` before restoring — see
[Installation](https://github.com/valence-works/groundwork-v2/blob/main/docs/wiki/Installation.md).

Pin one exact version across your whole `Groundwork.*` closure — mixing versions is not supported
and not tested. Previews are pre-1.0; read the
[versioning policy](https://github.com/valence-works/groundwork-v2/blob/main/docs/v2/versioning.md)
and the [support matrix](https://github.com/valence-works/groundwork-v2/blob/main/docs/v2/support-matrix.md)
before adopting a provider in production. The
[package map](https://github.com/valence-works/groundwork-v2/blob/main/docs/wiki/Package-Map.md)
explains the layering and which packages an application actually references.

Runtime packages target `net8.0` and `net10.0`. Analyzers, source generators, and the portable
contract packages target `netstandard2.0`. `Groundwork.SchemaTool.MSBuild` targets `net10.0`
because its task loads into the SDK's own MSBuild process.

MIT licensed. Source and issues: <https://github.com/valence-works/groundwork-v2>.
