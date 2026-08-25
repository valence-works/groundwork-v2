# Documents

`Groundwork.Documents` stores a typed object as **canonical versioned JSON** in an ordinary row,
with selected values **projected into real typed columns** so they can be indexed and queried.

It is not a second write path. A document maps to a plain `RowWrite` and executes through the same
`Groundwork.Store` session as everything else.

## When to use Documents instead of Records

| Use **Records** when | Use **Documents** when |
| --- | --- |
| The shape is stable and flat | The shape evolves, or is nested/heterogeneous |
| Every field should be a column | Only a few fields need to be queryable |
| You want the simplest possible mapping | You need explicit schema versioning and upcasters |

## Declaring a document unit

```csharp
using Groundwork.Documents;
using Groundwork.Store;

public sealed record Note(Guid Id, string CustomerId, string Body);

var unit = DocumentUnit.For<Note>(documentKind: "note", name: "notes")
    .Id(note => note.Id)
    .Project(note => note.CustomerId)          // becomes a real typed column
    .Index("by_customer", note => note.CustomerId)
    .OptimisticConcurrency()
    .SchemaVersion(current: 2, minimumReadable: 1)
    .Build();

connection.Schema.Apply(unit.StorageUnit);
```

| Builder method | Effect |
| --- | --- |
| `Id(selector)` | The native key column. **Required** (`GW-DOC-DECL-001`) |
| `Project(selector)` | Projects a JSON value into a typed column so it can be indexed/queried |
| `Index(name, selector)` | Index over a **projected** column (`GW-DOC-DECL-005` if not projected) |
| `OptimisticConcurrency()` | Opt into the system-owned version token |
| `Scoped()` | Multi-tenant unit |
| `SharedKind()` | Multiple document kinds share one physical unit; `kind` joins the key |
| `SchemaVersion(current, minimumReadable)` | Versioning policy |
| `Upcaster(...)` | Register a JSON upcaster |
| `JsonOptions(...)` | Custom `JsonSerializerOptions` |

`unit.StorageUnit` (alias `unit.Definition`) is the plain kernel declaration.

## Writing and reading

```csharp
var note = new Note(Guid.NewGuid(), "ada@example.test", "Welcome");

var write   = unit.Insert(note, WriteOptions.CreateOnly);
var outcome = unit.Execute(connection, write);
// outcome.Status == WriteOutcomeStatus.Inserted

var session   = connection.OpenSession(unit.StorageUnit, StorageAccess.Global);
var persisted = session.Read(new StorageKey(
    new Dictionary<string, object?> { [unit.IdColumn] = note.Id }));

var materialized = unit.Read(new RowValues(persisted!.Values.Values), persisted.Version);
// materialized.Value == note, materialized.Version == outcome.Version
```

Mapping methods: `Insert`, `Update`, `Upsert`, `Delete` → `RowWrite`; `ToRowWrite(value, mode, options)`
for the general form. Execution: `unit.Execute(connection, write, access?)`.

Because the result is an ordinary `RowWrite`, documents compose with **[units of
work](Unit-of-Work-and-Batching)** and mix freely with Records writes in the same transaction.

Materialization: `unit.Materialize(rowValues)` returns `T`; `unit.Read(rowValues, version)` returns
`DocumentReadResult<T>(Value, Version)`.

## Projected columns

A projection is a **real typed column** in the physical schema, filled from the JSON on write.

- Only projected values can be indexed. Indexing an unprojected path is `GW-DOC-DECL-005`.
- **JSON projections are not portable index keys** (`GW-DOC-DECL-006`) — project a scalar instead.
- Duplicate paths (`GW-DOC-DECL-002`) and colliding column names (`GW-DOC-DECL-003`) are refused.
- A member that the effective JSON contract can omit is refused (`GW-DOC-DECL-009`) with the
  corrective action — a projected column must always have a defined value.
- Enums with unsigned underlying types are refused (`GW-DOC-DECL-007`); a custom enum converter must
  emit string or integral JSON (`GW-DOC-DECL-008`).

## Schema versioning and upcasters

Documents are stored with an explicit schema version, and old versions are **upcast on read**.

```csharp
public sealed class NoteV1ToV2 : IDocumentJsonUpcaster
{
    public string DocumentKind => "note";
    public int FromVersion => 1;

    public JsonObject Upcast(JsonObject content)
    {
        content["customerId"] ??= content["customer"];
        content.Remove("customer");
        return content;
    }
}

var unit = DocumentUnit.For<Note>("note", "notes")
    .Id(n => n.Id)
    .SchemaVersion(current: 2, minimumReadable: 1)
    .Upcaster(new NoteV1ToV2())
    .Build();
```

- `minimumReadable` is a **hard floor**. A document older than it raises
  `DocumentSchemaVersionException` rather than being silently misread.
- Upcasters chain from the stored version up to `current`.
- `DocumentSchemaVersionException` carries `Failure`, `DocumentKind`, `DocumentId`, `SchemaVersion`,
  `ParsedVersion`, `MinimumReadableVersion`, and `CurrentVersion` — enough to log the exact offending
  document without re-reading it.

`VersionedJsonDocumentCodec` is exposed as `unit.Codec` if you need to serialize or inspect payloads
directly (`Serialize`, `Deserialize`, `IsCurrentVersion`).

## Canonical JSON

Serialization is canonical: object properties are sorted, and numeric lexemes such as `1`, `1.0`,
and `1e0` are normalized **without converting through a lossy floating-point representation**. This
is what lets equivalent payloads produce identical fingerprints for idempotency and change detection.

## Package boundary

`Groundwork.Documents` depends on `Groundwork.Records` and `Groundwork.Store` only. CI builds it
from a **local package feed with no project references** (`documents-package-boundary`) to prove the
boundary is real and not an artifact of the solution graph.

## Diagnostics

| Code | Meaning |
| --- | --- |
| `GW-DOC-DECL-001` | Missing `Id` selector |
| `GW-DOC-DECL-002` | JSON path projected more than once |
| `GW-DOC-DECL-003` | Column name collides with a reserved/declared column |
| `GW-DOC-DECL-004` | Duplicate index name |
| `GW-DOC-DECL-005` | Index targets an unprojected path |
| `GW-DOC-DECL-006` | Index targets a JSON projection |
| `GW-DOC-DECL-007` | Enum with unsupported unsigned underlying type |
| `GW-DOC-DECL-008` | Enum JSON converter unusable or emits an unsupported kind |
| `GW-DOC-DECL-009` | Projected member can be omitted by the JSON contract |
| `GW-DOC-MAT-001`…`004` | Materialization failures (`DocumentMaterializationException`) |

## Next

- **[Writing Data](Writing-Data)** — the shared write path
- **[Querying](Querying)** — querying projected columns
