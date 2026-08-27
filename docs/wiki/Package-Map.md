# Package Map

Groundwork ships 24 public packages. Most applications reference **two or three**. This page tells
you which, and explains the layering — because the layering is what the whole design rests on.

## The dependency rule

Everything depends **inward**. Nothing depends outward.

```
     ┌────────────────────────────────────────────────────────┐
     │  Groundwork.Query.Model      (BCL-only, netstandard2.0)│  innermost
     │  the portable predicate/query AST                      │
     └────────────────────────────────────────────────────────┘
                              ▲
     ┌────────────────────────────────────────────────────────┐
     │  Groundwork.Kernel                                     │
     │  storage declarations, portability rules, capabilities │
     │  (its ONLY non-BCL reference is Query.Model)            │
     └────────────────────────────────────────────────────────┘
                              ▲
     ┌────────────────────────────────────────────────────────┐
     │  Groundwork.Store                                      │
     │  the runtime contract: connections, sessions,          │
     │  units of work, batching, retention, idempotency       │
     └────────────────────────────────────────────────────────┘
                    ▲                          ▲
     ┌──────────────┴────────┐   ┌─────────────┴──────────────┐
     │ providers             │   │ contract families          │
     │ Sqlite / PostgreSql   │   │ Records.Store / Documents  │
     │ SqlServer / MongoDb   │   │ (+ anything you write)     │
     └───────────────────────┘   └────────────────────────────┘
```

Two consequences you can rely on:

- **A provider never knows about `Records` or `Documents`.** Adding a contract family cannot break
  a provider, and vice versa.
- **`Groundwork.Testing` is a *consumer* of Store, not a dependency of it.** A runtime provider
  never needs `Groundwork.Testing` to open a connection or execute a write. If you ever find
  yourself referencing it in production code, something is wrong.

These boundaries are enforced by an architecture test in CI (`Groundwork.Architecture.Tests`) that
inspects compiled assembly references — not by convention. It runs once per shipped target
framework, so the layering is proved for the `net8.0` and the `net10.0` assemblies separately.

## Target frameworks

| Group | Target | Packages |
| --- | --- | --- |
| Runtime | `net8.0`, `net10.0` | The providers, `Groundwork.Store`, `Groundwork.Kernel`, `Groundwork.Records`, `Groundwork.Records.Store`, `Groundwork.Documents`, `Groundwork.Extensions.DependencyInjection`, `Groundwork.Testing`, both substrates, `Groundwork.Diagnostics`, `Groundwork.Query.Linq.Execution`, `Groundwork.Query.Linq.Sqlite` |
| Portable | `netstandard2.0` | `Groundwork.Query.Model`, `Groundwork.Query.Linq`, `Groundwork.Query.Planning`, `Groundwork.Schema`, `Groundwork.Analyzers`, `Groundwork.Schema.Generator` |
| Tooling | `net8.0`, `net10.0` | `Groundwork.Tool` — a `dotnet tool` runs on the deployment host's own runtime |
| Build task | `net10.0` | `Groundwork.SchemaTool.MSBuild` — its task loads into the SDK's MSBuild process, not your application |

The framework set lives in `Directory.Build.props` as a single property each; no project names a
literal framework of its own, and `Groundwork.Packaging.Tests` enforces that.

## Choosing your packages

### Application packages

| Package | Reference it when | Brings in |
| --- | --- | --- |
| **`Groundwork.Sqlite`** | Targeting SQLite (file or in-memory) | Store, Kernel, Query.Model, Diagnostics |
| **`Groundwork.PostgreSql`** | Targeting PostgreSQL 17+ | same + Npgsql |
| **`Groundwork.SqlServer`** | Targeting SQL Server 2022+ | same + Microsoft.Data.SqlClient |
| **`Groundwork.MongoDb`** | Targeting MongoDB (replica set / sharded) | same + MongoDB.Driver |
| **`Groundwork.Records.Store`** | You want typed rows (`RecordTable<T>`) — **this is the one to reference**, not `Groundwork.Records` | Records + Store |
| **`Groundwork.Documents`** | You want typed JSON documents with schema versioning | Records + Store |
| **`Groundwork.Extensions.DependencyInjection`** | You are hosting Groundwork in an ASP.NET Core or generic host | Store, Kernel, `Microsoft.Extensions.*` — **no provider** |

> **Common mistake:** referencing `Groundwork.Records` directly. That package deliberately has *no*
> provider dependency, so it has no `table.Open(connection)`. Reference **`Groundwork.Records.Store`**,
> which adds the production bridge. `Groundwork.Records` comes along transitively.

### Contract and model packages (usually transitive)

