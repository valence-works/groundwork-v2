# W2 concurrency conformance harness

`Groundwork.Testing` exposes the provider-neutral `IConcurrencyProviderFactory` seam and
`ConcurrencyHarness`. A provider adapter supplies one conditional-upsert operation and a read;
the harness owns the deterministic workload, retry policy, named invariants, and machine-load
measurement. The reference InMemory adapter, SQLite adapter, and native MongoDB adapter are
exercised by `tests/Groundwork.Concurrency.Tests`.

The proof uses 32 writers with both `M=1` (maximum contention) and `M=1000`, repeated scenarios,
partial-unique-index and ordinary-index declarations, and both `ConcurrencyKind.None` and
`ConcurrencyKind.Optimistic`. Optimistic final rows must match one and only one accepted
`(value, version)` pair. None mode deliberately has no provider version token; it checks accepted
content and reports that limitation rather than manufacturing a provider version.

PostgreSQL and SQL Server adapters are not yet in the repository. The CI W2 job records those
providers as missing coverage explicitly; when their provider packages land, they implement the
same adapter seam and are added to the job's executed matrix. A deliberately broken adapter stays
in the test assembly and must fail the inserted-count invariant.

Run locally with:

```bash
dotnet test tests/Groundwork.Concurrency.Tests
```

Set `GROUNDWORK_MONGO_CONNECTION` to run the live MongoDB replica-set scenario. Every scenario
report includes processor count, process CPU percentage, and managed-memory bytes so a red result
under machine contention is diagnosable.
