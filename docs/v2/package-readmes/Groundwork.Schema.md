# Groundwork.Schema

The declaration attributes and the canonical schema model: `[GwTable]`, `[GwColumn]`, `[GwKey]`,
`[GwIndex]`, `[GwRetention]`, `[GwAppendIdempotency]`, `[GwRetentionIdempotency]`, `[GwAggregate]`.

Attributes on your types are the input; the canonical schema document is the output, and it is what
the `groundwork` CLI plans and applies from. Pair this with
[`Groundwork.Schema.Generator`](https://www.nuget.org/packages/Groundwork.Schema.Generator), which
emits the canonical schema and its fingerprint as an assembly attribute at compile time.

Targets `netstandard2.0` so the generator and the runtime read the same model.

## Referencing it

```bash
dotnet add package Groundwork.Schema --prerelease
dotnet add package Groundwork.Schema.Generator --prerelease
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
