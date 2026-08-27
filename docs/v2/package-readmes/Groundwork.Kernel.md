# Groundwork.Kernel

The declaration layer. `StorageUnit`, `ColumnDefinition`, `PortableType`, `KeyDefinition`,
`IndexDefinition`, `ConcurrencyDeclaration`, `RetentionDeclaration`, `AggregationProfile`,
`PortabilityValidator`, the capability registry, and the schema subject and fingerprint model that
startup admission compares against.

Kernel declarations describe *physical* storage in provider-neutral terms. They do not know about
records, documents, connections, or any particular database. A declaration that cannot be honoured
portably is refused at declaration time rather than at the first write.

Its only non-BCL reference is `Groundwork.Query.Model`, and an architecture test in CI enforces
that by inspecting compiled assembly references.

## Referencing it

You rarely reference this package directly — a provider package brings it transitively. Reference
it explicitly when you declare storage in a library that deliberately picks no provider.

```bash
dotnet add package Groundwork.Kernel --prerelease
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
