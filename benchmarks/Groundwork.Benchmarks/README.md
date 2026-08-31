# Groundwork comparative benchmarks

This project contains two deliberately separate performance lanes:

- `benchmarks` is the BenchmarkDotNet comparison suite introduced by issue #185.
- `roundtrips`, `linq`, and `records` are deterministic assertions for provider command counts,
  closed-accessor caching, and reflection-free generated Records access. They are correctness
  evidence, not latency or throughput benchmarks.

The comparative suite measures five public workloads: point reads, covered queries, offset-paged
queries, 32-row batched writes, and one-row unit-of-work commits. Every workload runs through
Groundwork, EF Core with its checked-in compiled model, and Dapper. All three use the same temporary
SQLite database, physical table, index, seed rows, process, and hardware. The query and batched-write
paths use equivalent pre-opened connections; the unit-of-work lifecycle difference is documented
below.
Each connection uses foreign keys, WAL journaling, `synchronous=NORMAL`, a 5-second busy timeout,
and Groundwork's UTF-16 ordinal collation. Point reads materialize the same `BenchmarkItem` shape,
and EF uses separate compiled-model contexts for query, batch-write, and one-row-commit state.
The canonical `StorageUnit` declaration and its kernel-computed SHA-256 fingerprint are exposed by
`BenchmarkMethodology`; the benchmark applies that same declaration, so the evidence fingerprint
cannot drift independently. A focused test prevents a workload or stack from silently leaving the
comparison matrix.
BenchmarkDotNet reports mean latency, operations per second, allocation cost, and ratios against
the Groundwork baseline inside each workload category. One operation is one public API call; for
the batched-write category, `Op/s` therefore means batches per second and row throughput is
`Op/s * 32`.

The unit-of-work category measures each stack's public commit contract. Groundwork's call includes
opening its independent non-pooled SQLite connection, runtime schema admission, and beginning its
transaction. The EF Core and Dapper paths begin and commit transactions on their already-open
benchmark connections. That lifecycle difference is intentional and remains part of the reported
Groundwork cost; interpret this category as end-to-end public-call overhead, not transaction-only
driver timing.

Build and discover the suite without collecting performance evidence:

```bash
dotnet build benchmarks/Groundwork.Benchmarks/Groundwork.Benchmarks.csproj -c Release
dotnet run --project benchmarks/Groundwork.Benchmarks -c Release --no-build -- \
  benchmarks --list flat
```

Use a dry job only as a bounded execution smoke test:

```bash
dotnet run --project benchmarks/Groundwork.Benchmarks -c Release -- \
  benchmarks --filter '*PointRead_Groundwork*' --job Dry --warmupCount 0 --iterationCount 1
```

A real measurement must run in Release mode on an otherwise idle host and retain BenchmarkDotNet's
environment metadata. Do not compare results from different machines or mix them with the existing
round-trip/reflection assertions. The controlled-host exact-SHA collection procedure and publication
handoff are defined in [`evidence/methodology.md`](evidence/methodology.md). Ordinary correctness
checks validate the benchmark matrix and evidence contract without elapsed-time pass/fail criteria.

The non-packable benchmark executable also exposes a fail-closed `performance-gate` comparator for
two checked-in controlled-run bundles. It validates both manifests, their controlled-host and idle
confirmations, their report SHA-256 bindings, the policy-pinned baseline digest, the exact 15-case
catalog, BenchmarkDotNet version and measured environment (including hardware intrinsics) before
applying explicit mean and allocation-ratio budgets to Groundwork's five cases. This command does not
run a benchmark and is not an active gate
until an approved controlled baseline, candidate result, and reviewed policy exist. The exact
arguments and lifecycle are documented in [`evidence/methodology.md`](evidence/methodology.md).

When the EF mapping changes, regenerate its compiled model with the repository's matching EF tool:

```bash
dotnet ef dbcontext optimize \
  --project benchmarks/Groundwork.Benchmarks/Groundwork.Benchmarks.csproj \
  --context Groundwork.Benchmarks.BenchmarkDbContext \
  --output-dir CompiledModels \
  --namespace Groundwork.Benchmarks
```
