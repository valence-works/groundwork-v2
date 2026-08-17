---
title: PostgreSQL provider
---

# PostgreSQL provider

Install `Groundwork.PostgreSql` and create a connection with
`PostgreSqlProviderFactory`. The 0.1 preview conformance baseline targets a
PostgreSQL 17-compatible deployment.

Groundwork uses native typed columns, indexes, identity sequences, transactions,
and query plans. Supply the connection string through the application's secret
provider; do not place credentials in declarations or generated schema files.

Before release, run the public provider conformance suite and explain-plan
assertions against the exact PostgreSQL topology and statistics profile used by
the application.
