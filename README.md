# Groundwork v2

Groundwork is a provider-neutral persistence kernel for .NET. A single logical
storage declaration can be mapped to SQLite, PostgreSQL, SQL Server, or MongoDB
without making provider concerns part of the public model.

This repository is the greenfield implementation of the
[Groundwork v2 program](https://github.com/orgs/valence-works/projects/5).
Program issues and delivery status remain in
[`valence-works/Groundwork`](https://github.com/valence-works/Groundwork/issues).

## Build

```shell
dotnet restore Groundwork.slnx
dotnet test Groundwork.slnx --no-restore
```

The shared integration branch is `codex/groundwork-v2`; issue branches merge
there before the completed program is integrated into `main`.
