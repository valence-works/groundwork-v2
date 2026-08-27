# Groundwork.Substrate.Mongo

The document-store equivalent of `Groundwork.Substrate.Relational`: the shared seam for
MongoDB-shaped providers — session and transaction ownership, collection dispatch, and the
write-conflict retry behavior that exact append and durable idempotency depend on.

`Groundwork.MongoDb` is built on it.

Transactional guarantees require a replica-set or sharded deployment. Standalone MongoDB cannot
provide them, and Groundwork does not pretend otherwise: the capability is simply not advertised.

## Referencing it

`Groundwork.MongoDb` brings this transitively. Reference it directly when writing a document-store
provider.

```bash
dotnet add package Groundwork.Substrate.Mongo --prerelease
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
