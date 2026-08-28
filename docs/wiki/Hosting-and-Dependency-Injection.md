# Hosting & Dependency Injection

`Groundwork.Extensions.DependencyInjection` wires Groundwork into a .NET host: named connections,
the connection lifetime, startup schema admission, and a health check.

```xml
<PackageReference Include="Groundwork.Extensions.DependencyInjection" Version="0.2.0-preview.1" />
<PackageReference Include="Groundwork.Sqlite" Version="0.2.0-preview.1" />
```

```csharp
builder.Services.AddGroundwork().AddConnection(options => options
    .UseProvider(new SqliteProviderFactory(), "Data Source=app.db")
    .AddUnits(Orders.Definition, Notes)
    .RequireCapabilities(BatchWriteCapabilities.StagedUnitOfWork.Value));

builder.Services.AddHealthChecks().AddGroundwork();
```

That is the whole registration. Everything below explains why it has the shape it has.

---

## The lifetime model

This is the part that catches people, so it comes first.

| Object | Lifetime | Registered as |
| --- | --- | --- |
| `IStorageProviderConnection` | **Process singleton**, one per database | `AddKeyedSingleton` per name, plus an unkeyed alias for the default connection |
| `IGroundworkStorage` | **Scoped** — one per request | `AddKeyedScoped` per name, plus an unkeyed alias for the default connection |
| `IStorageSession` | Scope-owned when opened from `IGroundworkStorage` | Opened from `IGroundworkStorage`, released when the scope is disposed |
| `IUnitOfWork` | Owned by the scope until it becomes terminal | Opened from `IGroundworkStorage`, never registered |

A connection owns provider resources for its whole life — pools, transactions, and for SQLite the
single `${database}.schema.lock` file handle. **One connection per database per process.** The
per-request connection that most data-access libraries encourage is not merely wasteful here; on
SQLite the second connection blocks on a lock the first will not release until the process exits.

```csharp
app.MapGet("/orders/{id}", (string id, IGroundworkStorage storage) =>
{
    var session = storage.OpenSession(Orders.Definition, StorageAccess.Global);
    return session.Read(new StorageKey(new Dictionary<string, object?> { ["id"] = id }));
});

app.MapPost("/orders", (Order order, IGroundworkStorage storage) =>
{
    // Owned by the request scope: if the request throws before commit, the scope disposes the unit
    // of work, which rolls it back. Nothing to remember, nothing to leak.
    var work = storage.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, Orders.Definition);
    work.Stage(RowWrite.Insert(Orders.Definition, values));
    return work.CommitWithOutcomes();
});
```

A unit of work stops being owned as soon as it commits or rolls back, so a scope that outlives one
request — a `BackgroundService` holding a single scope for the life of the process — does not
accumulate them. Only units that never reached a terminal call are still there when the scope ends,
and those are exactly the ones that need rolling back.

### The model is enforced, not suggested

1. **`AddConnection` has no lifetime setting.** The wrong combination cannot be expressed through the
   supported API at all.
2. **A hand-written non-singleton registration is refused** the first time a connection is opened,
   with `GW-HOST-001`, naming the offending service type and the fix:

   ```csharp
   services.AddScoped<IStorageProviderConnection>(_ => factory.Create(connectionString));
   // GW-HOST-001: A Groundwork storage connection is registered with a non-singleton lifetime …
   ```

   The check reads the live `IServiceCollection`, so a registration added *after* `AddGroundwork()`
   is caught too — at startup, not on the request that deadlocks.
3. **The provider refuses the second connection** even if you bypass the container entirely. SQLite
   raises `GW-SQLITE-LIFETIME-001` naming the file, the one-connection-per-file rule, and what to do
   in hosts, tests, and tools.

### Session release

`IGroundworkStorage.OpenSession` opens a provider-owned session for the current scope and tracks it
until the scope ends. The scope releases every session during synchronous or asynchronous disposal,
so a request-per-scope session does not accumulate one provider handle per request. A session can
also be used as `IOwnedStorageSession` when a caller needs to release it before the scope ends.

The lower-level `IStorageProviderConnection.OpenSession` remains a non-owning view for callers whose
connection lifetime is the natural bound. For concurrent or per-call work opened directly from a
connection, use `OpenOwnedSession` and dispose the returned session when the operation completes.

### Tests and tools

- **Tests:** give each test its own SQLite file, or use `Data Source=:memory:`. Two tests sharing one
  file at the same time is the same refusal as two processes.
- **The `groundwork` CLI:** it opens the store through the same lock. Do not run `groundwork apply`
  against a database a running application already has open.

---

## Named connections and keyed services

```csharp
builder.Services.AddGroundwork()
    .AddConnection("primary",   options => options.UseProvider(new PostgreSqlProviderFactory(), primary))
    .AddConnection("reporting", options => options.UseProvider(new PostgreSqlProviderFactory(), replica));
```

```csharp
app.MapGet("/reports", ([FromKeyedServices("reporting")] IGroundworkStorage storage) => …);
```

`IGroundworkConnections` enumerates the registered names and resolves any of them. A name that was
never registered refuses with `GW-HOST-003` and lists the ones that were.

`AddConnection` is the named options pattern, so configuration binding works as usual:

```csharp
builder.Services.Configure<GroundworkConnectionOptions>(
    "primary", builder.Configuration.GetSection("Groundwork:Primary"));
```

