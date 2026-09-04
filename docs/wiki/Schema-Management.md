# Schema Management

Groundwork treats physical schema as **deployment-time work with explicit authorization**, not as a
side effect of application startup.

## Runtime: apply, diff, inspect

```csharp
var result = connection.Schema.Apply(unit);
// result.Applied — whether work was performed
// result.Diff    — what changed
// result.IsNoOp  — reapplying an unchanged schema is a no-op

var diff = connection.Schema.Diff(unit);
if (!diff.IsEmpty)
    foreach (var change in diff.Changes)
        Console.WriteLine($"{change.Kind}: {change.Identity}");

var indexes = connection.Catalog.ReadIndexes(unit.Id);
```

`Apply` is genuinely useful for tests and local development. **In production, prefer the CLI.**

## Runtime admission

At startup, providers compare the deployed catalog with the compiled physical target:

| Drift | Code | Effect |
| --- | --- | --- |
| Missing/changed **columns** — including collation and persisted search-key algorithm | `GW-RUNTIME-001` | **Startup-fatal**, names the column |
| Missing/changed declared **indexes** | `GW-RUNTIME-002` | Reported separately; only makes **dependent query shapes** refuse |

The split is deliberate: a missing column means data cannot be read correctly (fail hard), while a
missing index means *some queries* are no longer safe (fail those, keep the app up).

Admission runs **once per storage unit per provider connection**: the first session (or unit of
work) that touches a unit verifies the deployed catalog read-only and caches the verdict for the
connection's lifetime; a schema apply re-arms verification for that unit. Detection is therefore
per connection lifetime — out-of-band tampering while a connection stays open is out of scope and
surfaces on the next new connection.

A successful apply through a provider connection also changes that connection's in-process
declaration publication. Direct, owned, and unit-of-work sessions opened against the previous
declaration refuse their next operation before provider I/O with `GW-RUNTIME-005`; reopen them after
apply. Reapplying the same fingerprint is a no-op and does not stale sessions. Failed or refused
application does not publish. Changes made through another process or connection remain governed by
the admission boundary above rather than by polling retained sessions.

MongoDB does the same inspect-only split at `OpenSession` via its public `InspectSchema` report.

## The `groundwork` CLI

```bash
dotnet tool install --global Groundwork.Tool --version 0.4.0-preview.11 \
  --add-source https://f.feedz.io/valence-works/groundwork/nuget/index.json
```

```text
groundwork plan     --schema groundwork.schema.json --provider <alias>
groundwork validate --schema groundwork.schema.json --provider <alias> [--offline]
groundwork status   --schema groundwork.schema.json --provider <alias>
groundwork apply    --schema groundwork.schema.json --provider <alias> --safe
groundwork adopt    --schema groundwork.schema.json --provider <alias> --safe
groundwork schema emit --input schema.json --file groundwork.schema.json
```

Add `--output json` for the stable machine-readable report.

`Groundwork.Tool` embeds the first-party provider plug-ins, so an isolated installation includes the
`sqlite`, `mysql`, `postgresql`, `sqlserver` and `mongodb` aliases. All five speak one plan and
report format: operation kinds, operation identities, authorization addresses, refusal codes and
exit codes mean the same thing whether the target is a table or a collection. Third-party providers
remain supported through `--provider-assembly <file>`. The MongoDB plug-in requires a replica set or
a sharded cluster, because publishing the applied schema ledger needs a transaction; a standalone
deployment is refused when the session opens rather than part-way through an apply.

An isolated CI image can deploy without building the application that owns the schema:

```bash
dotnet tool install --global Groundwork.Tool --prerelease \
  --add-source https://f.feedz.io/valence-works/groundwork/nuget/index.json
groundwork apply --schema groundwork.schema.json \
  --provider sqlite --database ./app.db --safe
```

> The package is `Groundwork.Tool`; its assembly and namespace remain `Groundwork.SchemaTool` for
> source compatibility.

### Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Success |
| `2` | Pending changes |
| `3` | Validation blocked |
| `4` | Authorization required |
| `5` | Invalid invocation |
| `10` | Execution failure |
| `130` | Cancellation |

