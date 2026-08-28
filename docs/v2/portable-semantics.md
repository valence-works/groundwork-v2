# Portable query semantics (v2)

`Groundwork.Query.Model` is the provider-neutral contract used before a query is
sent to a database. A predicate is either true or false for a row; there is no
`UNKNOWN`. Missing values are treated as explicit `null`, and `Not(p)` is the
exact complement of `p`.

The public entry points are `PortableQuerySemantics.Validate` and
`PortableQuerySemantics.Evaluate`. Validation returns immutable `Refusals` of
`PortableSemanticRefusal` values. A
refusal always has a `GW-SEM-*` code, names the offending path, and names a
portable alternative. `Evaluate` is the pure two-valued AST oracle, including
for a shape that a provider plan must refuse; callers compile only shapes for
which `Validate` returns no refusals.

## Normalized behavior

- Equality and membership are exact and typed. `In([])` is false. A null member
  matches only a null value; null and missing are equivalent. `Not(Equal(...))`
  includes null when the compared value is non-null.
- Ranges, substring search, and column relations never match a null value.
  Their supported positive forms are total when evaluated; unsupported
  negative forms are refused rather than delegated to SQL/BSON three-valued
  logic. `ElementOf(Any)` is false for an empty owner, so its complement is
  true for an empty owner. Element sets declare an exact element type; legacy
  untyped set references are refused rather than allowing provider-specific
  JSON/BSON element coercion.
- Text accepted for provider planning is explicitly `Ordinal`. Ordinal
  `StartsWith` is lowered to an exact `[prefix, successor)` range on the base
  column and does not create a derived column. `UnicodeOrdinalIgnoreCase` and
  `AsciiIgnoreCase` prefix matching require the schema's versioned persisted
  search-key mapping; the `ColumnRef` policy must match that mapping exactly or
  rendering fails with `GW-QUERY-031`. Culture, ICU, accent, and implicit
  Unicode-normalization predicate semantics are refused. Locale-aware ordering
  is available only through a declared, versioned ICU sort-key projection; the
  provider still compares its encoded text ordinally. Malformed UTF-16 is
  rejected at binding. Evaluating a refused folded policy remains deterministic
  and never invokes runtime `OrdinalIgnoreCase` behavior.
- Only exact `Int32`, `Int64`, and declared `Decimal(18,4)` values are portable
  for comparison. No numeric coercion or rounding is performed. Binary floating
  point is storable but not comparable: a `Double` column is declarable and
  round-trips bit-for-bit on every supported store, and is refused for
  predicates, ordering, and indexes.
- Boolean equality and total complements are portable. Direct Boolean ranges,
  ordered column comparisons, and ordering require a future explicit three-state
  projected key and are refused until that projection is present.
- Date/time values are compared as UTC ticks, preserving all seven supported
  fractional-digit positions. Offsets are converted to UTC; values arriving as
  `DateTime` or with an unspecified/local kind are not accepted by the model.
- Guid equality and ordering use an RFC-4122/network-byte key. Binary equality
  and membership use exact bytes; binary range, prefix, and ordering are
  refused. Null and empty binary values remain distinct.
- A `Distinct` request applies to its public projection before paging and cardinality checks.
  Coverage requires every projected column in the candidate index and an equality/range predicate
  bound, because the provider read is unpaged; otherwise the caller must record an explicit live
  `AcceptedScan`. A bounded `Take` or cardinality terminal alone is not coverage.
- Ordering normalizes nulls-first ascending and nulls-last descending and
  appends the identity tie-break before paging. Callers must provide the
  corresponding explicit null order; `ProviderDefault` is refused. Guid
  ordering uses the same network-byte key; binary ordering is refused.
- `First` and `FirstOrDefault` cardinality queries require a caller-supplied deterministic order. The
  model refuses an un-ordered cardinality request with `GW-SEM-ORDER-006`; `First` reads at most
  one row, while `Single` reads at most two so an over-one result can be detected.

The accepted #230 gate records the UTC-tick and network-GUID decisions. They
intentionally supersede the earlier exploratory wording that proposed BSON
millisecond timestamps and equality-only GUIDs; the differential gate is the
binding v2 decision record.
