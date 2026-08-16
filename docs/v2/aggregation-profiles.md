# Declared aggregation profiles

`StorageUnit.AggregationProfiles` is a closed aggregation surface. A profile fixes the group-by
columns, output aliases, aggregate kinds, and resource bounds at declaration time. A caller selects
the profile by name:

```csharp
var result = session.Aggregate(new AggregationQuery("diagnostic-summary")
{
    SourcePredicate = new Predicate.Equal(
        new ColumnRef(new TableId("diagnostics"), "service", QueryType.String),
        QueryConstant.Of("orders")),
    PostPredicate = new AggregationPredicate.Comparison(
        "total", AggregationPredicateOperator.RangeInclusive, [1L, 1000L])
});
```

`SourcePredicate` is evaluated against declared source columns before grouping and reduction.
`PostPredicate` is evaluated only against declared output aliases after reduction. When both are
present, the operation order is source filter, reduction, post filter, ordering, then the caller's
group page. Every post operator must be listed in the profile's `AllowedPredicates`; an undeclared
alias or operator is refused. Source predicates use the existing portable `Predicate` AST and are
bound to the storage unit's declared columns and literal types during admission. Callers cannot
submit an aggregate expression, change a reducer, or raise a profile's budgets.

This ordering matters when repeated records share a group. For example, a trace can contain an
older row with `service = "orders"`, `duration = 7`, and a newer row with `service = "billing"`,
`duration = 11`. A source predicate for `service == "orders"` reduces only the matching row and
returns `duration = 7`. A post predicate on the reduced `duration` sees the sum of both rows
(`18`) and cannot reproduce the source-filtered result. SQL providers emit the source predicate in
the base `WHERE`, MongoDB emits `$match` before `$group`, and the reference provider filters its
input before reduction. Query shape and bound values are included in the operation identity. The
result exposes both `ShapeFingerprint` (literal values elided) and `ValueFingerprint` (all bound
values included), so equivalent shapes with different source literals do not share an execution
identity; the same identities are available through `AggregationQueryFingerprint` for admission or
cache diagnostics. The legacy rows-only `AggregationResult` constructor leaves these nullable
fingerprints unset.

## Portable reducers

`Min` and `Max` ignore null input and return null when a group has no non-null values. `Sum` accepts
only `Int32`, `Int64`, and `Decimal`; integer sums have an `Int64` result and decimal sums retain
decimal arithmetic. An all-null sum is null. `SetUnion` accepts only strings, excludes nulls,
returns an ordinal-sorted distinct set (independent of physical column collation and without a
reserved delimiter), and refuses once `MaxValues` would be exceeded. `FirstBy` requires a non-null
orderable order column and chooses the value from the first row in that order. Equal order values
are resolved by the storage unit's declared key, so each reducer remains deterministic; profiles
may declare multiple `FirstBy` reducers with independent order columns and directions.

`MaxInputRows` and `MaxGroups` are refusal budgets, not page sizes. Exceeding either budget fails
the operation; rows or groups are never silently truncated. Native providers cap the input at one
row beyond `MaxInputRows` and run bounded group/set cardinality evidence before materializing
aggregate values.

Profiles are part of the schema subject fingerprint, so changing a reducer, alias, allowance, or
budget is schema semantic drift and cannot be hidden behind a query.
