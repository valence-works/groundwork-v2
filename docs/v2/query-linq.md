# Closed LINQ front-end

`Groundwork.Query.Linq` is a convenience front-end over `Groundwork.Query.Model`. Its
`IGwQueryable<T>` is intentionally closed and does not implement `System.Linq.IQueryable`.
`ToQueryRequest()` is the provider-neutral boundary; providers execute that request through their
existing session APIs.

The conformance corpus in `tests/Groundwork.Query.Linq.Tests` is the source of truth for this
surface. The following diagnostic table is generated deterministically from the predicate corpus
decision rows and checked byte-for-byte by the corpus test; predicate corpus version `p2` locks the
ten predicate codes and 250 predicate cases together. The query-shape corpus below extends that
versioned contract with projection, distinct, cardinality, and scalar-reduction terminals.
The query-shape corpus and `QueryFingerprint.QueryShapeVersion` are currently `q3`; changing a
lowered shape requires an intentional version bump.

| Code | AST equivalent / fix |
| --- | --- |
| GW-LINQ-101 | Declare a computed column; expressions over columns are not portable. |
| GW-LINQ-102 | Declare a computed column; expressions over columns are not portable. |
| GW-LINQ-103 | Add `.AcceptScan(...)`. |
| GW-LINQ-104 | Activate one declared reference with `.Join(reference)`. |
| GW-LINQ-105 | Use `.LatestPer(...)` for grouped top-1. |
| GW-LINQ-106 | Declare the element set. |
| GW-LINQ-107 | Mark it `[GwQueryFragment]`. |
| GW-LINQ-108 | Use Ordinal/OrdinalIgnoreCase matching the column's folding. |
| GW-LINQ-109 | Use `DateTimeOffset.UtcNow`. |
| GW-LINQ-110 | The value has more scale/range than `decimal(10,2)`. |

| Source decision | AST equivalent / fix |
| --- | --- |
| Conjunction | `Predicate.And(terms)`, normalized by term |
| Disjunction | `Predicate.Or(terms)`, normalized by term |
| Element-set quantifier | `Predicate.ElementOf(set, values, Any|All)` |
| Equality | `Predicate.Equal(column, constant)` |
| Inequality | `Predicate.Not(Predicate.Equal(column, constant))` |
| Membership | `Predicate.In(column, values)`, with the value count retained |
| Prefix matching | `Predicate.StartsWith(column, prefix)` |
| Range | `Predicate.Range(column, lower?, upper?)`, retaining bound inclusivity |
| Substring matching | `Predicate.Substring(column, needle, Contains|EndsWith)` |
| Computed/member expression | GW-LINQ-101: declare a computed column; expressions over columns are not portable |
| Arithmetic expression | GW-LINQ-102: declare a computed column; expressions over columns are not portable |
| Column-to-column comparison | GW-LINQ-103: add `.AcceptScan(...)` |
| Undeclared cross-table expression | GW-LINQ-104: activate one declared reference with `.Join(reference)` |
| Grouped top-one | GW-LINQ-105: use `.LatestPer(...)` for grouped top-1 |
| Unsupported element-set predicate | GW-LINQ-106: declare the element set |
| Opaque helper | GW-LINQ-107: mark it `[GwQueryFragment]` |
| Unpinned string comparison | GW-LINQ-108: use Ordinal/OrdinalIgnoreCase matching the column's folding |
| Non-UTC clock | GW-LINQ-109: use `DateTimeOffset.UtcNow` |
| Decimal precision/scale | GW-LINQ-110: the value has more scale/range than `decimal(10,2)` |

| Query shape | Lowered contract |
| --- | --- |
| `Select` over mapped columns | `Projection.ColumnsOnly(...)` |
| `Distinct` | `QueryRequest.Distinct = true`; duplicates are removed before paging/cardinality |
| `First` / `FirstOrDefault` | `ResultShape.First` / `ResultShape.FirstOrDefault`, limit 1, explicit order required |
| `Single` / `SingleOrDefault` | `ResultShape.Single` / `ResultShape.SingleOrDefault`, limit 2, over-one detection |
| `Sum(selector)` | `ResultShape.Sum`, over a mapped `Int32`, `Int64`, or `Decimal` column; results are nullable `Int64` or `Decimal` |
| `Min(selector)` / `Max(selector)` | `ResultShape.Min` / `ResultShape.Max`, over closed overloads for mapped orderable columns; nulls are ignored and an empty/all-null input yields null |

Every async reduction convenience (`SumAsync`, `MinAsync`, and `MaxAsync`) dispatches through
`IGwQueryExecutor.ReduceAsync`. Providers must return one native scalar row; the runtime never
materializes source rows and reduces them on the client. Input `Distinct`, ordering, and paging are
applied natively before the aggregate, and integer sums are returned as nullable `Int64` values.

The query-shape diagnostics are versioned separately from the predicate corpus:

| Code | Query-shape refusal | Fix |
| --- | --- | --- |
| GW-LINQ-111 | First/FirstOrDefault without deterministic order | Add an explicit `OrderBy` before `First` or `FirstOrDefault` |
| GW-LINQ-112 | Sum/Min/Max selector is not a mapped portable column, or follows `Select` | Reduce a mapped source column before projection |
| GW-LINQ-113 | `Skip` has no bounded `Take` | Add a bounded `Take`; offset-only pages are not portable |

Closed terms are read from constants and closure fields without compiling an expression per query
call. Unsupported expression nodes are rejected rather than evaluated on the client.

Declared string sets also support `Any(value => value.Contains(needle, comparison))` and
`Any(value => value.EndsWith(needle, comparison))`, lowering to `Predicate.ElementSubstring`.
Use an explicit `StringComparison.Ordinal` or `StringComparison.OrdinalIgnoreCase`; the latter
lowers to the Unicode policy and is refused for a raw array unless a persisted per-element search
key is introduced. `All` and culture-sensitive overloads remain refused. Construct the AST with
`QueryStringComparisonPolicy.AsciiIgnoreCase` when ASCII-only folding is the intended contract.

Prefix matching is index-coverable when its comparison policy matches the declared column:
ordinal prefixes use an exact range on the base column, while ASCII and Unicode folded prefixes
use the schema-owned versioned search-key column. Culture/ICU policies and forged policy metadata
are refused before provider I/O.
