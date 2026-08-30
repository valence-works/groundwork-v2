# Production Operations

These runbooks define Groundwork's support boundary. They do not replace the database vendor's
backup, high-availability, security, or disaster-recovery procedures.

## Ownership and incident evidence

The **deployment owner** owns database/host availability, credentials and grants, capacity,
monitoring, backup/restore drills, upgrades, and topology. The **Groundwork maintainers** own
reproducible package defects on a **[production-supported topology](Providers#support-tiers-and-topology)**:
a portable-result mismatch, a documented capability executing with weaker semantics, or a stable
diagnostic that still fires after its documented remedy.

Support is open-source and best effort, with no response-time, restoration-time, or 24-hour SLA.
Open a GitHub issue for non-sensitive defects. Never include credentials, tenant values, or private
schema/data. Preserve:

- exact `Groundwork.*` versions and application commit;
- provider/server version, topology, and whether the endpoint was writable;
- declaration fingerprint and redacted
  `groundwork status --schema groundwork.schema.json --provider <provider> --connection-env GROUNDWORK_CONNECTION --output json`;
- the `GW-*` code, exception chain, and provider command event names;
- the last successful deployment/recent migration and a minimal safe reproducer;
- whether a newly opened connection still fails after native service health is restored.

If writes could widen loss or corruption, stop schema application and application writes. Preserve
the database and logs before repair. Never edit Groundwork history, idempotency, lifecycle, or
data-migration ledgers by hand.

## Deployment checklist

1. Pin one exact Groundwork version across the package closure.
2. Use a production-supported topology and verify required capabilities at startup.
3. Back up the database and prove restoration outside production.
4. Preserve the redacted JSON plan, apply its exact reviewed fingerprint with a deployment identity,
   then run `groundwork status`.
5. Separate the deployment principal from the least-privilege application principal.
6. Monitor native availability/capacity, lock waits, command errors/latency, `GW-*` diagnostics, and
   incomplete schema or data-migration status.

## SQLite — single-writer file

- Run SQLite 3.35.0+ on storage with ordinary local file-lock semantics. Keep one long-lived
  `IStorageProviderConnection` and one writer process per file. A second opener is refused as
  `GW-SQLITE-LIFETIME-001`; never delete `.schema.lock` while an owner may be running.
- Do not use a shared/network filesystem or multiple containers mounting one file as the supported
  topology. Use a SQLite-aware online backup, or stop the owner before copying; do not copy only the
  main file while WAL activity may exist.
- For repeated `BUSY` failures, find the extra process or long transaction. After a crash, prove the
  owner exited, retain all files, reopen once, and inspect status before writes resume.

## PostgreSQL — writable primary

- Route runtime and schema-tool traffic to a PostgreSQL 17-compatible writable primary; Groundwork
  does not split reads to replicas. Preserve transaction and session advisory-lock behavior.
- Monitor availability, connection exhaustion, transaction/lock waits, and storage.
- After failover, discard failed connections, wait for the writable endpoint, reopen Groundwork, and
  inspect status. Never blindly retry an authorization-sensitive schema plan after an unknown result.

## SQL Server — writable primary database

- Route Groundwork to a SQL Server 2022-compatible writable primary. Permit the deployment principal
  to use database-scoped `sp_getapplock`, fence/history tables, and authorized DDL; do not give the
  application principal deployment rights.
- After failover or a broken pooled connection, reopen and inspect status. During a rolling
  Groundwork upgrade, permit only the current release line to apply schema until every
  schema-applying instance uses the same database-scoped lock contract.

## MySQL/MariaDB — InnoDB writable primary

- Use MySQL 8.0.17+ or MariaDB 11.4.13+, InnoDB, a writable primary endpoint, and the startup-verified
  NO PAD `utf8mb4_0900_bin`. Never substitute a server-default collation or route Groundwork to a
  read replica.
- Monitor availability, connections, transaction/metadata lock waits, and storage. `GET_LOCK` is
  connection-bound, so a broken schema connection loses its lease.
- DDL can commit implicitly. After a failed apply, stop deployment, inspect every target, and resume
  only from the newly reported plan; do not assume rollback restored the catalog.

## MongoDB — transaction-capable deployment

- Use a replica set or sharded cluster. Verify sessions, transactions, and every required capability
  after topology changes; standalone is not a production fallback.
- Monitor primary availability, replication health/lag, transaction errors, pool pressure, and
  storage. Keep the official driver's retryable-write/transaction behavior enabled.
- After an election or unknown transaction result, allow the bounded provider retry to finish. If it
  fails, reopen, inspect schema/migration status, and retry the idempotent public operation.

## Schema and data migrations

`groundwork apply` and `adopt` process targets independently. Treat a non-success multi-target run as
partial until status proves every target. MySQL/MariaDB also requires reconciliation within a target
because DDL may implicitly commit.

An interrupted data migration is pending work. Preserve its ledger, fix the provider or transform,
and resume with the same semantic identity and transform version. Changed logic needs a new reviewed
version or identity; never rewrite a recorded fingerprint to force replay.
