# Groundwork v2

Groundwork is a provider-neutral persistence kernel for .NET. A single logical
storage declaration can be mapped to SQLite, MySQL/MariaDB, PostgreSQL, SQL Server, or MongoDB
without making provider concerns part of the public model.

This repository is the greenfield implementation of the
[Groundwork v2 program](https://github.com/orgs/valence-works/projects/5).
Program issues and delivery status remain in
[`valence-works/Groundwork`](https://github.com/valence-works/Groundwork/issues).

The first public packages are pre-1.0 previews. See the [v2 versioning
policy](docs/v2/versioning.md) and [provider support
matrix](docs/v2/support-matrix.md) before consuming a preview package.

## Documentation

The consumer-facing documentation lives in the
[project wiki](https://github.com/valence-works/groundwork-v2/wiki). Start with
[Installation](https://github.com/valence-works/groundwork-v2/wiki/Installation)
and [Core Concepts](https://github.com/valence-works/groundwork-v2/wiki/Core-Concepts).

The wiki is generated from `docs/wiki/` in this repository and published by the
`publish-wiki` workflow on every push to `main`. Edit the markdown there and open
a pull request; direct edits in the wiki UI are overwritten on the next publish.

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
dotnet add package Groundwork.Sqlite --version 0.4.0-preview.14
```

## Sample application

`samples/Groundwork.Samples.Api` is a runnable ASP.NET Core minimal API covering
declaration, schema deployment, typed CRUD, a covered query with paging, a unit
of work, optimistic concurrency, and tenant scopes — switchable across all four
providers from configuration.

```shell
dotnet run --project samples/Groundwork.Samples.Api
```

See its [README](samples/Groundwork.Samples.Api/README.md).

`samples/Groundwork.Samples.NativeAotApi` is the SQLite-backed package-only Native AOT proof. It
publishes to a real native executable, uses generated Records metadata with zero dynamic code, and
records reproducible startup and deploy-size observations separately from correctness gates. See
its [README](samples/Groundwork.Samples.NativeAotApi/README.md).

## Build

```shell
dotnet restore Groundwork.slnx
dotnet test Groundwork.slnx --no-restore
```

Issue branches merge into `main` directly. Agents working this repository should
read `AGENTS.md` first: it covers claiming an issue, the release-note and
diagnostics conventions, and how to run the provider suites on a shared machine.
