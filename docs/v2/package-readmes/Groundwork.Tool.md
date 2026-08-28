# Groundwork.Tool

The `groundwork` CLI: `plan`, `validate`, `status`, `apply`, `adopt`, and `schema emit`.
Deployment-time schema planning and application, separate from your application's startup path on
purpose.

`plan` reports exactly what would change and a plan fingerprint. `apply` refuses unless you
authorize that exact fingerprint, and refuses again if the plan is no longer current — so a schema
change cannot be applied on the strength of a plan somebody read yesterday. Destructive and
semantic operations each need their own explicit authorization.

`adopt` covers the database that already holds exactly what the declaration describes while
Groundwork has no history of applying it. It executes no DDL: it proves the deployed catalog matches
the compiled target exactly and publishes the applied state that applying it would have published.
Any difference is a refusal that names the column or index that differs — adoption verifies, it
never infers.

`apply` writes the applied-state history that runtime startup admission compares against, which is
why the tool multi-targets `net8.0` and `net10.0`: a deployment host running .NET 8 can apply schema
with the runtime it already has.

## Installing it

```bash
dotnet tool install --global Groundwork.Tool --prerelease
groundwork --version
```

The assembly and namespace remain `Groundwork.SchemaTool`; the package and command are
`Groundwork.Tool` and `groundwork`.

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
