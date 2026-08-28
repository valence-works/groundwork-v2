# Groundwork.Query.Linq.Execution

`GwLinqExecutor` — the **one** execution adapter behind the closed LINQ front-end, for every
provider.

There is deliberately no per-provider executor. Everything an executor does is provider-neutral:
admitting the request through the shared coverage gate, honouring an explicit scan acceptance,
carrying the caller's paging and keyset continuation, materializing rows, and answering the async
terminals. A second copy of that per provider would be a second place for the coverage guarantee to
drift, and the whole value of a *closed* query surface is that the guarantee holds identically
everywhere.

What is genuinely provider-specific already lives with the provider: the SQL or query dialect,
behind `IStorageSession.Query`, and the native budgets it advertises through
`QueryAdmissionProfile`.

The same adapter also admits `UpdateWhere` and `DeleteWhere` through the read coverage gate before
delegating to a provider's `ISetMutationStorageSession` capability. Aggregate mode keeps the
provider-native affected-count path; `SetMutationOptions.Exact` takes a deterministic key snapshot
and reuses keyed writes to return one `WriteOutcome` per selected row.

## Pass the connection, not just the session

Both constructors work, but prefer `new GwLinqExecutor(session, connection)`. Given only the
session, an index that was *declared* but never *deployed* can still satisfy the coverage gate, and
the fence falls back to portable defaults instead of the provider's real budgets. With the
connection, coverage is checked against the catalog that actually exists.

## Referencing it

```bash
dotnet add package Groundwork.Query.Linq --prerelease
dotnet add package Groundwork.Query.Linq.Execution --prerelease
```

Pair it with your provider package. See
[`Groundwork.Query.Linq`](https://www.nuget.org/packages/Groundwork.Query.Linq) for the query
surface itself and why it is deliberately not `IQueryable`.

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
