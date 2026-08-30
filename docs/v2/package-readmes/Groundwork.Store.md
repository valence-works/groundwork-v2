# Groundwork.Store

The runtime contract that providers implement and applications call.
`IStorageProviderConnection`, `IStorageSession`, `IUnitOfWork`, `StorageAccess`, `StorageScope`,
`WriteOutcome`, `RowWrite`, `BatchWriteOptions`, set-based mutation capabilities, and the execution
of retention, exact append, and durable idempotency.

Set-based mutation defaults to the provider-native affected-count result:

```csharp
var changed = session.UpdateWhere(predicate, assignments);
long count = changed.MatchedRows;
```

Use `SetMutationOptions.Exact` when one keyed `WriteOutcome` per selected row is required:

```csharp
var result = session.DeleteWhere(predicate, SetMutationOptions.Exact);
foreach (var item in result.Outcomes)
    Console.WriteLine($"{item.Key.Values[\"id\"]}: {item.Outcome.Status}");
```

Exact mode takes a deterministic key snapshot and applies the existing keyed mutation contract, so
its outcomes preserve provider-neutral version and conflict/not-found statuses. On a unit-of-work
session, any earlier staged writes flush before that snapshot and later staged keyed writes run
after the set operation, all inside the unit's transaction. Exact mode can require one read and one
keyed write per selected row; choose the default aggregate mode when whole-set atomicity and an
affected count are sufficient outside a unit of work.

Provider-native `ISetMutationStorageSession` methods are not application entry points. Provider
authors must call `SetMutationExecutionAdmission.Require(where)` before validation, flush,
rendering, or I/O; only the admitted extensions can create that evidence. Direct calls refuse with
`GW-COVER-001` instead of bypassing coverage or explicit scan acceptance.

Sessions execute declared aggregation profiles and runtime-composed profiles only when the latter
carry an active `AggregationAcceptance`; scoped and privileged-access refusals apply to both.

Sessions capture the declaration published by their provider connection. If that connection later
applies a different declaration, retained sessions throw `StaleStorageSessionException`
(`GW-RUNTIME-005`) before provider I/O; open a new session after schema application.

Store sits directly above `Groundwork.Kernel` and knows nothing about contract families: a provider
implementing these interfaces never learns that `Groundwork.Records` or `Groundwork.Documents`
exists. Scoped sessions are tenant-isolated; cross-scope reads require the explicit, query-only
[`StorageAccess.PrivilegedAcrossScopes`](https://github.com/valence-works/groundwork-v2/blob/main/docs/v2/privileged-cross-scope.md)
contract, which is audited.
The audit sink is required at execution time and receives attempt plus success/failure lifecycle
events. Provider authors use `StorageAccessValidation.BeginPrivilegedQuery` and complete the
returned `StorageAccessAuditOperation` around their native execution.

## Referencing it

A provider package brings this transitively. Reference it directly when you write a provider or a
contract family of your own.

```bash
dotnet add package Groundwork.Store --prerelease
```

## Every Groundwork package

Previews are published to the public
[Groundwork Feedz source](https://f.feedz.io/valence-works/groundwork/nuget/index.json), not yet to
nuget.org, so configure that source for `Groundwork.*` before restoring — see
[Installation](https://github.com/valence-works/groundwork-v2/blob/main/docs/wiki/Installation.md).

Pin one exact version across your whole `Groundwork.*` closure — mixing versions is not supported
and not tested. Previews are pre-1.0; read the
[versioning policy](https://github.com/valence-works/groundwork-v2/blob/main/docs/v2/versioning.md)
and the [support matrix](https://github.com/valence-works/groundwork-v2/blob/main/docs/v2/support-matrix.md)
before adopting a provider in production. The
[package map](https://github.com/valence-works/groundwork-v2/blob/main/docs/wiki/Package-Map.md)
explains the layering and which packages an application actually references.

Runtime packages target `net8.0` and `net10.0`. Analyzers, source generators, and the portable
contract packages target `netstandard2.0`. `Groundwork.SchemaTool.MSBuild` targets `net10.0`
because its task loads into the SDK's own MSBuild process.

MIT licensed. Source and issues: <https://github.com/valence-works/groundwork-v2>.
