# v2 provider support matrix

Package maturity and support are separate. A `0.x-preview` package can be supported on a named
topology while its public contract is still allowed to change under the preview versioning policy.
Passing the provider-neutral conformance suites is evidence for a support decision; it is not a
support tier by itself.

## Support tiers

| Tier | Commitment |
| --- | --- |
| **Production-supported** | Groundwork maintainers accept provider defects reproduced on the named topology, keep an operational runbook, and ship fixes under the release policy. This is open-source, best-effort support, not a response-time or availability SLA. |
| **Compatibility-only** | The deployment can use the capabilities it advertises and will fail closed for capabilities it lacks, but maintainers do not represent the topology as suitable for production. |
| **Development/reference-only** | Intended for tests, provider development, samples, or local evaluation; not an application production database. |

The support boundary covers Groundwork's packages and portable contract. The deployment owner
continues to own database availability, capacity, credentials, backups and restore tests, upgrades,
replication/failover, and the operating-system or managed-service layer. See the
[production operations runbooks](production-operations.md) for incident ownership and evidence to
capture before escalation.

## Provider matrix

| Component/provider | Tier | Supported topology |
| --- | --- | --- |
| SQLite | **Production-supported** | SQLite 3.35.0+ in a file on storage with ordinary local filesystem locking, with exactly one long-lived `IStorageProviderConnection` and one application writer process per database file. WAL and the provider's busy timeout remain enabled. `:memory:` databases are development/reference-only. |
| MySQL 8.4.6 | **Production-supported** | InnoDB, one writable primary endpoint, and a runtime-verified NO PAD `utf8mb4_0900_bin`. Groundwork sessions and the schema tool must not be routed to a read replica. |
| MariaDB 11.4.13+ | **Compatibility-only** | The provider targets InnoDB plus a runtime-verified NO PAD `utf8mb4_0900_bin`, but the release has no MariaDB live conformance, schema-tool, or concurrency lane and makes no production suitability promise. |
| Other MySQL 8.0.17+ | **Compatibility-only** | The provider admits a writable InnoDB primary only when `utf8mb4_0900_bin` exists with NO PAD semantics. Versions other than the live-tested 8.4.6 remain outside the production-supported topology. |
| PostgreSQL | **Production-supported** | A PostgreSQL 17-compatible writable primary endpoint. Groundwork sessions and the schema tool use that endpoint; split read/write routing and read-replica sessions are outside the supported topology. |
| SQL Server | **Production-supported** | A SQL Server 2022-compatible writable primary database where the Groundwork principal can use the documented database-scoped `sp_getapplock` and schema facilities. Read-only replica routing is outside the supported topology. |
| MongoDB replica set | **Production-supported** | A transaction-capable replica set reached through the official driver with sessions and transactions available. The runtime capability probe remains authoritative after a topology or server configuration change. |
| MongoDB sharded cluster | **Compatibility-only** | Capability-gated evaluation only. The provider recognizes transaction-capable sharded deployments, but this release has no live sharded conformance, schema-tool, or concurrency lane and makes no production suitability promise. |
| MongoDB standalone | **Compatibility-only** | Operations whose required capabilities are advertised may be used for evaluation. Transaction-dependent guarantees—including atomic commit, exact append, durable idempotency, and data migrations—are absent or refused, so this topology is not production-supported. |
| `Groundwork.Testing` | **Development/reference-only** | Deterministic reference provider and public provider-author conformance contracts; not an application database. |
| `Groundwork.Tool` | **Production-supported** | Deployment-time planning, status, adoption, and explicitly authorized application against a production-supported provider topology. Multi-target execution is not a distributed transaction. |

Production support applies only to the capabilities a connected deployment advertises. A
production-supported provider can still omit a capability it cannot honor—for example,
MySQL 8.4.6 does not advertise durable high-water inspection or compare-and-delete. Applications
must inspect required capabilities at startup rather than inferring them from this table.

## Evidence boundary

Provider-neutral correctness, provider-specific integration suites, schema-tool end-to-end proofs,
and the separately scheduled concurrency matrix establish the implementation evidence. The
[MySQL/MariaDB provider report](mysql-provider-evidence.md) records the MySQL 8.4.6 live lanes and
does not claim MariaDB execution evidence. MongoDB's positive production evidence lanes use a
replica set, not a sharded cluster; a separate standalone lane proves compatibility-mode refusals.
Performance runs are planning evidence, not a semantic gate or a latency/SLA promise.

## Interop view capability

Interop reporting views are a separate opt-in capability.

| Provider | Schema-tool interop view | Native projection |
| --- | --- | --- |
| SQLite | Supported | Decimal `TEXT` is cast to `NUMERIC` |
| PostgreSQL | Supported | UTC tick `bigint` is computed as `timestamptz` (microsecond precision) |
| SQL Server | Supported | Native `datetimeoffset(7)` and decimal values are selected directly |
| MySQL/MariaDB | Supported | UTC tick `bigint` is computed as `DATETIME(6)` (microsecond precision) |
| MongoDB | Refused | Per-scope collections do not form one stable relational view |
| `Groundwork.Testing` | Refused | No native catalog or provider view exists |

A scoped view contains all scopes and exposes `__groundwork_scope`, so database grants—not the
view—provide authorization. MySQL/MariaDB DDL may implicitly commit; a failed multi-operation apply
must be reconciled with `groundwork status` as its runbook describes.

Every provider implements the complete asynchronous session surface. MySQL/MariaDB, PostgreSQL,
SQL Server, and MongoDB use asynchronous driver I/O. SQLite and the reference provider complete
their asynchronous members synchronously because their drivers do. See
[the asynchronous surface](async-surface.md).
