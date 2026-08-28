# Groundwork.Store

The runtime contract that providers implement and applications call.
`IStorageProviderConnection`, `IStorageSession`, `IUnitOfWork`, `StorageAccess`, `StorageScope`,
`WriteOutcome`, `RowWrite`, `BatchWriteOptions`, set-based mutation capabilities, and the execution
of retention, exact append, and durable idempotency.

Store sits directly above `Groundwork.Kernel` and knows nothing about contract families: a provider
implementing these interfaces never learns that `Groundwork.Records` or `Groundwork.Documents`
exists. Scoped sessions are tenant-isolated; cross-scope reads require the explicit, query-only
[`StorageAccess.PrivilegedAcrossScopes`](https://github.com/valence-works/groundwork-v2/blob/main/docs/v2/privileged-cross-scope.md)
contract, which is audited.

## Referencing it

A provider package brings this transitively. Reference it directly when you write a provider or a
contract family of your own.

```bash
dotnet add package Groundwork.Store --prerelease
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
