---
title: Transactions and exact outcomes
---

# Transactions and exact outcomes

Use `BeginUnitOfWork` when several writes must commit or roll back as one
operation. Supply every participating declaration up front, stage writes, then
call `CommitWithOutcomes` when result attribution matters.

An `AtomicCommit` capability means the connected deployment can commit the
declared unit atomically. Relational providers advertise it. MongoDB advertises
it only for a transaction-capable replica set or sharded deployment; standalone
MongoDB refuses the operation.

Do not keep sessions obtained from a unit of work after commit, rollback, or
disposal. The unit owns its transaction and provider resources.

See [batched unit-of-work semantics](../../../v2/w3-batched-unit-of-work.md)
for provider command shapes and exact-outcome behavior.
