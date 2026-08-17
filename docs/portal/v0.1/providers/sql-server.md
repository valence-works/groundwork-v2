---
title: SQL Server provider
---

# SQL Server provider

Install `Groundwork.SqlServer` and create a connection with
`SqlServerProviderFactory`. The 0.1 preview conformance baseline targets a SQL
Server 2022-compatible deployment.

The provider uses native typed tables, indexes, identity sequences,
transactions, and binary-safe portable ordering expressions. Provider-specific
identifier and key-size limits are validated before schema I/O.

See the [detailed SQL Server contract](../../../sqlserver-provider.md)
and run native conformance against the deployment used in production.
