---
title: Groundwork 0.1 preview
description: Guides and reference for the Groundwork 0.1 preview line.
---

# Groundwork 0.1 preview

Use this documentation for `0.1.x` preview packages. Groundwork separates the
portable storage contract from native provider execution:

1. Declare typed storage units, keys, indexes, scope, and concurrency.
2. Apply or verify the declaration through a provider connection.
3. Open a session with explicit global, scoped, or audited cross-scope access.
4. Execute typed writes, queries, aggregations, or exact units of work.

Start with the [SQLite quickstart](getting-started/quickstart.md), then read the
[provider matrix](../../v2/support-matrix.md) before
choosing a production topology.

> [!IMPORTANT]
> These packages are previews. Pin an exact version from Feedz; do not use a
> floating prerelease in a production application.
