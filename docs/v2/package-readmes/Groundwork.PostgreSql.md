# Groundwork.PostgreSql

The PostgreSQL provider: the PostgreSQL implementation of the provider-neutral `Groundwork.Store`
contracts, built on `Groundwork.Substrate.Relational` and `Npgsql`.

Requires a PostgreSQL 17-compatible deployment. Conditional upserts execute as one native statement
with an inferred conflict target, partial unique violations are reported by index name without a
probe read, and scoped writes do not duplicate scope parameters.

Portable ordinal string semantics, retention, exact append, provider sequences, and durable
idempotency are all proved against the same provider-neutral conformance suites the other three
providers run, and against a four-provider query differential suite that compares native results to
a portable oracle.

## Referencing it

```bash
dotnet add package Groundwork.PostgreSql --prerelease
dotnet add package Groundwork.Records.Store --prerelease
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
