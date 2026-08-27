# Groundwork.Schema.Generator

A Roslyn source generator. It reads the `Groundwork.Schema` attributes in your assembly and emits
the canonical schema document plus its fingerprint as an assembly attribute, so the schema your code
declares and the schema your deployment applies cannot drift apart.

Downstream, `Groundwork.Analyzers` reads that attribute to check query coverage, and the
`groundwork` CLI reads it to plan and apply.

Targets `netstandard2.0`, ships under `analyzers/dotnet/cs`, and has no runtime footprint.

## Referencing it

Reference it as a build-time asset:

```xml
<PackageReference Include="Groundwork.Schema.Generator" Version="..." PrivateAssets="all" />
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
