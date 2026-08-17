---
title: Schema deployment
---

# Schema deployment

Treat schema work as an explicit deployment operation:

1. Build the same portable declaration used by the application.
2. Ask the provider for a diff or generate a plan with `Groundwork.Tool`.
3. Review destructive or provider-specific changes.
4. Apply with explicit authorization.
5. Verify that the resulting diff is empty before starting traffic.

Runtime startup should verify compatible schema; it should not silently repair
drift. Derived search-key algorithm changes require a rebuild because old and
new values cannot safely share one index.

See the [schema tool reference](../../../schema-tool.md)
for command syntax and exit behavior.
