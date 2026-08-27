# P3.2 expand–contract workflows

Additive-only was never a limitation to hide: it is the first half of expand–contract. This makes
the pattern first-class. One declaration produces two plans — an **expand** plan that is additive
and a **contract** plan that removes what the expand deliberately left behind — with an explicit
readiness gate between them.

```text
groundwork apply --phase expand   …   # additive: add the replacement, retain the old column
   … application rolls out, backfill runs, dual-presence window elapses …
groundwork apply --phase contract …   # destructive: remove the superseded column
```

`--phase` defaults to `expand`, and it changes nothing for a declaration that supersedes no column:
both phases derive the same operations there.

## Declaring a supersession

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

Three things are declared, and each one is load-bearing.

**The superseded column is carried in full, not by name.** It is deliberately *not* a declared
column of the subject any more, so nothing else describes its portable type — and the backfill that
populates the replacement has to read it with that type, on every provider, exactly as it would read
a declared column. `PhysicalSchemaApplication` carries retained supersessions into the storage unit
the data migration runs against for precisely that reason.

**The superseded column must not also be declared.** `SchemaSubject` refuses a declaration that both
declares and supersedes one column. A column that is still declared is not superseded — it is simply
present, and the write path would keep maintaining it.

**A supersession requires a semantic migration id.** The data migration recorded under it is what
populates the replacement column, and its recorded completion is what opens the contract gate. A
supersession with nothing to populate its replacement is a data-loss trap wearing the workflow's
name, so it is refused at declaration time rather than documented as a caveat.

## Dual-presence window semantics

While the old and new columns coexist:

| | superseded column (`total`) | replacement column (`total_amount`) |
| --- | --- | --- |
| **Reads** by the new declaration | not visible | authoritative |
| **Writes** by the new declaration | never written | written |
| **Reads/writes** by the previous application version | authoritative | not visible |
| **Drift inspection** | ignored | compared as usual |
| **Expand plan** | one ledger marker, no physical work | added, backfilled |

These are properties of the model, not conventions:

- **A read sees the declared column only.** The superseded column is undeclared, so the runtime
  projects nothing for it. There is no "which side wins" question to answer.
- **A write never touches the superseded column.** Not by policy — the declaration that supersedes
  it cannot name it, so clobbering it is unrepresentable rather than discouraged.
- **The expand plan is invisible to the previous application version.** The only operation in an
  expand plan that names the superseded column is the supersession marker, and the marker performs
  no physical work at all. Nothing renames, alters, or removes the column the old version owns.
  `ExpandContractTests` asserts this as a property of the plan.
- **The superseded column's continued physical presence is not drift.** A deployed column the
  declaration does not describe is refused as drift, but a retained superseded column is described:
  the declaration names it in `Evolution.Supersessions` and keeps it in the catalog on purpose, and
  `RelationalSchemaExecutor` exempts it from the foreign-column scan for exactly that reason. A
  running application therefore admits cleanly through the whole window, and the two live proofs in
  `SqliteExpandContractTests` fail without the exemption.
- **Indexes over the superseded column are not retained.** An index cannot reference an undeclared
  column, so any index the previous declaration had over it is dropped by the expand plan under the
  ordinary `DropIndex` authorization. An index is derivable and its removal destroys nothing; the
  column is not, and it is the thing the workflow protects. This also means no index depends on the
  column by the time the contract plan drops it, which is what SQL Server requires.

### What the window does *not* prove

The kernel cannot observe an application rollout. A pre-expand application version writes the
superseded column and knows nothing about the replacement, so a row it writes *after* the backfill
scanned past that row's key leaves the replacement stale. No provider-neutral mechanism closes that:
the fix would be a database-side trigger or generated column, which is not portable.

So the window is exactly what it says it is — **the operator's declared upper bound on how long a
pre-expand application version may still be writing**, measured from the later of the retention
being recorded and the backfill being recorded complete. Stopping those writers before it elapses is
the operator's obligation; the gate bounds it and refuses to move early, and it does not pretend to
have observed it.

## The readiness gate

The contract plan refuses until three facts hold, and each is read from durable state rather than
supplied by the caller:

1. the applied schema ledger records the column as **retained** beside its replacement, which only an
   applied expand plan writes;
2. the data migration named by the subject's semantic migration id is recorded **complete** in
   `__groundwork_data_migrations` — which, per P3.3, only an exhausted source can produce;
3. the declared **dual-presence window** has elapsed since the later of those two instants.

`ExpandContractWorkflow.AssessContractReadiness` is the only implementation of that rule. The
deployment tool's read-only report and `PhysicalSchemaApplication.Apply` both call it, so a gate that
opens in `groundwork plan --phase contract` cannot close differently under `apply`.

### Readiness cannot be asserted

`ContractReadinessAssessment` is a sealed class whose only constructor is internal to the kernel. An
application or a provider assembly cannot manufacture one; the sole way to obtain an instance is
`AssessContractReadiness`, which reads durable state. This is the same discipline
`DataMigrationExhaustion` enforces for migration completion, and it is why the type is not a
positional record — a record's public constructor would let a caller write "ready, refusals: none".

