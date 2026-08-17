---
title: Storage units and access
---

# Storage units and access

A storage unit declares its logical name, physical name, columns, key, indexes,
scope policy, and optional behaviors such as optimistic concurrency,
idempotency, retention, or aggregation profiles. Providers validate the same
portable declaration before schema or data I/O.

Access is explicit:

- `StorageAccess.Global` opens a globally scoped unit.
- `StorageAccess.Scoped(scope)` opens one tenant partition of a scoped unit.
- `StorageAccess.PrivilegedAcrossScopes(audit)` opens a query-only audited view
  over all partitions of a scoped unit.

Global access cannot open a scoped unit. Scoped access cannot open a global
unit. Cross-scope access refuses point reads and writes because a key is not
unambiguous without its scope.

See [audited privileged access](../../../v2/privileged-cross-scope.md)
for the observer and purpose requirements.