Exit code `2` is the useful one in CI: *"there is work to do"* is distinct from *"something is wrong"*.

```bash
groundwork status --schema groundwork.schema.json --provider postgresql --output json
case $? in
  0) echo "up to date" ;;
  2) echo "pending changes — run apply in the deploy step" ;;
  *) exit 1 ;;
esac
```

### Authorization

**`apply` mutates nothing** unless the invocation either selects `--safe` or supplies an exact
`--expected-plan` fingerprint.

- `--safe` refuses protected work.
- A **destructive** plan requires its current fingerprint **plus every exact operation identity**
  through `--allow-destructive`.
- **Semantic migrations** require their exact ids through `--allow-semantic`.

```bash
# Safe additive changes.
groundwork apply --schema groundwork.schema.json --provider postgresql --safe

# Destructive: pin the plan and name every operation you are authorizing.
groundwork apply --schema groundwork.schema.json --provider postgresql \
  --expected-plan sha256:… \
  --allow-destructive drop-column:orders.legacy_total \
  --allow-destructive drop-index:orders.ix_legacy

# Semantic: renames and widenings carry their data, and are named the same way.
groundwork apply --schema groundwork.schema.json --provider postgresql \
  --expected-plan sha256:… \
  --allow-semantic rename-column:orders.buyer
```

You cannot authorize destructive work generically. You authorize *these operations, in this plan*.
Each operation answers to a readable address — `<kind>:<storage-unit>.<subject>` — and to its exact
operation identity; `plan --output json` reports the spelling that authorizes it under
`authorization.destructiveOperationsRequired`. Where a plan would let one address name two
operations, that address is withdrawn and only the exact identity authorizes them.

### Opt-in interop reporting views

Groundwork can create one provider-native reporting view for a declared unit. Opt in
explicitly in the fluent declaration. See [ADR 0004](../adr/0004-relational-interop-views) for the
decision and provider boundaries:

```csharp
var orders = Groundwork.Kernel.StorageUnit
    .Declare("orders", "orders")
    .String("id", 32, column => column.Required())
    .Decimal("total", 18, 2, column => column.Required())
    .Key("id")
    .InteropView("reporting_orders")
    .Build();
```

The canonical schema spelling is `"interopView": "reporting_orders"`; source-generated
`[GwTable]` declarations use the `InteropView` property. The view is a provider-owned schema
operation, included in the plan fingerprint and applied ledger. Create, replacement, and removal
are protected operations: `connection.Schema.Apply` refuses them, while the deployment tool admits
them only with the exact current plan and operation authorization.

The view exposes application columns, omitting Groundwork's internal columns. A scoped unit's view
also exposes `__groundwork_scope` and therefore contains all scopes. It is not an authorization
boundary; grant the view only to database principals allowed to read across scopes. A view name
must be a valid physical identifier and cannot collide with a source table, another storage object,
or another declared view. The portability refusal is `GW-PORT-015`.

Relational provider projections are:

| Provider | View representation |
| --- | --- |
| SQLite | Decimal text is projected with `CAST(... AS NUMERIC)`; SQLite's dynamic numeric typing still applies |
| PostgreSQL | UTC tick `bigint` timestamps are projected as `timestamptz` at microsecond precision |
| SQL Server | `datetimeoffset(7)` and decimal columns are selected in their native types |
| MySQL/MariaDB | UTC tick `bigint` timestamps are projected as `DATETIME(6)` at microsecond precision |

The portable table remains authoritative for exact runtime values. PostgreSQL and MySQL/MariaDB
report timestamps at microsecond precision; SQL Server's `datetimeoffset(7)` retains 100-nanosecond
ticks. Relational view DDL uses the schema operation transaction, but MySQL/MariaDB may implicitly commit DDL, so a failed
multi-operation apply may require a subsequent plan to reconcile physical work. MongoDB and the
in-memory testing provider reject interop views before provider mutation because neither has one
stable relational catalog object for this contract.

---

## Evolving a deployed schema

Planning is **not additive-only**. Every applied definition the declaration changed, renamed, or
removed is planned as an explicit operation that carries its own authorization:

