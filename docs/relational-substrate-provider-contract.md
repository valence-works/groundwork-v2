# Relational substrate provider contract

Groundwork.Substrate.Relational keeps connection ownership, schema-operation dispatch,
application-lock cleanup, and fencing in RelationalSchemaExecutor. A provider implements the
public RelationalDialect class and supplies only provider-specific behavior.

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

The provider project references the substrate and Groundwork.Kernel normally. It must not rely on
InternalsVisibleTo, internal helper types, contract-family assemblies, or provider assumptions in
the substrate. A provider can therefore be maintained outside the Groundwork repository and still
implement the complete dialect contract.
