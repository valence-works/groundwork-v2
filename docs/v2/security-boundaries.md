# Security boundaries

Groundwork protects the integrity of its own storage contract, but it is not an identity provider,
secrets manager, database firewall, or authorization service. Hosts and deployment systems must
authenticate operators, restrict database credentials, and bind Groundwork's caller-supplied labels
to their own trusted identity.

The security-sensitive boundaries are deployment-tool input to schema I/O, privileged access to
tenant-scoped data, public set-mutation entry points to provider commands, and host transforms to
durable migration progress.

## Deployment authorization and credentials

An exact `groundwork plan` fingerprint is an intent and time-of-check/time-of-use gate. It is not a
signature or an RBAC decision. When `apply` or `adopt` uses `--expected-plan`, the invocation must
also supply a non-secret `--deployment-id`; that identity participates in the fingerprint, so a
plan produced for one deployment cannot authorize another. Use a stable identifier such as
`orders-production-eu`, not a connection string or credential. `--safe` remains available only for
plans the protection policy classifies as safe.

Keep connection strings out of process arguments and shell history. Prefer exactly one of:

```text
--connection-env GROUNDWORK_PRODUCTION_CONNECTION
--connection-file /run/secrets/groundwork-connection
--connection-stdin
```

The modes above and legacy `--connection` are mutually exclusive. The CLI redacts the exact
resolved connection string from both human and JSON errors, including provider-authored errors.
Redaction is defense in depth: restrict file permissions, environment inspection, process tracing,
and deployment logs, and do not place other secrets in a deployment identity or schema document.

Database authorization remains authoritative. Give the tool principal only the schemas and
operations intended for that deployment. A plan fingerprint cannot grant a permission the database
denies, and it must not be treated as a bearer credential.

## Privileged cross-scope reads

Privileged access is query-only and requires both `StorageAccessAudit` metadata and an
`IStorageAccessObserver`. A missing sink fails closed. The sink receives an attempt before provider
I/O and a success or failure outcome afterward; if recording the attempt throws, no provider command
runs. Audit identity and purpose are caller-supplied labels, not proof of authentication. A host
should populate them from its authenticated principal and protected operation context rather than
from untrusted request text.

Continuation tokens bind the privileged identity and purpose, but contain neither the labels nor raw
scope values. Cross-scope rows retain their owning scope. Database grants remain necessary because
provider-native reporting views can expose every scope.

## Set-based mutation

`SetMutationSessionExtensions.UpdateWhere` and `DeleteWhere` are the admitted public entry points.
They run the shared coverage gate or verify an explicit, unexpired scan acceptance before entering
an internal execution scope bound to the exact rewritten predicate. Shipped provider capabilities
require that scope immediately before native work. Casting a shipped provider session directly to
`ISetMutationStorageSession`, including through a unit of work, therefore refuses instead of
bypassing coverage admission. Provider packages execute with the host's database authority and are
part of the trusted computing base; Groundwork cannot constrain arbitrary code in an untrusted
provider. Third-party providers must call
`SetMutationExecutionAdmission.Require(where)` at their native capability seam before validation,
flush, rendering, or I/O; only the shared extensions can create the corresponding evidence. Run the
provider conformance suite, which verifies that a direct capability call fails closed, before
trusting or publishing a provider package.

Scan acceptance is operational evidence, not authentication. Protect the code and configuration
that issue acceptances, use narrow reasons and expirations, and continue to grant the runtime
principal only the write access it needs.

## Data migrations

Each `IDataMigrationTransform` declares a logical `Identity` and an explicit `Version` or content
digest. Groundwork hashes both into durable replay evidence; changing the version under an already
recorded semantic migration identity is refused rather than replayed as the old transform. Change
the version whenever output logic can change. A caller-supplied version cannot detect a maintainer
who changes code but forgets to update it, so review the version or derive it from a controlled
artifact digest in the deployment host.

Before provider execution, every produced value is checked against its declared target column for
nullability, portable CLR type, the finite non-negative-zero Double domain, string/binary length,
and decimal precision and scale. A transform may write only its declared targets. Every direct run
acquires the provider's durable target lease for the complete pass; schema application reuses the
lease it already holds. Concurrent processes therefore cannot both transform the same recorded
cursor. Renewable leases are heartbeated while provider work is in flight and rechecked after each
chunk before its progress is accepted. Transforms must remain deterministic and side-effect free: chunks can be retried after
rollback, while the durable ledger is the authority for committed progress.

## Catalog adoption and multi-target operations

`groundwork adopt` proves physical shape and publishes the same applied snapshot that an ordinary
apply would publish. It does not inspect application row meaning, validate tenant ownership, or
prove how derived values were produced when their algorithm evidence is absent. Inspect and back up
data separately before adoption.

One target is inspected and published under one provider lease. A document with multiple targets is
processed target by target; it is not a distributed transaction. A later target can therefore fail
after an earlier target was adopted or applied. Reports name each outcome, and rerunning is
idempotent for completed targets. Run plan/status with the same document and deployment identity,
but do not treat that as cross-target atomicity. When all-or-none operational behavior matters, use
one target per invocation. Retain a backup and treat a non-success exit as a partial deployment
until status confirms every target.
MySQL/MariaDB can also implicitly commit DDL inside one target, so its documented replay and
reconciliation guidance applies.

## Review checklist

- Bind exact-plan authorization to the intended deployment and preserve the reviewed JSON report.
- Supply credentials through a protected environment, file, or standard input and restrict the
  database principal independently.
- Bind audit labels to an authenticated host identity and make the audit sink durable and monitored.
- Use only admitted set-mutation extensions; investigate every direct-capability refusal.
- Version migration logic deliberately and review output declarations and budgets.
- Back up and inspect data before adoption, and verify every target after a partial failure.
