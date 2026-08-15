# Declared aggregation profiles

`StorageUnit.AggregationProfiles` is a closed aggregation surface. A profile fixes the group-by
columns, output aliases, aggregate kinds, and resource bounds at declaration time. A caller selects
the profile by name:

```csharp
var result = session.Aggregate(new AggregationQuery("diagnostic-summary")
{
    PostPredicate = new AggregationPredicate.Comparison(
        "total", AggregationPredicateOperator.RangeInclusive, [1L, 1000L])
});
```

The predicate is evaluated only against declared output aliases. Every operator must be listed in
the profile's `AllowedPredicates`; an undeclared alias or operator is refused. Callers cannot
submit an aggregate expression, change a reducer, or raise a profile's budgets.

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
