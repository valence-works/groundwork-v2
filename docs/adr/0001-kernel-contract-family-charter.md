# ADR 0001: Separate kernel facilities from contract families

- Status: Accepted
- Date: 2026-08-15

## Context

Groundwork v1 coupled reusable persistence mechanisms to its document contract.
That made provider capabilities, schema evolution, queries, and diagnostics look
document-specific even when they were useful to other storage subjects.

The v2 program must prove that the shared machinery is reusable by supporting a
plain typed-row declaration before adding an optional document contract family.

## Decision

Groundwork separates a small provider-neutral kernel from contract families.

The kernel owns logical storage declarations, portable types, naming and stable
identity, schema planning primitives, capabilities, query semantics, execution
contracts, and conformance rules. A consumer may build a contract family using
only those public facilities; no internal extension point is required.

Contract families own their subject-specific semantics and projections. A
document family may add document bindings and serialization, while another
family may model events, queues, ledgers, or graphs. These concerns do not enter
the kernel merely because one family needs them.

Dependencies point inward: provider adapters and contract families depend on
the kernel. The kernel never references a provider, contract family, I/O API,
or runtime implementation.

## Consequences

- The first public declaration is subject-first and describes typed storage.
- Provider mapping and runtime behavior can evolve without widening the kernel.
- The document family must be proven as a consumer of the public kernel.
- Public kernel names avoid family-specific words such as `Document`,
  `Envelope`, and `Record`.

This decision restates the reusable-kernel direction of v1 ADR 0005 for the v2
greenfield architecture.
