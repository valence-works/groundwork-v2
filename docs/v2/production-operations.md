# Production operations runbooks

These runbooks define Groundwork's support boundary. They do not replace the database vendor's
backup, high-availability, security, or disaster-recovery procedures.

## Ownership and escalation

The **deployment owner** owns database and host availability, credentials and grants, capacity,
monitoring, backups and restore drills, database upgrades, and provider topology. The **Groundwork maintainers**
own reproducible defects in Groundwork packages on a production-supported topology,
including a portable-result mismatch, a documented capability that executes with weaker semantics,
or a stable diagnostic that fires after its documented remedy has been applied.

Open a GitHub issue for a non-sensitive Groundwork defect. Do not put credentials, tenant values, or
private schema/data in an issue. This repository does not promise a response-time, restoration-time,
or 24-hour incident SLA; operators must retain their own rollback and restore path.

Before escalation, preserve:

- the exact `Groundwork.*` package closure and application commit;
- provider/server version and topology, including whether the endpoint was writable;
- the storage declaration fingerprint and redacted
  `groundwork status --schema groundwork.schema.json --provider <provider> --connection-env GROUNDWORK_CONNECTION --output json`
  report;
- the stable `GW-*` diagnostic, complete exception chain, and provider command event names;
- the last successful deployment, recent schema/data migration, and the smallest safe reproducer;
- whether the failure survives a newly opened provider connection after native service health is
  restored.

If continuing writes could widen loss or corruption, stop schema application and application writes
first. Preserve the database and logs before attempting repair. Do not edit Groundwork history,
idempotency, lifecycle, or data-migration ledgers by hand.

## Common deployment checklist

1. Pin one exact Groundwork version across the complete package closure.
2. Use a production-supported topology from the [support matrix](support-matrix.md), and verify every
   required runtime capability at startup.
3. Back up the database and prove restoration outside the production instance.
4. Run `groundwork plan` and preserve its redacted JSON output. Apply the exact reviewed plan with a
   deployment identity; run `groundwork status` afterward.
5. Use a separately authorized deployment principal. Application credentials should have only the
   data and metadata permissions the running service needs.
6. Monitor native availability, storage/capacity, lock waits, command latency/error rate, Groundwork
   diagnostics, and incomplete schema or data-migration status.

## SQLite: single-writer file

- Keep one long-lived `IStorageProviderConnection` for the file and one application writer process.
  A second opener is a topology error and is refused as `GW-SQLITE-LIFETIME-001`; do not delete the
  `.schema.lock` file while an owner may still be running.
- Place the database on storage that provides ordinary local file-lock semantics. Do not treat a
  shared/network filesystem or several containers mounting the same file as the supported
  single-writer topology.
- Use a SQLite-aware online backup or stop the owner before copying the database. Do not copy only
  the main file while WAL activity can still be present.
- On repeated `BUSY`/lock failures, find the extra process or long transaction rather than opening a
  second Groundwork connection. After a crash, prove the old owner has exited, retain the files,
  reopen once, and run `groundwork status` before resuming writes.

## PostgreSQL: writable primary

- Route runtime and schema-tool traffic to one writable primary endpoint; Groundwork does not split
  reads to replicas. Preserve transaction support and session-level advisory-lock behavior.
- Monitor primary availability, connection exhaustion, transaction/lock waits, and storage growth.
- After failover, discard failed provider connections, wait until the endpoint reaches the writable
  primary, open a new Groundwork connection, and run `groundwork status`. Do not retry an
  authorization-sensitive schema plan blindly after an unknown outcome.

## SQL Server: writable primary database

- Route Groundwork to the writable primary database. The deployment principal must be allowed to
  use the provider's database-scoped `sp_getapplock`, durable fence/history tables, and authorized
  DDL; application principals should not receive deployment DDL rights.
- Monitor database availability, connection-pool exhaustion, transaction/lock waits, and log/data
  growth.
- After failover or a broken pooled connection, reopen the Groundwork connection and inspect status.
  During a rolling Groundwork upgrade, allow only the current release line to apply schema until all
  schema-applying instances use the same database-scoped lock contract.

## MySQL/MariaDB: InnoDB writable primary

- Keep every Groundwork table on InnoDB and route runtime/schema-tool traffic to the writable
  primary. Startup must continue to verify NO PAD `utf8mb4_0900_bin`; do not replace that check with
  a server-default collation assumption.
- Monitor primary availability, connection exhaustion, transaction/metadata lock waits, and storage
  growth. `GET_LOCK` is connection-bound, so a broken schema connection loses its lease.
- MySQL/MariaDB DDL can commit implicitly. After a failed apply, stop further deployment steps, run
  `groundwork status`, compare every target with the preserved plan, and resume only from the newly
  reported current plan. Do not assume rollback restored the earlier catalog.

## MongoDB: transaction-capable deployment

- Use a replica set or sharded cluster for production. Verify sessions, transactions, and every
  application-required capability after deployment or topology changes; a standalone node is not a
  production fallback.
- Monitor primary availability, replication health/lag, transaction errors, connection-pool
  pressure, and storage growth. Keep retryable-write/transaction behavior enabled for the official
  driver path used by Groundwork.
- After election or an unknown transaction result, let the provider's bounded transaction retry
  finish. If it fails, open a new Groundwork connection, inspect schema and migration status, and
  retry the idempotent operation through its public API. Never repair a Groundwork ledger document
  manually.

## Schema and data-migration incidents

`groundwork apply` and `adopt` process targets independently; success for one target is not rolled
back when a later target fails. Treat every non-success multi-target run as partial until status
proves each target. MySQL/MariaDB also needs this reconciliation within a target because DDL may
implicitly commit.

An interrupted data migration is pending work, not a successful deployment. Preserve its ledger,
fix the underlying provider/transform problem, and resume with the same semantic migration identity
and transform version. A changed transform needs a new reviewed version or identity; never rewrite
the recorded fingerprint to force a replay.
