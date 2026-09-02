# Groundwork.Analyzers

[Groundwork v2 documentation portal](https://github.com/valence-works/groundwork-v2/wiki) contains the full consumer documentation.

The Roslyn analyzer: uncovered queries and portability problems reported **in the editor and at
build**, not at the first production write.

It is a front end for the one provider-neutral `QueryCoverageChecker` in
`Groundwork.Query.Planning`, reading your assembly's generated `GroundworkSchema` attribute,
referenced assembly attributes, or a `.json` AdditionalFile selected by `gw_schema_file`. So the
diagnostic you see in the IDE, the build failure from `Groundwork.SchemaTool.MSBuild`, and the
runtime coverage gate all agree by construction.

Targets `netstandard2.0` and ships its dependencies under `analyzers/dotnet/cs`, so it has no
runtime footprint.

For typed navigation, the analyzer follows the source-visible `.Join(reference)` back to the exact
`.Reference(row => row.Navigation, ...)` declaration. An undeclared, different, deeper, or opaque
navigation remains `GW-LINQ-104`; target type or member depth alone never implies a join.

Full analyzer notes, including which query shapes are enumerated:
[docs/query-coverage-analyzer.md](https://github.com/valence-works/groundwork-v2/blob/main/docs/query-coverage-analyzer.md).

## Referencing it

```xml
<PackageReference Include="Groundwork.Analyzers" Version="..." PrivateAssets="all" />
<PackageReference Include="Groundwork.Schema.Generator" Version="..." PrivateAssets="all" />
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
