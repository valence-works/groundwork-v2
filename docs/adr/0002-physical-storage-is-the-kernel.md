# ADR 0002: Make physical typed storage the kernel

- Status: Accepted
- Date: 2026-08-15
- Supersedes: Groundwork v1 ADR 0003

## Context

Groundwork v1 selected three physical storage forms and retained a canonical
JSON payload as the authoritative representation. That design prevented a
columns-only table or collection from being a first-class declaration.

The v2 program starts from a smaller promise: declare typed columns, a native
key, optional indexes, and schema metadata once, then map that declaration to
each provider.

## Decision

The kernel models one logical typed storage unit. It has columns, a key,
optional derived columns and indexes, scope, concurrency, timestamps, and a
schema version. It has no storage-form discriminator, envelope, payload path,
canonical JSON authority, serialization policy, or lifecycle policy.

Providers map the same declaration to their native substrate:

- SQLite, PostgreSQL, and SQL Server use typed table columns and native keys.
- MongoDB uses collection fields and a native identity mapping.

A document contract family is optional. It may add ordinary columns or bindings
for a serialized body, headers, or derived values, but those are family-level
semantics expressed through the public kernel.

## Consequences

- A plain columns-only declaration is complete without a document payload.
- Native keys and provider mappings are part of conformance evidence.
- Serialization is no longer an implicit source of truth for all workloads.
- Provider differences are reported through mappings and capabilities rather
  than leaked into the logical declaration.
