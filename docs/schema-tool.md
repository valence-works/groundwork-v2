# Schema tooling

`Groundwork.Tool` is the public package and command-line entry point for explicit deployment-time physical-schema work. Its assembly and namespace remain `Groundwork.SchemaTool` for source compatibility. Runtime admission remains inspect-only unless a host opts into safe startup application.

## CLI

```text
groundwork plan     --schema groundwork.schema.json --provider <alias>
groundwork validate --schema groundwork.schema.json --provider <alias> [--offline]
groundwork status   --schema groundwork.schema.json --provider <alias>
groundwork apply    --schema groundwork.schema.json --provider <alias> --safe
groundwork schema emit --input schema.json --file groundwork.schema.json
```

Use `--output json` for the stable machine-readable report. Exit codes are `0` success, `2` pending changes, `3` validation blocked, `4` authorization required, `5` invalid invocation, `10` execution failure, and `130` cancellation.

`apply` mutates nothing unless the invocation selects `--safe` or supplies an exact `--expected-plan` fingerprint. Safe mode refuses protected work. A destructive plan requires its current fingerprint plus every exact operation identity through `--allow-destructive`; semantic migrations require their exact IDs through `--allow-semantic`.

Provider packages implement `ISchemaToolProviderSessionFactory`. The tool discovers loaded factories and can load a provider plug-in explicitly with `--provider-assembly`; `--connection` and `--database` are passed to the factory without being echoed in reports. Hosts can instead inject an `ISchemaToolProviderSession` resolver directly.

`schema emit` parses and rewrites the source-generator contract in canonical order. Its reported fingerprint is therefore identical to the assembly metadata fingerprint for the same schema.

## MSBuild verification

Reference `Groundwork.SchemaTool.MSBuild` and configure the generated schema artifact:

```xml
<PropertyGroup>
  <GroundworkSchemaFile>$(MSBuildProjectDirectory)/groundwork.schema.json</GroundworkSchemaFile>
  <GroundworkCoverageFile>$(MSBuildProjectDirectory)/groundwork.coverage.json</GroundworkCoverageFile>
</PropertyGroup>
```

The task runs before compilation. Portability refusals and uncovered queries are MSBuild errors with their Groundwork diagnostic codes.

Coverage inventory uses a `queries` array. Each query names a `table` and may declare `equal`, `range`, `order`, `skip`, `take`, and `totalCount`. Order entries use `column`, `direction` (`asc`/`desc`), and `nulls` (`first`/`last`). The inventory is checked against the indexes in the canonical schema rather than a provider-specific approximation.

`samples/Groundwork.Samples.CoverageNegative` is intentionally unbuildable and proves the uncovered-query build gate.