| Change | Operation | Authorization |
| --- | --- | --- |
| New column, index, or unit | `AddColumn`, `CreatePhysicalIndex`, `CreatePrimaryStorage` | `--safe` |
| Physical name changed, logical `Id` kept | `RenamePrimaryStorage`, `RenameColumn` | `--allow-semantic` |
| String length up, decimal precision up at the same scale, required → optional | `AlterColumn` (widening) | `--allow-semantic` |
| Length down, precision down, scale changed, type changed, optional → required | `AlterColumn` (narrowing) | `--allow-destructive` |
| Index definition changed in any way | `RebuildPhysicalIndex` | `--allow-destructive` |
| Column, index, or retired unit removed | `DropColumn`, `DropIndex`, `DropPrimaryStorage` | `--allow-destructive` |
| An index moved out of the way of a column alteration | `DropIndex` + `CreatePhysicalIndex` | carried by the alteration |

Only evolutions with **no portable meaning** are refused outright: changing logical key identity or
order — including replacing or dropping a key column — (`GW-SCHEMA-015`), changing a key column's
portable type or renaming onto a name another applied column still holds (`GW-SCHEMA-003`), and
adding a required column with nothing to put in it for existing rows (`GW-SCHEMA-005`). See
**[Diagnostics Reference](Diagnostics-Reference)**.

### Renames need the logical id

A rename is only a rename because the declaration says so. Change the physical name and keep the
original as the logical `Id`:

```csharp
new ColumnDefinition { Name = "buyer", Id = "customer", Type = PortableType.String, MaxLength = 64 }
```

```csharp
[GwColumn(Name = "buyer", Id = "customer", Length = 64, Required = true)]
public string Buyer { get; init; } = "";
```

`[GwTable("purchase_orders", Id = "orders")]` does the same for the storage unit. Planning keys its
slots on the logical id, so the change plans as `RenameColumn` / `RenamePrimaryStorage` and the rows
come with it. **Drop the id and the same edit plans as a drop plus an add** — which is the honest
reading of a declaration that no longer claims any connection to the old column. Keep the id in the
declaration from then on; it is what the applied ledger matches against on every later deploy.

An index moves with the storage it belongs to: every relational provider derives its physical index
name from the storage name, so a `RenamePrimaryStorage` carries the applied indexes across.

### Retiring a unit

Removing a declaration does not remove its storage — the tool would have nothing left to plan
against. Mark the declaration retired instead, and the plan becomes one authorized removal:

```csharp
var subject = new SchemaSubject(unit, new SchemaEvolutionMetadata(retiresPrimaryStorage: true));
```

The canonical document expresses the same declaration without an in-process compiler:

```json
"evolution": {
  "isDestructive": true,
  "semanticMigrationId": null,
  "retiresPrimaryStorage": true,
  "supersessions": [],
  "dualPresenceWindowTicks": 0
}
```

```bash
groundwork apply --schema groundwork.schema.json --provider postgresql \
  --expected-plan sha256:… --allow-destructive drop-primary-storage:orders
```

The applied ledger then shrinks to nothing for that unit, which is the durable evidence that the
storage is gone. Delete the declaration afterwards.

### Expand and contract: removing a column without a downtime window

A rename or a widening planned as one operation changes the column an application version is already
using. When both versions run at once, plan it as **expand–contract** instead: declare the departing
column as *superseded*, and the same declaration yields an additive expand plan and a later
destructive contract plan.

```csharp
new SchemaEvolutionMetadata(
    semanticMigrationId: "2026-08-widen-total",
    supersessions:
    [
        new ColumnSupersession(
            new ColumnDefinition { Name = "total", Type = PortableType.Decimal, Precision = 10, Scale = 2 },
            replacementColumn: "total_amount")
    ],
    dualPresenceWindow: TimeSpan.FromHours(24))
```

In `groundwork.schema.json`, put the full superseded column beside its replacement:

```json
"evolution": {
  "isDestructive": false,
  "semanticMigrationId": "2026-08-widen-total",
  "retiresPrimaryStorage": false,
  "supersessions": [{
    "supersededColumn": {
      "name": "total", "type": "Decimal", "nullable": true,
      "length": null, "precision": 10, "scale": 2,
      "folding": "None", "generation": "Supplied", "default": null
    },
    "replacementColumn": "total_amount"
  }],
  "dualPresenceWindowTicks": 864000000000
}
```

