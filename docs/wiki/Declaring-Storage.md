# Declaring Storage

The kernel declaration is the single source of truth. Everything — provider DDL, query coverage,
schema fingerprints, capability requirements — is derived from it.

## The fluent builder

```csharp
using Groundwork.Kernel;

var unit = StorageUnit.Declare("log-record", "log_records")
    .Int64("seq", c => c.Required().ProviderSequence())
    .String("traceId", 64, c => c.Required())
    .String("level", 16, c => c.Required())
    .Timestamp("occurredAt", c => c.Required())
    .String("message", 4000)
    .Json("attributes")
    .Key("seq")
    .Index("by_trace", x => x.Column("traceId").Column("seq"))
    .Index("by_time",  x => x.Descending("occurredAt").Descending("seq"))
    .Scoped()
    .AppendIdempotency(window: TimeSpan.FromMinutes(10))
    .KeepNewest(1_000_000, orderBy: "seq", trigger: RetentionTrigger.OnAppend)
    .Aggregate("by-trace-summary", a => a
        .GroupBy("traceId")
        .Min("firstSeen", "occurredAt")
        .Max("lastSeen", "occurredAt")
        .SetUnion("levels", "level", maxValues: 8)
        .FirstBy("firstMessage", "message", orderBy: "seq"))
    .Build();
```

That is the complete event-log declaration from `samples/Groundwork.Samples.EventLog`. It references
only `Groundwork.Kernel` — no Records, no Documents, no provider.

`Declare(id, name)` takes the **logical id** first and the **physical name** second.

## Column types

`PortableType` is the whole type system:

| `PortableType` | Notes | Predicates | Ordering | Index key |
| --- | --- | --- | --- | --- |
| `String` | `MaxLength` required for index keys | ✅ | ✅ | ✅ |
| `Int32`, `Int64` | Exact | ✅ | ✅ | ✅ |
| `Decimal` | **Must** declare `Precision` + `Scale`; portable predicates require `(18,4)` | ✅ | ✅ | ✅ |
| `Boolean` | Equality + total complement only | equality only | ❌ `GW-SEM-ORDER-005` | ✅ |
| `DateTimeOffset` | Compared as UTC ticks, 7 fractional digits | ✅ | ✅ | ✅ |
| `Guid` | RFC-4122 network-byte key | ✅ | ✅ | ✅ |
| `Binary` | Exact equality/membership only | equality only | ❌ `GW-SEM-ORDER-001` | equality only |
| `Json` | Opaque payload | ❌ | ❌ | ❌ `GW-DECL-INDEX-003` |
| `Double` | Storage-only IEEE-754 binary64 | ❌ `GW-SEM-TYPE-006` | ❌ `GW-SEM-ORDER-001` | ❌ `GW-PORT-012` |

`Double` is **declarable and storable but never comparable**. Binary64 round-trips bit-for-bit on
PostgreSQL `double precision`, SQL Server `float`, SQLite `REAL`, and MongoDB `double`, so telemetry,
coordinates, and embeddings can be declared and stored. Comparison is a different question: rounding
and index behaviour differ across stores, so predicates, ordering, index membership, key membership,
and aggregation grouping are all refused. Declare `Decimal(18,4)` or `Int64` for a value you query on.

Only the binary64 values that every store returns unchanged can be written. NaN and the infinities
are refused outright by SQL Server and SQLite, and negative zero comes back as positive zero from
SQLite and MongoDB, so all four are refused at the write with `GW-VALUE-DOUBLE-001` rather than
stored and quietly changed. There is deliberately **no `Single`**: SQLite `REAL` and BSON `double`
are both binary64, so a 32-bit column would be a widened one on half the providers.

A *declared default* is narrower still, because it reaches the store through DDL rather than as a
parameter: SQL Server's float literal parser flushes a subnormal to zero, so a subnormal default is
refused with `GW-PORT-013` even though the same value is perfectly writable as a value.

```csharp
.Double("reading")                              // stored, never compared
.Double("calibration", c => c.Required().Default(0.1))
```

### Typed helpers

```csharp
.String("email", 320, c => c.Required())
.String("bio")                                  // unbounded — cannot be an index key
.Int32("attempts", c => c.Default(0))
.Int64("sequence", c => c.Required().ProviderSequence())
.Decimal("total", 18, 4)
.Boolean("isActive", c => c.Required().Default(false))
.Timestamp("createdAt", c => c.Required())      // alias for DateTimeOffset(...)
.Guid("id", c => c.Required())
.Binary("hash", 32)
.Json("metadata")
.Column("dynamic", runtimeType)                 // runtime-typed alias
```

### `ColumnBuilder` policies

| Method | Effect |
| --- | --- |
| `.Required()` / `.Nullable()` | Nullability |
| `.MaxLength(n)` | Bounded width. **Required** for variable-length index key columns (`GW-PORT-003`) |
| `.Precision(p, s)` | Decimal precision/scale. Both required (`GW-PORT-002`) |
| `.Collation(PortableCollation.…)` | `Ordinal`, `OrdinalIgnoreCase`, `UnicodeOrdinalIgnoreCase` |
| `.Default(value)` | Portable default |
| `.ProviderSequence()` | Provider-assigned monotonic `Int64` |

## Keys

```csharp
.Key("id")                  // single column
.Key("tenantId", "orderId") // composite
```

A `ProviderSequence` column must be **non-nullable `Int64` and the sole primary-key column** of its
unit (`GW-PORT-005`). Supplying its value on `Insert` is refused.

> **MongoDB caveat:** changing the *order* of composite key columns after a schema has been applied
> is refused (`GW-PORT-008`), because the physical `_id` composition would change meaning.

## Indexes

