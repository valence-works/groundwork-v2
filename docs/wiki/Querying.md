# Querying

Groundwork has two query surfaces:

- **`Groundwork.Query.Model`** — the provider-neutral `QueryRequest` AST. The boundary every provider
  actually consumes.
- **`Groundwork.Query.Linq`** — a **closed** LINQ front-end that lowers to that AST.

## The closed LINQ front-end

```csharp
var query = table.Query
    .Where(c => c.Email == "ada@example.test")
    .Where(c => c.Status == Status.Active)
    .OrderBy(c => c.CreatedAt)
    .Take(50);

var results = records.Query(query, RecordQueryOptions.UsingIndex("by_email"));
```

`IGwQueryable<T>` **deliberately does not implement `System.Linq.IQueryable`.** That is the whole
point: an open `IQueryable` accepts any expression tree and then has to decide at runtime whether to
translate it, fall back to client evaluation, or throw. A closed surface accepts only what it can
translate, and tells you *at build time* when you've stepped outside it.

Supported: `Where`, `WhereIf`, `OrderBy`, `OrderByDescending`, `ThenBy`, `ThenByDescending`, `Skip`,
`Take`, mapped-column `Select`, `Distinct`, `LatestPer`, `AcceptScan`, and the terminals `ToList`,
`ToListAsync`, `Count`, `Any`, `CountAsync`, `AnyAsync`, `First`, `FirstOrDefault`, `Single`,
`SingleOrDefault`, `Sum`, `Min`, and `Max`. The async reduction forms `SumAsync`, `MinAsync`, and
`MaxAsync` dispatch through the provider-native scalar executor. `First` and
`FirstOrDefault` require an explicit deterministic order; `Distinct` removes duplicate projected
values before paging. `Sum` is limited to mapped `Int32`, `Int64`, and `Decimal` columns (integer
sums return nullable `Int64`); `Min` and `Max` use mapped orderable columns, ignore nulls, and return null
when no non-null value exists.

`ToQueryRequest()` is the provider-neutral boundary:

```csharp
QueryRequest request = query.ToQueryRequest();
```

### Executing a query

The async terminals run through `GwLinqExecutor` (package `Groundwork.Query.Linq.Execution`) — one
adapter for **all four providers**, because everything an executor does is provider-neutral:

```csharp
var executor = new GwLinqExecutor(session, connection);

var rows  = await table.Query.Where(c => c.Email == email).ToListAsync(executor);
var count = await table.Query.Where(c => c.Email == email).CountAsync(executor);
var any   = await table.Query.Where(c => c.Email == email).AnyAsync(executor);
var total = await table.Query.Where(c => c.Email == email).SumAsync(executor, c => c.LoginCount);
```

