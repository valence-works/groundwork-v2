# Agent Instructions

## Delivery

- The v2 program is tracked in GitHub Project 5. Source issues live in
  `valence-works/Groundwork`, even though implementation pull requests live in
  this repository.
- Target issue pull requests at `codex/groundwork-v2` and use an explicit
  cross-repository closing reference such as
  `Closes valence-works/Groundwork#232`.
- Preserve the dependency order and acceptance criteria stated by each issue.

## Architecture

- Keep the innermost `Groundwork.Query.Model` assembly BCL-only and provider-neutral. It owns the
  portable predicate/query semantics. `Groundwork.Kernel` is the second innermost kernel assembly;
  its sole non-BCL reference is `Groundwork.Query.Model`, because the public kernel aggregation
  contract reuses that predicate AST. No other provider, contract-family, Store, I/O, or runtime
  dependency is permitted in either kernel assembly.
- Contract families, provider adapters, and runtime facilities depend inward;
  kernel declarations never depend outward.
- Read and respect the ADRs under `docs/adr/` before changing public contracts.

## Engineering

- Prefer behavior tests through public interfaces.
- Keep tests DRY with shared fixtures where that improves clarity.
- Run focused tests while implementing and the full solution test suite before
  committing.

## Reading a failed CI job

Getting the failure text out of a CI job has cost several agents significant time, so use this route
rather than rediscovering it.

**Do not** try to download run artifacts or fetch a raw log URL. Both resolve to a blob host the
agent proxy rejects with `403`, and no amount of retrying changes that.

Use the GitHub MCP tool, which fetches log content server-side:

```
mcp__github__get_job_logs(job_id: <id>, return_content: true, tail_lines: 4000)
```

Two things to know:

- The result is far larger than the tool's token budget, so it is **not** returned inline. The tool
  writes it to a local file and reports the path. Read that file.
- The content is a **single line** with escaped `\n`, so line-oriented tools see nothing useful, and
  `tail_lines: 150` returns container logs rather than test output. Ask for thousands of lines.

What works against that file:

```bash
grep -oE "Failed Groundwork\.[A-Za-z0-9_.]{0,140}" "$LOG" | sort -u        # which tests failed
grep -oE "(Failed!|Passed!)[^\\]{0,140}" "$LOG"                            # the run summary
python3 -c "s=open('$LOG').read(); i=s.find('Failed Groundwork'); print(s[i:i+1500].replace('\\\\n', chr(10)))"
```

The last one gives the error message and stack trace, which is usually the whole diagnosis.

## Running provider suites on a shared machine

Several agents share one small box, and the live-provider suites contend for both CPU and the
provider servers themselves.

- Serialize anything that runs `Groundwork.Concurrency.Tests`, including a full `Groundwork.slnx`
  run, behind `flock /tmp/groundwork-tests.lock`.
- Check `/proc/loadavg` and `pgrep -c -f "testhost|vstest"` before starting a live-provider run. If
  another agent is running the same provider suite, your result and theirs are both unreliable.
- A **pass** under load is trustworthy; a **failure** under load is not. Contention causes timeouts
  and lock waits, not wrong answers. Never report a red from a loaded box as a real failure without
  re-running it clean.
- If you cannot get a clean window, say so and let CI's dedicated per-provider environment be the
  authority, rather than running anyway and reporting a result you do not trust.
- `Groundwork.Differential.Tests` drives all four providers. A green local run with SQL Server or
  MongoDB unconfigured **skips** those cases — it is not evidence that the four-provider CI job
  passes. Live-provider classes there must join `NativeProviderDifferentialCollection`, which
  serializes them; provider infrastructure DDL is created on first use and races otherwise.
