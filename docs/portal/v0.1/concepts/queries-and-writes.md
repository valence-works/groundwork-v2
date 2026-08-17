---
title: Queries and writes
---

# Queries and writes

Typed Records queries start from `RecordTable<T>.Query`. Groundwork lowers only
the documented closed expression set, binds continuation tokens to query values
and access scope, and enforces portable string and ordering semantics across
providers.

Writes return explicit outcomes such as `Inserted`, `Updated`, `NotFound`,
`UniqueViolation`, and `ConcurrencyConflict`. A zero-row native update is never
reported as success. Optimistic version preconditions are valid only for units
that opt into concurrency machinery.

Use an exact unit of work when application correctness depends on every staged
write's outcome. Capability descriptors report whether a connected deployment
can supply a contract; code must still handle a truthful refusal when its
topology cannot.

Read the detailed [query contract](../../../v2/query-rendering.md)
and [write contract](../../../v2/w1-write-path.md).
