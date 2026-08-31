# Migrate from EF Core

Groundwork is not a drop-in EF Core provider. EF Core starts from an object graph and a change
tracker; Groundwork starts from an explicit portable storage contract. A safe migration therefore
has two parts:

1. scaffold and review the storage contract, and
2. replace each implicit ORM behavior with an explicit Groundwork operation.

The `Groundwork.EntityFrameworkCore` package helps with the first part. It reads an already-created
relational EF model and returns real `StorageUnit` declarations plus a deterministic report of every
semantic decision it could not make safely. It does not load an application assembly, start a host,
connect to a database, translate migrations, or modify the model.

This guide uses a small orders application to take the migration from inventory through schema
deployment and application cutover.

---

## Concept map

| EF Core concept | Groundwork equivalent | Important difference |
| --- | --- | --- |
| `DbContext` | One process-long `IStorageProviderConnection` per named connection, with short sessions and units of work | There is no change tracker or identity map. |
| `DbSet<T>` | `RecordTable<T>` or a kernel `StorageUnit` | The declaration is the portable contract, not a view of an arbitrary existing table. |
| `SaveChanges` | `IUnitOfWork.Stage` followed by `Commit` or `CommitWithOutcomes` | Writes are explicit. Groundwork does not discover changed objects. |
| `Include` / navigation loading | One declared reference activated with `Join`, or a keyed batch read | There is no lazy loading and no arbitrary include graph. |
| LINQ over `IQueryable<T>` | Groundwork's closed `IGwQueryable<T>` or the query AST | Unsupported expressions are refused; there is no client-evaluation fallback. |
| EF migrations | A target declaration, `groundwork plan`, and an authorized `groundwork apply` | Groundwork plans from desired state and its applied ledger, not from an imperative `Up`/`Down` chain. |
| Existing compatible catalog | `groundwork adopt` | Adoption performs no DDL and succeeds only after exact catalog inspection. |
| Data migration in `migrationBuilder.Sql(...)` | A named `DataMigration` with a pure `IDataMigrationTransform`, or an application-owned backfill | The runner and ledger make a Groundwork migration resumable; the transform itself cannot perform arbitrary I/O. |
| Global tenant query filter | `ScopePolicy.Scoped` plus `StorageAccess.Scoped(scope)` | Scope is provider-enforced and explicit at session open; it is not an application predicate. |
| Soft-delete or business filter | An ordinary declared predicate | Do not label a non-tenant filter as a storage scope. |
| Concurrency token | `.OptimisticConcurrency()` and `IfVersion(...)` | The token is system-owned and returned as an outcome, not copied from a tracked entity. |
| Identity column | A non-null `Int64` sole key with `ProviderSequence` | Other identity shapes are refused rather than approximated. |
| Value converter | Explicit application-boundary conversion and a portable stored type | The importer refuses converters because their provider representation is not the CLR contract. |

The practical rule is simple: if an EF feature made database work happen implicitly, name that work
explicitly before cutting it over.

---

## Worked migration: customers and orders

Assume the existing EF application has two ordinary entities and one relationship:

```csharp
public sealed class Customer
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<Order> Orders { get; set; } = [];
}

public sealed class Order
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public decimal Total { get; set; }
    public Customer Customer { get; set; } = null!;
}

protected override void OnModelCreating(ModelBuilder model)
{
    model.Entity<Customer>(entity =>
    {
        entity.ToTable("customers");
        entity.HasKey(customer => customer.Id);
        entity.Property(customer => customer.Name).HasMaxLength(200).IsRequired();
    });

    model.Entity<Order>(entity =>
    {
        entity.ToTable("orders");
        entity.HasKey(order => order.Id);
        entity.Property(order => order.Total).HasPrecision(18, 4).IsRequired();
        entity.HasIndex(order => order.CustomerId).HasDatabaseName("by_customer");
        entity.HasOne(order => order.Customer)
            .WithMany(customer => customer.Orders)
            .HasForeignKey(order => order.CustomerId);
    });
}
```

The old write and read rely on implicit EF behavior:

```csharp
db.Customers.Add(customer);
db.Orders.Add(order);
await db.SaveChangesAsync();

var details = await db.Orders
    .Include(candidate => candidate.Customer)
    .SingleAsync(candidate => candidate.Id == orderId);
```

