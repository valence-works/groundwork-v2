# Groundwork.Testing

For provider authors. The public conformance suites (`ConformanceSuite`, `ConformanceScenario`),
the deterministic `InMemoryProviderFactory`, the concurrency harness, and the schema tool CLI
harness.

If you implement `IStorageProviderConnection`, running these suites is how you find out whether you
implemented the contract or merely something that compiles. The same suites gate the four
first-party providers.

**This is not an application database.** `InMemoryProviderFactory` exists to make provider contracts
testable deterministically, not to back your service. `Groundwork.Testing` is a *consumer* of
`Groundwork.Store`, never a dependency of it — if production code references it, something is wrong,
and CI enforces the direction.

## Referencing it

```bash
dotnet add package Groundwork.Testing --prerelease
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
