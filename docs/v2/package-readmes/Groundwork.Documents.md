# Groundwork.Documents

Typed JSON documents with schema versioning, composed over `Groundwork.Records` and the
provider-neutral `Groundwork.Store` contracts.

`DocumentUnit.For<T>(kind, name)` declares an ordinary kernel `StorageUnit`: a typed key column, a
required `document` JSON column, and a required `schemaVersion` string column. Providers receive no
document-specific contract — a mapped document write is an ordinary `RowWrite` carrying the same
`StorageValues` as any other record — so adding this package cannot change provider behavior.

Versioned upcasting is explicit: a document read at an older `schemaVersion` is upcast through the
declared chain, and an unknown version is refused rather than guessed.

Full notes on the stable storage contract and upcasting:
[src/Groundwork.Documents/README.md](https://github.com/valence-works/groundwork-v2/blob/main/src/Groundwork.Documents/README.md).

## Referencing it

```bash
dotnet add package Groundwork.Sqlite --prerelease
dotnet add package Groundwork.Documents --prerelease
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
