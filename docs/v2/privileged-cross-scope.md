# Audited cross-scope queries

Use privileged access only for recovery, administration, or diagnostics over a
unit declared with `ScopePolicy.Scoped`.

```csharp
var access = StorageAccess.PrivilegedAcrossScopes(
    new StorageAccessAudit(
        identity: "workflow-recovery-worker",
        purpose: "recover-stalled-workflows",
        observer: auditSink));

var session = connection.OpenSession(workflowUnit, access);
var result = session.QueryAcrossScopes(request);

foreach (var row in result.Rows)
    Console.WriteLine($"{row.Scope.Value}: {row.Values["id"]}");
```

The returned `CrossScopeQueryRow` always pairs public values with the scope that
owned them. Counted paging, continuation tokens, narrow projections, and
`LatestPerKey` retain provider-neutral behavior. `LatestPerKey` partitions by
scope before applying its logical key.

The access is deliberately query-only. `Query`, `Read`, writes, append,
retention, aggregation, inspection, and `BeginUnitOfWork` fail before provider
work. Open an ordinary `StorageAccess.Scoped(scope)` session for those
operations.

Audit identity and purpose bind continuation tokens to the privileged
invocation. Tokens contain neither raw scope values nor audit strings. An
`IStorageAccessObserver` receives one `query-across-scopes` event before each
native query.

`__groundwork_scope` and `__groundwork_scope_token` are provider-owned logical
column names. Schema admission refuses application declarations that use them;
third-party providers can reuse `ProviderOwnedColumns.ValidateLogicalDeclaration`.

MongoDB cannot apply one pinned physical index across separate scope
collections and explicitly refuses that combination. Unpinned cross-scope
queries remain supported.
