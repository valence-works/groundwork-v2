# Aggregation

Aggregation in Groundwork is a **closed, declared surface**. A profile fixes the group-by columns,
output aliases, aggregate kinds, and resource bounds **at declaration time**; a caller selects the
profile by name and may only vary what the profile permits.

This is deliberately not "run any GROUP BY you like". An open aggregation surface is unbounded work
against a shared database, and its cost is invisible until it isn't.

## Declaring a profile

```csharp
AggregationProfiles =
[
    new AggregationProfile
    {
        Name           = "diagnostic-summary",
        GroupByColumns = ["service"],
        Aggregates     =
        [
            new Aggregate.Count("count"),
            new Aggregate.Sum("total", "duration"),
            new Aggregate.Min("firstSeen", "occurredAt"),
            new Aggregate.Max("lastSeen",  "occurredAt"),
            new Aggregate.SetUnion("levels", "level", MaxValues: 8),
            new Aggregate.FirstBy("firstMessage", "message", OrderColumn: "seq")
        ],
        AllowedPredicates =
        [
            new AggregationPredicateAllowance
            {
                Alias = "total",
                SupportedPredicates = new HashSet<AggregationPredicateOperator>
                {
                    AggregationPredicateOperator.Equal,
                    AggregationPredicateOperator.RangeInclusive
                }
            }
        ],
        MaxGroups    = 1_000,
        MaxInputRows = 100_000
    }
]
```

Or fluently:

```csharp
.Aggregate("by-trace-summary", a => a
    .GroupBy("traceId")
    .Min("firstSeen", "occurredAt")
    .Max("lastSeen", "occurredAt")
    .SetUnion("levels", "level", maxValues: 8)
    .FirstBy("firstMessage", "message", orderBy: "seq"))
```

## Executing

```csharp
var result = session.Aggregate(new AggregationQuery("diagnostic-summary")
{
    SourcePredicate = new Predicate.Equal(
        new ColumnRef(new TableId("diagnostics"), "service", QueryType.String),
        QueryConstant.Of("orders")),

    PostPredicate = new AggregationPredicate.Comparison(
        "total", AggregationPredicateOperator.RangeInclusive, [1L, 1000L]),

    OrderByTerms =
    [
        new AggregationOrderTerm("count", SortDirection.Descending),
        new AggregationOrderTerm("service", SortDirection.Ascending)
    ],
    Take = 100
});

foreach (var row in result.Rows)
    Console.WriteLine($"{row["service"]}: {row["count"]} ({row["total"]})");
```

## Source vs. post predicates — the distinction that bites

**Operation order: source filter → reduction → post filter → ordering → group page.**

- **`SourcePredicate`** is evaluated against **declared source columns before grouping**. It uses the
  ordinary portable `Predicate` AST and is bound to the unit's declared columns and literal types at
  admission.
- **`PostPredicate`** is evaluated **only against declared output aliases after reduction**. Every
  operator must be listed in the profile's `AllowedPredicates`; an undeclared alias or operator is
  refused.

They are not interchangeable. Worked example — a trace with an older row
(`service = "orders"`, `duration = 7`) and a newer row (`service = "billing"`, `duration = 11`):

| Filter | Result |
| --- | --- |
| `SourcePredicate: service == "orders"` | Reduces **only the matching row** → `duration = 7` |
| `PostPredicate` on reduced `duration` | Sees the sum of **both** rows (`18`) — cannot reproduce the source-filtered result |

Callers **cannot** submit an aggregate expression, change a reducer, or raise a profile's budgets.
Those are declaration-time decisions.

## Accepted ad-hoc aggregation

For a report whose grouping and reducers are selected at runtime, compose an ad-hoc query from the
same closed `AggregationGroup` and `Aggregate` vocabulary and attach an explicit acceptance:

```csharp
var query = AggregationQuery.ForAdHoc(
    "support-summary",
    ["team"],
    [new Aggregate.Count("count"), new Aggregate.Sum("total", "amount")],
    AggregationAcceptance.Allow(
        "GW-AGG-0001", "temporary support report", "operations",
        DateTimeOffset.UtcNow.AddDays(30), maxGroups: 100, maxInputRows: 10_000));
var result = session.Aggregate(query);
```

`AggregationAcceptance` is the operational inventory entry: id, reason, owner, expiry,
`MaxGroups`, and `MaxInputRows` are all required. An ad-hoc query without an active acceptance,
or with an expired acceptance, is refused before provider I/O. Its budgets override any profile
budget supplied during composition, and its shape is not persisted in schema history. Scoped
sessions retain their scope restriction; privileged cross-scope sessions continue to refuse
aggregation.

Provider rendering: SQL emits the source predicate in the base `WHERE`; MongoDB emits `$match` before
`$group`; the reference provider filters its input before reduction. Relational aggregation source
predicates use the **same ordinary renderer fragment** as `QueryRequest`, so provider hooks, portable
ordering (including SQL Server GUID ordering), substring semantics, and bound parameters cannot drift
between a normal query and pre-reduction input.

## Portable reducers

