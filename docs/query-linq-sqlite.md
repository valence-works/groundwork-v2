# SQLite adapter for the closed LINQ front-end

`Groundwork.Query.Linq.Sqlite` is the named SQLite entry point to the closed LINQ front-end. Open and
migrate a SQLite session with `Groundwork.Sqlite`, then configure
`new GwQueryDatabase(new SqliteLinqExecutor(session, connection))`; consumers can execute
`query.Where(...).ToListAsync(cancellationToken)` without implementing an executor. The adapter is
deliberately separate from `Groundwork.Sqlite` so the provider itself does not reference the LINQ
contract family.

Execution itself is not SQLite-specific and is not duplicated here: `SqliteLinqExecutor` is
`GwLinqExecutor` from `Groundwork.Query.Linq.Execution`, the one adapter every provider uses. Every
terminal is admitted through the shared `RuntimeCoverageGate` before SQLite renders anything, so an
uncovered query is refused with the same `GW-COVER-*` code the analyzer reported at build time.
SQLite's own contribution is its dialect — already owned by `SqliteQueryRenderer` behind the session
— and its real 999-parameter budget, which the connection advertises so the pre-execution value
fence uses SQLite's limit rather than a portable guess. SQLite's ceiling is a compile-time option of
the native library, so it is genuinely a property of the deployment rather than a constant.

The async terminals call `IStorageSession.QueryAsync`, so any session — including a custom consumer
decorator — reaches the provider's async query path. They run on the async ADO.NET surface with real
cancellation propagation into the native statement, but they still complete synchronously:
Microsoft.Data.Sqlite executes its async operations synchronously, and the provider serializes every
command on the connection's gate, so a call waiting for that gate blocks its thread. `Count()`
executes the provider's total-count shape over a single-row page and `Any()` a limit-1 probe;
neither materializes the matching rows, and a result without a provider-side total count is refused
rather than counted client-side.

`Sum`, `Min`, and `Max` use the same scalar request path over a covered mapped column. SQLite
rendering and four-provider differential evidence for these shapes are tracked by issue #150.
