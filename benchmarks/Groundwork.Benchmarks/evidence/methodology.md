# Comparative performance evidence protocol

Groundwork publishes comparative latency evidence only from an actual BenchmarkDotNet run at an
exact commit on a named, controlled host while that host is otherwise idle. A Dry job, method
discovery, or a run on a busy development machine is smoke evidence, not a publishable result.

## Controlled comparison

The key matrix contains point reads, covered queries, and 32-row batched writes through Groundwork,
EF Core with its checked-in compiled model, and Dapper. The implementations share one SQLite
schema, seed data, indexes, connection policy, process, and host. The benchmark README documents
the schema fingerprint, PRAGMAs, materialized shapes, and the meaning of each reported operation.

`eng/collect-comparative-performance.sh` records the tested commit, schema fingerprint, UTC time,
host, operating system, load snapshot, .NET environment, and resolved package graph. It exports
BenchmarkDotNet JSON, Markdown, and CSV reports. The checked-in catalog must match discovery before
measurement starts. Results compare within-run ratios only; they are not cross-machine comparisons,
service-level objectives, or support promises.

## Publication handoff

1. Reserve a named performance host and confirm no other workload is using it. Use the same host,
   power mode, and storage for future comparisons.
2. Check out the exact 40-character commit with a clean worktree.
3. Run `GROUNDWORK_CONFIRM_IDLE_HOST=true eng/collect-comparative-performance.sh <commit>`.
4. Review the manifest, package graph, BenchmarkDotNet environment metadata, outliers, and result
   stability. If valid, copy the untouched output into `evidence/runs/<commit>/` in a follow-up
   evidence commit and describe the host and run conditions in that directory's README.

No controlled comparative result is checked in by this scaffolding change. Producing that result
requires the bounded idle-host run above; discovery and Dry smoke output must never be substituted.

Ordinary correctness workflows validate the workflow shape, catalog, paths, and benchmark behavior.
They do not compare elapsed time. The manual GitHub `Performance evidence` workflow preserves
Native AOT startup, Records, and provider round-trip diagnostics for explicit investigations, but
its hosted-runner artifact is explicitly not a comparative latency baseline.
