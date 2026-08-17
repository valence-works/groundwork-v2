---
title: SQLite quickstart
description: Declare, apply, write, and query a typed Groundwork table.
---

# SQLite quickstart

The following is the repository's compiled and executed quickstart. It uses
only public package APIs and a real temporary SQLite database.

[!code-csharp[](../../../../samples/Groundwork.Samples.Quickstart/Program.cs)]

Run it from the repository:

```bash
dotnet run --project samples/Groundwork.Samples.Quickstart
```

Expected output:

```text
Found Ada Lovelace <ada@example.test>
```

The sample deliberately selects the declared `by_email` index. Groundwork
validates query shapes and index choices before provider execution instead of
silently accepting an uncovered query.
