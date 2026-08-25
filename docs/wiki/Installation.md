# Installation

Groundwork previews are **not on nuget.org**. They are published to a public Feedz source:

```text
https://f.feedz.io/valence-works/groundwork/nuget/index.json
```

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
dotnet add package Groundwork.Sqlite --version 0.2.0-preview.1
dotnet add package Groundwork.Records.Store --version 0.2.0-preview.1
```

A typical application references:

| You want | Packages |
| --- | --- |
| Typed rows on SQLite | `Groundwork.Sqlite`, `Groundwork.Records.Store` |
| Typed rows on PostgreSQL | `Groundwork.PostgreSql`, `Groundwork.Records.Store` |
| Typed JSON documents | your provider + `Groundwork.Documents` |
| Raw kernel declarations only (no contract family) | your provider only (`Groundwork.Store` and `Groundwork.Kernel` come transitively) |
| Build-time query coverage enforcement | add `Groundwork.Analyzers` and `Groundwork.Schema.Generator` |
| Deployment-time schema tooling | `Groundwork.Tool` (a `dotnet tool`) and/or `Groundwork.SchemaTool.MSBuild` |
| Provider authoring / conformance | `Groundwork.Testing` |

See **[Package Map](Package-Map)** for the full list and the dependency rules.

> ⚠️ **Keep the whole Groundwork closure on one exact version.** Mixing preview versions across
> `Groundwork.*` packages is not supported and is not tested. If you bump one, bump all.

## 3. Target framework

Runtime packages target **`net10.0`**. Analyzer, schema, and query-model packages target
`netstandard2.0` so they can be consumed by tooling and older build hosts, but the provider and
Store packages require .NET 10.

```xml
<TargetFramework>net10.0</TargetFramework>
```

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
dotnet tool install --global Groundwork.Tool --version 0.2.0-preview.1 \
  --add-source https://f.feedz.io/valence-works/groundwork/nuget/index.json
```

The command is `groundwork`. See **[Schema Management](Schema-Management)**.

## What ships with each package

Every public package ships:

- **SourceLink** — step into Groundwork source in the debugger.
- **A symbol package (`.snupkg`)** — published alongside the `.nupkg` on the same feed.
- **Deterministic builds** — the same commit produces byte-identical assemblies.

## Next

- **[Package Map](Package-Map)** — what each package owns
- **[Core Concepts](Core-Concepts)** — the mental model before you write real code
