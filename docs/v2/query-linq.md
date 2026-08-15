# Closed LINQ front-end

`Groundwork.Query.Linq` is a convenience front-end over `Groundwork.Query.Model`. Its
`IGwQueryable<T>` is intentionally closed and does not implement `System.Linq.IQueryable`.
`ToQueryRequest()` is the provider-neutral boundary; providers execute that request through their
existing session APIs.

The conformance corpus in `tests/Groundwork.Query.Linq.Tests` is the source of truth for this
surface. The following diagnostic table is generated deterministically from the corpus decision
rows and checked byte-for-byte by the corpus test; the test locks the ten codes and 250 cases
together:

| Code | AST equivalent / fix |
| --- | --- |
| GW-LINQ-101 | Declare a computed column; expressions over columns are not portable. |
| GW-LINQ-102 | Declare a computed column; expressions over columns are not portable. |
| GW-LINQ-103 | Add `.AcceptScan(...)`. |
| GW-LINQ-104 | V2 has no joins; use a declared element set or two queries. |
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
| Cross-table expression | GW-LINQ-104: v2 has no joins; use a declared element set or two queries |
| Grouped top-one | GW-LINQ-105: use `.LatestPer(...)` for grouped top-1 |
| Unsupported element-set predicate | GW-LINQ-106: declare the element set |
| Opaque helper | GW-LINQ-107: mark it `[GwQueryFragment]` |
| Unpinned string comparison | GW-LINQ-108: use Ordinal/OrdinalIgnoreCase matching the column's folding |
| Non-UTC clock | GW-LINQ-109: use `DateTimeOffset.UtcNow` |
| Decimal precision/scale | GW-LINQ-110: the value has more scale/range than `decimal(10,2)` |

Closed terms are read from constants and closure fields without compiling an expression per query
call. Unsupported expression nodes are rejected rather than evaluated on the client.