Default-valued members may be omitted when authoring JSON. Canonical emission writes the complete
object whenever evolution is active and omits the object entirely when evolution is default.

```bash
groundwork apply --schema groundwork.schema.json --provider postgresql --phase expand   …
#  … the previous version drains; the backfill runs to completion; the window elapses …
groundwork apply --schema groundwork.schema.json --provider postgresql --phase contract …
```

The superseded column is deliberately no longer declared, so nothing in the new declaration can read
it, write it, alter it, or rename it — the expand plan is invisible to the application version that
still owns it. The contract plan refuses with a `GW-EXPAND-*` code until the applied ledger records
the column as retained, the data migration is recorded complete, and the declared window has elapsed
since the later of those two. See **[expand–contract workflows](../v2/expand-contract.md)** for the
full dual-presence semantics and both worked examples.

### Provider coverage

| Provider | Drop / rename / alter |
| --- | --- |
| SQLite | Native `DROP COLUMN` and `RENAME COLUMN`; an alteration rebuilds the table in the schema transaction, the same mechanism the dialect already uses to finalize a backfilled column |
| MySQL/MariaDB | Native `DROP COLUMN`, `RENAME COLUMN`, and `MODIFY COLUMN` under the connection-bound schema lease; index rebuilds use the same provider-neutral plan ordering as the other relational providers |
| PostgreSQL | Native, including in-place index rename |
| SQL Server | Native, through `sp_rename`; a column's auto-named default constraint is dropped with it |
| MongoDB | Native, through `MongoSchemaExecutor`: a rename `$rename`s the stored field, a drop `$unset`s it, and an alteration re-encodes stored values when the BSON representation changes. Work spans the primary collection and every per-scope collection. The in-process `connection.Schema.Apply` reads the same applied ledger and plans the same evolution |

> Provider-owned definitions move with their storage for the same reason indexes do: each names
> itself after the table. SQL Server's batch table type is dropped and recreated under the new name;
> the derived search-key algorithm record is re-keyed. Neither leaves a dead object behind, and the
> derived column itself is an ordinary column that travels with its table and is never rebuilt.
> Retiring a unit removes its provider definitions too.

> An index over a column being altered is dropped before the alteration and recreated after it, so
> the plan does not depend on the most permissive provider's willingness to alter an indexed column.
> The same plan puts the index back and the applied ledger still describes it, so that is a
> **rebuild, not a removal**: it needs no `--allow-destructive drop-index:…` of its own and is
> authorized by the alteration that required it.

### What `connection.Schema.Apply` will and will not do

`Apply` takes no authorization callback, so it cannot ask anyone anything. It performs everything a
re-apply of the same declaration could put back, and refuses the rest by name with
`GW-SCHEMA-010`:

| Work | `Schema.Apply` |
| --- | --- |
| Create, add, rename, widen, rebuild an index, recompute a derived backfill, drop an index | **Performs it.** Nothing is lost that re-applying could not restore. |
| Drop a column, drop retired storage, narrow a column past the values in it | **Refuses.** Nothing re-runs the loss away. |

A refusal throws and names the operation, so a removed column is a message telling you to authorize
`drop-column:orders.legacy_total` through the CLI — not a silent no-op. `Schema.Diff` still reports
the pending removal, because reading is what `Diff` is for.

Startup admission remains the gate for a running application: it refuses unauthorized destructive
and semantic work with `GW-SCHEMA-007` / `GW-SCHEMA-008`.

### Providers and connections

Provider packages implement `ISchemaToolProviderSessionFactory`. The tool discovers loaded factories
and can load a plug-in explicitly with `--provider-assembly`.

`--connection` and `--database` are passed to the factory and are **not echoed in reports** — reports
are safe to attach to a CI job or a change ticket.

Hosts can instead inject an `ISchemaToolProviderSession` resolver directly.

### `schema emit`

Parses and rewrites the source-generator contract in **canonical order**. Its reported fingerprint is
therefore **identical to the assembly metadata fingerprint** for the same schema — which is what makes
"is the deployed schema the one this build expects?" an exact comparison rather than a guess.

