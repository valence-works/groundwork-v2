# Testing

`Groundwork.Testing` gives you a deterministic in-memory provider, the provider-neutral conformance
suites, and a concurrency harness.

> It is a **consumer** of `Groundwork.Store`, never a dependency of it. A runtime provider must never
> require `Groundwork.Testing` merely to open a connection or execute a write. Keep it in test
> projects.

## In-memory provider

```csharp
using Groundwork.Testing;

using var connection = new InMemoryProviderFactory().Create("orders-tests");

connection.Schema.Apply(table.Definition);
var records = table.Open(connection);
records.Insert(new Customer(Guid.NewGuid(), "ada@example.test", "Ada"));
```

Deterministic, no external service, and it implements the same contracts as a real provider —
including capability advertisement:

```csharp
connection.Capabilities.Any(c => c.Id == BatchWriteCapabilities.ExactAppendOutcomes); // true
```

`SchemaConflictException` surfaces conflicting schema application, so your tests can assert it.

**What it is good for:** unit tests of your declarations, mapping, query shapes, and business logic.
**What it is not:** proof that your workload behaves on PostgreSQL. For that, run against the real
provider — see below.

## Conformance suites

If you author a provider, or a storage *family*, run the shipped contract against it:

```csharp
var report = ConformanceSuite.Run(new MyProviderFactory(), connectionString);

foreach (var check in report.Checks)
    Console.WriteLine($"{check.Name}: {(check.Passed ? "pass" : check.Failure)}");
```

Covers schema apply/no-op/diff, provider catalog and index verification, storage-scope isolation,
audited cross-scope queries, CRUD outcomes, optimistic conflicts, unique violations, and
unit-of-work commit/rollback.

### Custom scenarios

The default scenario uses a shipped probe schema. A storage family can supply its own declaration and
value/key mapping instead:

```csharp
var scenario = new ConformanceScenario(
    global:     myGlobalUnit,
    scoped:     myScopedUnit,
    values:     (id, value, unique) => new StorageValues(/* … */),
    key:        (id, outcome) => new StorageKey(/* … */),
    attachKey:  (values, key) => /* … */,
    missingKey: id => new StorageKey(/* … */),
    valueColumn: "value");

var report = ConformanceSuite.Run(factory, connectionString, scenario);
```

The value column must be declared on **both** scenario units.

This is what makes the suite reusable by an externally authored family. The event-log sample uses it
to run the full suite on all five local providers **without** referencing Records or Documents.

## Concurrency harness

`ConcurrencyHarness` (the W2 harness) runs deterministic concurrent-write scenarios and asserts
invariants across providers:

```csharp
var report = ConcurrencyHarness.Run(
    new StorageProviderConcurrencyFactory("sqlite", new SqliteProviderFactory()),
    connectionString,
    new ConcurrencyProbeOptions
    {
        WriterCount = 32,
        KeyCount = 1,
        Concurrency = ConcurrencyKind.Optimistic
    });
```

It exercises both `None` and `Optimistic` concurrency modes and captures a `MachineLoadSnapshot` so a
failure on a loaded CI runner is distinguishable from a genuine invariant violation.

SQLite and in-memory runs need no external services.

## Running against real providers

Live provider tests activate through environment variables; without them they are **explicitly
skipped**, not silently passed.

| Variable | Provider |
| --- | --- |
| `GROUNDWORK_POSTGRES_CONNECTION` | PostgreSQL |
| `GROUNDWORK_SQLSERVER_CONNECTION` | SQL Server |
| `GROUNDWORK_MONGO_CONNECTION` | MongoDB (replica set) |
| `GROUNDWORK_MONGO_STANDALONE_CONNECTION` | MongoDB standalone (capability-refusal proofs) |

```bash
export GROUNDWORK_POSTGRES_CONNECTION="Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=…;Pooling=false"
dotnet test tests/Groundwork.StreamCapabilities.Tests --filter Retention
```

A quick local matrix with Docker:

```bash
docker run -d --name gw-pg -e POSTGRES_PASSWORD=groundwork -p 5432:5432 postgres:17.6-alpine3.22

docker run -d --name gw-mongo -p 27017:27017 mongo:7.0.24 --replSet rs0 --bind_ip_all
docker exec gw-mongo mongosh --quiet --eval \
  'rs.initiate({_id:"rs0",members:[{_id:0,host:"localhost:27017"}]})'
```

Remember: MongoDB **must** be a replica set for the transactional capabilities. A standalone instance
will legitimately skip or refuse those tests.

### The variable gates the tests; the database still has to exist

The skip logic asks whether the connection variable is **set**, not whether the server behind it can
be reached. Point `GROUNDWORK_POSTGRES_CONNECTION` at a database that does not exist and the suites
run and **fail** — dozens of them, with a provider stack trace:

