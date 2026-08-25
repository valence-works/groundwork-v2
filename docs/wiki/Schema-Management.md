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

MongoDB does the same inspect-only split at `OpenSession` via its public `InspectSchema` report.

## The `groundwork` CLI

```bash
dotnet tool install --global Groundwork.Tool --version 0.2.0-preview.1 \
  --add-source https://f.feedz.io/valence-works/groundwork/nuget/index.json
```

```text
groundwork plan     --schema groundwork.schema.json --provider <alias>
groundwork validate --schema groundwork.schema.json --provider <alias> [--offline]
groundwork status   --schema groundwork.schema.json --provider <alias>
groundwork apply    --schema groundwork.schema.json --provider <alias> --safe
groundwork schema emit --input schema.json --file groundwork.schema.json
```

Add `--output json` for the stable machine-readable report.

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
```

You cannot authorize destructive work generically. You authorize *these operations, in this plan*.

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
<PackageReference Include="Groundwork.SchemaTool.MSBuild" Version="0.2.0-preview.1" PrivateAssets="all" />

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
A change to folding or prefix-boundary encoding is a **rebuild**, not an additive metadata edit.

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

## The clean-break preview rule

Groundwork v2 is a **clean-break pre-1.0 product**. When a preview release note marks a persisted
schema boundary:

> **Discard the earlier preview catalog and create a fresh one from the new declarations.**
> There is no in-place migration, compatibility alias, dual-write, or fallback path between preview
> catalogs.

`0.2.0-preview.1` marks such a boundary for SQLite. See
**[Versioning & Support](Versioning-and-Support)**.

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

## Next

- **[Declaring Storage](Declaring-Storage)** — the declaration being deployed
- **[Query Coverage & Indexes](Query-Coverage-and-Indexes)** — build-time enforcement
