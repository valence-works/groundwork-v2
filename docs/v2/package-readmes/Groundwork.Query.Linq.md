# Groundwork.Query.Linq

A **closed** LINQ front-end: `IGwQueryable<T>`, with `Table<T>()`, `Where`, `WhereIf`, ordering,
`Skip`/`Take`, mapped-column `Select`, `Distinct`, and the `ToList`/`ToListAsync`,
`Count`/`CountAsync`, `Any`/`AnyAsync`, `First`/`FirstOrDefault`, `Single`/`SingleOrDefault`,
`Sum`/`SumAsync`, `Min`/`MinAsync`, and `Max`/`MaxAsync` terminals. `First` and `FirstOrDefault`
require an explicit deterministic order; `Single` and `SingleOrDefault` use a bounded over-one
probe. Reductions select one mapped column: `Sum` accepts `Int32`, `Int64`, and `Decimal` (integer
results are `Int64`), while `Min` and `Max` accept orderable columns and return null for empty or
all-null input when the result type is nullable.

Deliberately *not* `IQueryable`. An open provider surface is what lets an expression compile
happily and then fall back to client-side evaluation, or fail at runtime on one database and not
another. A closed surface can be checked completely: every shape this package admits is a shape
`Groundwork.Query.Planning` and `Groundwork.Analyzers` can prove is covered by a declared index
before the code ships.

Execution needs an adapter — see
[`Groundwork.Query.Linq.Sqlite`](https://www.nuget.org/packages/Groundwork.Query.Linq.Sqlite).

## Referencing it

```bash
dotnet add package Groundwork.Query.Linq --prerelease
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