| Reducer | Accepts | Null behavior | Notes |
| --- | --- | --- | --- |
| `Min` / `Max` | Orderable types | Ignores nulls; **null when a group has no non-null values** | |
| `Sum` | `Int32`, `Int64`, `Decimal` only | **All-null sum is null** | Integer sums → `Int64`; decimal sums retain decimal arithmetic |
| `Count` | — | — | Source-free `Int64`, rendered by each provider's native grouping |
| `SetUnion` | **Strings only** | Excludes nulls | Ordinal-sorted distinct set, independent of physical collation, **no reserved delimiter**; refuses once `MaxValues` would be exceeded |
| `FirstBy` | Requires a non-null orderable order column | — | Ties resolved by the unit's declared key; multiple `FirstBy` reducers may use independent order columns/directions |

Every reducer is deterministic. That is why ties are resolved by the declared key rather than left to
the provider.

## Budgets are refusals, not page sizes

`MaxInputRows` and `MaxGroups` are **refusal budgets**. Exceeding either **fails the operation**.
Rows and groups are **never silently truncated**.

Native providers cap the input at one row beyond `MaxInputRows` and run bounded group/set cardinality
evidence before materializing values. A native operation is one or more bounded budget-evidence
commands followed by the final grouped-result command — all provider-native, none materializing
source rows or going through the ordinary `Query` path.

> This is **not** a promise of one network round trip. It is a promise that nothing unbounded is
> materialized.

## Ordering and paging

```csharp
OrderByTerms =
[
    new AggregationOrderTerm("count",   SortDirection.Descending),
    new AggregationOrderTerm("service", SortDirection.Ascending)
],
Take = 100
```

Terms name **only declared group or aggregate aliases**. Missing group columns are appended as
ascending tie-breakers. Duplicate aliases and invalid directions are refused at admission.

## Fingerprints

```csharp
result.ShapeFingerprint  // literal values elided
result.ValueFingerprint  // all bound values included
```

Equivalent shapes with different source literals **do not share an execution identity**. The same
identities are available up front via `AggregationQueryFingerprint` for admission or cache
diagnostics.

Scope values bind the `ValueFingerprint`, while `ShapeFingerprint` intentionally stays the same for
the same profile and shape across scopes — so shape-level caching and diagnostics aggregate across
tenants without leaking tenant values.

> The legacy rows-only `AggregationResult` constructor leaves these nullable fingerprints unset.

## Calendar time buckets

```csharp
GroupByExpressions =
[
    AggregationGroup.TimeBucket.FixedUtc("bucket", "createdAt", TimeSpan.FromHours(1))
]
```

```csharp
var result = session.Aggregate(new AggregationQuery("hourly")
{
    TimeRange = new AggregationTimeRange(from, from.AddHours(1))   // from inclusive, to exclusive
});
```

Two kinds:

- **`FixedUtc(alias, column, width)`** — fixed-width UTC buckets. Set `TimeBucketOrigin` explicitly if
  you need a specific anchor; otherwise a **bounded** request anchors at `TimeRange.From` and an
  **unbounded** request uses the Unix epoch.
- **`LocalCalendarDay(alias, column)`** — uses the invocation's named IANA zone
  (`TimeZoneId = "Europe/Amsterdam"`) and returns the **UTC instant of local midnight**.

DST is handled properly rather than approximated: spring-forward days are 23 hours and fall-back days
are 25 hours, **without collapsing or duplicating bucket identities**. If a zone advances *at* local
midnight, the bucket is the earliest valid instant on that local calendar date; if midnight is
ambiguous, the first occurrence is selected.

Null input timestamps are excluded from the derived grouping. The range, width/kind, invocation
zone/origin, and ordered terms are all part of the query identity, and native providers render the
grouping in **one bounded aggregation operation** — not one call per requested bucket.

## Scoped and privileged access

Scoped sessions add the provider-owned physical scope restriction **before** source filtering and
reduction. A scope is never exposed as a caller-visible column or predicate.

**Privileged cross-scope aggregation is refused.** Privileged access is query-only.

## Schema semantics

Profiles are part of the **schema subject fingerprint**. Changing a reducer, alias, allowance, or
budget is schema semantic drift and cannot be hidden behind a query. Treat a profile change like a
column change.

The aggregation session adapter scans only columns required by the declared profile and key, so an
unrelated JSON column does not make an otherwise-valid aggregation unexecutable.

## Diagnostics

Aggregation diagnostics are grouped by concern: `GW-AGG-DECL-*` (declaration), `GW-AGG-QUERY-*`
(query admission), `GW-AGG-SOURCE-*` (source predicate), `GW-AGG-PRED-*` (post predicate),
`GW-AGG-GROUP-*` (grouping), `GW-AGG-BOUND-*` (budgets), `GW-AGG-TYPE-*` / `GW-AGG-COLUMN-*` /
`GW-AGG-SUM-001` / `GW-AGG-FIRST-001` (reducers). See **[Diagnostics Reference](Diagnostics-Reference)**.

Accepted ad-hoc aggregation values are inventoried by the coverage analyzer when the assembly opts
in with `[assembly: GwAllowAcceptedAggregations]`. The analyzer emits `GW-AGG-ADHOC-905` inventory
records, warns with `GW-AGG-ADHOC-904` during the final 30 days, and errors with
`GW-AGG-ADHOC-903` after expiry; `GW-AGG-ADHOC-902` refuses an acceptance without the opt-in.
`GW-AGG-ADHOC-906` fails closed when required acceptance metadata cannot be resolved to constants.

## Next

- **[Streams: Append & Retention](Streams-Append-and-Retention)** — the stream this usually summarizes
- **[Declaring Storage](Declaring-Storage)** — declaring profiles