### 1. Scaffold the contract

First configure the preview Feedz source and package-source mapping from
**[Installation](Installation#1-configure-the-feed)**. Pin the same exact Groundwork version for the
importer, typed bridge, and chosen provider, then create the `DbContext` or design-time model in
application-controlled code:

```bash
dotnet add package Groundwork.EntityFrameworkCore --version 0.4.0-preview.1
dotnet add package Groundwork.Records.Store --version 0.4.0-preview.1
dotnet add package Groundwork.PostgreSql --version 0.4.0-preview.1
```

Install the `Groundwork.Tool` CLI as described in **[Installation](Installation#installing-the-schema-tool)**
before the deployment steps below.

```csharp
using Groundwork.EntityFrameworkCore;

var import = EfCoreModelImporter.Import(dbContext);

foreach (var finding in import.Findings)
{
    Console.WriteLine(
        $"{finding.Severity} {finding.Code} {finding.Target}: " +
        $"{finding.Message} Alternative: {finding.Alternative}");
}

if (!import.IsComplete)
    throw new InvalidOperationException("Resolve every EF import error before adopting the scaffold.");

foreach (var declaration in import.Declarations)
    Console.WriteLine($"{declaration.Id.Value} -> {declaration.Name}");
```

Run this against the migrations snapshot when migration-only annotations such as defaults or
computed SQL matter. A compiled runtime model may legitimately omit them.

`IsComplete` means the inferred declarations passed the portability gate. It does **not** mean that
the current physical catalog uses Groundwork's representations, that every application query is
covered, or that a navigation has become a typed CLR binding. Treat the result as reviewed
scaffolding and copy the accepted decisions into durable application source.

Typical decisions at this point are intentional product changes, not importer configuration:

- replace a queryable `double` with `Decimal` or a scaled `Int64`;
- flatten an owned/complex value into named scalar columns;
- move a provider SQL default into application code or a portable declared default;
- keep a read-only view outside Groundwork;
- replace a value converter with an explicit boundary type;
- choose whether an EF query filter is truly a tenant boundary.

### 2. Adopt durable typed declarations

Use separate persistence rows during the transition. That prevents EF-only navigation and tracking
state from becoming part of the Groundwork contract:

```csharp
using Groundwork.Records;

public sealed class CustomerRow
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
}

public sealed class OrderRow
{
    public Guid Id { get; init; }
    public Guid CustomerId { get; init; }
    public decimal Total { get; init; }

    // Bound below as a logical reference; it is not stored as a column.
    public CustomerRow Customer { get; init; } = null!;
}

public static class ShopStorage
{
    public static RecordTable<CustomerRow> Customers { get; } =
        RecordTable.For<CustomerRow>("customers")
            .Key(customer => customer.Id)
            .Column(customer => customer.Name, column => column.MaxLength(200).Required())
            .Build();

    public static RecordTable<OrderRow> Orders { get; } =
        RecordTable.For<OrderRow>("orders")
            .Key(order => order.Id)
            .Column(order => order.Total, column => column.Precision(18, 4).Required())
            .Index("by_customer", order => order.CustomerId)
            .Reference("customer", order => order.Customer, Customers, order => order.CustomerId)
            .Build();
}
```

The reference is logical by default. It gives query planning a portable relationship without
claiming that every deployment supports or already has a physical foreign-key constraint. Opt in
to physical constraints separately only when the deployment advertises that capability. EF cascade
behavior is not copied into a logical reference.

For Native AOT, express the same durable declarations with `Groundwork.Schema.Generator` and bind
the generated record accessors; the reflection-based `RecordTable.For<T>` form above is the compact
migration example, not the trimmed-runtime recommendation.

The importer result and the Records builder are not serializers for the CLI artifact. Transcribe the
reviewed declarations into one supported `Groundwork.Schema` document/additional-file source, then
canonicalize that source explicitly:

```bash
groundwork schema emit \
  --input schema-source.json \
  --file groundwork.schema.json
```

Keep that reviewed schema source in version control. The generated `groundwork.schema.json` is what
`plan`, `adopt`, `apply`, build verification, and deployment consume; do not imply that
`EfCoreModelImporter.Import` writes it. See **[Declaring Storage](Declaring-Storage)** for the JSON
and source-generator forms.

### 3. Decide whether to adopt or create storage

Do this against a disposable copy of production first.

The commands below consume the canonical artifact emitted in the previous step, not the in-memory
import result. If the existing catalog exactly matches that Groundwork physical target, adopt it:

```bash
groundwork plan \
  --schema groundwork.schema.json \
  --provider postgresql \
  --connection "$GROUNDWORK_CONNECTION" \
  --output json

groundwork adopt \
  --schema groundwork.schema.json \
  --provider postgresql \
  --connection "$GROUNDWORK_CONNECTION" \
  --safe
```

`adopt` executes no DDL. It inspects names, portable physical types, nullability, defaults,
collations, keys, generation, and indexes, then writes applied history only if all of them match.
Any untolerated difference is a refusal. That includes missing Groundwork-owned columns required by
an opted-in policy. A declaration that explicitly uses
`ForeignColumnPolicy.TolerateDatabaseSupplied` may retain extra database-supplied columns as warned
`ToleratedDrift`; it does not make other differences compatible. Do not weaken the declaration to
make adoption pass.

An ordinary EF catalog often does **not** match. Timestamp representations, collations, generated
columns, defaults, identifiers, and indexes may differ even when the CLR model looks identical. In
that case:

1. give the Groundwork units new physical names;
2. deploy them additively with `groundwork apply --safe`;
3. backfill through an application-owned job;
4. dual-write for a bounded verification window; and
5. cut reads over only after row counts and domain invariants agree.

A Groundwork `IDataMigrationTransform` can transform rows already present in one Groundwork target;
it is pure row-in/value-out code and cannot read an old EF database or another store. Use it for an
in-place target transformation after data is present, not as a cross-store copy mechanism.

Do not run EF migrations and Groundwork schema application as competing owners of the same objects.
During coexistence, give each physical object one schema owner.

### 4. Replace `SaveChanges` with an explicit unit of work

The equivalent transaction names every unit it can touch and every write it intends to perform:

```csharp
using Groundwork.Kernel;
using Groundwork.Store;

var customer = new CustomerRow { Id = customerId, Name = "Ada" };
var order = new OrderRow { Id = orderId, CustomerId = customerId, Total = 125.5000m };

using var work = storage.BeginUnitOfWork(
    StorageAccess.Global,
    BatchWriteOptions.Exact,
    ShopStorage.Customers.Definition,
    ShopStorage.Orders.Definition);

work.Stage(RowWrite.Insert(
    ShopStorage.Customers.Definition,
    new StorageValues(ShopStorage.Customers.ToRowValues(customer).Values)));
work.Stage(RowWrite.Insert(
    ShopStorage.Orders.Definition,
    new StorageValues(ShopStorage.Orders.ToRowValues(order).Values)));

var report = await work.CommitWithOutcomesAsync();
Console.WriteLine($"Applied {report.Applied} of {report.Submitted} staged writes.");
```

There is no call that scans the object graph for changes. Updates, deletes, version preconditions,
and create-only behavior are explicit `RowWrite` or typed Records operations. An attributed failure
throws `BatchWriteException`, poisons the unit against further work, and identifies the refused
writes. Call `Rollback`, or let the `using` scope dispose the non-terminal unit and roll it back.
Commit, rollback, and dispose are terminal.

Choose `BatchWriteOptions.Default` when aggregate counts are enough. Exact outcomes are useful for
cutover evidence but can cost more, particularly on MongoDB.

### 5. Replace `Include` with one declared join

Activate the reference by name, then project both sides explicitly:

```csharp
using Groundwork.Records;
using Groundwork.Store;

var customerReference = ShopStorage.Orders.Reference<CustomerRow>("customer");
var joined = customerReference.Join(ShopStorage.Orders.Query)
    .Where(order => order.Id == orderId)
    .Take(1);

var projection = ShopStorage.Orders.Select(
    joined,
    customerReference,
    (order, customer) => new OrderDetails(
        order.Id,
        order.Total,
        customer.Id,
        customer.Name));

var orders = ShopStorage.Orders.Open(storage.Connection, StorageAccess.Global);
var details = orders.Query(projection).SingleOrDefault();

public sealed record OrderDetails(
    Guid OrderId,
    decimal Total,
    Guid CustomerId,
    string CustomerName);
```

Groundwork accepts one activated declared reference. It refuses an undeclared navigation, a second
join, a deeper navigation, and an arbitrary LINQ `Join`. That boundary is deliberate: it keeps the
same query meaning and qualification on every provider.

For a collection include, several relationships, or a deeper graph, query the source rows first and
batch-read each target set. The shared primitive chunks under the connected provider's real key and
payload budgets and preserves requested-key order:

```csharp
using Groundwork.Query.Model;
using Groundwork.Store;

var table = new TableId(ShopStorage.Customers.Definition.Name);
var id = new ColumnRef(table, "id", QueryType.Guid, isNullable: false);
var requestedCustomerIds = orderRows.Select(order => (object?)order.CustomerId).ToArray();

var customerSession = storage.OpenSession(
    ShopStorage.Customers.Definition,
    StorageAccess.Global);
var customers = await customerSession.BatchReadAsync(
    new KeyedBatchReadRequest(table, id, requestedCustomerIds),
    storage.Connection,
    cancellationToken);
```

`customers.Rows` carries matched rows in requested-key order and `customers.MissingKeys` names
unmatched requests. Materialize the rows and assemble the graph in application code. This replaces
`Include` without an N+1 loop and makes the number and shape of database operations visible.

### 6. Cut over and remove EF ownership

Use a bounded cutover rather than changing schema ownership and application behavior at once:

1. keep EF reads authoritative while backfilling Groundwork-owned storage;
2. dual-write and compare exact outcomes or domain-level checks;
3. move read paths to Groundwork and monitor refusals, coverage, and latency;
4. stop EF writes;
5. remove EF migrations for objects Groundwork now owns; and
6. retire old storage only through an exact destructive Groundwork plan after the rollback window.

A rollback during the window changes routing, not schema history. Preserve the old read path until
the Groundwork path has been verified under production data and load.

### 7. Evolve the schema through a reviewed plan

After cutover, do not recreate EF's imperative migration chain. For example, renaming the physical
`total` column to `amount` keeps the old logical identity in the new declaration:

```csharp
public decimal Amount { get; init; }

// In the v2 RecordTable declaration:
.Column(order => order.Amount, column => column
    .LogicalId("total")
    .Precision(18, 4)
    .Required())
```

Plan first and save the machine-readable result with the change ticket:

```bash
groundwork plan \
  --schema groundwork.schema.json \
  --provider postgresql \
  --connection "$GROUNDWORK_CONNECTION" \
  --output json > plan.json
```

Copy the exact current plan fingerprint and the reported semantic operation identity from
`plan.json`; authorize those values, not a generic class of changes:

```bash
groundwork apply \
  --schema groundwork.schema.json \
  --provider postgresql \
  --connection "$GROUNDWORK_CONNECTION" \
  --expected-plan 'sha256:…' \
  --allow-semantic 'rename-column:orders.amount'
```

That direct rename assumes a coordinated maintenance window. For rolling old/new application
versions, use the expand–contract form with a semantic migration id, a pure data transform, a
dual-presence window, and a separately authorized contract plan. The full operational sequence is
in **[Schema Management](Schema-Management#expand-and-contract-removing-a-column-without-a-downtime-window)**.

---

## Global query filters become scopes only when they are tenancy

The importer refuses to guess what an expression means. If an EF filter is exactly the tenant
boundary, make that decision explicit:

```csharp
using Groundwork.EntityFrameworkCore;
using Groundwork.Kernel;

var import = EfCoreModelImporter.Import(dbContext, new EfCoreImportOptions
{
    ScopePolicies = new Dictionary<string, ScopePolicy>
    {
        [typeof(Customer).FullName!] = ScopePolicy.Scoped,
        [typeof(Order).FullName!] = ScopePolicy.Scoped
    }
});
```

`ScopePolicies` is keyed by EF's metadata entity name (`IEntityType.Name`), not by table name. The
CLR full name shown above is the conventional value for these ordinary entities; inspect the model
and use its actual entity name for shared, owned, or otherwise customized metadata.

The importer still preserves mapped tenant-id columns because it does not rewrite the entity
shape. When adopting the scaffold, decide whether that column is business data or only the former
filter mechanism. If it was only the filter mechanism, remove it from the durable Groundwork row
contract and use the provider-owned scope instead; keeping both creates two tenant identities that
can disagree.

Every declared reference must use compatible source and target scope policies. Import every
participating unit with the same deliberate scope decision or manifest validation refuses the
mismatch with `GW-DECL-REF-003`.

Every operation then supplies the tenant explicitly:

```csharp
using Groundwork.Kernel;
using Groundwork.Store;

var scopedCustomers = StorageUnit.Declare("customers", "customers")
    .Guid("id", column => column.Required())
    .String("name", 200, column => column.Required())
    .Key("id")
    .Scoped()
    .Build();
var scopedOrders = StorageUnit.Declare("orders", "orders")
    .Guid("id", column => column.Required())
    .Guid("customer_id", column => column.Required())
    .Decimal("total", 18, 4, column => column.Required())
    .Key("id")
    .Index("by_customer", "customer_id")
    .Reference("customer", scopedCustomers, "customer_id")
    .Scoped()
    .Build();

var access = StorageAccess.Scoped(new StorageScope(tenantId));
var session = storage.OpenSession(scopedOrders, access);
using var work = storage.BeginUnitOfWork(access, BatchWriteOptions.Exact, scopedOrders);
```

Opening a scoped unit with `StorageAccess.Global` refuses before I/O. Cross-tenant administration
uses `StorageAccess.PrivilegedAcrossScopes(...)`, which is audited and query-only.

Do **not** map soft delete, temporal visibility, row ownership, authorization, or any other business
predicate to `ScopePolicy.Scoped`. Keep those as explicit portable predicates and declare the index
that covers them. Choosing `ScopePolicy.Global` merely keeps the unit global; it does not preserve or
execute the original EF filter.

---

## What the importer intentionally refuses

| EF shape | Why it is not copied | Migration direction |
| --- | --- | --- |
| Inheritance, table splitting, complex properties, or shared/split owned mappings | Column ownership is implicit or shared | Flatten into independent storage rows and keep polymorphism/composition in application code. |
| Keyless entity or view | It is not mutable keyed storage | Keep it as an external read model or create a keyed Groundwork projection. |
| Provider SQL default or computed SQL | Meaning belongs to one dialect | Use a portable declared default; calculate business values in application writes or a named data migration. Groundwork's derived columns are reserved search/sort projections, not general computed business columns. |
| Value converter | CLR and provider representations differ by application code | Choose one portable storage type and convert explicitly at the boundary. |
| Culture collation | Providers do not share one ordering implementation | Supply an explicit locale mapping and persist a versioned ICU sort key. |
| `float`/`double` predicate or index | Binary floating comparison/index behavior is not portable | Use `Decimal`/scaled `Int64`; keep `Double` only for storage and round-trip. |
| Alternate-key foreign key | A Groundwork reference targets the complete declared storage key | Make the target key canonical or model the lookup explicitly. |
| Arbitrary migration SQL | It is neither portable nor a pure row transform | Keep an operational migration outside Groundwork or write a bounded data migration. |

Refusal is useful migration inventory. Each `GW-EF-*` finding names the target, the lost semantic,
and an alternative; resolve it in source control rather than suppressing it.

---

## Migration completion checklist

- The importer report has no error findings, and every warning has a recorded decision.
- Durable declarations are application-owned source, not regenerated blindly from EF on every build.
- Every production query is expressible on the closed query surface and has declared coverage.
- Every EF global filter is classified as tenant scope or an ordinary predicate.
- The catalog was either adopted after an exact inspection or created under a separate physical name.
- Backfill and dual-write evidence cover counts, keys, nulls, decimals, timestamps, and concurrency.
- Schema application is deployment-time, with exact authorization for semantic or destructive work.
- EF and Groundwork do not both own migrations for the same physical object.
- The rollback window and eventual retirement plan are explicit.

## Next

- **[Schema Management](Schema-Management)** — plan, adoption, authorization, and retirement
- **[Unit of Work & Batching](Unit-of-Work-and-Batching)** — commit semantics and exact outcomes
- **[Querying](Querying)** — declared joins, projections, and keyed batch reads
- **[Multi-Tenancy & Scopes](Multi-Tenancy-and-Scopes)** — provider-enforced tenant isolation
- **[Diagnostics Reference](Diagnostics-Reference#gw-ef---ef-core-model-import)** — importer findings