```csharp
.Index("by_email", "email")                              // simple ascending
.UniqueIndex("uq_email", "email")
.Index("by_time", x => x.Descending("createdAt").Descending("seq"))
.Index("sparse_ref", x => x.Column("externalRef").ExcludeMissingValues())
```

`IndexBuilder` exposes `Column(name)` (ascending alias), `Ascending(name)`, `Descending(name)`, and
missing-value behavior.

**`MissingValueBehavior`** decides whether rows with a null/missing indexed value are kept in the
index (`Included`, default) or omitted (`Excluded`, a sparse index). This is load-bearing:

- A **unique** index over nullable columns with `Included` is refused (`GW-PORT-001`) — cross-provider
  uniqueness of multiple nulls is genuinely ambiguous, so Groundwork will not guess.
- A pinned index that **excludes** nulls is refused for a query whose predicate could match an
  excluded null — the v1 sparse-index safety rule, preserved.

Two indexes with the same physical signature are refused (`GW-PORT-009`): consolidate them onto one.
Duplicate index names are refused (`GW-PORT-011`).

## Physical naming rules

Physical identifiers (`Name`, index names, ledger names) must be:

- ASCII letters, digits, underscores
- starting with a letter or underscore
- **at most 63 ASCII bytes**
- not using the reserved `__groundwork_` prefix

Violations are `GW-PORT-010`, reported with the offending name and the corrective action. The
provider refuses the declaration **before** any schema I/O, so a bad name never reaches the database.

Reserved provider-owned logical names include `__groundwork_scope` and `__groundwork_scope_token`.
Provider authors can reuse `ProviderOwnedColumns.ValidateLogicalDeclaration(unit)` to enforce this.

## Validating before you deploy

```csharp
var result = PortabilityValidator.Validate(unit);
if (!result.IsPortable)
{
    foreach (var refusal in result.Refusals)
        Console.WriteLine($"{refusal.Code} at {refusal.Path}: {refusal.Message}");
}
```

Every refusal carries a **code**, a **path** into the declaration, and a **message naming the fix**.
`Build()` runs this automatically and throws `StorageDeclarationException` / `DeclarationBuildException`
carrying the same diagnostics.

You can also run it as a build gate — see `Groundwork.SchemaTool.MSBuild` in
**[Schema Management](Schema-Management)**.

## Attribute-based declarations

For source-generated canonical schemas (used by the analyzer and the CLI):

```csharp
[GwTable("customers")]
[GwIndex("by_email", "email ASC", Unique = true)]
public sealed class Customer
{
    [GwKey] public Guid Id { get; init; }
    [GwColumn(Length = 320, Required = true)] public string Email { get; init; } = "";
    [GwColumn(Folding = TextFolding.UnicodeOrdinalIgnoreCase, Length = 200)] public string Name { get; init; } = "";
}
```

`Groundwork.Schema.Generator` emits an assembly-level `[GroundworkSchema(canonicalJson, fingerprint)]`
that the analyzer and `groundwork` CLI read. The fingerprint is canonical — `groundwork schema emit`
produces the identical value for the same schema.

## Case-insensitive text and search keys

`PortableCollation.OrdinalIgnoreCase` and `UnicodeOrdinalIgnoreCase` do **not** turn into
database-side case folding. No renderer emits `LOWER()`. Instead the schema owns a versioned,
provider-owned **persisted search-key column** (`__groundwork_search_*`) with a recorded
`AlgorithmId`.

Consequences you need to plan for:

- The query's comparison policy must match the declared mapping **exactly**, or rendering fails with
  `GW-QUERY-031`.
- Changing folding or prefix-boundary encoding is a **rebuild**, not an additive metadata edit.
  Opening a session against drifted derived columns fails with an actionable rebuild diagnostic.
- SQL Server validates the physical key budget against the logical source width using an expansion
  factor: **5×** for ASCII ignore-case, **7×** for Unicode ordinal ignore-case.

Ordinal `StartsWith` needs none of this — it lowers to an exact `[prefix, successor)` range on the
base column and creates no derived column.

## Lifecycle policies on a declaration

```csharp
.Scoped()                                                   // multi-tenant
.OptimisticConcurrency()                                    // or .Optimistic("version")
.AppendIdempotency(TimeSpan.FromMinutes(10))                // durable append replay
.Retention(keepNewest: 1000, orderBy: "seq",
           trigger: RetentionTrigger.OnAppend,
           partitionColumns: "tenantId")
.RetentionIdempotency(TimeSpan.FromHours(24))               // requires Retention (GW-RETENTION-004)
.Aggregate("summary", a => a.GroupBy("status").Count("n"))
```

Append and retention ledgers must use **distinct** names, and neither may claim a Groundwork-reserved
name.

The attribute surface declares the same policies, so an attribute or canonical-file declaration
compiles to the same physical target the fluent builder does:

```csharp
[GwTable("orders", Scope = SchemaScope.Scoped, ConcurrencyToken = "version")]
[GwRetention(1000, "seq", Trigger = SchemaRetentionTrigger.OnAppend, PartitionBy = "status")]
[GwAppendIdempotency("00:10:00")]
[GwRetentionIdempotency("1.00:00:00")]
[GwAggregate("summary", "group status, count n")]
public sealed class Order
{
    [GwKey, GwColumn(Length = 64)] public string Id { get; init; } = "";
    [GwColumn(Length = 16, Default = "pending")] public string Status { get; init; } = "";
    [GwColumn(Required = true)] public long Seq { get; init; }
}
```

Idempotency windows are `TimeSpan` text, so a day is `1.00:00:00` — `24:00:00` parses as 24 days.

## Next

- **[Portable Semantics](Portable-Semantics)** — the exact rules behind the refusals
- **[Schema Management](Schema-Management)** — getting the declaration into a database
