# Importing an EF Core model

`Groundwork.EntityFrameworkCore` scaffolds kernel `StorageUnit` declarations from relational EF
Core metadata. It accepts an already-created model; it does not load an application assembly,
construct a host, run startup code, or connect to a database.

That boundary is intentional. Compiled models and migrations snapshots are executable application
code, so the application or design-time factory remains responsible for creating them. The same
importer accepts all three supported entry points:

```csharp
using Groundwork.EntityFrameworkCore;

var fromContext = EfCoreModelImporter.Import(dbContext);
var fromCompiledModel = EfCoreModelImporter.Import(compiledModel);
var fromSnapshot = EfCoreModelImporter.Import(migrationsSnapshot.Model);
```

The result contains the declarations that could be inferred and a deterministic list of findings.
`IsComplete` is true only when no error finding remains. Review the report before copying the
scaffold into the application's durable Groundwork declaration source.

```csharp
var result = EfCoreModelImporter.Import(dbContext, new EfCoreImportOptions
{
    LocaleOrderings = new Dictionary<string, EfCoreLocaleOrdering>
    {
        ["sv_SE_provider"] = new("sv-SE", maximumExpansionFactor: 12)
    },
    ScopePolicies = new Dictionary<string, ScopePolicy>
    {
        ["Example.Order"] = ScopePolicy.Scoped
    }
});

foreach (var finding in result.Findings)
    Console.WriteLine($"{finding.Code} {finding.Target}: {finding.Message} {finding.Alternative}");
```

The importer preserves ordinary relational tables, columns, portable constant defaults, primary
keys, indexes, and foreign-key navigations. Foreign keys become logical Groundwork references; when
their source columns are not already an ordered key/index prefix, the scaffold adds a covering index
and reports `GW-EF-004`.

Some EF semantics need an explicit decision:

- `float` and `double` become storage-only `PortableType.Double` with `GW-EF-003`; use `Decimal` or
  a scaled `Int64` if the value must participate in queries.
- Provider culture collations require a `LocaleOrderings` entry and become persisted locale sort
  keys. The importer refuses to guess a culture or expansion bound.
- Global query filters require a `ScopePolicies` entry. The importer cannot infer that an arbitrary
  predicate is a tenant boundary.
- Views/keyless entities, schema-qualified tables, table or entity splitting, inheritance, complex
  properties, filtered indexes, value converters, unsupported value generation/concurrency tokens,
  provider SQL defaults/computed columns, and CLR types without a lossless portable mapping remain
  errors with a named alternative.
- Every inferred unit runs the kernel portability validator before the result can be complete, so
  Double keys/indexes, nullable unique indexes without an explicit missing-value policy, unbounded
  decimals, invalid defaults, and invalid locale options remain blocking findings.

Compiled runtime models can omit migration-only annotations by design. The importer reads the
annotations they expose without calling migration-only metadata APIs that throw on a runtime model;
use the migrations snapshot when the report must include design-time defaults, computed SQL, or
other annotations that the compiled model did not retain.

The findings are scaffolding diagnostics, not runtime exceptions. Their stable meanings are listed
in the [Diagnostics Reference](../wiki/Diagnostics-Reference.md#gw-ef-ef-core-model-import).
For the complete concept map, catalog-adoption boundary, and a worked application cutover, see
[Migrate from EF Core](../wiki/EF-Core-Migration.md).
