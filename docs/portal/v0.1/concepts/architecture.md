---
title: Architecture and packages
---

# Architecture and packages

Groundwork uses small packages with one-way dependencies:

| Layer | Packages | Responsibility |
| --- | --- | --- |
| Model | `Groundwork.Kernel`, `Groundwork.Query.Model` | Portable declarations, values, predicates, and outcomes |
| Authoring | `Groundwork.Records`, `Groundwork.Documents`, `Groundwork.Query.Linq` | Typed records/documents and closed LINQ query construction |
| Runtime | `Groundwork.Store`, `Groundwork.Records.Store` | Connections, sessions, schema, writes, queries, and units of work |
| Providers | `Groundwork.Sqlite`, `Groundwork.PostgreSql`, `Groundwork.SqlServer`, `Groundwork.MongoDb` | Native execution and schema inspection |
| Delivery | `Groundwork.Tool`, `Groundwork.SchemaTool.MSBuild` | Explicit schema planning and build-time verification |
| Provider authoring | `Groundwork.Testing` | Public conformance contracts and deterministic reference provider |

Application code should normally depend on a typed authoring package, its
Store bridge, and one provider. Provider-neutral domain code should not depend
on a native provider package.
