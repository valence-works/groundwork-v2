# Native AOT minimal API

This ASP.NET Core minimal API is the concrete Native AOT proof for Groundwork. It uses the shipped
SQLite provider and exercises a source-generated declaration, generated Records accessors, an
asynchronous unit of work, point reads, and an index-covered typed query through a native HTTP
executable.

The sample deliberately references only one provider. The broader
[`Groundwork.Samples.Api`](../Groundwork.Samples.Api/README.md) demonstrates four-provider switching;
including all four drivers here would make the deploy-size evidence meaningless.

## Run from source

Development schema creation is explicit. The default remains inspect-only.

```bash
ASPNETCORE_ENVIRONMENT=Development \
Groundwork__DevelopmentApplySchema=true \
Groundwork__ConnectionString='Data Source=groundwork-native-aot.db' \
dotnet run --project samples/Groundwork.Samples.NativeAotApi
```

Then exercise the generated declaration, unit of work, and query:

```bash
curl localhost:5000/health
curl -X POST localhost:5000/todos -H 'content-type: application/json' \
  -d '{"id":"todo-1","title":"Ship Native AOT","isDone":false}'
curl localhost:5000/todos/todo-1
curl 'localhost:5000/todos?done=false'
```

`TodoItemStorageUnit.Definition` and the row accessor are emitted by
`Groundwork.Schema.Generator`. `RecordTable.FromGenerated<T>` binds them without reflection or
runtime code generation and refuses an ungenerated row instead of falling back.

## Reproduce the native proof

The verifier packs the public package closure, copies this sample outside the repository, selects
only `PackageReference` inputs, publishes it with `PublishAot=true`, verifies the ELF/Mach-O output,
starts the native process, and drives its HTTP endpoints.

```bash
dotnet restore Groundwork.slnx --nologo -m:1
eng/pack-public-packages.sh artifacts/aot-packages 0.2.0-aot.local
GROUNDWORK_AOT_STARTUP_RUNS=7 \
  samples/Groundwork.Samples.NativeAotApi/verify-native-aot.sh \
  artifacts/aot-packages osx-arm64
```

Use `linux-x64` on Linux. The generated evidence is written to
`artifacts/native-aot-sample/evidence.md`.

## Published local baseline

Measured on 2026-08-29 with .NET 10, `osx-arm64`, on an Apple arm64 host. The SQLite catalog was
created by a separate smoke launch; startup observations use that pre-applied catalog with runtime
auto-apply disabled.

| Measurement | Result |
| --- | ---: |
| Native main executable | 16,300,576 bytes |
| Self-contained deploy payload, excluding PDB files | 88,765,225 bytes |
| Spawn-to-first-`/health`, 7 launches, median | 150 ms |
| Spawn-to-first-`/health`, 7 launches, p95 | 194 ms |
| Generated Records dynamic-code count | 0 |

These are reproducible observations, not regression thresholds. Host load, SDK/runtime version,
RID, filesystem cache, and hardware affect them. There is no EF Core comparison in this repository
yet, so this sample makes no measured comparative claim; #185 owns like-for-like benchmark work.
The wedge proved here is the closed generated application path—package restore, native compilation,
and provider-backed execution—not an unsupported claim that these numbers beat another stack.

The `Native AOT conformance` workflow owns native publish and HTTP correctness. Repeated startup and
size capture is also wired into the manual-only `Performance evidence` workflow, preserving the
repository's correctness/concurrency/performance lane split.