| Package | Owns |
| --- | --- |
| `Groundwork.Kernel` | `StorageUnit`, `ColumnDefinition`, `PortableType`, `ConcurrencyDeclaration`, `RetentionDeclaration`, `AggregationProfile`, `PortabilityValidator`, capability registry |
| `Groundwork.Store` | `IStorageProviderConnection`, `IStorageSession`, `IUnitOfWork`, `StorageAccess`, `WriteOutcome`, `RowWrite`, `BatchWriteOptions`, retention/idempotency execution |
| `Groundwork.Query.Model` | `QueryRequest`, `Predicate`, `ColumnRef`, `Paging`, `Projection`, `PortableQuerySemantics` |
| `Groundwork.Query.Linq` | The **closed** LINQ front-end (`IGwQueryable<T>`) — deliberately *not* `IQueryable` |
| `Groundwork.Query.Linq.Execution` | `GwLinqExecutor` — the one execution adapter behind the LINQ terminals, for every provider. Admits each request through `RuntimeCoverageGate` before the provider renders it. |
| `Groundwork.Query.Planning` | `QueryCoverageChecker`, `RuntimeCoverageGate`, `QueryCoverageException` |
| `Groundwork.Records` | `RecordTable<T>` mapping and the `IRecordStore` seam, with no provider dependency |

### Build-time and tooling packages

| Package | What it does |
| --- | --- |
| `Groundwork.Analyzers` | Roslyn analyzer: flags uncovered queries and portability problems **in the editor and at build**. Ships its dependencies under `analyzers/dotnet/cs`, so it has no runtime footprint. |
| `Groundwork.Schema` | The `[GwTable]`, `[GwColumn]`, `[GwKey]`, `[GwIndex]`, `[GwRetention]`, `[GwAppendIdempotency]`, `[GwRetentionIdempotency]`, `[GwAggregate]` attributes and the canonical schema model |
| `Groundwork.Schema.Generator` | Source generator producing the canonical schema + fingerprint as an assembly attribute |
| `Groundwork.Tool` | The `groundwork` CLI (`plan`/`validate`/`status`/`apply`/`schema emit`). Assembly and namespace remain `Groundwork.SchemaTool`. |
| `Groundwork.SchemaTool.MSBuild` | Fails the build on portability refusals and uncovered queries |

### Provider-author and diagnostic packages

| Package | What it does |
| --- | --- |
| `Groundwork.Testing` | Public conformance suites (`ConformanceSuite`, `ConformanceScenario`), the deterministic `InMemoryProviderFactory`, and the concurrency harness. **Not an application database.** |
| `Groundwork.Substrate.Relational` | Shared relational execution: connection ownership, schema dispatch, app locks, fencing. Implement `RelationalDialect` to add a SQL provider. |
| `Groundwork.Substrate.Mongo` | The equivalent seam for document stores |
| `Groundwork.Diagnostics` | Opt-in native explain-plan assertions (`GW_EXPLAIN_ASSERT`). Deliberately kept out of the Store contract — it is a test/diagnostic concern. |
| `Groundwork.Query.Linq.Sqlite` | The named SQLite entry point to `GwLinqExecutor`, kept separate so `Groundwork.Sqlite` does not depend on the LINQ family. It adds no behavior of its own. |

## Quick recipes

**An ASP.NET Core service on SQLite:**
```xml
<PackageReference Include="Groundwork.Sqlite" Version="0.2.0-preview.1" />
<PackageReference Include="Groundwork.Records.Store" Version="0.2.0-preview.1" />
<PackageReference Include="Groundwork.Extensions.DependencyInjection" Version="0.2.0-preview.1" />
```
See **[Hosting & Dependency Injection](Hosting-and-Dependency-Injection)**. The DI package references
no provider: you hand it the factory from whichever provider package you chose.

**Typed rows on PostgreSQL, with build-time coverage enforcement:**
```xml
<PackageReference Include="Groundwork.PostgreSql" Version="0.2.0-preview.1" />
<PackageReference Include="Groundwork.Records.Store" Version="0.2.0-preview.1" />
<PackageReference Include="Groundwork.Analyzers" Version="0.2.0-preview.1" PrivateAssets="all" />
<PackageReference Include="Groundwork.Schema.Generator" Version="0.2.0-preview.1" PrivateAssets="all" />
```

**A library that declares storage but does not pick a provider:**
```xml
<PackageReference Include="Groundwork.Records" Version="0.2.0-preview.1" />
```
Consumers supply the provider and `Groundwork.Records.Store`. Your library stays provider-neutral.

**Unit tests without a database:**
```xml
<PackageReference Include="Groundwork.Testing" Version="0.2.0-preview.1" />
```
See **[Testing](Testing)**.

## Next

- **[Core Concepts](Core-Concepts)** — what the objects in these packages actually are
- **[Hosting & Dependency Injection](Hosting-and-Dependency-Injection)** — wiring them into a host
