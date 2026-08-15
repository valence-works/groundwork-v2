# Groundwork.Documents

`Groundwork.Documents` is an optional typed layer over `Groundwork.Records` and
the provider-neutral `Groundwork.Store` contracts.
It composes ordinary kernel `StorageUnit` declarations, stores canonical JSON
alongside typed projections, and provides versioned JSON upcasting. Providers
do not receive a document-specific contract: a mapped write is an ordinary
`RowWrite` carrying `StorageValues` with the same columns and values as any
other record.

## Stable storage contract

`DocumentUnit.For<T>(kind, name)` requires a native typed `Id` member. `Build()`
declares the typed key column, the required `document` JSON column, and the
required `schemaVersion` string column. `SharedKind()` additionally declares a
required `kind` string column. Optimistic concurrency and scope are opt-in
Kernel declarations; timestamps are not synthesized or persisted by this
layer. A provider-owned version result is carried separately from the JSON
application fields by `DocumentReadResult<T>`.

`ToRowValues` writes the native ID value (for example, a `Guid` remains a
`Guid`), canonical JSON, and a stable `vN` schema stamp. A projection writes a
typed value extracted from its serialized JSON path. Missing or JSON `null`
values map to `null`; arrays and objects remain `JsonElement` values. The
`ColumnBinding` list is Documents metadata and is not part of the Kernel
declaration.

`Insert`, `Update`, `Upsert`, and `Delete` return ordinary `Groundwork.Store`
`RowWrite` values. The same mapped row values and caller-supplied `WriteOptions`
are used by `Execute(connection, write, access)`, which opens a provider-neutral
`IStorageSession`; the caller owns the connection lifetime. No document-specific
provider command or system-owned concurrency token is introduced.

`SharedKind()` makes `kind` part of the ordinary composite key (`kind`, `id`),
so equal IDs from distinct kinds cannot collide. Materialization rejects a
missing or mismatched discriminator and rejects JSON whose typed ID differs from
the provider row key.

## Canonical JSON and paths

The JSON document is a compact object. Object properties are ordered by their
ordinal serialized names recursively; array order and scalar representation
are preserved. `JsonPropertyName` takes precedence, followed by the configured
`JsonSerializerOptions.PropertyNamingPolicy`. When options are omitted,
Documents uses the web-style lower-camel default; when options are supplied,
their contract is honored exactly, so `new JsonSerializerOptions()` preserves
CLR names such as `Customer.Name`. Consequently, projection paths are based on
the serialized contract, not CLR member spelling. Configure options before or
after `Project()`; names and portable projection types resolve at `Build()`.
Selected fields must be enabled by `IncludeFields` or `[JsonInclude]`. Ignored
or otherwise unserializable selected members are refused with
`GW-DOC-DECL-009`.

Schema policies use a caller-owned `DocumentSchemaVersionFormat` and contiguous
upcaster steps. Malformed, too-old, future, unknown-kind, invalid-content, and
broken-upcaster cases raise `DocumentSchemaVersionException` with a structured
failure code and context. A declaration refuses ambiguous mappings with
actionable diagnostics: `GW-DOC-DECL-001` (missing ID), `002` (duplicate JSON
path), `003` (column collision), `004` (duplicate index), `005` (index without
a projection), `006` (index over JSON), `007` (unsupported unsigned enum), and
`008` (an enum converter whose output is not a supported scalar), and `009`
(a selected ID or projection member that is not serialized).