Every terminal is admitted through the shared runtime coverage gate before the provider renders
anything, so an uncovered query is refused with the same `GW-COVER-*` code the analyzer gave you at
build time, on whichever provider you happen to be running. See
**[Query Coverage & Indexes](Query-Coverage-and-Indexes)** — including
[why the declared key counts as coverage](Query-Coverage-and-Indexes#what-counts-as-a-covering-index)
and when `session.Read(key)` is still the right call for fetching a row by its key.

`SqliteLinqExecutor` remains as the named SQLite entry point and adds no behavior of its own.

Reduction execution returns one provider-produced scalar row. Filtering, latest-per-key selection,
`Distinct`, ordering, and any input `Skip`/`Take` window happen before the native `SUM`, `MIN`, or
`MAX`; no full source row set is materialized on the client. Null inputs are ignored and an empty or
all-null input returns null.

Closed terms are read from constants and closure fields **without compiling an expression per query
call**. Unsupported expression nodes are rejected rather than evaluated on the client.

### LINQ refusals

| Code | Problem | Fix |
| --- | --- | --- |
| `GW-LINQ-101` | Computed/member expression over columns | Declare a computed column |
| `GW-LINQ-102` | Arithmetic expression over columns | Declare a computed column |
| `GW-LINQ-103` | Column-to-column comparison | Add `.AcceptScan(...)` |
| `GW-LINQ-104` | Cross-table expression | **v2 has no joins** — use a declared element set or two queries |
| `GW-LINQ-105` | Grouped top-one | Use `.LatestPer(...)` |
| `GW-LINQ-106` | Unsupported element-set predicate | Declare the element set |
| `GW-LINQ-107` | Opaque helper method | Mark it `[GwQueryFragment]` |
| `GW-LINQ-108` | Unpinned string comparison | Use `Ordinal`/`OrdinalIgnoreCase` matching the column's folding |
| `GW-LINQ-109` | Non-UTC clock | Use `DateTimeOffset.UtcNow` |
| `GW-LINQ-110` | Decimal precision/scale overflow | The value exceeds the declared decimal |
| `GW-LINQ-111` | First/FirstOrDefault without deterministic order | Add an explicit `OrderBy` before `First` or `FirstOrDefault` |
| `GW-LINQ-112` | Sum/Min/Max selector is not a mapped portable column | Select a mapped numeric or orderable column |
| `GW-LINQ-113` | Skip without Take | Add a bounded Take; offset-only pages are not portable |

The predicate codes are locked by a **250-case conformance corpus** checked byte-for-byte in CI;
the query-shape rows are covered by the model, lowering, and coverage suites so those contracts
cannot drift either.

### Reusable predicate helpers

```csharp
[GwQueryFragment]
static bool IsActiveIn(Customer c, string region) =>
    c.Status == Status.Active && c.Region == region;

var q = table.Query.Where(c => IsActiveIn(c, "eu-west"));
```

Without the attribute the helper is opaque and refused with `GW-LINQ-107` — Groundwork will not
guess at a method body it cannot see through.

### Pinning string comparison

```csharp
[GwStringComparison(StringComparison.Ordinal)]
```

The comparison policy must match the column's declared folding, or you get `GW-LINQ-108` (at build)
or `GW-QUERY-031` (at render).

## Building a request directly

```csharp
using Groundwork.Query.Model;

var table  = new TableId("customers");
var email  = new ColumnRef(table, "email", QueryType.String, isNullable: false);

var request = new QueryRequest(
    table,
    new Predicate.Equal(email, QueryConstant.Of(email, "ada@example.test")),
    order: [],
    Projection.All,
    Paging.Keyset(50),
    ResultShape.Rows.Instance);

var result = session.Query(request, storageUnit.CreateQueryRenderOptions("by_email"));
```

### Predicate nodes

```csharp
Predicate.AlwaysTrue.Instance
new Predicate.Equal(column, constant)
new Predicate.In(column, values)                      // capped at 1,000 (GW-QUERY-015)
new Predicate.Range(column, lower, upper)             // Bound.Inclusive / Bound.Exclusive
new Predicate.StartsWith(column, prefix)
new Predicate.Substring(column, needle, Anchor.Contains | Anchor.EndsWith)
new Predicate.ElementOf(set, values, SetQuantifier.Any | SetQuantifier.All)
new Predicate.ColumnCompare(left, CompareOp.LessThan, right)
Predicate.Not(inner)
Predicate.And(terms)
Predicate.Or(terms)
```

Predicates are **normalized and canonicalized** on construction. `request.CanonicalPredicate`,
`request.ShapeFingerprint`, and `request.ContinuationFingerprint` are stable identities you can use
for caching, logging, and admission.

### Projection and result shape

```csharp
Projection.All
Projection.ColumnsOnly(idColumn, emailColumn)

ResultShape.Rows.Instance        // never adds a count expression
ResultShape.TotalCount.Instance  // adds the provider window-count projection
```

## Paging

```csharp
Paging.None
Paging.OffsetLimit(offset, limit)     // rendered only when explicitly requested
Paging.Keyset(limit)                  // first keyset page
Paging.Continuation(token, limit?)    // later pages
```

**Prefer keyset paging.** Offset paging degrades on every provider as the offset grows.

```csharp
var page = session.Query(request);
if (page.NextContinuationToken is { } token)
{
    var next = new QueryRequest(table, where, order, projection,
        Paging.Continuation(token), ResultShape.Rows.Instance);
}
```

Three rules for correct pages:

1. **Every order term must name its null rank.** `NullOrder.ProviderDefault` is refused
   (`GW-SEM-ORDER-004`).
2. **For joined continuations, you must supply your complete declared identity through
   `QueryRenderOptions.DrivingIdentityColumns`.** Joined continuations include that identity in
   declaration order even when `TieBreakColumns` contains only additional tie-breaks. Without a
   complete identity, pages are not deterministic.
3. Ordering automatically normalizes nulls-first-ascending / nulls-last-descending and appends the
   identity tie-break before paging.

Continuation tokens are typed tuples built with `QueryContinuationToken.Encode`. Under privileged
cross-scope access they are additionally bound to the audit identity and purpose, and contain
**neither raw scope values nor audit strings**.

## Latest-per-key

```csharp
var q = table.Query.LatestPer(c => c.CustomerId, c => c.CreatedAt);
```

Requires a **non-null `DateTimeOffset`** timestamp (`GW-SEM-LATEST-001`). Under cross-scope access it
partitions by scope before applying its logical key.

## Render options and index selection

```csharp
var options = storageUnit.CreateQueryRenderOptions(selectedIndex: "by_email");
var result  = session.Query(request, options);
```

`CreateQueryRenderOptions` translates the admitted unit's index names, column types, nullability, and
missing-value policy — you do not restate schema metadata.

> **Selecting an index is not hinting it.** The default policy is `QueryIndexPinning.ProviderDefault`
> and emits **no native hint**. Selection enables coverage/explain evidence only. A declaration must
> explicitly use `QueryIndexPinning.Pinned` before SQL Server or MongoDB receive a hint; PostgreSQL
> and SQLite have no hint syntax and remain unhinted regardless.

A pinned index that **excludes nulls** is refused when the predicate could match an excluded null —
except for match-none. This preserves the v1 sparse-index safety rule.

## Provider rendering

| Provider | Command | Notes |
| --- | --- | --- |
| SQLite / PostgreSQL / SQL Server | `RelationalQueryCommand` | Renderers are synchronous and do not change the query model |
| MongoDB | `MongoQueryCommand` | Native BSON filter + sort; an aggregation pipeline when explicit null ranks or a count are required |

Parameter budgets are enforced against the provider's real limit — **SQLite 999, SQL Server 2,098
caller-owned parameters, PostgreSQL 65,535** — including cursor and page parameters. SQL Server's
2,100 statement limit reserves two slots for the `Microsoft.Data.SqlClient` `sp_executesql` wrapper.
Exceeding the effective budget is a refusal, not a truncation. Each provider connection advertises
that limit as a `QueryAdmissionProfile`, so the pre-execution fence and the renderer use the same
number rather than two guesses at it. MongoDB has no bound-parameter budget and advertises no
parameter ceiling; ordinary membership still uses the portable 1,000-value renderer limit.

Keyed batch reads use the profile's separate `MaximumBatchReadKeys` budget (999 by default; SQLite
999, SQL Server 2,098, and PostgreSQL 65,535). A scoped session reserves one key slot for its
provider-injected scope parameter. The shared planner also applies a conservative 15 MiB
encoded-payload budget by default, so an omitted connection cannot reach MongoDB's 16 MiB BSON
command limit; a provider connection or explicit profile can advertise a different deployment
budget. Folded string key columns must use the comparison policy matching their persisted search-key
mapping or the batch read refuses with `GW-QUERY-031`.

