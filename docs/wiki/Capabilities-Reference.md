# Capabilities Reference

A capability is a **stable, namespaced promise about what a deployed connection can actually do**.
Not a per-provider constant — the same provider package advertises different capabilities depending
on the deployment it connected to.

## Checking capabilities

```csharp
using var connection = new MongoProviderFactory().Create(connectionString);

if (!connection.Capabilities.Any(c => c.Id == BatchWriteCapabilities.CompareAndDelete))
    throw new NotSupportedException("This deployment cannot release claims atomically.");
```

`CapabilityDescriptor` carries:

| Member | Meaning |
| --- | --- |
| `Id` | Stable dotted lowercase id (`vendor.area.name`) |
| `DisplayName`, `Description` | Human-readable, **including honest cost** |
| `EvidenceGatedByDefault` | Whether benchmark/operational evidence is required before serving it |
| `OwningModule` | Which module contributed it |
| `AdditionalProviderCommandsPerWrite` | Extra commands this capability costs per write |

That last field is the interesting one. MongoDB's provider-sequence descriptor reports **1** for
its counter command. SQLite, PostgreSQL, and SQL Server also report **1** for durable high-water on
an ordinary generated-key write; exact append amortizes its allocation and high-water commands
across bounded batches as described by each provider. Providers without an additional per-write
command report **0**. The library tells you the cost rather than letting you discover it in a load
test.

## The capability ids

| Id | Constant | Promise |
| --- | --- | --- |
| `groundwork.operational.atomic-commit` | `WellKnownCapabilities.AtomicCommit` | Cross-unit atomic commit across storage units |
| `groundwork.column.provider-sequence` | `BatchWriteCapabilities.ProviderSequence` | Provider-assigned strictly increasing `Int64` keys |
| `groundwork.storage.batched-unit-of-work` | `BatchWriteCapabilities.StagedUnitOfWork` | Staged writes in a transactional unit of work |
| `groundwork.storage.batched-outcomes` | `BatchWriteCapabilities.PerRowOutcomes` | One outcome per staged input in `Exact` mode |
| `groundwork.storage.batched-native` | `BatchWriteCapabilities.NativeBatch` | Native multi-row batch command path |
| `groundwork.storage.append-idempotency` | `BatchWriteCapabilities.AppendIdempotency` | Durable operation ledger; replay returns `Replayed` without rewriting |
| `groundwork.storage.exact-append-outcomes` | `BatchWriteCapabilities.ExactAppendOutcomes` | Replay-stable per-row generated values |
| `groundwork.storage.durable-high-water-inspection` | `BatchWriteCapabilities.DurableHighWaterInspection` | Durable scoped `ProviderSequence` high-water surviving retention and restart |
| `groundwork.storage.exact-retention` | `BatchWriteCapabilities.ExactRetention` | Operation-identified, replay-stable retention |
| `groundwork.storage.compare-and-delete` | `BatchWriteCapabilities.CompareAndDelete` | Atomic delete-if-values-match |
| `groundwork.storage.set-mutation` | `BatchWriteCapabilities.SetMutation` | One provider-native update/delete for every row matching an admitted predicate in aggregate mode; exact mode returns keyed outcomes through the existing write contract |

## Capability → interface → refusal

Optional capabilities have a matching interface, implemented **only when the capability is real**.
The public extension methods check first and refuse **before** provider work:

| Capability | Interface | Entry point | Refusal |
| --- | --- | --- | --- |
| Exact append | `IExactAppendStorageSession` | `session.AppendWithOutcomes(...)` | `GW-APPEND-003` |
| High-water inspection | `IStorageInspectionSession` | `session.Inspect()` | `GW-INSPECT-001` / `-002` |
| Exact retention | `IExactRetentionStorageSession` | `session.ApplyRetention(operationId, …)` | `GW-RETENTION-003` |
| Bounded affected retention keys | `IExactRetentionAffectedKeysStorageSession` | `session.ApplyRetention(operationId, options with { AffectedKeyProjection = … })` | `GW-RETENTION-005` / `-007` |
| Compare-and-delete | `ICompareAndDeleteStorageSession` | `session.CompareAndDelete(...)` | capability absent |
| Set-based mutation | `ISetMutationStorageSession` | `session.UpdateWhere(...)` / `session.DeleteWhere(...)` (aggregate default; `SetMutationOptions.Exact` for keyed outcomes) | `GW-SET-001` |
| Cross-scope query | `IPrivilegedCrossScopeQuerySession` | `session.QueryAcrossScopes(...)` | `GW-ACCESS-001` / `-002` |