```text
Npgsql.PostgresException : 3D000: database "gw_scratch" does not exist
```

That reads like a broken provider rather than an absent one, and a red suite you have learned to
explain away is worse than a skipped one. If a live suite fails everywhere at once, check the
database exists before reading the diff:

```bash
psql "$GROUNDWORK_POSTGRES_CONNECTION" -c 'select 1'   # or: createdb gw_scratch
```

The same applies to `GROUNDWORK_SQLSERVER_CONNECTION` and the MongoDB variables. Unset the variable
if you want a clean skip; a set-but-unreachable variable is not the same thing.

## Explain-plan assertions

Verify that a query carrying a coverage-proven selected index actually uses its deployed physical
index:

```bash
GW_EXPLAIN_ASSERT=1 \
GW_EXPLAIN_ARTIFACT_DIR="$PWD/TestResults/groundwork-explain" \
dotnet test tests/Groundwork.Differential.Tests
```

Off by default and adds no plan command to normal execution. Artifacts (JSON/XML/text plans) are
written to the artifact directory, or `TestResults/groundwork-explain` by default. A failure includes
the artifact path.

> Plans can contain identifiers and query values — treat the artifact directory as potentially
> sensitive test output.

## Testing your own code

### Assert declarations are portable

```csharp
[Fact]
public void Declaration_is_portable()
{
    var result = PortabilityValidator.Validate(MySchema.Orders);
    Assert.True(result.IsPortable,
        string.Join("\n", result.Refusals.Select(r => $"{r.Code} at {r.Path}: {r.Message}")));
}
```

### Assert query coverage without a database

```csharp
[Fact]
public void Customer_lookup_is_covered()
{
    var gate = new RuntimeCoverageGate(
        declaredIndexes: [new CoverageIndex("by_email", [new CoverageIndexColumn("email")])],
        deployedIndexes: [new CoverageIndex("by_email", [new CoverageIndexColumn("email")])]);

    var request = table.Query.Where(c => c.Email == "a@b.test").ToQueryRequest();
    Assert.Equal(CoverageDecision.Covered, gate.Check(request).Coverage.Decision);
}
```

### Assert semantics without a provider

```csharp
Assert.Empty(PortableQuerySemantics.Validate(request).Refusals);
Assert.True(PortableQuerySemantics.Evaluate(predicate, row));
```

`Evaluate` is a pure two-valued oracle — it is defined even for shapes a provider must refuse, which
makes it a genuine test oracle rather than a second implementation.

### Assert mapping stays on the fast path

```csharp
var before = RecordTable<Customer>.AccessorCompilationCount;
// … exercise writes and materialization …
Assert.Equal(before, RecordTable<Customer>.AccessorCompilationCount);
Assert.Equal(0, RecordTable<Customer>.AccessorReflectionInspectionCount);
```

## Test suites in this repository

| Suite | Proves |
| --- | --- |
| `Groundwork.Architecture.Tests` | Assembly reference boundaries (inspects compiled references) |
| `Groundwork.PublicApi.Acceptance.Tests` | The clean-room package-only consumer journey |
| `Groundwork.Differential.Tests` | Four-provider query differential + explain plans |
| `Groundwork.Concurrency.Tests` | W2 concurrency invariants |
| `Groundwork.ProviderCommands.Tests` | W1 native conditional write path + session-scoped provider-command observation |
| `Groundwork.StreamCapabilities.Tests` | Sequences, idempotency, retention, lifecycle, aggregation |
| `Groundwork.Query.Linq.Tests` | The 250-case LINQ corpus and its 10 locked diagnostic codes |
| `Groundwork.Packaging.Tests` | Package layout and allowlist |
| `Groundwork.Samples.EventLog.Tests` | Kernel-only second-family conformance on all five providers |

## The clean-room public API proof

CI builds a consumer **outside the repository source graph**, from packed artifacts only:

```bash
dotnet pack Groundwork.slnx --configuration Release --output artifacts/packages
dotnet test tests/Groundwork.PublicApi.Acceptance.Tests --configuration Release
```

The test copies the consumer into a temporary external solution, restores **only package
references**, builds it **twice** with repository props disabled, and runs it after each build.

The consumer contains **no project reference, internal namespace access, reflection, friend assembly,
or Testing adapter**. It exercises SQLite schema apply/verify, typed Records writes and queries,
Documents, batch outcomes, coverage enforcement, concurrency diagnostics, and public schema-drift
admission.

This is the strongest available evidence that the public API is genuinely usable from outside — and
it is why the `friction-log.md` in that test directory is worth reading if you hit an ergonomic wall.

## Next

- **[Extending: Writing a Provider](Extending-Writing-a-Provider)** — using conformance as your spec
- **[Providers](Providers)** — deployment requirements for live tests
