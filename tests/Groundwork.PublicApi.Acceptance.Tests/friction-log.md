# Clean-room public API friction log

This log is part of the acceptance fixture. Each entry records a consumer-visible constraint
encountered while building the package-only SQLite journey and the resulting resolution.

| Finding | Resolution | Status |
| --- | --- | --- |
| Provider sessions and typed Records writes must share one lifetime-owning object. | `SqliteProviderFactory.Create` returns the production `IStorageProviderConnection`; `RecordTable.Open(connection)` and `DocumentUnit.Execute(connection, RowWrite)` use that same public connection. | Resolved |
| Optimistic tokens are system-owned and are not supplied in mapped application values. | Declare `.OptimisticConcurrency()` and use `RecordWriteOptions.IfVersion(version)` / `WriteOptions.IfVersion(version)` for exact conditional operations. | Resolved |
| A typed query needs an explicitly declared and selected index for deterministic coverage. | Declare `.Index("by-email", ...)`, use `RecordQueryOptions.UsingIndex("by-email")`, and fail closed with `QueryCoverageException` when deployed coverage is absent. | Resolved |
| Documents are provider-neutral and must not expose a provider-specific execution wrapper. | Map to an ordinary `RowWrite`, execute through `DocumentUnit.Execute` over `IStorageProviderConnection`, and materialize through `DocumentReadResult<T>`. | Resolved |
| Schema drift in derived folded search keys must be repaired before admitting a session. | Apply the declared collation/index schema and treat the provider's actionable rebuild diagnostic as the repair instruction. | Resolved |
| A restore still needs NuGet.org for third-party dependencies, which could otherwise hide use of a published Groundwork package. | Copy the freshly packed artifacts into the external project and map `Groundwork.*` exclusively to that local feed; map only third-party packages to NuGet.org. | Resolved |
| Running the boundary test inside the ordinary solution test graph could start a nested pack while sibling projects are building. | Keep the acceptance project outside `Groundwork.slnx` and run the explicit pack-then-test sequence in its dedicated CI job. | Resolved |

The consumer contains no source `ProjectReference`, testing adapter, internal namespace, reflection,
or friend-assembly access. Any future workaround must be added here and either removed by a public
API improvement or explicitly accepted with rationale.
