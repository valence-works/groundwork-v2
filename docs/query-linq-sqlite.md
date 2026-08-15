# SQLite adapter for the closed LINQ front-end

`Groundwork.Query.Linq.Sqlite` is the provider adapter. Open and migrate a SQLite session with
`Groundwork.Sqlite`, then configure `new GwQueryDatabase(new SqliteLinqExecutor(session))`;
consumers can execute `query.Where(...).ToListAsync(cancellationToken)` without implementing an
executor. The adapter is deliberately separate from `Groundwork.Sqlite` so the provider itself
does not reference the LINQ contract family.
