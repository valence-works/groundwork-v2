# CI workflow lanes

Groundwork separates verification by purpose and cadence. The split changes when evidence runs; it
does not remove checks.

## Release publication

`publish-feedz.yml` publishes Feedz-only previews when a GitHub release is published. The separate
`publish-nuget.yml` workflow is manual-only, so publishing that same GitHub release does not start a
nuget.org run. To intentionally publish an exact version to nuget.org, dispatch it against the
release tag and repeat the version in both inputs:

```bash
gh workflow run publish-nuget.yml --ref v0.4.0-preview.5 \
  -f version=0.4.0-preview.5 -f publish=true -f confirm=0.4.0-preview.5
```

The dispatch still runs the full package/test, layout, clean-room, integrity, credential, symbol,
protected-environment, and post-publication exact-version restore gates. A preview release does not
need this dispatch unless nuget.org distribution is explicitly intended.

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

### Full-solution recurrence evidence

Use `eng/run-full-solution-recurrence.sh` only when investigating an intermittent failure whose
signal depends on the unfiltered, cross-assembly solution shape. It is not a routine pull-request
gate. Reserve an idle host, check out the exact candidate commit in a clean worktree, and run:

```bash
GROUNDWORK_CONFIRM_IDLE_HOST=true \
  eng/run-full-solution-recurrence.sh <40-character-commit-sha>
```

The harness holds `/tmp/groundwork-tests.lock` for the complete operation (`flock` on Linux,
`lockf` on macOS), rejects neighboring test processes and a one-minute load above 1.0 throughout a
five-minute preflight, restores once, and then runs the unfiltered Release solution five times in
sequence. It stops on the first failure. Every iteration has its own directory containing console
output and TRX results; a crash or hang also retains its sequence XML and any full dump requested
after the 20-minute VSTest hang threshold. The manifest records the exact SHA, host, lock
implementation, preflight samples, and per-run outcome. Every invocation writes to a unique
timestamped attempt directory below the SHA, so an invalid busy-window attempt remains available
for audit without blocking a later retry.

A failure is recurrence evidence and must be classified locally from its TRX, console output, and
dump. Those files can contain host paths, identifiers, query values, and process memory. The hosted
workflow therefore publishes only a whitelisted `summary.txt` by default and retains it for seven
days. It also suppresses raw restore and test output from the hosted job log. Raw logs, TRX,
sequence files, and dumps remain runner-local unless the dispatch explicitly
sets `upload_sensitive_recurrence_artifacts=true`; that sensitive artifact is conspicuously named
and retained for one day. Five passes mean only “not reproduced in this clean window”; they are not
proof that an intermittent bug is fixed.

For the independent hosted observation, enable the recurrence option when manually dispatching the
`Concurrency` workflow from the candidate branch that contains this optional job. The workflow ref
and the tested SHA must refer to the same candidate checkpoint:

```bash
workflow_ref=codex/210-recurrence-result
# After the PR is merged, use the target integration branch instead.
head=$(git rev-parse "$workflow_ref")
gh workflow run concurrency.yml --ref "$workflow_ref" \
  -f ref="$head" -f full_solution_recurrence=true
```

Only when an authorized investigation requires the raw hosted diagnostics, add
`-f upload_sensitive_recurrence_artifacts=true`. Treat the downloaded artifact as sensitive and
delete local copies after classification.

The optional recurrence job uses a fresh service-free runner, verifies the requested exact head,
delegates the five-minute idle preflight and five unfiltered Release iterations to the same harness,
and uploads a publication-safe summary whenever the harness produced an attempt manifest, including
after a later fast failure. The other dedicated concurrency jobs run alongside it for the same
exact-SHA checkpoint. Its 60-minute outer timeout leaves the 20-minute VSTest hang threshold time to
produce diagnostics. A skipped run while the repository cost brake is active is not evidence.
Record the exact run URL and head SHA when classifying the result, and retain the local observation
as a separate provenance source.

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
  GROUNDWORK_CONTROLLED_HOST_ID=groundwork-controlled-m2 \
  eng/collect-comparative-performance.sh "$head"
```

The collector records exact-head, schema, stable publish-safe host identity, load, runtime, package,
catalog, and structured BenchmarkDotNet evidence. It scrubs private host/home/workspace markers,
recursively removes raw execution logs on success or failure, and refuses an output tree if either
kind of private evidence remains. Every collection uses a new output tree whose root and
BenchmarkDotNet child resolve beneath the physical workspace without traversing a symbolic link.
Follow
`benchmarks/Groundwork.Benchmarks/evidence/methodology.md` to review and check a valid result into the
exact-SHA run directory. Dry output and busy-host results are not publishable. Within-run ratios are
diagnostic evidence, not cross-machine comparisons or SLAs.

Ordinary correctness gating executes the benchmark contract tests through `Groundwork.slnx`; it
checks the matrix, compiled model, canonical schema, and every comparison path without using
wall-clock thresholds. Performance evidence is never allowed to hide a correctness failure.

After a controlled baseline, candidate bundle, and variance-informed policy have been reviewed and
checked in, `.github/workflows/performance-comparison.yml` applies those budgets without running a
benchmark or starting provider services. Dispatch it from the exact evidence commit and pass only
repository-relative paths from that checkout:

```bash
evidence_ref=<40-character-evidence-commit>
gh workflow run performance-comparison.yml --ref main \
  -f ref="$evidence_ref" \
  -f policy='benchmarks/Groundwork.Benchmarks/evidence/performance-policy.json' \
  -f baseline_manifest='benchmarks/Groundwork.Benchmarks/evidence/runs/<baseline-sha>/manifest.txt' \
  -f baseline_result='benchmarks/Groundwork.Benchmarks/evidence/runs/<baseline-sha>/benchmarkdotnet/results/<report>.json' \
  -f candidate_sha='<measured-candidate-sha>' \
  -f candidate_manifest='benchmarks/Groundwork.Benchmarks/evidence/runs/<candidate-sha>/manifest.txt' \
  -f candidate_result='benchmarks/Groundwork.Benchmarks/evidence/runs/<candidate-sha>/benchmarkdotnet/results/<report>.json' \
  -f reason='release performance checkpoint'
```

The policy pins the approved baseline SHA and report digest. The comparator also requires matching
named host, BenchmarkDotNet environment, and hardware intrinsics, so changing input paths cannot
silently replace the baseline or compare unlike machines. Workflow inputs must resolve to tracked
regular files in the exact checkout without traversing symbolic links. A red comparison is
performance evidence; it remains separate from correctness and concurrency and never triggers an
Actions measurement.

## Enforcement note

At the time this split was introduced, GitHub reported no branch protection or ruleset for `main`.
Until repository governance explicitly makes checks required, the control-room merge procedure must
verify the exact-head Correctness and Concurrency results itself.

All three correctness, concurrency, and Native AOT jobs honor the repository-level
`GROUNDWORK_CI_PAUSED` cost brake. A skipped job is not evidence; while paused, record the equivalent
focused local command and native/package boundary where applicable. After unpausing, consolidate
required exact-head hosted evidence into one candidate checkpoint instead of rerunning a matrix for
each intermediate commit.
