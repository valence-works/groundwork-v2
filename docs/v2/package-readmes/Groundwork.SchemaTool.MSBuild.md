# Groundwork.SchemaTool.MSBuild

Build-time verification. The `GroundworkVerify` task fails the build on a portability refusal or an
uncovered query, so a schema or query problem surfaces at compile time rather than at the first
production write.

It is the same `QueryCoverageChecker` and `PortabilityValidator` the analyzer and the runtime use —
one implementation, three places it can stop you.

## Target framework

This package targets **`net10.0`**, and it is the one Groundwork package whose framework is not
about your application. An MSBuild task loads into the SDK's own MSBuild process, so its framework
tracks the SDK you build with, not the framework you build *for*. Your application can target
`net8.0`; building it with this verification requires a .NET 10 SDK.

## Referencing it

```xml
<PackageReference Include="Groundwork.SchemaTool.MSBuild" Version="..." PrivateAssets="all" />
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
