# Groundwork.Sqlite

[Groundwork v2 documentation portal](https://github.com/valence-works/groundwork-v2/wiki) contains the full consumer documentation.

The SQLite provider: the SQLite implementation of the provider-neutral `Groundwork.Store` contracts
and relational schema behaviors, built on `Groundwork.Substrate.Relational` and
`Microsoft.Data.Sqlite`.

Requires SQLite 3.35.0 or newer — the Groundwork write contract depends on modern upsert and
`RETURNING` behavior, and opening an older native library fails with a version diagnostic rather
than misbehaving. The provider enables WAL and a busy timeout when a store opens, and schema and
unit-of-work writes begin an immediate write transaction so a read transaction is never upgraded
into a `BUSY_SNAPSHOT` failure.

Portable ordinal strings are stored with the registered `GROUNDWORK_UTF16_ORDINAL` collation, and
ordinary indexes inherit it, so equality and range predicates and index ordering use .NET UTF-16
ordinal semantics — including supplementary characters — and native plans can use the declared
indexes.

**Support tier:** Production-supported for a file-backed SQLite 3.35.0+ database on local-locking
storage, with one long-lived provider connection and one application writer process per file.
`:memory:` is development/reference-only. See the
[support matrix](https://github.com/valence-works/groundwork-v2/blob/main/docs/v2/support-matrix.md)
and [operations runbook](https://github.com/valence-works/groundwork-v2/blob/main/docs/v2/production-operations.md).

Full provider notes:
[docs/sqlite-provider.md](https://github.com/valence-works/groundwork-v2/blob/main/docs/sqlite-provider.md).

## Referencing it

```bash
dotnet add package Groundwork.Sqlite --prerelease
dotnet add package Groundwork.Records.Store --prerelease
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
