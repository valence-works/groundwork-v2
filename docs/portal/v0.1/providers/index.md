---
title: Choose a provider
---

# Choose a provider

All four native providers execute the portable contract, but their supported
deployment topologies differ.

| Provider | Preview baseline | Transaction note |
| --- | --- | --- |
| SQLite | File-backed or shared in-memory SQLite | One connection owns session lifetimes |
| PostgreSQL | PostgreSQL 17 compatible | Native relational transaction |
| SQL Server | SQL Server 2022 compatible | Native relational transaction |
| MongoDB | Replica set or sharded cluster | Transactions required for atomic multi-write contracts |

Conformance-passing is not a production support promise. Review the current
[support matrix](../../../v2/support-matrix.md) and run
the public conformance suite against the exact topology you will deploy.
