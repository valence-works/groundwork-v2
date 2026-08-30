# Groundwork.MongoDb

The MongoDB provider: the MongoDB implementation of the provider-neutral `Groundwork.Store`
contracts, built on `Groundwork.Substrate.Mongo` and the official `MongoDB.Driver`.

## Deployment topology matters

Exact append and durable idempotency need transactions and sessions, which MongoDB provides only on
a **replica-set or sharded** deployment. On a standalone deployment the provider does not advertise
`groundwork.operational.atomic-commit`, and the capability registry will refuse a declaration that
requires it rather than silently degrading. Standalone MongoDB is deliberately not represented as
production-supported.

Transactional same-identity races return portable deterministic write outcomes, and wrapper-owned
transactions retry transient write-conflict bodies.

**Support tier:** Production-supported on a transaction-capable replica set or sharded cluster.
Standalone MongoDB is compatibility-only and is not a production fallback. See the
[support matrix](https://github.com/valence-works/groundwork-v2/blob/main/docs/v2/support-matrix.md)
and [operations runbook](https://github.com/valence-works/groundwork-v2/blob/main/docs/v2/production-operations.md).

## Referencing it

```bash
dotnet add package Groundwork.MongoDb --prerelease
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
