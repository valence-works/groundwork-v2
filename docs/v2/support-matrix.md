# v2 provider support matrix

“Conformance” means the provider passes the provider-neutral contract suites.
“Production-supported” additionally requires a supported deployment topology,
operational guidance, and an owner for provider-specific incidents.

| Component/provider | Status in the first preview | Required topology and evidence |
| --- | --- | --- |
| SQLite | Conformance-passing / preview | File-backed or in-memory SQLite with the documented connection lifetime; production support requires an operational pilot and runbook. |
| MySQL/MariaDB | Implementation preview | MySQL 8.0.17+ or MariaDB 11.4.13+ with InnoDB and a runtime-verified NO PAD `utf8mb4_0900_bin`; the live hosted conformance lane is delivered separately in #180. |
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

Every provider implements the whole asynchronous session surface. MySQL/MariaDB,
PostgreSQL, SQL Server, and MongoDB use asynchronous driver I/O; SQLite and the reference provider
complete their asynchronous members synchronously because their drivers do. See
[async-surface.md](async-surface.md).

All relational providers and the reference provider advertise
`groundwork.operational.atomic-commit`. MongoDB advertises it only when the
connected deployment reports transaction support; standalone MongoDB omits the
descriptor. The five providers currently marked conformance-passing support audited, query-only
cross-scope access for scoped units.
