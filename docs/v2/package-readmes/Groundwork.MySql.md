# Groundwork.MySql

[Groundwork v2 documentation portal](https://github.com/valence-works/groundwork-v2/wiki) contains the full consumer documentation.

The MySQL/MariaDB provider for the provider-neutral `Groundwork.Store` contracts. It is built on
`Groundwork.Substrate.Relational` and `MySqlConnector`, with provider-specific code limited to the
dialect, catalog, locking/fencing, and native command adapters.

The first provider release targets MySQL 8.0.17+ and MariaDB 11.4.13+ with InnoDB. At startup the
provider verifies that `utf8mb4_0900_bin` exists and has NO PAD semantics; it refuses older or
incompatible servers rather than silently changing key equality. Ordinal string columns declare
that collation, while query and key comparisons additionally use binary expressions so case,
accents, supplementary characters, and trailing spaces are not delegated to a server default.
Folded portable collations remain refused. Generated Int32/Int64 keys use `AUTO_INCREMENT`; schema
application locks retain their exact physical connection through `GET_LOCK`/`RELEASE_LOCK`.
The repository's [provider evidence report](https://github.com/valence-works/groundwork-v2/blob/main/docs/v2/mysql-provider-evidence.md)
records the reproducible before/after source comparison and the live correctness, schema-tool, and
concurrency lanes.

**Support tier:** Production-supported for the live-tested MySQL 8.4.6 topology using InnoDB, the
runtime-verified NO PAD `utf8mb4_0900_bin`, and one writable primary endpoint. Other MySQL 8.0.17+
releases and MariaDB 11.4.13+ are compatibility-only until matching live evidence exists. See the
[support matrix](https://github.com/valence-works/groundwork-v2/blob/main/docs/v2/support-matrix.md)
and [operations runbook](https://github.com/valence-works/groundwork-v2/blob/main/docs/v2/production-operations.md).

## Referencing it

```bash
dotnet add package Groundwork.MySql --prerelease
dotnet add package Groundwork.Records.Store --prerelease
```

Previews are published to the public
[Groundwork Feedz source](https://f.feedz.io/valence-works/groundwork/nuget/index.json), not yet to
nuget.org. Pin one exact version across the complete `Groundwork.*` closure; mixing versions is not
supported. Runtime packages target `net8.0` and `net10.0`.

MIT licensed. Source and issues: <https://github.com/valence-works/groundwork-v2>.
