# Groundwork.SqlServer

The SQL Server provider: the SQL Server implementation of the provider-neutral `Groundwork.Store`
contracts and relational schema behaviors, built on `Groundwork.Substrate.Relational` and
`Microsoft.Data.SqlClient`.

Requires a SQL Server 2022-compatible deployment. Lifecycle identity columns use a binary collation
so scopes and nonces stay case-distinct, and an existing lifecycle table created with a legacy
collation is refused with migration guidance rather than silently producing different comparison
semantics from the other providers.

Full provider notes:
[docs/sqlserver-provider.md](https://github.com/valence-works/groundwork-v2/blob/main/docs/sqlserver-provider.md).

## Referencing it

```bash
dotnet add package Groundwork.SqlServer --prerelease
dotnet add package Groundwork.Records.Store --prerelease
```

## Every Groundwork package

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
