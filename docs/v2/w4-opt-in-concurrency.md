# W4: opt-in optimistic concurrency

Concurrency is explicit in the v2 storage contract. A unit that does not opt in
uses `ConcurrencyDeclaration.None`; providers must not add a version column,
version metadata, token projection, or version read/increment/CAS work for that
unit. `ConcurrencyDeclaration.Optimistic("version")` opts the unit in. The
token name is logical and system-owned; providers may map it to a hidden
physical token. When a declared token column is present in the portable schema,
it must be a non-null `Int64` with default `0`.

```csharp
var unit = new StorageUnit
{
    Id = new StorageUnitId("orders"),
    Name = "orders",
    Columns =
    [
        new ColumnDefinition { Name = "id", Type = PortableType.String, IsNullable = false },
        new ColumnDefinition { Name = "total", Type = PortableType.Decimal },
    ],
    Key = new KeyDefinition { Columns = ["id"] },
    Concurrency = ConcurrencyDeclaration.None
};
```

`None` is the default. To opt in, use `ConcurrencyDeclaration.Optimistic()`;
the provider creates its physical token with default `0`, while the first
accepted write returns logical version `1`. Applications cannot supply or
change the token value. Supplying it is rejected with diagnostic
`GW-WRITE-CONCURRENCY-003`.

## Explicit write preconditions

The write API does not use a nullable expected-version field. Choose one
precondition deliberately:

```csharp
session.Insert(values, WriteOptions.CreateOnly);
session.Update(values, WriteOptions.IfVersion(3));
session.Upsert(values, WriteOptions.Unconditional);
```

`Unconditional`, `CreateOnly`, and `IfVersion(long)` are distinct values of
`WritePrecondition`. `CreateOnly` and `IfVersion` are rejected on a
`ConcurrencyDeclaration.None` unit with diagnostic
`GW-WRITE-CONCURRENCY-001`. Invalid operation/precondition pairings fail with
`GW-WRITE-CONCURRENCY-002`. The same validation is performed by direct writes
and `RowWrite` construction, before provider I/O.

For optimistic units, a successful insert starts at version `1`; an accepted
update or upsert increments the token; a stale `IfVersion` returns
`ConcurrencyConflict`. An optimistic conditional upsert remains a single
provider write primitive. None-mode conditional upsert is still a single
write, but it reports logical `Inserted`/`Updated` outcomes without any token
machinery.

## Staged writes and proof

`RowWrite` applies the same precondition and system-owned-token validation as
direct writes. A staged non-unconditional precondition uses the provider's
row-attributed fallback when a native multi-row command cannot preserve its
semantics. Unconditional homogeneous groups retain the W3 native batch paths.
The conformance suite exercises both a None unit and an optimistic unit,
including catalog/index checks, CRUD outcomes, optimistic conflict behavior,
scope isolation, and unit-of-work commit/rollback. The deterministic W2
harness runs both concurrency modes; SQLite and in-memory runs are available
without external services, while live PostgreSQL, SQL Server, and MongoDB
matrix tests activate through their documented connection-string variables.