An empty `In` normalizes to match-none; a pinned declaration is still carried on the native command.

## Runtime value fence

Before execution, `RuntimeValueFence` re-checks membership cardinality, provider parameter count, and
continuation order/plan binding. The typed query model separately enforces value length, decimal
precision/scale, and well-formed UTF-16 **at construction**. A `RuntimeValueFenceException` means a
value slipped past the shape check but not past the value check.

## Verifying native plans

```bash
GW_EXPLAIN_ASSERT=1 \
GW_EXPLAIN_ARTIFACT_DIR="$PWD/TestResults/groundwork-explain" \
dotnet test tests/Groundwork.Differential.Tests
```

Off by default; adds **no** plan command to normal execution. When enabled, the query runs normally
and then its native plan is fetched (`EXPLAIN (FORMAT JSON)`, showplan XML, `EXPLAIN QUERY PLAN`, or
`explain` with `executionStats`) and the exact resolved physical index name is asserted.

Output labels the proof `optimizer-selected` for unhinted PostgreSQL/SQLite plans and `hinted` for
SQL Server/MongoDB — the latter proves the deployed index exists and is usable, **not** that the
optimizer chose it freely.

> Plans can contain identifiers and query values. Treat the artifact directory as potentially
> sensitive test output.

## Next

- **[Query Coverage & Indexes](Query-Coverage-and-Indexes)** — why a query gets refused
- **[Portable Semantics](Portable-Semantics)** — what the predicates mean
