# Native AOT

Groundwork's Native AOT path is deliberately closed and generated. Runtime packages build with trim
and dynamic-code analyzer findings promoted to errors, while applications generate row accessors,
materializers, schema declarations, and JSON metadata at compile time.

## Generated Records declarations

Annotate the row and add `Groundwork.Schema.Generator` as an analyzer:

```csharp
[GwTable("todos")]
public sealed record Todo(
    [property: GwKey, GwColumn(Required = true)] string Id,
    [property: GwColumn(Required = true)] string Title);

var table = RecordTable.FromGenerated<Todo>(TodoStorageUnit.Definition);
```

`FromGenerated` is the trim-safe construction path. It uses the generator's registered accessor and
fails closed when generated metadata is absent. `RecordTable.For<T>` remains a managed compatibility
surface because it infers a declaration from CLR members at runtime.

Keep JSON on a source-generated `JsonSerializerContext` and use ASP.NET Core's request-delegate
generator. Options-based arbitrary-object JSON, fluent Records inference, and runtime-compiled
Records projections/aggregations remain explicitly annotated compatibility boundaries.

## Executable proof

[`samples/Groundwork.Samples.NativeAotApi`](https://github.com/valence-works/groundwork-v2/tree/main/samples/Groundwork.Samples.NativeAotApi)
is a SQLite-backed ASP.NET Core minimal API. Its package-only verifier publishes a real ELF/Mach-O
executable and drives schema admission, a unit of work, a point read, and a covered query over HTTP.
The sample README contains current size/startup observations and the exact reproduction command.

The repository keeps the evidence lanes separate:

- `Native AOT conformance` proves exact-head package restore, native compilation, and execution.
- `Performance evidence` is manual-only and records repeated startup/size observations.
- `Concurrency` remains independent and is not implied by either AOT result.

A skipped workflow is not evidence. During a CI cost pause, run the same verifier locally and record
the exact command and native executable description on the pull request.
