# Groundwork.Query.Model

The innermost assembly: the portable predicate and query AST. `QueryRequest`, `Predicate`,
`ColumnRef`, `Paging`, `Projection`, and `PortableQuerySemantics`.

It is BCL-only and provider-neutral, and CI enforces that by inspecting its compiled references.
Everything a query means in Groundwork — comparison semantics, ordering, null handling, paging — is
defined here once, so four providers cannot quietly disagree about it.

Targets `netstandard2.0` so analyzers, generators, and build hosts can consume the same AST the
runtime uses.

## Referencing it

It arrives transitively with any provider or query package. Reference it directly only when you are
building tooling over the query model itself.

```bash
dotnet add package Groundwork.Query.Model --prerelease
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
