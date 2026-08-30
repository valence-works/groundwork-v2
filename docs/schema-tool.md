# Schema tooling

`Groundwork.Tool` is the public package and command-line entry point for explicit deployment-time physical-schema work. Its assembly and namespace remain `Groundwork.SchemaTool` for source compatibility. Runtime admission remains inspect-only unless a host opts into safe startup application.

## CLI

```text
groundwork plan     --schema groundwork.schema.json --provider <alias> [--deployment-id <id>] [--phase expand|contract]
groundwork validate --schema groundwork.schema.json --provider <alias> [--offline]
groundwork status   --schema groundwork.schema.json --provider <alias> [--deployment-id <id>] [--phase expand|contract]
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

Exact-plan authorization also requires a non-secret `--deployment-id`, which is part of the plan
fingerprint and prevents a plan reviewed for one deployment from authorizing another. Prefer
`--connection-env`, `--connection-file`, or `--connection-stdin` over putting a credential in
process arguments. See [security boundaries](v2/security-boundaries.md) for the trust model,
credential handling, and partial multi-target outcome guidance.

### Opt-in interop reporting views

A table may opt into one provider-native reporting view by naming it in the canonical
schema. The design and provider boundaries are recorded in [ADR 0004](adr/0004-relational-interop-views.md):

```json
{
  "tables": [{
    "name": "orders",
    "interopView": "reporting_orders",
    "columns": [
      { "name": "id", "type": "String", "nullable": false, "length": 32 },
      { "name": "total", "type": "Decimal", "nullable": false, "precision": 18, "scale": 2 }
    ],
    "key": ["id"],
    "indexes": []
  }]
}
```

The fluent equivalent is `.InteropView("reporting_orders")`; source-generated `[GwTable]`
declarations use `InteropView = "reporting_orders"`. The view is a separate provider-owned
schema operation, appears in the plan and JSON report, and is included in the applied target
fingerprint. Creating, replacing, or removing it requires deployment-tool authorization for the
exact current plan. Runtime `connection.Schema.Apply` refuses this protected work; use `groundwork apply`
with the plan fingerprint and operation identity shown by `plan --output json`.

The view exposes declared application columns and omits Groundwork's internal columns. For a
scoped table it also exposes `__groundwork_scope`, so the view contains every scope's rows. It is
not a security boundary: grant access to the view only to database principals that may read all
scopes. View names must be valid provider identifiers and must not collide with a source table,
another storage object, or another declared view; such declarations fail before schema I/O with
`GW-PORT-015`.

The shipped relational providers use these projections:

| Provider | Reporting projection | Precision/transaction note |
| --- | --- | --- |
| SQLite | Decimal `TEXT` is cast to `NUMERIC`; other values retain their declared projection | SQLite's dynamic numeric typing applies; view casts do not make decimal precision constraints stricter |
| PostgreSQL | UTC tick `bigint` timestamps are computed as `timestamptz`; decimal `numeric` remains native | `timestamptz` has microsecond precision, so the view is not a full sub-microsecond round trip; DDL is transactional |
| SQL Server | Native `datetimeoffset(7)` and `decimal` values are selected directly | DDL runs in the shared schema transaction |
| MySQL/MariaDB | UTC tick `bigint` timestamps are computed as `DATETIME(6)`; decimal `decimal(p,s)` remains native | Timestamp output is microsecond precision, and server DDL may implicitly commit; do not assume rollback of a failed multi-operation batch |

MongoDB and `Groundwork.Testing`'s in-memory provider reject a declared interop view rather than
claiming a view was deployed. `plan`, `validate`, `status`, and `adopt` must therefore be used
against a relational provider for this feature. Base-table drift comparison considers only the
declared table; the interop view is inspected as its own provider-owned definition.
Inspection checks both its declared output columns and the canonical-definition marker embedded in
the live view text, so a same-shaped replacement is still reported as drift.

Provider packages implement `ISchemaToolProviderSessionFactory`. The tool discovers loaded factories and can load a provider plug-in explicitly with `--provider-assembly`; the resolved connection and `--database` are passed to the factory without being echoed in reports. Hosts can instead inject an `ISchemaToolProviderSession` resolver directly.
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
