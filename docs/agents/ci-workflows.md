# CI workflow lanes

Groundwork separates verification by purpose and cadence. The split changes when evidence runs; it
does not remove checks.

## Correctness

`.github/workflows/ci.yml` (`Correctness`) runs for every pull request and push to `main`. It owns
builds, architecture and public API checks, deterministic provider and capability conformance, and
all tests except those explicitly tagged `Category=Concurrency`. A failure is actionable merge
feedback.

## Concurrency

`.github/workflows/concurrency.yml` (`Concurrency`) owns the high-contention provider-neutral
harness, PostgreSQL asynchronous contention surfaces, SQL Server's full W2 matrix, and concurrent
retention coalescing. It runs automatically after a push to `main`. Before merging a pull request,
dispatch it once against the candidate branch's exact final head:

```bash
head=$(gh pr view 123 --json headRefOid --jq .headRefOid)
gh workflow run concurrency.yml --ref codex/123-example -f ref="$head"
gh run list --workflow concurrency.yml --branch codex/123-example --limit 1 \
  --json databaseId,headSha,status,conclusion,url
```

Compare `headSha` with the pull request's current `headRefOid`. Any later push invalidates that
evidence and requires another dispatch. Do not use a skipped job or a run from an older commit as a
substitute.

## Performance evidence

`.github/workflows/performance.yml` (`Performance evidence`) is manual-only. Run it for the final
performance phase, a release milestone, or an explicit investigation—not during ordinary feature
iteration:

```bash
head=$(git rev-parse origin/main)
gh workflow run performance.yml --ref main -f ref="$head" -f reason='0.2.0 release evidence'
```

The artifact records the commit, ref, runner, reason, .NET environment, Records hot-path output,
and per-provider write round-trip measurements. Performance evidence is diagnostic and reproducible;
it is not allowed to hide a correctness failure.

## Enforcement note

At the time this split was introduced, GitHub reported no branch protection or ruleset for `main`.
Until repository governance explicitly makes checks required, the control-room merge procedure must
verify the exact-head Correctness and Concurrency results itself.
