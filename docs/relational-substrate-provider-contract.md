# Relational substrate provider contract

Groundwork.Substrate.Relational keeps connection ownership, schema-operation dispatch,
application-lock cleanup, and fencing in RelationalSchemaExecutor. A provider implements the
public RelationalDialect class and supplies only provider-specific behavior.

The required public members are:

- ProviderName, identifier quoting, portable type/collation/default mapping, and column validation.
- DDL emission for table creation, column addition/finalization, index creation/removal.
- Conditional-upsert and bounded batch-insert SQL emission.
- Value conversion and TryMapUniqueViolation(DbException, out string indexName).
- Application-lock acquire/release/verify, server-session identity, fence acquisition/assertion, and
  infrastructure setup.
- History read/publish and catalog inspection (TableExists, ReadColumns, and ReadIndex).

Optional provider-specific behavior is exposed through virtual hooks for column backfill, provider
schema definitions, and target validation. Returning null from BackfillColumnSql makes an
unsupported backfill explicit; the shared executor refuses that operation.

The provider project references the substrate and Groundwork.Kernel normally. It must not rely on
InternalsVisibleTo, internal helper types, contract-family assemblies, or provider assumptions in
the substrate. A provider can therefore be maintained outside the Groundwork repository and still
implement the complete dialect contract.
