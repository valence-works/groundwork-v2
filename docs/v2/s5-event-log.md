# S5 event-log second-family proof

The event-log declaration in `samples/Groundwork.Samples.EventLog/EventLog.cs` is the
second-family proof for the public kernel API. It has no project reference to Records or
Documents; the architecture test checks the compiled assembly references and the sample build
therefore fails if the declaration surface regresses into a family-specific dependency.

## Headline metric

| Measure | Before (v1) | After (v2) |
| --- | ---: | ---: |
| Event-log family + provider implementation | 11,880 lines | 0 lines |
| Public declaration chain | n/a | 20 lines |

The before count is the v1 family-and-provider total cited by Groundwork #263. The after count is
the non-blank fluent chain from `StorageUnit.Declare` through `Build`; the sample's namespace,
comments, and type wrapper are not included in that headline metric.

## Kernel facilities added or exposed

- `StorageUnit.Declare` and `StorageDeclarationBuilder` now live in `Groundwork.Kernel`.
- Typed column helpers were moved to the neutral builder, with `Column(name, type, ...)` as the
  runtime-type alias. `ColumnBuilder` retains required/nullable, sizing, precision, defaults,
  collation, and provider-sequence policies.
- `IndexBuilder.Column` was added as the ascending-column alias alongside `Ascending` and
  `Descending`.
- `Scoped()` exposes the existing `ScopePolicy.Scoped` contract through the public declaration
  surface.
- `AppendIdempotency(window, ledgerName)` exposes the durable replay contract.
- `Retention(...)` and `KeepNewest(...)` expose count-based retention with named `orderBy`,
  trigger, and partition arguments.
- `Aggregate(...)` exposes a closed aggregation DSL for `GroupBy`, `Min`, `Max`, `Sum`,
  `SetUnion`, and `FirstBy`.
- `ConformanceScenario` makes the shipped `ConformanceSuite` reusable by an externally authored
  storage family, including generated-key and row-value mapping; the EventLog fixture invokes
  the full suite on all five local providers and retains its capability-specific proofs.
- The aggregation session adapter now scans only columns required by the declared profile and
  key, so an unrelated JSON column does not make a valid aggregation unexecutable.

Records keeps only a compatibility forwarding wrapper; it does not maintain a second declaration
implementation. The event-log conformance fixture exercises schema application, catalog indexes,
provider sequences, idempotent append replay, explicit retention, named aggregation, and scoped
isolation on InMemory, SQLite, PostgreSQL, SQL Server, and MongoDB. MongoDB's transaction wrapper
also closes an already-aborted transaction after a duplicate-key outcome so the suite can assert
the portable `UniqueViolation` result and continue; configured replica-set failures are not
skipped.
