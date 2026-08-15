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

`IStorageProviderConnection` owns the provider resources it opens, including sessions opened
directly from the connection. Keep the connection alive for every session, schema, catalog, or
query operation that uses it; disposing the connection releases its resources and invalidates its
sessions.

`IStorageSession` is intentionally a non-disposable view over a declared storage unit. It does not
own the provider connection or its resources. A session opened from a connection is valid only
while that connection is alive. A session opened from an `IUnitOfWork` is additionally bounded by
that unit of work and becomes invalid when the unit reaches a terminal state or is disposed.

`IUnitOfWork` owns its transaction, staged sessions, and their provider resources. Commit and
rollback are terminal operations; disposing a non-terminal unit rolls it back. Dispose the unit of
work after the terminal operation and do not retain or use sessions obtained from it afterward.
`RecordTableStoreUnitOfWork<T>` follows the same rule for typed Records writes.

`RecordTableStoreUnitOfWork<T>` provides the typed staged write path. Documents can add a sibling
integration over the same `Groundwork.Store` connection contract without changing this API.

`Groundwork.Diagnostics` owns opt-in native explain-plan artifact assertions. The environment
variable and artifact behavior are diagnostic/test concerns and are deliberately not part of the
core Store contract.
