# Groundwork.Substrate.Relational

The shared relational seam. Connection ownership, schema dispatch, application locks, fencing, and
the transaction and retry behavior every SQL provider needs — written once instead of four times.

Implement `RelationalDialect` to add a SQL provider: you supply the dialect's naming, type mapping,
and statement shapes, and inherit the execution semantics `Groundwork.Sqlite`,
`Groundwork.PostgreSql`, and `Groundwork.SqlServer` already prove out against the conformance
suites.

## Referencing it

Provider packages bring this transitively. Reference it directly when writing a SQL provider.

```bash
dotnet add package Groundwork.Substrate.Relational --prerelease
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
