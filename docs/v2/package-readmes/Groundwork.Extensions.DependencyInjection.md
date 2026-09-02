# Groundwork.Extensions.DependencyInjection

[Groundwork v2 documentation portal](https://github.com/valence-works/groundwork-v2/wiki) contains the full consumer documentation.

Dependency injection and hosting integration: `services.AddGroundwork()`, named connections, the
process-singleton connection lifetime, startup schema admission, and a health check.

## Startup admission is the point

A service that starts against a catalog it does not match will fail at the first write, in
production, on whichever request happens to arrive first. This package moves that decision to
startup and makes the outcome explicit:

| Status | Meaning |
| --- | --- |
| `Ready` | The deployed catalog matches the compiled target. |
| `Degraded` | Physical index drift is present against an otherwise matching target. Dependent query shapes refuse; the application serves. |
| `Blocked` | Unit-, column-, or declaration-level work is pending. The application must not serve. |
| `Failed` | Admission itself could not run — the connection or the catalog read failed. |

The split is the runtime admission contract, not a new one: a missing unit, column, or derived
column means data cannot be read or written correctly (`GW-RUNTIME-001`). Physical index drift
against an otherwise matching applied target only makes dependent query shapes refuse
(`GW-RUNTIME-002`); changing the declaration, including adding or changing an index, changes the
target fingerprint and blocks startup until it is applied.

Admission inspects by default. `AutoApplyOnStartup` is an explicit development opt-in: it may apply
only plans the kernel's schema protection rules deem safe, while destructive or semantic work still
requires authorization. Enabling it in any non-Development environment refuses startup with
`GW-HOST-007` before provider admission can mutate schema. For production, review with
`groundwork plan` and apply deliberately at deployment time with `groundwork apply --safe` — see
[`Groundwork.Tool`](https://www.nuget.org/packages/Groundwork.Tool).

The health check reports the same status, so a `Degraded` catalog is visible to your orchestrator
rather than showing up as scattered query refusals.

## Referencing it

Add it alongside a provider and whichever contract family you use:

```bash
dotnet add package Groundwork.Sqlite --prerelease
dotnet add package Groundwork.Records.Store --prerelease
dotnet add package Groundwork.Extensions.DependencyInjection --prerelease
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