Evidence also carries the target identity and the fingerprint of the applied snapshot it was
established against. `PhysicalSchemaDiffPlanner` refuses evidence that does not describe exactly the
state it is planning from, so an assessment taken before something else moved the applied state is
`GW-EXPAND-005` rather than a stale green light.

## The marker, and why the contracted state is terminal

The expand plan records one `ColumnSupersession` operation per supersession in the applied ledger.
It performs no physical work: it is the durable fact that a column is still there on purpose, the
instant the dual-presence window opened, and — once the contract runs — the record that the column
is gone.

That makes the terminal state sticky. After a contract:

- replanning the **contract** phase has nothing to do;
- replanning the **expand** phase has nothing to do either — a superseded column is never re-added,
  in either phase;
- **withdrawing** the supersession from the declaration is clean, and is how the workflow ends.

Withdrawing it while the column is still *retained* is refused with `GW-EXPAND-006`: dropping the
declaration then would strand the column, physically present and named by nothing.

## Worked example: a rename

`orders.total` becomes `orders.total_amount`.

1. **Declare.** The declaration drops `total`, declares `total_amount`, and names the supersession
   with a migration id and a window. Deploy the application; it reads and writes `total_amount`
   only.
2. **Expand.**
   ```text
   groundwork plan  --schema groundwork.schema.json --provider sqlite --output json
   groundwork apply --schema groundwork.schema.json --provider sqlite \
       --expected-plan <planFingerprint> --allow-semantic 2026-08-widen-total
   ```
   The plan is `AddColumn total_amount`, the transform attached to `2026-08-widen-total`, and the
   retention marker. Nothing is removed. The previous application version keeps running against
   `total` untouched.
3. **Wait.** The old version drains. `groundwork status --phase contract` reports the supersession
   with `retainedSince`, `backfillCompletedAt`, and `contractableAt`.
4. **Contract.**
   ```text
   groundwork plan  --schema groundwork.schema.json --provider sqlite --phase contract --output json
   groundwork apply --schema groundwork.schema.json --provider sqlite --phase contract \
       --expected-plan <contractPlanFingerprint> \
       --allow-destructive drop-column:orders.total \
       --allow-semantic 2026-08-widen-total
   ```
   The contract plan fingerprint is a different value from the expand plan's, so authorizing the
   expand can never authorize the contract.
5. **Tidy.** Remove the supersession from the declaration whenever convenient. The marker records it
   as contracted, so the expand plan is already a no-op for it.

## Worked example: a type widening

`orders.total decimal(10,2)` becomes `decimal(18,2)`.

An in-place widening is already available — `AlterColumn` with `ColumnAlterationKind.Widening`,
classified by `ColumnEvolution.Classify` — and it is the right tool when a single `ALTER` is
acceptable. Expand–contract is the right tool when it is not: when the table is large enough that a
rewrite would lock it, or when the change is a narrowing that would refuse rows, or when the
application rolls out gradually.

The steps are identical to the rename above; only the transform differs, because the replacement
column has a different type from the one it supersedes:

```csharp
public sealed class WidenTotalTransform : IDataMigrationTransform
{
    public string Identity => "widen-total/v1";
    public ImmutableArray<string> SourceColumns => ["total"];
    public ImmutableArray<string> TargetColumns => ["total_amount"];

    public DataMigrationValues Transform(DataMigrationRow row) =>
        DataMigrationValues.Set(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["total_amount"] = row["total"]
        });
}
```

`row["total"]` reads the superseded column at its declared portable type — that is what carrying the
column definition in the supersession buys. The gate then refuses the contract until this transform
is recorded complete, so `total` cannot be dropped while any row's `total_amount` is unwritten.

## Startup never contracts

`GroundworkRuntimeSchemaAdmission` plans the expand phase and only the expand phase. There is no
option that makes an application contract its own schema on startup: the contract half is a
deployment-tool action, authorized against an exact plan fingerprint by an operator who names the
exact operation.

## Refusal codes

| Code | Meaning |
| --- | --- |
| `GW-EXPAND-001` | The applied ledger does not record the column as retained beside its replacement — the expand plan has not been applied |
| `GW-EXPAND-002` | The data migration that populates the replacement is not recorded complete |
| `GW-EXPAND-003` | The declared dual-presence window has not elapsed |
| `GW-EXPAND-004` | A contract plan was requested without readiness established from durable state |
| `GW-EXPAND-005` | Readiness was established for another target or against another applied state |
| `GW-EXPAND-006` | A declaration withdrew a supersession whose column is still retained |

## Not covered

Evolution metadata — `IsDestructive`, `SemanticMigrationId`, `RetiresPrimaryStorage`, and now
`Supersessions` and `DualPresenceWindow` — is a kernel declaration attached by an
`IPhysicalSchemaTargetCompiler`. It is not expressible in `groundwork.schema.json`, so a supersession
cannot be declared through the canonical schema document today. That gap predates this work and
applies to every member of `SchemaEvolutionMetadata`; closing it belongs with the schema-document
contract, not here.
