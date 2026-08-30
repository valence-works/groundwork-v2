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
invocation. Tokens contain neither raw scope values nor audit strings. An audit sink is required:
`IStorageAccessObserver` receives `query-across-scopes.attempt` before native work and then
`query-across-scopes.success` or `query-across-scopes.failure`. If recording the attempt throws,
the provider is not called. If provider execution and failure recording both throw, the operation
fails with an aggregate that preserves both exceptions. Provider authors must use
`BeginPrivilegedQuery`; the obsolete one-shot `ObservePrivilegedQuery` helper cannot satisfy the
lifecycle contract. Identity and purpose are caller-supplied labels, not authentication;
bind them to the host's authenticated operation context. See [security boundaries](security-boundaries.md).

`__groundwork_scope` and `__groundwork_scope_token` are provider-owned logical
column names. Schema admission refuses application declarations that use them;
third-party providers can reuse `ProviderOwnedColumns.ValidateLogicalDeclaration`; the same call also
enforces the shared logical key/index reference rules before physicalization.

MongoDB cannot apply one pinned physical index across separate scope
collections and explicitly refuses that combination. Unpinned cross-scope
queries remain supported.
