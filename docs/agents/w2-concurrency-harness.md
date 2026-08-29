# W2 concurrency conformance harness

`Groundwork.Testing` exposes the provider-neutral `IConcurrencyProviderFactory` seam and
`ConcurrencyHarness`. A provider adapter supplies one conditional-upsert operation and a read;
the harness owns the deterministic workload, retry policy, named invariants, and machine-load
measurement. The reference InMemory, SQLite, native MongoDB, and PostgreSQL adapters run through the
provider-neutral harness project; SQL Server owns its full W2 matrix in the provider test project.
The dedicated Concurrency workflow runs both against an explicitly selected final head and after
each push to `main`; ordinary pull-request pushes run the Correctness workflow instead.

The proof uses 32 writers with both `M=1` (maximum contention) and `M=1000`, repeated scenarios,
partial-unique-index and ordinary-index declarations, and both `ConcurrencyKind.None` and
`ConcurrencyKind.Optimistic`. Optimistic final rows must match one and only one accepted
`(value, version)` pair. None mode deliberately has no provider version token; it checks accepted
content and reports that limitation rather than manufacturing a provider version.

A deliberately broken adapter stays in the test assembly and must fail the inserted-count invariant.

Run locally with:

```bash
dotnet test tests/Groundwork.Concurrency.Tests
```

Set `GROUNDWORK_MONGO_CONNECTION`, `GROUNDWORK_POSTGRES_CONNECTION`, and
`GROUNDWORK_SQLSERVER_CONNECTION` to run the live provider scenarios. Every scenario report includes
processor count, process CPU percentage, and managed-memory bytes so a red result under machine
contention is diagnosable.
