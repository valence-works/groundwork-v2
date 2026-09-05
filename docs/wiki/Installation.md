# Installation

Groundwork previews are **not on nuget.org yet**. They are published to a public Feedz source:

```text
https://f.feedz.io/valence-works/groundwork/nuget/index.json
```

> A nuget.org publication pipeline exists (`.github/workflows/publish-nuget.yml`) but is manual-only:
> publishing a GitHub release does not start it. An intentional dispatch must provide the exact
> package version in both `version` and `confirm`, runs behind a protected environment, and needs a
> credential this repository does not hold. **Until a maintainer decides to publish, install from
> Feedz.** When that changes, this page will say so and the nuget.org steps will be the shorter
> ones — no `nuget.config` and no `--add-source`.

## Intentionally publishing to nuget.org

Feedz-only previews stay on the published-release workflow. If a maintainer deliberately wants the
same exact package closure on nuget.org, dispatch the NuGet workflow against the release tag and
repeat the exact version in both inputs:

```bash
gh workflow run publish-nuget.yml --ref v0.4.0-preview.15 \
  -f version=0.4.0-preview.15 -f publish=true -f confirm=0.4.0-preview.15
```

The workflow still requires the full package/test proof, package layout and integrity manifests,
the symbol push, the protected `nuget-org` environment, and a clean exact-version restore from
nuget.org after publication.

## 1. Configure the feed

Add a `nuget.config` next to your solution. Map `Groundwork.*` **exclusively** to the Feedz source
and keep nuget.org for everything else — this prevents a same-named package from another source
being resolved instead.

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
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
</configuration>
```

> Package source mapping is not optional hygiene here. Without it, restore order decides which feed
> wins, and a preview build can silently resolve differently on a teammate's machine or in CI.

## 2. Install the packages you need

Pin an **exact** version. Preview numbers are immutable and a published package is never replaced,
so an exact pin is reproducible.

```bash
dotnet add package Groundwork.Sqlite --version 0.4.0-preview.15
dotnet add package Groundwork.Records.Store --version 0.4.0-preview.15
```

A typical application references:

| You want | Packages |
| --- | --- |
| Typed rows on SQLite | `Groundwork.Sqlite`, `Groundwork.Records.Store` |
| Typed rows on MySQL/MariaDB | `Groundwork.MySql`, `Groundwork.Records.Store` |
| Typed rows on PostgreSQL | `Groundwork.PostgreSql`, `Groundwork.Records.Store` |
| Typed JSON documents | your provider + `Groundwork.Documents` |
| Raw kernel declarations only (no contract family) | your provider only (`Groundwork.Store` and `Groundwork.Kernel` come transitively) |
| Build-time query coverage enforcement | add `Groundwork.Analyzers` and `Groundwork.Schema.Generator` |
| Deployment-time schema tooling | `Groundwork.Tool` (a `dotnet tool`) and/or `Groundwork.SchemaTool.MSBuild` |
| Provider authoring / conformance | `Groundwork.Testing` |
| Hosting in ASP.NET Core or a generic host | add `Groundwork.Extensions.DependencyInjection` |
| Scaffold declarations from an EF Core model | `Groundwork.EntityFrameworkCore` |

See **[Package Map](Package-Map)** for the full list and the dependency rules.

> ⚠️ **Keep the whole Groundwork closure on one exact version.** Mixing preview versions across
> `Groundwork.*` packages is not supported and is not tested. If you bump one, bump all.

## 3. Target framework

Runtime packages multi-target **`net8.0`** and **`net10.0`**, so an application on either can
reference them.

```xml
<TargetFramework>net8.0</TargetFramework>
```

| Packages | Target | Why |
| --- | --- | --- |
| Providers, `Groundwork.Store`, `Groundwork.Kernel`, `Groundwork.Records*`, `Groundwork.Documents`, `Groundwork.EntityFrameworkCore`, `Groundwork.Testing`, the substrates, `Groundwork.Diagnostics`, `Groundwork.Query.Linq.Sqlite` | `net8.0`, `net10.0` | Referenced by your application or design-time scaffolder |
| `Groundwork.Analyzers`, `Groundwork.Schema.Generator`, `Groundwork.Schema`, `Groundwork.Query.Model`, `Groundwork.Query.Linq`, `Groundwork.Query.Planning` | `netstandard2.0` | Loaded by Roslyn and by build hosts |
| `Groundwork.Tool` | `net8.0`, `net10.0` | A `dotnet tool` runs on the deployment host's own runtime |
| `Groundwork.SchemaTool.MSBuild` | `net10.0` | See below |

The two runtime targets are the same code, not two variants. Nothing in the product is compiled
conditionally per target: where a .NET 9+ API was used, both targets now run one shared
implementation instead. Schema subject fingerprints, portable comparison keys, and the canonical
schema documents the CLI emits are pinned to literal values by tests that run on **each** target, so
a catalog applied from one and admitted from the other cannot disagree.

> ⚠️ **One deliberate exception.** `Groundwork.SchemaTool.MSBuild` targets `net10.0` only. An
> MSBuild task loads into the SDK's own MSBuild process rather than into your application, so its
> framework tracks the SDK you build *with*, not the framework you build *for* — and its
> `Microsoft.Build` dependency does not support `net8.0`. Your application can target `net8.0`;
> adding build-time schema verification to it requires a .NET 10 SDK on the build machine. Nothing
> else in Groundwork imposes that.

## 4. Verify the install

The smallest end-to-end proof — no schema tooling, no analyzer, just a file-backed SQLite catalog:

```csharp
using Groundwork.Kernel;
using Groundwork.Records;
using Groundwork.Sqlite;
using Groundwork.Store;

var table = RecordTable.For<Customer>("customers")
    .Key(c => c.Id)
    .Column(c => c.Email, col => col.MaxLength(320).Required())
    .Build();

using var connection = new SqliteProviderFactory().Create("Data Source=demo.db");
connection.Schema.Apply(table.Definition);

var records = table.Open(connection);
var result = records.Insert(new Customer(Guid.NewGuid(), "ada@example.test"));
Console.WriteLine(result.Status); // Inserted

public sealed record Customer(Guid Id, string Email);
```

If this runs, your feed, versions, and target framework are correct.

## Installing the schema tool

```bash
dotnet tool install --global Groundwork.Tool --version 0.4.0-preview.15 \
  --add-source https://f.feedz.io/valence-works/groundwork/nuget/index.json
```

The command is `groundwork`. See **[Schema Management](Schema-Management)**.

## What ships with each package

Every public package ships:

- **Its own readme** — the listing describes that package, not the family.
- **Source Link** — step into Groundwork source in the debugger, at the exact commit the package was
  built from. The commit is recorded in the package metadata and the map is embedded in the PDB.
- **A symbol package (`.snupkg`)** — published alongside the `.nupkg` on the same feed.
- **Deterministic builds** — the same commit produces byte-identical assemblies, and source paths are
  normalized so they carry no trace of the machine that built them.

None of that is a claim about project settings: `Groundwork.Packaging.Tests` packs the real
allowlist and asserts it against the resulting `.nupkg` and `.snupkg`.

## Next

- **[Package Map](Package-Map)** — what each package owns
- **[Hosting & Dependency Injection](Hosting-and-Dependency-Injection)** — `AddGroundwork()` and the connection lifetime
- **[Core Concepts](Core-Concepts)** — the mental model before you write real code
- **[Migrate from EF Core](EF-Core-Migration)** — scaffold and cut over an existing EF application
