---
title: SQLite provider
---

# SQLite provider

Install `Groundwork.Sqlite`, then create a connection with
`SqliteProviderFactory`. The connection owns every schema coordinator and
session opened from it. Keep it alive for the full operation scope.

File-backed databases are the simplest production-like setup. For shared
in-memory SQLite, keep the owning connection open; closing the last connection
removes the database.

SQLite is the recommended first provider for local development and compiled
examples. See the [detailed SQLite contract](../../../sqlite-provider.md).
