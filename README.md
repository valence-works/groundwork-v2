# Groundwork v2

Groundwork is a provider-neutral persistence kernel for .NET. A single logical
storage declaration can be mapped to SQLite, PostgreSQL, SQL Server, or MongoDB
without making provider concerns part of the public model.

This repository is the greenfield implementation of the
[Groundwork v2 program](https://github.com/orgs/valence-works/projects/5).
Program issues and delivery status remain in
[`valence-works/Groundwork`](https://github.com/valence-works/Groundwork/issues).

The first public packages are pre-1.0 previews. See the [v2 versioning
policy](docs/v2/versioning.md) and [provider support
matrix](docs/v2/support-matrix.md) before consuming a preview package.

The [public documentation portal](docs/portal/index.md) contains the compiled
quickstart, concepts and guides, provider pages, versioned reference material,
and the generated .NET API. Build and verify it locally with:

```shell
eng/build-docs.sh
eng/verify-docs-site.sh
```

## Consume preview packages

Groundwork previews are published to the public Feedz source:

```text
https://f.feedz.io/valence-works/groundwork/nuget/index.json
```

Map `Groundwork.*` exclusively to that source and keep nuget.org for third-party
dependencies. For example:

```xml
<packageSources>
  <clear />
  <add key="Groundwork Preview" value="https://f.feedz.io/valence-works/groundwork/nuget/index.json" />
  <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
</packageSources>
<packageSourceMapping>
  <packageSource key="Groundwork Preview">
    <package pattern="Groundwork.*" />
  </packageSource>
  <packageSource key="nuget.org">
    <package pattern="*" />
  </packageSource>
</packageSourceMapping>
```

Then install the exact preview required by the application:

```shell
dotnet add package Groundwork.Sqlite --version 0.1.0-preview.1
```

## Build

```shell
dotnet restore Groundwork.slnx
dotnet test Groundwork.slnx --no-restore
```

The shared integration branch is `codex/groundwork-v2`; issue branches merge
there before the completed program is integrated into `main`.
