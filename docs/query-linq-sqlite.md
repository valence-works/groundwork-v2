# SQLite adapter for the closed LINQ front-end

`Groundwork.Query.Linq.Sqlite` is the named SQLite entry point to the closed LINQ front-end. Open and
migrate a SQLite session with `Groundwork.Sqlite`, then configure
`new GwQueryDatabase(new SqliteLinqExecutor(session, connection.Catalog))`; consumers can execute
`query.Where(...).ToListAsync(cancellationToken)` without implementing an executor. The adapter is
deliberately separate from `Groundwork.Sqlite` so the provider itself does not reference the LINQ
contract family.

Execution itself is not SQLite-specific and is not duplicated here: `SqliteLinqExecutor` is
`GwLinqExecutor` from `Groundwork.Query.Linq.Execution`, the one adapter every provider uses. Every
terminal is admitted through the shared `RuntimeCoverageGate` before SQLite renders anything, so an
uncovered query is refused with the same `GW-COVER-*` code the analyzer reported at build time.
SQLite's own contribution is its dialect — already owned by `SqliteQueryRenderer` behind the session
— and its real 999-parameter budget, which the session advertises so the pre-execution value fence
uses SQLite's limit rather than a portable guess.

The async terminals run on the async ADO.NET surface with real cancellation propagation into the
native statement, but they still complete synchronously: Microsoft.Data.Sqlite executes its async
operations synchronously, and the provider serializes every command on the connection's gate, so a
call waiting for that gate blocks its thread. `Count()` executes the provider's total-count shape
over a single-row page and `Any()` a limit-1 probe; neither materializes the matching rows, and a
result without a provider-side total count is refused rather than counted client-side. A session
that does not advertise the provider's async capability — for example a custom `IStorageSession`
decorator — executes the ordinary synchronous query path directly.
