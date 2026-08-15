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

| Decision | Corpus forms |
| --- | --- |
| Accepted ASTs | And(ElementOf,Equal), And(ElementOf,Not(Equal)), And(ElementOf,Range), And(Equal,Equal), And(Equal,Equal,Equal), And(Equal,Equal,Not(Equal)), And(Equal,Equal,Range), And(Equal,In), And(Equal,Not(Equal)), And(Equal,Or(Equal,Equal)), And(Equal,Range), And(Equal,StartsWith), And(Equal,Substring), And(In,Not(Equal)), And(In,Range), And(Not(Equal),Not(Equal)), And(Not(Equal),Or(Equal,Equal)), And(Not(Equal),Range), And(Not(Equal),StartsWith), And(Not(Equal),Substring), And(Or(Equal,Equal),Range), And(Range,Range), And(Range,StartsWith), And(Range,Substring), ElementOf, Equal, In, Not(Equal), Or(Equal,Equal), Range, StartsWith, Substring |
| Rejected diagnostics | GW-LINQ-101, GW-LINQ-102, GW-LINQ-103, GW-LINQ-104, GW-LINQ-105, GW-LINQ-106, GW-LINQ-107, GW-LINQ-108, GW-LINQ-109, GW-LINQ-110 |

Closed terms are read from constants and closure fields without compiling an expression per query
call. Unsupported expression nodes are rejected rather than evaluated on the client.
