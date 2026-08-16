# W2 concurrency conformance harness

`Groundwork.Testing` exposes the provider-neutral `IConcurrencyProviderFactory` seam and
`ConcurrencyHarness`. A provider adapter supplies one conditional-upsert operation and a read;
the harness owns the deterministic workload, retry policy, named invariants, and machine-load
measurement. The reference InMemory adapter, SQLite adapter, native MongoDB adapter, and SQL Server adapter
are exercised by their provider test projects. The W2 CI job runs both the provider-neutral matrix
and the live SQL Server W2 test.

The proof uses 32 writers with both `M=1` (maximum contention) and `M=1000`, repeated scenarios,
partial-unique-index and ordinary-index declarations, and both `ConcurrencyKind.None` and
`ConcurrencyKind.Optimistic`. Optimistic final rows must match one and only one accepted
`(value, version)` pair. None mode deliberately has no provider version token; it checks accepted
content and reports that limitation rather than manufacturing a provider version.

PostgreSQL remains outside the executed W2 matrix because its provider adapter is not yet in the
repository. A deliberately broken adapter stays in the test assembly and must fail the
inserted-count invariant.

Run locally with:

```bash
dotnet test tests/Groundwork.Concurrency.Tests
```

Set `GROUNDWORK_MONGO_CONNECTION` and `GROUNDWORK_SQLSERVER_CONNECTION` to run the live provider
scenarios. Every scenario report includes processor count, process CPU percentage, and managed-
memory bytes so a red result under machine contention is diagnosable.
