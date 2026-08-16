# v2 provider support matrix

“Conformance” means the provider passes the provider-neutral contract suites.
“Production-supported” additionally requires a supported deployment topology,
operational guidance, and an owner for provider-specific incidents.

| Component/provider | Status in the first preview | Required topology and evidence |
| --- | --- | --- |
| SQLite | Conformance-passing / preview | File-backed or in-memory SQLite with the documented connection lifetime; production support requires an operational pilot and runbook. |
| PostgreSQL | Conformance-passing / preview | PostgreSQL 17-compatible deployment; production support requires an operational pilot and runbook. |
| SQL Server | Conformance-passing / preview | SQL Server 2022-compatible deployment; production support follows an operational pilot. |
| MongoDB | Conformance-passing / preview | Replica-set or sharded deployment for transactional and exact-append behavior. |
| `Groundwork.Testing` | Public provider-author package | Public conformance contracts and deterministic reference provider; not an application database. |
| `Groundwork.Tool` | Preview | Deployment-time schema planning and explicit authorization only. |

MongoDB standalone deployments are intentionally not represented as
production-supported: they cannot provide the transaction/session guarantees
required by exact append and durable idempotency. A provider may be marked
production-supported in a later release when the matrix is updated with its
topology, test evidence, and operational owner.
