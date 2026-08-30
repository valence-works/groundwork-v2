# Production API boundary

Groundwork runtime contracts live in `Groundwork.Store`. This package owns the provider-neutral
connection, schema, session, write, query, and staged unit-of-work contracts, together with the
general batching, retention, idempotency, and aggregation execution behaviors used by providers.
It depends only on `Groundwork.Kernel` and `Groundwork.Query.Model`.

Provider packages reference `Groundwork.Store` directly. `Groundwork.Testing` is intentionally a
consumer of Store: it contains conformance scenarios, the in-memory provider, concurrency probes,
and test fixtures. A runtime provider package must never require `Groundwork.Testing` merely to
open a connection or execute a write.

The typed Records bridge is in `Groundwork.Records.Store`. Keeping it as a production integration
package preserves the small, provider-neutral `Groundwork.Records` package boundary while giving
ordinary consumers one obvious path:

```csharp
var table = RecordTable.For<Customer>("customers")
    .Key(customer => customer.Id)
    .Index("by-email", customer => customer.Email)
    .Build();

using var connection = new SqliteProviderFactory().Create("Data Source=customers.db");
connection.Schema.Apply(table.Definition);
var records = table.Open(connection);
records.Insert(new Customer("a", "a@example.com"));
```

## Ownership and lifetime

`IStorageProviderConnection` owns the provider resources it opens, including non-owning sessions
opened directly from the connection. Keep the connection alive for every session, schema, catalog,
or query operation that uses it; disposing the connection releases its resources and invalidates its
non-owning sessions. When a caller needs a shorter lifetime, `OpenOwnedSession` returns an
`IOwnedStorageSession` whose resources are released by synchronous or asynchronous disposal.

`IStorageSession` is the common operation surface. A session opened from a connection is a
non-owning view and is valid only while that connection is alive; a session opened through
`OpenOwnedSession` also implements `IOwnedStorageSession` and releases its provider resources on
disposal. A session opened from an `IUnitOfWork` is additionally bounded by that unit of work and
becomes invalid when the unit reaches a terminal state or is disposed. The DI `IGroundworkStorage`
scope uses owned sessions and releases them when the scope ends.

Every session also captures the declaration published by its provider connection. If that same
connection successfully applies a different declaration for the unit, an earlier direct, owned, or
unit-of-work session throws `StaleStorageSessionException` (`GW-RUNTIME-005`) before its next
provider command. Reopen the session after applying schema. A schema change made through another
process or connection is detected by that connection's normal admission boundary, not by polling
already-open sessions.

`IUnitOfWork` owns its transaction, staged sessions, and their provider resources. Commit and
rollback are terminal operations; disposing a non-terminal unit rolls it back. Dispose the unit of
work after the terminal operation and do not retain or use sessions obtained from it afterward.
`RecordTableStoreUnitOfWork<T>` follows the same rule for typed Records writes.

`RecordTableStoreUnitOfWork<T>` provides the typed staged write path. Documents can add a sibling
integration over the same `Groundwork.Store` connection contract without changing this API.

`Groundwork.Diagnostics` owns opt-in native explain-plan artifact assertions. The environment
variable and artifact behavior are diagnostic/test concerns and are deliberately not part of the
core Store contract.

## Clean-room public API approval

The public API dogfood journey is built outside the repository source graph from the packed
artifacts. It exercises SQLite schema apply/verify, typed Records writes and queries, Documents,
batch outcomes, coverage enforcement, concurrency diagnostics, and public schema-drift admission.
The compile-time fixture and `public-api.approved.txt` manifest cover the callable surface used by
the consumer; `friction-log.md` records the remaining ergonomic decisions. The separate
`eng/public-api-v1-net8.0.txt` and `eng/public-api-v1-net10.0.txt` manifests exhaustively freeze every
exported type and member. The clean-room proof answers “can a real package-only consumer use this
surface?”; the frozen manifests answer “did any exported contract change?” Neither proof substitutes
for the other.

Run the same proof used by CI:

```sh
dotnet pack Groundwork.slnx --configuration Release --output artifacts/acceptance-packages
dotnet test tests/Groundwork.PublicApi.Acceptance.Tests --configuration Release
```

The test copies the consumer into a temporary external solution, restores only package references,
builds it twice with repository props disabled, and runs it after each build. The consumer contains
no project references, internal namespace access, reflection, friend assembly, or Testing adapter.
