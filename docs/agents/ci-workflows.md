# CI workflow lanes

Groundwork separates verification by purpose and cadence. The split changes when evidence runs; it
does not remove checks.

## Correctness

`.github/workflows/ci.yml` (`Correctness`) runs for every pull request and push to `main`. It owns
builds, architecture and public API checks, deterministic provider and capability conformance, and
all tests except those explicitly tagged `Category=Concurrency`. A failure is actionable merge
feedback.

The `mysql-provider` job deliberately starts one MySQL 8.4 service and runs `net8.0`, `net10.0`,
and the schema-tool journey sequentially. This retains both-runtime and live CLI evidence without
paying the service-startup and restore cost of a two-leg job matrix. Its TRX guard refuses a green
result if either the provider conformance proof or schema-tool proof was skipped.

## Concurrency

`.github/workflows/concurrency.yml` (`Concurrency`) owns the high-contention provider-neutral
harness, PostgreSQL asynchronous contention surfaces, SQL Server's full W2 matrix, and concurrent
retention coalescing. The provider-neutral harness includes nine live MySQL cases per runtime TFM
and refuses a skipped MySQL service. It runs automatically after a push to `main`. Before merging a
pull request, dispatch it once against the candidate branch's exact final head:

```bash
head=$(gh pr view 123 --json headRefOid --jq .headRefOid)
gh workflow run concurrency.yml --ref codex/123-example -f ref="$head"
gh run list --workflow concurrency.yml --branch codex/123-example --limit 1 \
  --json databaseId,headSha,status,conclusion,url
```

Compare `headSha` with the pull request's current `headRefOid`. Any later push invalidates that
evidence and requires another dispatch. Do not use a skipped job or a run from an older commit as a
substitute.

The SQL Server W2 command has a 20-minute per-test hang threshold inside the job's 30-minute limit.
A test that exceeds it therefore asks VSTest for a full hang dump and fails while the runner still
has time to upload the dump, sequence XML, and any partial TRX from
`artifacts/sqlserver-w2/<tfm>`. The threshold resets between xUnit test cases and is above the
observed 11–13 minute successful W2 case duration; increasing the outer job timeout is not a hang
diagnostic. Preserve the exact-head check and exact-once TRX guard when changing this command.

## Native AOT correctness

`.github/workflows/aot.yml` (`Native AOT conformance`) packs the exact-head public packages, restores
an isolated package-only consumer, publishes it with `PublishAot=true`, verifies that the result is
an ELF/Mach-O executable, and runs that native binary. Runtime package builds already treat trim and
dynamic-code analyzer findings as errors; this lane proves both the shipped package boundary and the
closed in-memory execution path against the Native AOT compiler and runtime. It runs after a push to
`main` and can be dispatched against an exact candidate SHA when an AOT-sensitive change needs
pre-merge evidence. It does not run live-provider or concurrency matrices.

The equivalent local proof is:

```bash
dotnet restore Groundwork.slnx --nologo -m:1
eng/pack-public-packages.sh artifacts/aot-packages 0.2.0-aot.local
tests/Groundwork.Aot.Conformance/verify-native-aot.sh artifacts/aot-packages osx-arm64
```

Use `linux-x64` on a Linux workstation. The last command must print an ELF/Mach-O executable
description and `Native AOT conformance passed`; a managed-only publish is not evidence.

The SQLite minimal-API proof exercises the shipped provider, generated declaration and mapping,
unit of work, point read, and covered query through native HTTP endpoints. Reuse the same packed
package directory:

```bash
GROUNDWORK_AOT_STARTUP_RUNS=1 \
  samples/Groundwork.Samples.NativeAotApi/verify-native-aot.sh \
  artifacts/aot-packages osx-arm64
```

One launch is correctness evidence. Repeated startup observations belong only in the manual
performance workflow.

## Performance evidence

`.github/workflows/performance.yml` (`Performance evidence`) remains a manual-only diagnostic lane
for Native AOT startup, Records, and provider round-trip evidence. Its hosted runner is not stable
enough to publish comparative latency baselines. Do not run it during ordinary feature iteration:

```bash
head=$(git rev-parse origin/main)
gh workflow run performance.yml --ref main -f ref="$head" -f reason='provider investigation'
```

For publishable comparative evidence, reserve a named controlled host, check out the exact commit
with a clean worktree, and explicitly confirm that the host is idle:

```bash
GROUNDWORK_CONFIRM_IDLE_HOST=true \
  eng/collect-comparative-performance.sh "$head"
```

The collector records exact-head, schema, host, load, runtime, package, catalog, and raw
BenchmarkDotNet evidence. Follow `benchmarks/Groundwork.Benchmarks/evidence/methodology.md` to review
and check a valid result into the exact-SHA run directory. Dry output and busy-host results are not
publishable. Within-run ratios are diagnostic evidence, not cross-machine comparisons or SLAs.

Ordinary correctness gating executes the benchmark contract tests through `Groundwork.slnx`; it
checks the matrix, compiled model, canonical schema, and every comparison path without using
wall-clock thresholds. Performance evidence is never allowed to hide a correctness failure.

## Enforcement note

At the time this split was introduced, GitHub reported no branch protection or ruleset for `main`.
Until repository governance explicitly makes checks required, the control-room merge procedure must
verify the exact-head Correctness and Concurrency results itself.

All three correctness, concurrency, and Native AOT jobs honor the repository-level
`GROUNDWORK_CI_PAUSED` cost brake. A skipped job is not evidence; while paused, record the equivalent
focused local command and native/package boundary where applicable. After unpausing, consolidate
required exact-head hosted evidence into one candidate checkpoint instead of rerunning a matrix for
each intermediate commit.
