# Groundwork v2

Groundwork is a provider-neutral typed storage foundation for .NET applications.
The package containing this README is one component of the v2 public package
family. Use the package that matches the application boundary you need:

- `Groundwork.Kernel`, `Groundwork.Store`, and `Groundwork.Records` provide the
  provider-neutral storage and typed-record contracts.
- `Groundwork.Documents` provides typed documents on the ordinary row-write
  path.
- `Groundwork.Sqlite`, `Groundwork.PostgreSql`, `Groundwork.SqlServer`, and
  `Groundwork.MongoDb` provide native provider implementations.
- `Groundwork.Testing` provides the public conformance contracts and reference
  provider for provider authors.
- `Groundwork.Tool` and `Groundwork.SchemaTool.MSBuild` provide deployment-time
  schema planning and build verification.

All v2 packages are pre-1.0 previews. Read the [versioning policy](https://github.com/valence-works/groundwork-v2/blob/main/docs/v2/versioning.md)
and [support matrix](https://github.com/valence-works/groundwork-v2/blob/main/docs/v2/support-matrix.md)
before adopting a provider in production.

Previews are published through the public
[Groundwork Feedz source](https://f.feedz.io/valence-works/groundwork/nuget/index.json).
Configure that source for `Groundwork.*` packages before restoring a preview.

Scoped recovery and administration use the explicit, query-only
[`StorageAccess.PrivilegedAcrossScopes`](https://github.com/valence-works/groundwork-v2/blob/main/docs/v2/privileged-cross-scope.md)
contract; ordinary scoped sessions remain tenant-isolated.

```bash
dotnet add package Groundwork.Kernel --prerelease
```

The package is licensed under MIT. Source and issue tracking are available at
<https://github.com/valence-works/groundwork-v2>.
