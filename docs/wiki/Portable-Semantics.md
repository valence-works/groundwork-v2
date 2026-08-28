# Portable Semantics

`Groundwork.Query.Model` defines what a predicate *means*, independently of any database. This page
is the contract. If you internalise one thing: **a predicate is either true or false. There is no
`UNKNOWN`.**

## Two-valued logic

SQL's three-valued logic is the classic source of "the same query returns different rows on a
different database". Groundwork removes it from the model entirely:

- A predicate is **true or false** for a row.
- Missing values are treated as **explicit `null`** — null and missing are equivalent.
- **`Not(p)` is the exact complement of `p`.** No exceptions, no null-swallowing.

```csharp
// status is nullable.
Predicate.Not(new Predicate.Equal(status, QueryConstant.Of(status, "closed")))
// ⇒ TRUE for rows where status IS NULL.
// In raw SQL, `NOT (status = 'closed')` would be UNKNOWN and exclude them.
```

Unsupported *negative* forms are **refused** rather than delegated to SQL/BSON three-valued logic
(`GW-SEM-NOT-001`). Refusing is the point: you get a diagnostic instead of a portability bug.

## Validating and evaluating

```csharp
using Groundwork.Query.Model;

var refusals = PortableQuerySemantics.Validate(request).Refusals;
foreach (var r in refusals)
    Console.WriteLine($"{r.Code} at {r.Path}: {r.Message}");

// Pure two-valued oracle — useful in tests, and defined even for shapes a provider must refuse.
bool matched = PortableQuerySemantics.Evaluate(predicate, row);
```

Only compile shapes for which `Validate` returns **no** refusals.

## The rules

### Equality and membership
- Exact and typed. A constant's type must match the column's type exactly (`GW-SEM-TYPE-005`).
- `In([])` is **false** (and normalizes to match-none).
- A null member matches only a null value.
- `Not(Equal(column, nonNullValue))` **includes** null.
- `In` values are capped at **1,000** by default (`GW-QUERY-015`).

### Ranges, substrings, column comparisons
- Never match a null value.
- Supported *positive* forms are total when evaluated; unsupported *negative* forms are refused.
- A range with a null operand is refused (`GW-SEM-NULL-001`) — use `Equal(column, null)` or its
  total complement.
- Column-to-column comparison requires an exact matching type (`GW-SEM-TYPE-001`) and columns from
  the same table. Column comparison also requires an accepted scan — see
  **[Query Coverage & Indexes](Query-Coverage-and-Indexes)**.

### Element sets
- `ElementOf(Any)` is **false** for an empty owner, so its complement is **true** for an empty owner.
- Element sets must declare an **exact element type** (`GW-SEM-TYPE-007`). Legacy untyped set
  references are refused rather than allowing provider-specific JSON/BSON element coercion.

### Text
- Text accepted for provider planning is explicitly **`Ordinal`**.
- Ordinal `StartsWith` lowers to an exact `[prefix, successor)` range on the base column. **No derived
  column, no `LIKE` pattern, and index-coverable.**
- `UnicodeOrdinalIgnoreCase` and `AsciiIgnoreCase` prefix matching require the schema's **versioned
  persisted search-key mapping**; the `ColumnRef` policy must match that mapping exactly or rendering
  fails with `GW-QUERY-031`.
- Culture, ICU, accent, and implicit Unicode-normalization predicate semantics are refused
  (`GW-SEM-TEXT-001`). Locale ordering is available only through a declared, versioned ICU sort-key
  projection whose encoded text is ordered ordinally by providers.
- **No renderer emits database-side case folding.**
- Malformed UTF-16 is rejected at binding.
- Evaluating a refused folded policy stays deterministic and never invokes runtime
  `OrdinalIgnoreCase` behavior.

### Numbers
- Only exact `Int32`, `Int64`, and declared `Decimal(18,4)` are portable.
- **No numeric coercion or rounding is performed.**
- Scalar `Sum` is limited to mapped `Int32`, `Int64`, and `Decimal` columns. Integer sums return
  `Int64`; decimal sums retain `Decimal`; reductions return nullable results and null when no
  non-null input exists. `Min` and `Max` accept mapped orderable columns, ignore nulls, and return
  null when no non-null value exists. The
  reduction column is part of the query shape and must be present in the covering index.
