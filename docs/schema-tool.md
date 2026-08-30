# Schema tooling

`Groundwork.Tool` is the public package and command-line entry point for explicit deployment-time physical-schema work. Its assembly and namespace remain `Groundwork.SchemaTool` for source compatibility. Runtime admission remains inspect-only unless a host opts into safe startup application.

## CLI

```text
groundwork plan     --schema groundwork.schema.json --provider <alias> [--phase expand|contract]
groundwork validate --schema groundwork.schema.json --provider <alias> [--offline]
groundwork status   --schema groundwork.schema.json --provider <alias> [--phase expand|contract]
groundwork apply    --schema groundwork.schema.json --provider <alias> --safe [--phase expand|contract]
groundwork schema emit --input schema.json --file groundwork.schema.json
```

Use `--output json` for the stable machine-readable report. Exit codes are `0` success, `2` pending changes, `3` validation blocked, `4` authorization required, `5` invalid invocation, `10` execution failure, and `130` cancellation.

`--phase` selects which half of an expand–contract evolution is planned. It defaults to `expand`, the additive half, and changes nothing for a declaration that supersedes no column. `--phase contract` removes superseded columns and refuses with a `GW-EXPAND-*` code until its readiness is established from the applied schema ledger and the data-migration ledger; the report carries a `supersessions` array with `retainedSince`, `backfillCompletedAt`, and `contractableAt` per superseded column. The two phases of one declaration have distinct plan fingerprints, so an `--expected-plan` value that authorizes the expand can never authorize the contract. See `docs/v2/expand-contract.md`.

Each canonical table may carry an optional `evolution` object for destructive intent, retirement,
semantic migration identity, supersessions, and the dual-presence window. The deployment host
supplies named transforms through `ISchemaToolProviderSession.DataMigrationCatalog`; apply refuses
a missing transform by name with `GW-MIGRATION-008`. It also validates each transform's declared
columns and the provider's migration capabilities across every target before the first one mutates.
When the same document is a source-generator `AdditionalFile`, the generated assembly's canonical
schema attribute preserves evolution. Generated `StorageUnit.Definition` remains the logical table
shape because evolution is deployment-target metadata rather than part of a storage unit.

`apply` mutates nothing unless the invocation selects `--safe` or supplies an exact `--expected-plan` fingerprint. Safe mode refuses protected work. A destructive plan requires its current fingerprint plus every exact operation identity through `--allow-destructive`; semantic migrations require their exact IDs through `--allow-semantic`.

Provider packages implement `ISchemaToolProviderSessionFactory`. The tool discovers loaded factories and can load a provider plug-in explicitly with `--provider-assembly`; `--connection` and `--database` are passed to the factory without being echoed in reports. Hosts can instead inject an `ISchemaToolProviderSession` resolver directly.
The shipped aliases are `sqlite`, `mysql`, `postgresql`, `sqlserver`, and `mongodb`; each uses the
same canonical document, authorization vocabulary, plan fingerprint, and report format.

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
