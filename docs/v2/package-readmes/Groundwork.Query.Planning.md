# Groundwork.Query.Planning

[Groundwork v2 documentation portal](https://github.com/valence-works/groundwork-v2/wiki) contains the full consumer documentation.

Query coverage checking. `QueryCoverageChecker` decides whether a query shape is served by a
declared index; `RuntimeCoverageGate` refuses an uncovered query at runtime with
`QueryCoverageException` rather than letting it become a table scan in production.

Declared-reference joins use `QueryCoverageCandidates` to keep driving and target index evidence
separate. The driving side must be covered normally; the target join columns must be the leading
target-index prefix in declared key order, followed by any covered target predicate/order suffix.
Joined order terms must form a contiguous driving segment followed by an optional target segment.
The single-table checker overload fails closed when a joined request omits that target context.

This is the single checker behind both the editor experience and the build failure:
`Groundwork.Analyzers` and `Groundwork.SchemaTool.MSBuild` call into it, so a query that is green in
the IDE is green at build and at runtime for the same reason.

Targets `netstandard2.0` so the analyzer and the runtime share one implementation.

## Referencing it

Usually transitive. Reference it directly to gate coverage yourself at runtime.

```bash
dotnet add package Groundwork.Query.Planning --prerelease
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