```csharp
// Preferred: check the capability.
if (connection.Capabilities.Any(c => c.Id == BatchWriteCapabilities.ExactRetention)) { … }

// Also valid: test the session.
if (session is ICompareAndDeleteStorageSession) { … }
```

## Provider advertisement

| Capability | SQLite | PostgreSQL | SQL Server | Mongo (RS) | Mongo (standalone) | InMemory |
| --- | :-: | :-: | :-: | :-: | :-: | :-: |
| `atomic-commit` | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ |
| `provider-sequence` | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ |
| `append-idempotency` | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ |
| `exact-append-outcomes` | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ |
| `durable-high-water-inspection` | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ |
| `exact-retention` | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ |
| `compare-and-delete` | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ |
| `set-mutation` | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ |

MongoDB gates all of the above on `IMongoProviderConnection.ProviderSequenceFit` being
`ProviderFit.Supported`, which requires a transaction-capable replica set or sharded deployment.
Schema admission and writes retain the same early refusal as defense in depth.

All five conformance providers support audited, query-only cross-scope access for scoped units.

## Validating requirements up front

Rather than discovering a missing capability mid-workload, validate at startup:

```csharp
var validator = new ProviderCapabilityValidator();

var result = validator.Validate(
    requirements: [BatchWriteCapabilities.ExactRetention, WellKnownCapabilities.AtomicCommit],
    capabilities: report);

if (!result.IsCompatible)
    foreach (var issue in result.Errors)
        Console.WriteLine($"{issue.Code} at {issue.Target}: {issue.Message}");
```

Or per storage unit, which also checks the concurrency mode:

```csharp
var requirements = new StorageUnitCapabilityRequirements(
    unit.Id,
    [BatchWriteCapabilities.AppendIdempotency],
    unit.Concurrency);

var fit = validator.Evaluate([requirements], report);
// ProviderFit.Supported | RequiresEvidence(reasons) | Unsupported(missingRequirements)
```

| Issue | Meaning |
| --- | --- |
| `GW-CAP-002` | Provider warning (non-fatal) |
| `GW-CAP-004` | Missing required capabilities |
| `GW-CAP-005` | Unsupported concurrency mode |
| `GW-CAP-013` | Capability is evidence-gated and lacks evidence |
| `GW-CAP-014` | Requirement is not registered — register it via an `IGroundworkModule` |

## Evidence gating

A descriptor may be `EvidenceGatedByDefault`. Such a capability is only served once the provider
supplies benchmark or operational evidence. `WorkloadEvidencePolicy.FromRegistry(registry)` derives
the active policy; `ProviderCapabilityReport.EvidencedCapabilities` records what has been evidenced.

`groundwork.operational.atomic-commit` is evidence-gated by default — "we support transactions" is
exactly the kind of claim that deserves proof rather than a checkbox.

## Registering your own capabilities

```csharp
public sealed class MyModule : IGroundworkModule
{
    public string Name => "acme.storage";

    public void RegisterCapabilities(ICapabilityRegistryBuilder builder) =>
        builder.Add(new CapabilityDescriptor(
            new CapabilityId("acme.storage.geo-index"),
            "Geospatial index",
            "Native 2dsphere index support.",
            EvidenceGatedByDefault: false,
            OwningModule: "acme.storage"));
}

var (registry, evidencePolicy) = new GroundworkModuleCatalog().Add(new MyModule()).Build();
```

Capability ids must match `^[a-z0-9-]+(\.[a-z0-9-]+)+$` — dotted, lowercase, namespaced. Registering
the same id with a **different** definition or owning module throws, so two modules cannot silently
disagree about what an id means.

## Index and query capability reporting

`ProviderCapabilityReport` also carries:

- `IndexCapabilities` — supported `IndexValueKind`s, unique/sortable index support, supported
  `MissingValueBehavior`s
- `SupportedQueryOperations` — the `PortableQueryOperation` set
- `SupportedConcurrencyModes`
- `Warnings`

## Guidance

- **Check capabilities at startup**, not at the point of first use. A missing capability discovered
  mid-workflow is much more expensive than one discovered at boot.
- **Do not hardcode a provider-to-capability table** in your application. Read
  `connection.Capabilities`.
- **Read `AdditionalProviderCommandsPerWrite`** when sizing a write-heavy workload.
- Descriptors describe the provider's **ordinary** cost. They deliberately do not claim that a
  conditional or generated-column workload is one round trip.

## Next

- **[Providers](Providers)** — per-provider deployment requirements
- **[Streams: Append & Retention](Streams-Append-and-Retention)** — the capabilities in use
