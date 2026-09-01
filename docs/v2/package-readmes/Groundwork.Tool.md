# Groundwork.Tool

The `groundwork` CLI: `plan`, `validate`, `status`, `apply`, `adopt`, and `schema emit`.
Deployment-time schema planning and application, separate from your application's startup path on
purpose.

`plan` reports exactly what would change and a deployment-bound plan fingerprint. Exact-plan
`apply`/`adopt` authorization requires the same non-secret `--deployment-id`, and refuses again if
the plan is no longer current — so a plan reviewed for staging cannot authorize production and a
schema change cannot be applied on the strength of stale evidence. Destructive and semantic
operations each need their own explicit authorization.

Prefer `--connection-env`, `--connection-file`, or `--connection-stdin` over placing a connection
string in process arguments. Connection input modes are mutually exclusive, and the resolved secret
is redacted from human and JSON errors. Database RBAC and secret storage remain the deployment
host's responsibility; see the repository's security-boundary guide.

`adopt` covers the database that already holds exactly what the declaration describes while
Groundwork has no history of applying it. It executes no DDL: it proves the deployed catalog matches
the compiled target exactly and publishes the applied state that applying it would have published.
Any difference is a refusal that names the column or index that differs — adoption verifies, it
never infers.

`apply` writes the applied-state history that runtime startup admission compares against, which is
why the tool multi-targets `net8.0` and `net10.0`: a deployment host running .NET 8 can apply schema
with the runtime it already has.

**Support tier:** Production-supported for deployment work against a production-supported provider
topology. Multi-target application is not a distributed transaction; preserve the reviewed report
and reconcile every target after a non-success exit. See the
[support matrix](https://github.com/valence-works/groundwork-v2/blob/main/docs/v2/support-matrix.md)
and [operations runbook](https://github.com/valence-works/groundwork-v2/blob/main/docs/v2/production-operations.md).

## Installing it

```bash
dotnet tool install --global Groundwork.Tool --prerelease
groundwork --version
```

The assembly and namespace remain `Groundwork.SchemaTool`; the package and command are
`Groundwork.Tool` and `groundwork`.

The tool embeds the first-party schema provider plug-ins, so an isolated installation includes the
`sqlite`, `postgresql`, `sqlserver`, `mongodb`, and `mysql` aliases. No application build or custom
host is required for deployment-time schema work. For example, a CI or deployment image can apply
an SQLite declaration directly:

```bash
groundwork apply --schema groundwork.schema.json \
  --provider sqlite --database ./app.db --safe
```

Third-party providers can still be supplied with one or more `--provider-assembly <file>` options;
the built-in aliases do not require that option.

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
