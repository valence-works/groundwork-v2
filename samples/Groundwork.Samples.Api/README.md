# Groundwork sample API

A runnable ASP.NET Core minimal API showing one declaration serving five databases and the
deterministic in-memory reference provider: typed CRUD, a
covered query with paging, a unit of work, optimistic concurrency, tenant scopes, capability
advertisement, and the Groundwork health check.

Everything storage-related lives in two files: [`SampleStorage.cs`](SampleStorage.cs) declares the
storage, [`Program.cs`](Program.cs) registers it and exposes it.

## Run it

```bash
dotnet run --project samples/Groundwork.Samples.Api
```

The `Development` profile sets `Groundwork:DevelopmentApplySchema` to `true`, which applies the
declared schema on startup so there is a database to talk to. That switch is a **development
convenience only** — see [Schema](#schema-is-deployment-time-work) below.

```bash
curl localhost:5000/health
curl localhost:5000/capabilities

curl -X POST localhost:5000/orders -H 'content-type: application/json' \
  -d '{"id":"ord-1","customer":"ada","total":12.50}'

curl localhost:5000/orders/ord-1
curl -X PUT localhost:5000/orders/ord-1 -H 'content-type: application/json' -H 'If-Match: "1"' \
  -d '{"id":"ord-1","customer":"ada","total":13.00}'

curl 'localhost:5000/orders?customer=ada&take=20'

curl -X POST localhost:5000/tenants/acme/notes -H 'content-type: application/json' \
  -d '{"id":"note-1","body":"hello"}'
curl 'localhost:5000/tenants/acme/notes?limit=20'
```

## Switching providers

```json
{
  "Groundwork": {
    "Provider": "postgresql",
    "ConnectionString": "Host=localhost;Port=5432;Database=app;Username=app;Password=…"
  }
}
```

`Provider` accepts `sqlite`, `postgresql`, `sqlserver`, `mongodb`, `mysql`, and `inmemory`. Nothing
else in the sample changes — not the declaration, not the writes, not the queries. The factory switch lives in
`SampleStorage.ProviderFactory`; `Groundwork.Extensions.DependencyInjection` references no provider
at all, so a real application references exactly the one it deploys.

## What each endpoint demonstrates

| Endpoint | Shows |
| --- | --- |
| `GET /health` | Startup admission verdict and live capability advertisement |
| `GET /capabilities` | What the **deployed** database advertises, not a per-provider table |
| `POST /orders` | Typed insert through a unit of work owned by the request scope |
| `GET /orders/{id}` | A per-request session on the process-singleton connection |
| `PUT /orders/{id}` | Optimistic concurrency: `If-Match` carries the version you read |
| `DELETE /orders/{id}` | Delete, reported as a status |
| `POST /orders/batch` | One unit of work, many rows, one transaction, one outcome per row |
| `GET /orders?customer=` | A query covered by the declared `by_customer` index, with paging |
| `POST/GET /tenants/{tenant}/notes` | A `Scoped()` unit; keyset paging with a continuation token |

## Lifetimes

The connection is a **process singleton**. Sessions and units of work come from the scoped
`IGroundworkStorage`, which disposes any unit of work the request did not commit — so a request that
throws halfway through rolls back without a `try`/`finally`.

> **Known limitation.** A session opened from the storage connection currently keeps its provider
> connection until the storage connection is disposed
> ([#199](https://github.com/valence-works/groundwork-v2/issues/199)). The read endpoints here open a
> session per request, which is the model Groundwork documents — but under sustained load this sample
> would accumulate one open database handle per read request until the process restarts. Units of
> work are unaffected: they release their connection at commit, rollback, or dispose. Copy the write
> path into a service today; treat the read path as the documented model with a fix pending.

## Schema is deployment-time work

Runtime is inspect-only by default. Startup admission compares the deployed catalog against the
compiled declaration and refuses to start (`GW-HOST-005`) if a unit or column is missing; a missing
index degrades instead, because only dependent query shapes are unsafe.

The supported way to deploy schema is the CLI:

```bash
groundwork status --schema groundwork.schema.json --provider sqlite   # exit 2 means work is pending
groundwork apply  --schema groundwork.schema.json --provider sqlite --safe
```

`Groundwork:DevelopmentApplySchema` exists so `dotnet run` and this sample's tests can stand a
database up without a deployment step. It is off by default, applies only additive plans, logs a
warning when it fires, and is doubly gated: the environment must be `Development` **and** the switch
must be `true`. Do not turn it on anywhere else.

## Tests

`tests/Groundwork.Samples.Api.Tests` hosts this application with `WebApplicationFactory` and
exercises every endpoint above. The ordinary local run uses a temporary SQLite file; the dedicated
documentation-evidence workflow repeats the same public sample journey against all five shipped
databases and the in-memory reference provider, and refuses skipped tests.

## Read next

- [Hosting & Dependency Injection](../../docs/wiki/Hosting-and-Dependency-Injection.md)
- [Schema Management](../../docs/wiki/Schema-Management.md)
- [Core Concepts](../../docs/wiki/Core-Concepts.md)
