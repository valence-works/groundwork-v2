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