---

## MSBuild verification

```xml
<PackageReference Include="Groundwork.SchemaTool.MSBuild" Version="0.4.0-preview.11" PrivateAssets="all" />

<PropertyGroup>
  <GroundworkSchemaFile>$(MSBuildProjectDirectory)/groundwork.schema.json</GroundworkSchemaFile>
  <GroundworkCoverageFile>$(MSBuildProjectDirectory)/groundwork.coverage.json</GroundworkCoverageFile>
</PropertyGroup>
```

The task runs **before compilation**. Portability refusals and uncovered queries become MSBuild errors
carrying their Groundwork diagnostic codes.

See **[Query Coverage & Indexes](Query-Coverage-and-Indexes)** for the coverage inventory format.

---

## Source-generated canonical schema

```csharp
[GwTable("customers")]
[GwIndex("by_email", "email ASC", Unique = true)]
public sealed class Customer
{
    [GwKey] public Guid Id { get; init; }
    [GwColumn(Length = 320, Required = true)] public string Email { get; init; } = "";
    [GwColumn(Folding = TextFolding.UnicodeOrdinalIgnoreCase, Length = 200)] public string Name { get; init; } = "";
}
```

`Groundwork.Schema.Generator` emits an assembly-level
`[GroundworkSchema(canonicalJson, fingerprint)]`. This is what the analyzer, the runtime gate, and the
CLI all read — one canonical artifact, three consumers.

Referenced assemblies contribute through `GroundworkSchemaMetadata`, so a library can ship its schema
and an application can validate against it.

---

## Validating a declaration in code

```csharp
var result = PortabilityValidator.Validate(unit);
foreach (var refusal in result.Refusals)
    Console.WriteLine($"{refusal.Code} at {refusal.Path}: {refusal.Message}");
```

`PortabilityValidator.EnsurePhysicalIdentifiers(unit)` checks physical naming rules alone
(`GW-PORT-010`), which providers call before any schema I/O.

---

## Schema drift and rebuilds

Derived search-key columns carry a complete **algorithm identity** (`AlgorithmId`) in the declaration.
A change to folding or prefix-boundary encoding is a **rebuild**, not an additive metadata edit — the
backfill is planned as a destructive operation and needs its identity through `--allow-destructive`.

```csharp
try { var records = table.Open(connection); }
catch (InvalidOperationException ex)
{
    // Message explains that the derived search key must be REBUILT.
}
```

Adding `.Collation(PortableCollation.OrdinalIgnoreCase)` to an already-deployed column is exactly this
kind of change. Plan it as a rebuild.

---

## Adopting a catalog Groundwork has never applied

A database can already hold exactly the storage a declaration describes — created by an earlier
release, restored from a backup, or provisioned by the platform team — while Groundwork has no
history row saying so. Starting against it refuses, and applying would try to create what is
already there.

When that catalog is owned by an EF Core application, first follow **[Migrate from EF Core](EF-Core-Migration)**.
Model similarity is not enough: adoption still requires Groundwork's exact physical representation.

`groundwork adopt` is the way in. It **executes no DDL**. Under the schema application lock it
inspects the deployed catalog, proves it matches the compiled target exactly — every column's type,
nullability, default, collation, key position and generation, plus every declared index — and then
CAS-publishes the applied-state row that applying the target *would* have published. The row is
produced from the same plan and the same completion the applier uses, so an adopted catalog and an
applied one are indistinguishable to every later diff.

```bash
groundwork adopt --schema groundwork.schema.json --provider postgresql --safe
groundwork status --schema groundwork.schema.json --provider postgresql   # ready, nothing pending
```

Authorization is `apply`'s: `--safe`, or an exact `--expected-plan` fingerprint. The report's
`outcome` is `adopted` when history was written and `ready` when it was already recorded.

**Any difference is a refusal that names it** — `GW-RUNTIME-001` for a column, `GW-RUNTIME-002` for
an index — and nothing is published. Adoption never infers: it does not decide what a deployed
column probably corresponds to, and arbitrary legacy-schema mapping stays out of scope
(`GW-SCHEMA-001`).

It also refuses, by name, where the question does not apply:

- `GW-SCHEMA-011` — the target already has applied history. Run `apply`, not `adopt`.
- `GW-SCHEMA-012` — the subject is declared retired, so it describes no catalog to verify.
- `GW-SCHEMA-013` — the provider called the catalog invalid without saying what differs.

On MongoDB the deployed catalog is the collection set: the primary collection, every per-scope
collection, their indexes, and the declared fields in their documents. A field the declaration does
not mention is deliberately not drift there — MongoDB publishes no column catalog, so there is no
undefaulted `NOT NULL` column that could refuse a write — but a missing field or a wrong BSON type
is `GW-RUNTIME-001` exactly as elsewhere. Adoption also republishes the provider catalog entry the
runtime compares its declaration against, so an adopted collection set opens without a second
in-process apply.

**Known limitation.** A subject with a folded column has a derived search-key column whose
algorithm registration lives in Groundwork's own catalog, which a database Groundwork never applied
to does not have. Adoption cannot prove the column's contents were produced by the declared
algorithm, so it refuses with `GW-RUNTIME-001` naming the search-key algorithm. Adopt such a unit by
creating a fresh catalog with `apply` instead.

---

## Coexisting with a catalog another tool extends

By default every deployed column the declaration does not describe is drift (`GW-RUNTIME-001`).
Where another system owns columns in the same table, a unit can opt into tolerating them:

```csharp
StorageUnit.Declare("orders", "orders")
    .String("id", 64, column => column.Required())
    .Key("id")
    .TolerateForeignColumns()
    .Build();
```

```json
{ "name": "orders", "foreignColumns": "TolerateDatabaseSupplied", "...": "..." }
```

The opt-in is deliberately narrow:

- It covers **only** a foreign column the database supplies a value for — nullable, defaulted, or
  generated. Those are reported as `GW-RUNTIME-003` warnings, and Groundwork neither reads nor
  writes them.
- A foreign column that is not nullable, not defaulted and not generated stays `GW-RUNTIME-001`. No
  policy could make it writable: every insert Groundwork emits omits it.
- Nothing else changes. A declared column that differs, a missing column, and index drift are
  unaffected.

The policy is **not** part of the subject fingerprint and is not recorded in applied state: it
governs what to do about things outside the target, not the shape of the target. Turning it on
therefore does not force a no-op apply, and the deployment tool and the host reach the same verdict
because both read it from the same declaration.

---

## Preview boundaries and the 1.0 transition

Historical preview boundaries remain clean breaks. When a preview release note marks one:

> **Discard the earlier preview catalog and create a fresh one from the new declarations.**
> There is no in-place migration, compatibility alias, dual-write, or fallback path between preview
> catalogs.

`0.2.0-preview.1` marks such a boundary for SQLite, and `0.4.0-preview.1` marks one for every
provider: subject fingerprints changed, so an earlier catalog is refused with `GW-SCHEMA-006`
naming the storage unit and this remedy. See **[Versioning & Support](Versioning-and-Support)**.

The final preview-to-1.0 transition is different: back up and inspect the deployment, use
`groundwork adopt` when an existing Groundwork-shaped catalog has no applicable history, and apply
authorized schema and resumable data migrations where possible. Recreate only when the 1.0 release
note identifies an incompatibility with no safe adoption or migration path.

---

## Recommended deployment flow

1. **Build** — analyzer + MSBuild verification catch portability and coverage problems.
2. **Emit** — `groundwork schema emit` produces the canonical artifact and its fingerprint.
3. **Plan** — `groundwork plan` in CI; review the report.
4. **Gate** — `groundwork status` exit code `2` means work is pending.
5. **Apply** — `groundwork apply --safe` in the deploy step; destructive/semantic work needs the exact
   plan fingerprint and operation ids.
6. **Start** — the application starts inspect-only; column drift is fatal, index drift refuses
   dependent shapes.

Where the catalog already exists but Groundwork has never recorded applying it, `groundwork adopt
--safe` replaces step 5 once, and the flow is unchanged from then on.

## Next

- **[Declaring Storage](Declaring-Storage)** — the declaration being deployed
- **[Query Coverage & Indexes](Query-Coverage-and-Indexes)** — build-time enforcement
