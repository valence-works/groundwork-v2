# Groundwork comparative benchmarks

This project contains two deliberately separate performance lanes:

- `benchmarks` is the BenchmarkDotNet comparison suite introduced by issue #185.
- `roundtrips`, `linq`, and `records` are deterministic assertions for provider command counts,
  closed-accessor caching, and reflection-free generated Records access. They are correctness
  evidence, not latency or throughput benchmarks.

The comparative suite measures five public workloads: point reads, covered queries, offset-paged
queries, 32-row batched writes, and one-row unit-of-work commits. Every workload runs through
Groundwork, EF Core with its checked-in compiled model, and Dapper. All three use the same temporary
SQLite database, physical table, index, seed rows, open-connection policy, process, and hardware.
Each connection uses foreign keys, WAL journaling, `synchronous=NORMAL`, a 5-second busy timeout,
and Groundwork's UTF-16 ordinal collation. Point reads materialize the same `BenchmarkItem` shape,
and EF uses separate compiled-model contexts for query, batch-write, and one-row-commit state.
The canonical schema and its SHA-256 fingerprint are exposed by `BenchmarkMethodology`; a focused
test prevents a workload or stack from silently leaving the comparison matrix.
BenchmarkDotNet reports mean latency, operations per second, allocation cost, and ratios against
the Groundwork baseline inside each workload category. One operation is one public API call; for
the batched-write category, `Op/s` therefore means batches per second and row throughput is
`Op/s * 32`.

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

When the EF mapping changes, regenerate its compiled model with the repository's matching EF tool:

```bash
dotnet ef dbcontext optimize \
  --project benchmarks/Groundwork.Benchmarks/Groundwork.Benchmarks.csproj \
  --context Groundwork.Benchmarks.BenchmarkDbContext \
  --output-dir CompiledModels \
  --namespace Groundwork.Benchmarks
```
