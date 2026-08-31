# Comparative performance evidence protocol

Groundwork publishes comparative latency evidence only from an actual BenchmarkDotNet run at an
exact commit on a named, controlled host while that host is otherwise idle. A Dry job, method
discovery, or a run on a busy development machine is smoke evidence, not a publishable result.

## Controlled comparison

The measured matrix contains point reads, covered queries, offset-paged queries, 32-row batched
writes, and one-row unit-of-work commits through Groundwork, EF Core with its checked-in compiled
model, and Dapper. The checked-in 15-method catalog is a deterministic discovery preflight for that
complete matrix. The implementations share one SQLite schema, seed data, indexes, process, and host.
The query and batched-write paths use equivalent pre-opened connections; the unit-of-work lifecycle
difference is documented below. The benchmark README documents the schema fingerprint, PRAGMAs,
materialized shapes, and the meaning of each reported operation.

`eng/collect-comparative-performance.sh` records the tested commit, schema fingerprint, UTC time,
stable publish-safe host identifier, operating system, load snapshot, .NET environment, and resolved package graph. It exports
BenchmarkDotNet JSON, Markdown, and CSV reports, then binds the unique JSON report into the manifest
by relative path and SHA-256. The checked-in catalog must match discovery before
measurement starts. Results compare within-run ratios only; they are not cross-machine comparisons,
service-level objectives, or support promises.

## Publication handoff

1. Reserve a named performance host and confirm no other workload is using it. Use the same host,
   power mode, and storage for future comparisons.
2. Check out the exact 40-character commit with a clean worktree and choose a new output directory;
   the collector refuses existing output trees so nested redirects or stale artifacts cannot be
   inherited.
3. Run
   `GROUNDWORK_CONFIRM_IDLE_HOST=true GROUNDWORK_CONTROLLED_HOST_ID=<stable-publish-safe-id> eng/collect-comparative-performance.sh <commit>`.
4. Review the manifest, package graph, BenchmarkDotNet environment metadata, outliers, and result
   stability. If valid, copy the publication-safe output into `evidence/runs/<commit>/` in a
   follow-up evidence commit and describe the host and run conditions in that directory's README.
   Keep the structured report bytes unchanged; do not publish raw execution logs, machine names,
   usernames, or absolute checkout paths. The collector removes execution logs on success or
   failure, rejects symlinked or out-of-workspace output roots, and refuses a completed output tree
   if a private host/home/workspace marker remains.

The unit-of-work category is an end-to-end public-call comparison. Groundwork opens an independent
non-pooled connection, performs runtime schema admission, and begins its transaction as part of that
call; EF Core and Dapper begin their transactions on already-open benchmark connections. The
reported Groundwork ratio therefore includes its public admission and connection lifecycle rather
than hiding that work as setup.

The reviewed baseline bundle is checked in under `runs/6e064931fa6d5a623524d3cbef68802e1181a01d/`.
Its README records the run conditions and budget rationale. Discovery and Dry smoke output must never
be substituted for that controlled result or for a future candidate bundle.

## Regression-gate lifecycle

`performance-gate` is a comparison-only command in the non-packable benchmark executable. It accepts
an explicit policy, baseline manifest/report, candidate commit/manifest/report, and returns a failing
exit code when a reviewed budget is exceeded. Before comparing metrics it requires both manifests to
bind the expected exact commits and canonical schema fingerprint, name the same controlled host, and
confirm that host was idle. Both BenchmarkDotNet reports must use the manifest-declared path and
SHA-256, the baseline digest must also match the reviewed policy, version 0.15.8 and the measured
environment (including hardware intrinsics) must match, and each report must contain the exact
15-case catalog with finite means and allocations. The policy must budget exactly the five Groundwork
methods. Raw
`Statistics.Mean` and `Memory.BytesAllocatedPerOperation` values are compared; rendered Markdown,
CSV ratios, averages across runs, and hosted-runner timings are never treated as the baseline.

Synthetic correctness tests exercise the verifier's parser and failure modes, and the test suite also
applies the checked-in policy to the real baseline bundle as an integrity check. The structured report
and `performance-policy.json` are the active baseline. A separate manual, service-free performance
workflow compares a later checked-in bundle and fails that performance lane without re-measuring on
GitHub-hosted hardware. Changing the baseline or a budget is a review-visible evidence change, not an
automatic response to a red gate.

The manual `Controlled performance comparison` workflow is that service-free lane. It checks out an
exact evidence commit, accepts only tracked regular files whose physical paths do not traverse a
symbolic link, and passes the reviewed policy plus the two publication-safe bundles to
`performance-gate`. Its hosted runner evaluates JSON and does not produce timing evidence.

Ordinary correctness workflows validate the workflow shape, catalog, paths, and benchmark behavior.
They do not compare elapsed time. The manual GitHub `Performance evidence` workflow preserves
Native AOT startup, Records, and provider round-trip diagnostics for explicit investigations, but
its hosted-runner artifact is explicitly not a comparative latency baseline.
