# Groundwork.Documents

`Groundwork.Documents` is an optional typed layer over `Groundwork.Records`.
It composes ordinary kernel `StorageUnit` declarations, stores canonical JSON
alongside typed projections, and provides versioned JSON upcasting. Providers
do not receive a document-specific contract: a mapped write is an ordinary
`RowValues` record containing the same columns and values as any other record.

## Stable storage contract

`DocumentUnit.For<T>(kind, name)` requires a native typed `Id` member. `Build()`
declares the typed key column, the required `document` JSON column, and the
required `schemaVersion` string column. `SharedKind()` additionally declares a
required `kind` string column. Optimistic concurrency and scope are opt-in
Kernel declarations; timestamps are not synthesized or persisted by this
layer. A provider-owned version result is returned separately from
`DocumentReadResult<T>`.

`ToRowValues` writes the native ID value (for example, a `Guid` remains a
`Guid`), canonical JSON, and a stable `vN` schema stamp. A projection writes a
typed value extracted from its serialized JSON path. Missing or JSON `null`
values map to `null`; arrays and objects remain `JsonElement` values. The
`ColumnBinding` list is Documents metadata and is not part of the Kernel
declaration.

## Canonical JSON and paths

The JSON document is a compact object. Object properties are ordered by their
ordinal serialized names recursively; array order and scalar representation
are preserved. `JsonPropertyName` takes precedence, followed by the configured
`JsonSerializerOptions.PropertyNamingPolicy`, followed by the lower-camel
default. Consequently, projection paths are based on the serialized contract,
not CLR member spelling. Configure options before or after `Project()`; names
and portable projection types resolve at `Build()`.

Schema policies use a caller-owned `DocumentSchemaVersionFormat` and contiguous
upcaster steps. Malformed, too-old, future, unknown-kind, invalid-content, and
broken-upcaster cases raise `DocumentSchemaVersionException` with a structured
failure code and context. A declaration refuses ambiguous mappings with
actionable diagnostics: `GW-DOC-DECL-001` (missing ID), `002` (duplicate JSON
path), `003` (column collision), `004` (duplicate index), `005` (index without
a projection), `006` (index over JSON), `007` (unsupported unsigned enum), and
`008` (an enum converter whose output is not a supported scalar).
