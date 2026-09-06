# Structured execution evidence

Status: unreleased implementation for #405. Do not assume an existing published
package contains this surface. Provider coverage and the release gate remain
under validation; see [ADR 0004](../adr/0004-structured-provider-execution-evidence.md).

## Migrating a command-text consumer

Implement `IProviderExecutionObserver` on the observer supplied through the
existing `IProviderCommandObserver` registration. Keep `Observe` for existing
command diagnostics and counts. Handle structured terminal observations in
`ObserveExecution`; do not count that callback as another command.

Select shape-only evidence when native plans are unnecessary. Request
`ProviderExecutionEvidenceOptions.ShapeAndPlans` only for diagnostic capture:
plan collection adds provider work and must stay outside ordinary timing
measurements. Registration alone does not make an unsupported operation mapped.

Migrate each acceptance rule to the facts it actually requires:

- Require a successful actual-command outcome and collected semantic shape.
  A rendered request or a pre-execution legacy event does not prove execution.
- Check the complete predicate conjunction, effective ordering and transforms,
  scope binding, projection and native paging facts. Do not reconstruct missing
  facts from the input query or a schema declaration.
- Treat unknown, absent and explicit native bounds as different states.
  A client materialization bound is not a native limit or proof of an enforced
  unique key.
- Validate opaque command, statement and target identities when correlating
  facts. Identities are capture-local; use declared logical identities for
  comparisons across captures. Never derive opaque IDs from tenant values.
- Require the plan facts needed by the rule independently of shape facts.
  A successful read may have failed or unsupported plan collection.

Do not fall back to SQL/BSON parsing when required structured facts are missing.
Report that the rule cannot yet be proved and complete the provider mapping.
Consumers still own workload definitions, representative data, result checks,
performance budgets and acceptance decisions. Groundwork reports provider facts,
not application-specific verdicts.

## Interpreting native plans

Plan provenance distinguishes estimated explain output, diagnostic replay and
original-execution telemetry. Estimates and replay statistics cannot stand in
for measurements of the original command. Plan collection command counts are
separate from legacy observed command counts.

Requested plan collection can throw even after the application read succeeded.
Retain its terminal callback: it records the successful actual-command outcome
and failed plan collection independently. The original diagnostic exception
propagates after observation; a callback exception cannot mask a pending
diagnostic or actual-command failure. Unsupported plan grammar alone does not
throw. Consumers must inspect availability instead of assuming that a normally
returning query necessarily has usable plan evidence.

A chosen-index fact does not prove a complete access path. A mapped
`ProviderPlanForest` preserves native operator relationships; a missing forest
does not mean no sort. Even a complete operator structure does not establish
sort-key semantics, spill behavior, row counts or enforced uniqueness. Require
explicit evidence for each fact your policy needs.

Treat `TopNSort` as both sorting and row-limiting work. It represents one fused
native operator, not two separate nodes, and does not establish a numeric bound
or prove that the sort stayed in memory. Standalone operations remain `Sort`
and `Limit`.

`Filter` retains a native predicate-filtering stage, not its expression or
values. Its presence alone does not prove predicate identity, selectivity or
index coverage; it carries no storage target identity.

## Retaining observations

The immutable runtime types are not a default JSON wire contract. Map the actual
callback to a consumer-owned, versioned artifact schema. Preserve availability,
provenance, opaque identities and all bound states; validate the reloaded
artifact before admission. Test a real provider observation through capture,
save, reload and admission, plus mutations that must be refused. Synthetic DTO
round-trip tests alone do not prove integration.

Structured evidence omits parameter values, scope plaintext, credentials and
raw commands/plans. Existing raw diagnostic artifacts remain a separate,
potentially sensitive surface; do not copy them into the structured payload.
Legacy observed command totals also do not imply that all provider metadata
operations or diagnostic probes were observed.

## Release adoption

After publication, pin the complete Groundwork package family to the verified
release and restore from the published feed. Repeat the real artifact lifecycle
test against that exact package closure before removing the consumer's parser.
A source reference or local proof package is development evidence, not proof of
published-package adoption.
