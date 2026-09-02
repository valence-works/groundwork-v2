# Groundwork.EntityFrameworkCore

[Groundwork v2 documentation portal](https://github.com/valence-works/groundwork-v2/wiki) contains the full consumer documentation.

Design-time scaffolding from an existing EF Core relational model to provider-neutral Groundwork
kernel declarations.

Pass `DbContext.Model`, a compiled `IModel`, or a migrations snapshot's `Model` to
`EfCoreModelImporter.Import`. The result contains actual `StorageUnit` declarations plus structured
`GW-EF-*` findings for every semantic decision the importer cannot make honestly. Foreign keys
become logical references, provider culture collations require explicit locale sort-key mappings,
global query filters require an explicit scope decision, and floating point remains storage-only.
Views, split/inherited/complex shapes, unsupported generation/concurrency, and declarations that
fail the kernel portability validator remain blocking findings instead of losing semantics.

The package never loads an application assembly or starts its host. The caller creates the model in
its normal design-time environment, where executing compiled model or snapshot code is explicit.

Full usage and refusal semantics:
[Importing an EF Core model](https://github.com/valence-works/groundwork-v2/blob/main/docs/v2/ef-core-import.md).
The consumer migration path, including a worked customer/order cutover, is
[Migrate from EF Core](https://github.com/valence-works/groundwork-v2/blob/main/docs/wiki/EF-Core-Migration.md).

## Referencing it

```bash
dotnet add package Groundwork.EntityFrameworkCore --prerelease
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
before adopting it in production.

This package targets `net8.0` and `net10.0` and uses the matching EF Core relational metadata
version for each target.

MIT licensed. Source and issues: <https://github.com/valence-works/groundwork-v2>.
