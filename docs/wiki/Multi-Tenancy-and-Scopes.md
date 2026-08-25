# Multi-Tenancy & Scopes

Declare a unit `Scoped` and every ordinary session must name exactly one scope. Isolation is enforced
by the provider, not by application `WHERE` clauses you have to remember to write.

## Declaring a scoped unit

```csharp
var unit = StorageUnit.Declare("orders", "orders")
    .String("id", 64, c => c.Required())
    .String("total", 32)
    .Key("id")
    .Scoped()
    .Build();

// or, on the record form:
Scope = ScopePolicy.Scoped
```

## Opening a scoped session

```csharp
var session = connection.OpenSession(unit, StorageAccess.Scoped(new StorageScope("tenant-a")));
session.Insert(values);
```

Every read, write, query, and aggregation on that session is confined to `tenant-a`.

Mismatches fail **before any I/O**:

- Opening a **scoped** unit with `StorageAccess.Global` → `InvalidOperationException`
- Opening a **global** unit with `StorageAccess.Scoped(...)` → `InvalidOperationException`

There is no ambient/implicit tenant and no way to "forget" the scope.

## How isolation is enforced

The scope is a **provider-owned physical restriction**, applied before any caller predicate:

- Aggregation applies it **before source filtering and reduction**.
- Cross-scope `LatestPerKey` partitions by scope before applying its logical key.
- A scope is **never** exposed as a caller-visible column or predicate.
- `__groundwork_scope` and `__groundwork_scope_token` are provider-owned logical names. Schema
  admission refuses application declarations that use them; third-party providers can reuse
  `ProviderOwnedColumns.ValidateLogicalDeclaration(unit)`.

Two different scopes can hold the same logical key without conflict:

```csharp
foreach (var scope in new[] { "tenant-a", "tenant-b" })
{
    var s = connection.OpenSession(unit, StorageAccess.Scoped(new StorageScope(scope)));
    s.Insert(new StorageValues(new Dictionary<string, object?>
    {
        ["id"] = "same", ["value"] = scope
    }));   // both succeed
}
```

For SQLite, a scoped unit with a `ProviderSequence` key keeps the generated value **unit-wide** —
scope is an access predicate, not an additional physical primary-key column.

## Typed families

```csharp
var records = table.Open(connection, StorageAccess.Scoped(new StorageScope("tenant-a")));
var batch   = table.BeginUnitOfWork(connection, StorageAccess.Scoped(scope), BatchWriteOptions.Exact);
var outcome = documentUnit.Execute(connection, write, StorageAccess.Scoped(scope));
```

---

## Audited cross-scope queries

Recovery, administration, and diagnostics sometimes need to see across every tenant. The two obvious
workarounds are both bad: treating the caller as global weakens the declaration, and iterating tenant
sessions in application code makes paging, counts, and latest-per-key provider-dependent.

Groundwork provides an explicit, **audited, query-only** access instead.

```csharp
var access = StorageAccess.PrivilegedAcrossScopes(
    new StorageAccessAudit(
        identity: "workflow-recovery-worker",
        purpose:  "recover-stalled-workflows",
        observer: auditSink));

var session = connection.OpenSession(workflowUnit, access);
var result  = session.QueryAcrossScopes(request);

foreach (var row in result.Rows)
    Console.WriteLine($"{row.Scope.Value}: {row.Values["id"]}");

Console.WriteLine(result.TotalCount);
Console.WriteLine(result.NextContinuationToken);
```

### It is query-only, and that is enforced

`Query`, `Read`, all writes, `Append`, `ApplyRetention`, `Aggregate`, `Inspect`, and
`BeginUnitOfWork` **all fail before provider work**. Open an ordinary
`StorageAccess.Scoped(scope)` session for those.

```csharp
try { privileged.Read(key); }
catch (InvalidOperationException ex)
{
    // GW-ACCESS-003: a point read across scopes is ambiguous.
}
```

The reason a point read is refused rather than "returning the first match": the same key can exist in
many scopes, so there is no correct answer. Naming the scope is the fix.

### Audit is mandatory

`StorageAccessAudit` requires a **non-blank identity and purpose**
(`MaxIdentityLength = 128`, `MaxPurposeLength = 256`). An `IStorageAccessObserver` receives **one
`query-across-scopes` event before each native query**:

```csharp
public sealed class AuditSink : IStorageAccessObserver
{
    public void Observe(StorageAccessEvent accessEvent)
    {
        logger.LogInformation("{Operation} by {Identity} for {Purpose}",
            accessEvent.Operation, accessEvent.Identity, accessEvent.Purpose);
    }
}
```

### Results

`CrossScopeQueryRow` always pairs public values with the **scope that owned them**. Counted paging,
continuation tokens, narrow projections, and `LatestPerKey` all retain provider-neutral behavior.

Provider-owned columns are stripped — no `__groundwork_*` key ever appears in returned values.

### Continuation tokens

Tokens are bound to the audit **identity and purpose**, so a token issued for one privileged
invocation cannot be replayed under a different one. Tokens contain **neither raw scope values nor
audit strings**.

### Provider notes

MongoDB cannot apply one pinned physical index across separate scope collections and explicitly
refuses that combination. **Unpinned cross-scope queries remain supported.**

All five conformance providers support audited, query-only cross-scope access for scoped units.

### Diagnostics

| Code | Meaning |
| --- | --- |
| `GW-ACCESS-001` | Cross-scope query without privileged access |
| `GW-ACCESS-002` | Provider session does not advertise privileged cross-scope queries |
| `GW-ACCESS-003` | Point operation attempted under privileged access |
| `GW-ACCESS-004`…`006` | Additional access-context refusals |

## Design guidance

- Use a **stable, opaque** scope value (a tenant id), not a display name.
- Privileged access is for **recovery, administration, and diagnostics** — not for a "show all
  tenants" product feature. If a feature needs cross-tenant data routinely, model it as a global unit.
- Route audit events to durable storage. The observer is the only record that a privileged query ran.
- Give the `purpose` string operational meaning (`"recover-stalled-workflows"`), because that is what
  the person reading the audit log six months from now will have to work with.

## Next

- **[Core Concepts](Core-Concepts)** — access contexts
- **[Querying](Querying)** — paging and projections
