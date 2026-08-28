# Asynchronous storage surface

`IStorageSession` declares every operation twice: a synchronous member and an asynchronous
counterpart that takes a `CancellationToken`.

```csharp
StoredEntry? Read(StorageKey key);
ValueTask<StoredEntry?> ReadAsync(StorageKey key, CancellationToken cancellationToken = default);
```

The pairing covers the whole session surface — `Read`, `Query`, `Aggregate`, `Insert`, `Update`,
`Upsert`, `Delete`, `Append` — and every optional capability a provider can advertise:
`IConcurrencyStorageSession`, `ICompareAndDeleteStorageSession`, `IExactAppendStorageSession`,
`IStorageInspectionSession`, `IRetentionStorageSession`, `IExactRetentionStorageSession`,
`IBatchedStorageSession`, and `IPrivilegedCrossScopeQuerySession`. `IUnitOfWork` already carried
`CommitAsync` and `CommitWithOutcomesAsync`; both now flush and commit on the asynchronous provider
surface instead of wrapping the synchronous commit.

There is no separate asynchronous session type and no capability to inspect first. A provider
implements one session that serves both surfaces, so a decorator that forwards `IStorageSession`
forwards both.

## Why both surfaces stay

The asynchronous surface is the one a server-side host should use. The synchronous surface stays
supported for 1.0 for three reasons:

- Deleting it would not remove the blocking call, only move it into consumer code as
  `.GetAwaiter().GetResult()`, where this library can no longer keep it off a request thread.
- The conformance suite, the schema tool, the samples, and a large amount of existing consumer code
  are synchronous, and a storage library is the wrong place to force that migration.
- SQLite — the embedded default — is synchronous underneath, so asynchronous calls there buy
  nothing but allocations.

Both surfaces are proven by the same conformance suite, so neither can drift into being the
second-class one.

`IDataMigrationExecutor` follows the same rule: every member that talks to a store is declared
twice, and `DataMigrationRunner` keeps one orchestration body that selects the surface at its entry
point. `PhysicalSchemaApplication.ApplyAsync` is the asynchronous counterpart of `Apply`; its
data-migration phase runs on the asynchronous executor surface and observes the token, while the
schema operations themselves run on `IPhysicalSchemaExecutor`, which still declares one surface.

## What "asynchronous" means per provider

Whether a call actually yields its thread is a property of the driver underneath, not of the
contract. Each provider states what it does:

| Provider | Asynchronous reads/writes | Notes |
| --- | --- | --- |
| PostgreSQL | Yields the thread | Npgsql's asynchronous ADO.NET surface, including `BeginTransactionAsync`/`CommitAsync` for the write transaction and the unit-of-work commit. |
| SQL Server | Yields the thread | Microsoft.Data.SqlClient's asynchronous ADO.NET surface. The connection's write gate is a `SemaphoreSlim` rather than a monitor so the asynchronous write path can hold it across an await. |
| MongoDB | Yields the thread | The driver's `*Async` commands, including transaction start, commit-with-retry, and abort. |
| SQLite | **Does not yield the thread** | Microsoft.Data.Sqlite completes its asynchronous surface synchronously, and the provider serializes every session command on a gate a suspended continuation cannot hold. Asynchronous members observe cancellation, run the same gated body on the calling thread, and return an already-completed task. See [sqlite-provider.md](../sqlite-provider.md). |
| In-memory reference (`Groundwork.Testing`) | **Does not yield the thread** | Every unit lives in process behind a monitor; there is no I/O to yield for. |

Cancellation is observed before any provider work is issued, so an already-cancelled token is a
refusal, not a partially applied write.

## Provider implementation shape

Providers keep one implementation of each operation and select the driver surface at its entry
point, so the two surfaces cannot drift:

```csharp
public WriteOutcome Insert(StorageValues values, WriteOptions? options = null) =>
    InsertAsync(values, options, RelationalExecution.Synchronous).GetAwaiter().GetResult();

public ValueTask<WriteOutcome> InsertAsync(
    StorageValues values,
    WriteOptions? options = null,
    CancellationToken cancellationToken = default) =>
    InsertAsync(values, options, RelationalExecution.Asynchronous(cancellationToken));
```

`RelationalExecution` (in `Groundwork.Substrate.Relational`) and `MongoExecution` dispatch
`ExecuteNonQuery`, `ExecuteScalar`, `ExecuteReader`, `Read`, `NextResult`, and the transaction verbs
to the surface the caller selected. On the synchronous path nothing suspends, so the returned task
is already complete and the entry point does not block on a pending operation.

Closing a resource is part of that dispatch. A data reader whose result set is not drained still
talks to the server when it closes, so `ExecuteReader` hands back a `RelationalReader` scope that
closes it on the surface that opened it:

```csharp
await using var readerScope = await mode.ExecuteReader(command).ConfigureAwait(false);
var reader = readerScope.Reader;
```

The scope is asynchronously disposable and nothing else, so `using` over it does not compile and an
asynchronously opened reader cannot be closed with blocking I/O by forgetting the idiom. Commands
are disposed with a plain `using`, because disposing one talks to nobody; provider transactions go
through `mode.Dispose`.

## Proving it

- `ConformanceSuite.Run` proves the contract on the synchronous surface;
  `ConformanceSuite.RunAsync` proves the same named checks on the asynchronous one, plus
  `cancellation is refused before provider work` — which covers a read, a query, and a write, so a
  provider that issues any of them on a surface carrying no token is caught. Both runs execute the
  same check bodies.
- Each run scopes its own storage unit names (`ConformanceScenario.WithUnitNameSuffix`), so both
  surfaces can prove the whole contract independently against one database rather than depending on
  a reset between them.
- `ConcurrencyHarness` takes `ConcurrencyProbeOptions.Surface`, so the deterministic invariants are
  proven with writers on the asynchronous surface under contention.
- `StorageProviderConcurrencyFactory(..., commitThroughUnitOfWork: true)` routes each contended
  write through `IUnitOfWork.CommitWithOutcomesAsync`, so asynchronous commits are proven under the
  same contention as direct writes.
