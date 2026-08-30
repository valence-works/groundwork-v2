# Relational substrate provider contract

Groundwork.Substrate.Relational exposes the shared relational schema, runtime-admission, rendering,
materialization, and sync/async ADO.NET dispatch seams. `RelationalSchemaExecutor` owns the schema
connection lifetime, schema-operation dispatch, application-lock cleanup, fencing, and operation
transactions. A provider implements the public `RelationalDialect` class for that seam.

`RelationalDialect` is not a complete storage provider. `RelationalStorageSessionBase` and
`RelationalStorageSessionAdapter` expose one deliberate public composition seam over the shared
read/query/aggregation/CRUD state machines. `RelationalUnitOfWork`, `RelationalUnitOfWorkSession`,
and `RelationalUnitOfWorkLifetime` expose the shared staging and transaction-lifetime state machine.
`RelationalAppendAdapter` and `RelationalRetentionAdapter` expose only native commands while the
base retains validation, claim/replay, transaction, and `OnAppend` state machines. The provider
still owns its factory/connection, native command implementations, optional capabilities, and
driver resource/error mechanics. The individual shared state-machine classes remain internal
implementation.

The required public members are:

- ProviderName, identifier quoting, portable type/collation/default mapping, and column validation.
- DDL emission for table creation, column addition/finalization, index creation/removal. Column
  finalization receives both the column name and complete `ColumnDefinition`, so a provider can
  emit its type-specific `ALTER COLUMN` form.
- Conditional-upsert and bounded batch-insert SQL emission.
- Value conversion and TryMapUniqueViolation(DbException, out string indexName).
- Application-lock acquire/release/verify, server-session identity, fence acquisition/assertion, and
  infrastructure setup.
- History read and transactional publish. `PublishHistory` receives the active transaction, target,
  owner/fence, and the previously applied target fingerprint. It must compare-and-swap that old
  value before recording the new state; a null expected value means that no history row may exist.
- Catalog inspection (TableExists, ReadColumns, and ReadIndex). The shared executor uses these
  members for provider-neutral table/column/index presence and nullability validation, then calls
  `ValidateTarget` for provider-specific type or metadata checks. Their transaction parameter is
  nullable: runtime admission inspects the catalog read-only outside any transaction, so these
  members must work with a null transaction and must not write or take provider locks.

Optional provider-specific behavior is exposed through virtual hooks for column backfill, provider
schema definitions, and target validation. Returning null from BackfillColumnSql makes an
unsupported backfill explicit; the shared executor refuses that operation. Operation batches are
executed in one durable transaction with fencing before and after the batch; a failed operation
rolls back the complete batch. Dialect callbacks do not commit or roll back the transaction owned
by the shared executor.

An opted-in interop reporting view is a provider-owned schema definition. The shared relational
dialect emits the view from the declared physical columns, while the provider may override the
column expression for an idiomatic reporting type. View creation, replacement, and removal are
protected deployment-tool operations and must be represented in the target fingerprint and applied
ledger. A scoped view must retain `__groundwork_scope` and is an all-scopes read surface; database
permissions, not the view itself, provide authorization. Providers without one stable relational
catalog view must refuse the declaration before mutation. MySQL/MariaDB may implicitly commit DDL,
so its implementation documents reconciliation rather than promising full rollback for a failed
multi-operation batch.

The provider project references the substrate, `Groundwork.Store`, and `Groundwork.Kernel`
normally. It must not rely on `InternalsVisibleTo`, internal helper types,
contract-family assemblies, or provider assumptions in the substrate. The
`Groundwork.Samples.ExternalProviderStub` project compiles the complete public provider boundary and
marks every driver-owned operation explicitly; its dialect portion proves that schema reuse needs no
friend access.

A concrete provider retains connection-local schema-publication state outside the substrate. Every
direct, owned, and unit-of-work session must invoke its freshness guard from the adapter's
`EnsureUsable` path before observer accounting or ADO.NET dispatch. A successful changed-target
publication advances an epoch and causes earlier sessions to throw `StaleStorageSessionException`;
an unchanged fingerprint preserves the epoch. The concrete coordinator serializes application
through publication per connection so concurrent successful applies cannot publish out of durable
order.
