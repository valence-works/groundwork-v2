# Portable query semantics (v2)

`Groundwork.Query.Model` is the provider-neutral contract used before a query is
sent to a database. A predicate is either true or false for a row; there is no
`UNKNOWN`. Missing values are treated as explicit `null`, and `Not(p)` is the
exact complement of `p`.

The public entry points are `PortableQuerySemantics.Validate` and
`PortableQuerySemantics.Evaluate`. Validation returns immutable diagnostics. A
refusal always has a `GW-SEM-*` code, names the offending path, and names a
portable alternative. `Evaluate` is the pure two-valued AST oracle, including
for a shape that a provider plan must refuse; callers compile only shapes for
which `Validate` returns no diagnostics.

## Normalized behavior

- Equality and membership are exact and typed. `In([])` is false. A null member
  matches only a null value; null and missing are equivalent. `Not(Equal(...))`
  includes null when the compared value is non-null.
- Ranges, substring search, and column relations never match a null value.
  Their supported positive forms are total when evaluated; unsupported
  negative forms are refused rather than delegated to SQL/BSON three-valued
  logic. `ElementOf(Any)` is false for an empty owner, so its complement is
  true for an empty owner.
- Text is explicitly `Ordinal`, `UnicodeOrdinalIgnoreCase`, or
  `AsciiIgnoreCase`. The latter two require the versioned persisted search key
  defined by #256. Culture, ICU, accent, and implicit Unicode-normalization
  semantics are refused; malformed UTF-16 is rejected at binding.
- Only exact `Int32`, `Int64`, and declared `Decimal(18,4)` values are portable.
  No numeric coercion or rounding is performed. Binary floating point is
  refused for predicates, ordering, and indexes.
- Date/time values are compared as UTC ticks, preserving all seven supported
  fractional-digit positions. Offsets are converted to UTC; values arriving as
  `DateTime` or with an unspecified/local kind are not accepted by the model.
- Guid equality and ordering use an RFC-4122/network-byte key. Binary equality
  and membership use exact bytes; binary range, prefix, and ordering are
  refused. Null and empty binary values remain distinct.
- Ordering normalizes nulls-first ascending and nulls-last descending and
  appends the identity tie-break before paging. Guid ordering uses the same
  network-byte key; binary ordering is refused.

The accepted #230 gate records the UTC-tick and network-GUID decisions. They
intentionally supersede the earlier exploratory wording that proposed BSON
millisecond timestamps and equality-only GUIDs; the differential gate is the
binding v2 decision record.
