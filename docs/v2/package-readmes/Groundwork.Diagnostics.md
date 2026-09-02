# Groundwork.Diagnostics

[Groundwork v2 documentation portal](https://github.com/valence-works/groundwork-v2/wiki) contains the full consumer documentation.

Opt-in native explain-plan assertions. With `GW_EXPLAIN_ASSERT` set, a provider captures the
database's own query plan and asserts that the query used the index it was declared to use — so
"this query is covered" is checked against the optimizer rather than against a model of it.

Deliberately kept out of the `Groundwork.Store` contract. Explain-plan capture is a test and
diagnostic concern, and putting it in the runtime contract would oblige every provider to implement
it and every consumer to carry it.

## Referencing it

Provider packages bring this transitively. Reference it directly in a test project that asserts
native plans.

```bash
dotnet add package Groundwork.Diagnostics --prerelease
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
