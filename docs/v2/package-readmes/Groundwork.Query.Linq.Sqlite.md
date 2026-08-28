# Groundwork.Query.Linq.Sqlite

The SQLite execution adapter for the closed LINQ front-end. Open a SQLite session with
`Groundwork.Sqlite`, then configure `new GwQueryDatabase(new SqliteLinqExecutor(session))`, and
`query.Where(...).ToListAsync(cancellationToken)` executes without you implementing an executor.

Kept deliberately separate from `Groundwork.Sqlite` so the provider itself does not reference the
LINQ contract family — a provider never knows a contract family exists, and CI enforces that.

`Count()` executes the provider's total-count shape over a single-row page and `Any()` a limit-1
probe; neither materializes matching rows, and a result without a provider-side total count is
refused rather than counted client-side.

`Sum`, `Min`, and `Max` use the same adapter seam and issue a scalar reduction request over the
selected mapped column. All four providers render these reductions natively, applying distinct and
input paging before aggregation without client-side row fallback; nullable reductions ignore nulls
and Int32 sums widen to Int64.

Full adapter notes, including how the async terminals behave:
[docs/query-linq-sqlite.md](https://github.com/valence-works/groundwork-v2/blob/main/docs/query-linq-sqlite.md).

## Referencing it

```bash
dotnet add package Groundwork.Sqlite --prerelease
dotnet add package Groundwork.Query.Linq.Sqlite --prerelease
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
