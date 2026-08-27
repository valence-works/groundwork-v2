# Agent Instructions

## Delivery

- Issues and implementation pull requests both live in this repository,
  `valence-works/groundwork-v2`. Pull requests target `main` and close their
  issue with a plain reference: `Closes #232`.
- Preserve the dependency order and acceptance criteria stated by each issue.
- A behaviour change, a new CLI surface, or a new diagnostic code needs a
  release note in `docs/v2/releases/<current release>.md`, and a new diagnostic
  code also needs a row in `docs/wiki/Diagnostics-Reference.md`. The current
  release is named by `GroundworkCurrentRelease` in `Directory.Build.props`.
- Closing a **parent** issue is manual. GitHub closes the sub-issues a pull
  request names and leaves the parent open, so a phase can read as unfinished
  when every item in it has shipped.

## Claiming an issue

Several agents and people work this board at once, in separate worktrees and
sometimes in separate sessions. Two of them starting the same issue costs both
of them, and neither finds out until a pull request already exists. The claim
is therefore on the issue, where everyone looks, rather than in any one agent's
head.

- **Name the branch for the issue**: `<prefix>/<issue number>-<slug>`, for
  example `claude/203-key-coverage`. Without the number in the name there is no
  way to ask whether an issue is already being worked short of reading every
  branch, and two people can pick different reasonable names for the same issue
  and never recognise each other's work.
- **Before starting**, the issue must have no assignee, no open pull request
  referencing it, and no remote branch carrying its number. Free work is
  `is:issue is:open no:assignee`; the branch check is
  `git ls-remote --heads origin | grep -E "/<issue number>-"`.
- **To claim it**, assign yourself and leave one comment naming the branch you
  will push to and the date. The assignee is the signal that it is taken; the
  comment says who and where, because several agents can share one account and
  an assignee alone cannot tell them apart.
- **Push the branch early**, before the work is finished. An unpushed branch is
  invisible to everyone else, and a claim nobody can verify is a claim nobody
  can safely inherit.
- **To release it**, unassign and say why. Leaving a claim behind when you stop
  is worse than never making one, because the issue then looks worked when it
  is not.
- **A claim goes stale** when the branch it names has had no commit for four
  hours. Anyone may take a stale claim, after saying so on the issue.
- A merged pull request closes the issue and the claim goes with it.

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
