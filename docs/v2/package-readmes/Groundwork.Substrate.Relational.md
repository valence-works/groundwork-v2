# Groundwork.Substrate.Relational

The shared relational schema, runtime-admission, rendering, materialization, and ADO.NET execution
seam. Schema connection ownership, dispatch, application locks, fencing, and operation transactions
are written once instead of once per SQL provider.

Implement `RelationalDialect` to reuse those facilities, derive from
`RelationalStorageSessionBase` with one `RelationalStorageSessionAdapter`, and construct units of
work with `RelationalUnitOfWork` and `RelationalUnitOfWorkLifetime`. A complete provider also owns
its `Groundwork.Store` factory/connection, native commands supplied through
`RelationalAppendAdapter` and `RelationalRetentionAdapter`, optional capability interfaces, and
driver resource/error mechanics. The shared base supplies validation, replay, transaction, and
`OnAppend` state machines. The individual shared state-machine classes remain internal.

## Referencing it

Provider packages bring this transitively. Reference it directly when writing a SQL provider.

```bash
dotnet add package Groundwork.Substrate.Relational --prerelease
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