---

## Providers are not a dependency of this package

`Groundwork.Extensions.DependencyInjection` references `Groundwork.Store` and `Groundwork.Kernel`,
and no provider. Provider selection travels through `IStorageProviderFactory`, which the Store
contract already defines as the sole provider discovery seam:

```csharp
IStorageProviderFactory factory = alias switch
{
    "sqlite"     => new SqliteProviderFactory(),
    "postgresql" => new PostgreSqlProviderFactory(),
    "sqlserver"  => new SqlServerProviderFactory(),
    "mongodb"    => new MongoProviderFactory(),
    _ => throw new ArgumentOutOfRangeException(nameof(alias))
};

builder.Services.AddGroundwork().AddConnection(options => options.UseProvider(factory, connectionString));
```

Two consequences: referencing this package never drags four database drivers into an application,
and a fifth-party provider is first-class the moment it implements `IStorageProviderFactory` — there
is no second registration mechanism to also implement. An architecture test enforces the boundary.

---

## Startup admission

`AddGroundwork()` registers a hosted service that runs admission for every declared unit on every
registered connection, before the host serves anything. Admission is inspect-only by default and real:
it asks each connection for the kernel runtime-admission result for the compiled declaration, and it
checks the required capabilities against what the deployed database advertises.

| Finding | Status | Effect |
| --- | --- | --- |
| Deployed catalog matches the declaration | `Ready` | Host starts |
| Physical **index drift** against an otherwise matching applied target | `Degraded` | Host starts; dependent query shapes refuse (`GW-RUNTIME-002`) |
| The declaration differs from the applied target (including a changed or newly declared index) | `Blocked` | Startup refuses with `GW-HOST-005` |
| A unit, **column**, or derived column is missing | `Blocked` | Startup refuses with `GW-HOST-005` |
| A required capability is not advertised | `Blocked` | Startup refuses with `GW-HOST-006` |
| Admission could not run at all | `Failed` | Startup refuses with `GW-HOST-005` |

The split is the same one [Schema Management](Schema-Management) documents: a column mismatch means
data cannot be read correctly, while physical index drift against an otherwise matching applied
target means *some queries* are no longer safe. A declaration change, including adding or changing
an index, changes the target fingerprint and must be applied before startup. The hosting layer
reports the provider seam's result; it does not reclassify the public `SchemaDiff`.

A blocked refusal names the units and the command that fixes them:

```text
GW-HOST-005: Groundwork connection 'Default' has physical schema work pending …
orders: Blocked — CreateStorageUnit orders, AddColumn id …
Apply it from the deployment step with `groundwork apply --schema groundwork.schema.json
--provider <alias> --safe`; runtime is inspect-only by default.
```

The public `ISchemaCoordinator.InspectRuntimeAdmission` method exposes this same kernel result to
consumers that hold only an `IStorageProviderConnection`, including the result of safe-plan
authorization when auto-apply is enabled.

### Development auto-apply

```csharp
options.AutoApplyOnStartup = builder.Environment.IsDevelopment() && configuration.GetValue<bool>("DevelopmentApplySchema");
```

Off by default. When on, the provider delegates to the kernel's `PhysicalSchemaPlanProtection`; only
plans the kernel considers safe are applied at startup. Destructive or semantic work is refused with
its authorization details and belongs to the `groundwork` CLI. This exists so `dotnet run` and
integration tests can stand a database up. **Production physical schema belongs to the `groundwork`
CLI**, which requires explicit authorization for destructive and semantic work.

---

## Health checks

```csharp
builder.Services.AddHealthChecks().AddGroundwork();
app.MapHealthChecks("/health");
```

The check reports the startup admission verdict and the live capability advertisement per named
connection — not a synthetic ping, because a ping cannot tell you whether the deployed catalog still
matches the declaration this build was compiled against.

| Admission | Health |
| --- | --- |
| `Ready` | `Healthy` |
| `Degraded` | `Degraded` |
| `Blocked` / `Failed` | `Unhealthy` |

`HealthCheckResult.Data` carries, per connection: `status`, `capabilities`, `missingCapabilities`,
`units` (each with its per-unit verdict and pending changes), and `failure`.

---

## Diagnostics

| Code | Meaning |
| --- | --- |
| `GW-HOST-001` | A storage connection is registered with a non-singleton lifetime |
| `GW-HOST-002` | Two connections registered under the same name |
| `GW-HOST-003` | A connection name was requested that was never registered |
| `GW-HOST-004` | A registered connection has no provider factory or no connection string |
| `GW-HOST-005` | Startup admission found pending physical schema work, or could not run |
| `GW-HOST-006` | The deployed database does not advertise a required capability |

See **[Diagnostics Reference](Diagnostics-Reference)**.

---

## A complete application

`samples/Groundwork.Samples.Api` is a runnable minimal API covering declaration, schema deployment,
typed CRUD, a covered query with paging, a scope-owned unit of work, optimistic concurrency, and
tenant scopes, switchable across all four providers from configuration.

## Next

- **[Core Concepts](Core-Concepts)** — connection, session, unit of work
- **[Schema Management](Schema-Management)** — the deployment flow admission expects
- **[Providers](Providers)** — the SQLite schema lock, and what each provider needs