- Portable `Decimal` requires exactly `decimal(18,4)` (`GW-SEM-DECIMAL-001`).
- Binary floating point is **storable but not comparable**. `PortableType.Double` can be declared,
  written, and read back bit-for-bit, and is refused in predicates and indexes (`GW-SEM-TYPE-006`),
  in ordering (`GW-SEM-ORDER-001`), and as a key, index, or grouping column (`GW-PORT-012`).
  Equality is refused along with the rest: exact binary64 equality is well defined but is the trap
  the type is known for, and admitting it would mean admitting the type into the query model and
  then refusing every other operation on it one at a time.

### Booleans
- Equality and total complements are portable.
- Direct Boolean **ranges, ordered comparisons, and ordering** are refused (`GW-SEM-ORDER-005`) until
  an explicit three-state projected key exists.

### Date and time
- Compared as **UTC ticks**, preserving all seven supported fractional-digit positions.
- Offsets are converted to UTC.
- Values arriving as `DateTime`, or with unspecified/local kind, are **not accepted by the model**.
  Use `DateTimeOffset` with an explicit offset. In LINQ, use `DateTimeOffset.UtcNow` (`GW-LINQ-109`).

### GUIDs and binary
- Guid equality *and ordering* use an **RFC-4122 / network-byte key** — ordering is therefore stable
  across providers rather than following each store's internal layout.
- Binary equality and membership use exact bytes. Binary **range, prefix, and ordering are refused**.
- Null and empty binary values remain **distinct**.

### Ordering
- Normalizes nulls-first ascending, nulls-last descending, and appends the **identity tie-break**
  before paging.
- You must supply the corresponding explicit null order. **`NullOrder.ProviderDefault` is refused**
  (`GW-SEM-ORDER-004`) — that is exactly the setting that makes two databases disagree.
- `First` and `FirstOrDefault` cardinality requests require an explicit deterministic order
  (`GW-SEM-ORDER-006`); the model bounds them to one and two rows respectively.
- Latest-per-key requires a **non-null `DateTimeOffset`** timestamp (`GW-SEM-LATEST-001`).

> The UTC-tick and network-byte-GUID decisions are the binding v2 record. They deliberately supersede
> earlier exploratory wording that proposed BSON millisecond timestamps and equality-only GUIDs.

## Refusal codes at a glance

| Code | Meaning |
| --- | --- |
| `GW-SEM-NULL-001` | Range with a null operand |
| `GW-SEM-NULL-002` | Null value for a non-nullable column |
| `GW-SEM-NOT-001` | Non-portable negation |
| `GW-SEM-TYPE-001` | Column comparison needs exact matching types |
| `GW-SEM-TYPE-002` | Double membership |
| `GW-SEM-TYPE-004` | Untyped/null constant reference |
| `GW-SEM-TYPE-005` | Constant type must match column/set type exactly |
| `GW-SEM-TYPE-006` | Binary floating point in a predicate or index — the column is declarable and storable, only uncomparable |
| `GW-SEM-TYPE-007` | Element set without an exact declared element type |
| `GW-SEM-TEXT-001` | Non-portable text comparison policy |
| `GW-SEM-TEXT-003` | Non-portable substring anchor |
| `GW-SEM-DECIMAL-001` | Decimal is not `decimal(18,4)` |
| `GW-SEM-ORDER-001` | Ordering a non-orderable type (binary/double) |
| `GW-SEM-ORDER-002` | Ordering comparison on a non-orderable type |
| `GW-SEM-ORDER-003` | Range ordering on a non-orderable type |
| `GW-SEM-ORDER-004` | `ProviderDefault` null ordering |
| `GW-SEM-ORDER-005` | Boolean ordering |
| `GW-SEM-ORDER-006` | First/FirstOrDefault without an explicit deterministic order |
| `GW-SEM-LATEST-001` | Latest-per-key needs a non-null `DateTimeOffset` |
| `GW-SEM-UNKNOWN-001` | Unrecognised predicate node |

## Why this is stricter than you expect

Every one of these refusals exists because the alternative is a query that returns different rows on
PostgreSQL than on SQL Server — usually discovered in production, months later, by a customer.
Groundwork makes that a compile-time or admission-time failure with a named fix.

## Next

- **[Querying](Querying)** — building requests, LINQ, paging
- **[Diagnostics Reference](Diagnostics-Reference)** — every code in one place
