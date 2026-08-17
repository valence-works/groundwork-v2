---
title: Install Groundwork from Feedz
description: Configure the Groundwork Feedz source and install exact preview packages.
---

# Install Groundwork from Feedz

Groundwork packages are published only to the public Valence Works Feedz
source. Map `Groundwork.*` to Feedz and leave third-party packages on their
normal source:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="Groundwork" value="https://f.feedz.io/valence-works/groundwork/nuget/index.json" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="Groundwork">
      <package pattern="Groundwork.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
```

For the SQLite typed-record quickstart, install the exact preview used by your
application:

```bash
dotnet add package Groundwork.Records.Store --version 0.1.0-preview.1
dotnet add package Groundwork.Sqlite --version 0.1.0-preview.1
```

Provider packages bring the provider-neutral Store contract transitively.
Applications using lower-level declarations directly can reference
`Groundwork.Kernel` and `Groundwork.Store` explicitly.

Next: [run the compiled SQLite quickstart](quickstart.md).
