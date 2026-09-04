using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Query.Model;
using Groundwork.Substrate.Mongo;
using Groundwork.Store;
using Groundwork.Diagnostics;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;
using KernelSortDirection = Groundwork.Kernel.SortDirection;

namespace Groundwork.MongoDb;

/// <summary>The capability declared by MongoDB for provider-assigned sequence columns.</summary>
public static class MongoCapabilities
{
    public static readonly CapabilityId ProviderSequence = new("groundwork.column.provider-sequence");

    public static CapabilityDescriptor ProviderSequenceDescriptor { get; } = new(
        ProviderSequence,
        "Provider-assigned monotonic sequence",
        "MongoDB monotonically allocates a sequence in a counter collection and commits it with the row in a transaction-capable deployment; concurrent commit order may differ and each inserted row/coalesced exact write uses one additional counter command.",
        EvidenceGatedByDefault: true,
        OwningModule: "groundwork-mongodb",
        AdditionalProviderCommandsPerWrite: 1);
}

public sealed class MongoCapabilityModule : IGroundworkModule
{
    public string Name => "groundwork-mongodb";

    public void RegisterCapabilities(ICapabilityRegistryBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Add(MongoCapabilities.ProviderSequenceDescriptor);
    }
}

/// <summary>Creates native MongoDB storage connections from database-qualified connection strings.</summary>
public sealed class MongoDbProviderFactory : IMongoProviderFactory
{
    public IMongoProviderConnection Create(string connectionString) =>
        new MongoDbProviderConnection(new MongoClientContext(connectionString));
}

public sealed class MongoDbProviderConnection : IMongoProviderConnection
{
    internal static IReadOnlyList<CapabilityDescriptor> ConstraintCapabilities { get; } =
        Array.Empty<CapabilityDescriptor>();
    private readonly MongoProviderState state;
    private bool disposed;

    internal MongoDbProviderConnection(MongoClientContext context)
    {
        state = new MongoProviderState(context);
        Catalog = new MongoProviderCatalog(state);
        Schema = new MongoSchemaCoordinator(state);
    }

    public IMongoProviderCatalog Catalog { get; }

    public IMongoSchemaCoordinator Schema { get; }

    /// <summary>Data-migration execution for this connection's database.</summary>
    public MongoDataMigrationExecutor DataMigrations => new(state.Context);

    public ProviderFit ProviderSequenceFit => state.Context.SupportsTransactions()
        ? new ProviderFit.Supported()
        : new ProviderFit.Unsupported([MongoCapabilities.ProviderSequence]);

    /// <summary>Provides read-only access to the native database for catalog/evidence tests.</summary>
    public IMongoDatabase Database => state.Context.Database;

    public MongoSchemaAdmissionReport InspectSchema(StorageUnit unit, MongoStorageAccess access)
    {
        ThrowIfDisposed();
        SchemaCapabilityAdmission.EnsureSupported(unit, ConstraintCapabilities);
        var applied = state.Resolve(unit, access);
        return MongoSchemaCoordinator.InspectAdmission(state, applied, access);
    }

    public IMongoStorageSession OpenSession(StorageUnit unit, MongoStorageAccess access, IProviderCommandObserver? observer = null)
    {
        ThrowIfDisposed();
        SchemaCapabilityAdmission.EnsureSupported(unit, ConstraintCapabilities);
        var applied = state.Resolve(unit, access);
        var collection = access.IsPrivilegedAcrossScopes
            ? state.Context.Database.GetCollection<BsonDocument>(applied.CollectionName)
            : MongoSchemaCoordinator.EnsureAdmission(state, applied, access);
        if (!access.IsPrivilegedAcrossScopes)
            state.RegisterScope(applied, access);
        return new MongoStorageSession(state, applied, access, collection, null, observer: observer);
    }

    public IMongoUnitOfWork BeginUnitOfWork(MongoStorageAccess access, params StorageUnit[] units)
        => BeginUnitOfWork(access, observer: null, units);

    public IMongoUnitOfWork BeginUnitOfWork(MongoStorageAccess access, IProviderCommandObserver? observer, params StorageUnit[] units)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(units);
        if (access.IsPrivilegedAcrossScopes)
            throw new InvalidOperationException(
                "GW-ACCESS-003: privileged cross-scope access is query-only and cannot begin a unit of work.");
        if (units.Length == 0)
            throw new ArgumentException("A unit of work must declare at least one storage unit.", nameof(units));
        foreach (var unit in units)
            SchemaCapabilityAdmission.EnsureSupported(unit, ConstraintCapabilities);

        var applied = units.Select(unit => state.Resolve(unit, access)).ToArray();
        if (applied.Select(unit => unit.Declaration.Id).Distinct().Count() != applied.Length)
            throw new ArgumentException("A unit of work cannot list the same storage unit twice.", nameof(units));
        var collections = applied
            .Select(unit => MongoSchemaCoordinator.EnsureAdmission(state, unit, access))
            .ToArray();
        foreach (var unit in applied)
            state.RegisterScope(unit, access);
        return new MongoUnitOfWork(state, applied, collections, access, observer);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        state.Context.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(MongoDbProviderConnection));
    }
}

internal sealed class MongoProviderState
{
    private readonly object gate = new();
    private readonly Dictionary<StorageUnitId, MongoAppliedUnit> units = [];
    private readonly SchemaSessionPublicationRegistry schemaSessions = new();

    internal MongoProviderState(MongoClientContext context) => Context = context;

    internal MongoClientContext Context { get; }

    internal IMongoCollection<BsonDocument> Metadata =>
        Context.Database.GetCollection<BsonDocument>("__groundwork_metadata");

    internal IMongoCollection<BsonDocument> Sequences =>
        Context.Database.GetCollection<BsonDocument>("__groundwork_sequences");

    internal IMongoCollection<BsonDocument> Operations(string ledgerName) =>
        Context.Database.GetCollection<BsonDocument>(ledgerName);

    internal MongoAppliedUnit Resolve(StorageUnit declaration, MongoStorageAccess access)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        declaration = MongoSchemaTargets.Physicalize(declaration);
        ValidateScope(declaration, access);

        // The kernel history is the durable authority for both schema admission and session
        // resolution. The provider catalog is a derived compatibility record; trusting it here
        // would open a session if a standalone deployment crashed after publishing that record
        // but before publishing history.
        var appliedState = ReadAppliedState(declaration.Id);
        if (appliedState is null)
        {
            throw new InvalidOperationException(
                $"Storage unit '{declaration.Id}' has not been applied to this provider.");
        }
        if (appliedState.Snapshot.Subject.Evolution.RetiresPrimaryStorage)
        {
            throw new InvalidOperationException(
                $"Storage unit '{declaration.Id}' is retired on this provider and cannot be opened.");
        }

        var applied = new MongoAppliedUnit(
            MongoDeclarationSnapshot.Clone(appliedState.Snapshot.Subject.Definition),
            appliedState.Snapshot.Subject.Name,
            appliedState.TargetFingerprint);
        // Privileged access intentionally has no single scope. Its query path fans out across the
        // registered per-scope collections, while ordinary scoped sessions validate one concrete
        // scope-to-collection route here.
        if (!access.IsPrivilegedAcrossScopes)
            _ = MongoSchemaCoordinator.CollectionName(applied, access);
        if (!CollectionExists(applied.CollectionName))
        {
            throw new InvalidOperationException(
                $"Storage unit '{declaration.Id}' has not been applied to this provider.");
        }

        EnsureSameDeclaration(appliedState.Snapshot.Subject.Definition, declaration);

        MongoAppliedUnit resolved;
        lock (gate)
        {
            if (units.TryGetValue(declaration.Id, out var raced))
            {
                EnsureSameDeclaration(raced.Declaration, declaration);
                resolved = raced;
            }
            else
            {
                units.Add(declaration.Id, applied);
                resolved = applied;
            }
        }
        return resolved;
    }

    internal MongoAppliedUnit ResolveReferenceTarget(
        StorageUnit source,
        ReferenceJoin join,
        MongoStorageAccess access)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(join);
        ArgumentNullException.ThrowIfNull(access);

        var references = source.References
            .Where(candidate => string.Equals(candidate.Name, join.ReferenceName, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (references.Length != 1)
            throw InvalidReferenceJoin(join, "does not name exactly one applied source reference");

        var reference = references[0];
        if (reference.TargetScope is null)
            throw InvalidReferenceJoin(
                join,
                "does not carry persisted target scope metadata and cannot be admitted safely");

        if (source.Scope != reference.TargetScope)
        {
            throw new InvalidOperationException(
                $"GW-ACCESS-003: declared reference join '{join.ReferenceName}' crosses storage scope policies and was refused before provider I/O.");
        }

        if (!TryGet(reference.TargetUnitId, out var target))
        {
            var targetState = ReadAppliedState(reference.TargetUnitId);
            if (targetState is null || targetState.Snapshot.Subject.Evolution.RetiresPrimaryStorage)
                throw InvalidReferenceJoin(join, "targets a storage unit that is not currently applied");

            var candidate = new MongoAppliedUnit(
                MongoDeclarationSnapshot.Clone(targetState.Snapshot.Subject.Definition),
                targetState.Snapshot.Subject.Name,
                targetState.TargetFingerprint);
            lock (gate)
            {
                target = units.TryGetValue(reference.TargetUnitId, out var raced)
                    ? raced
                    : units[reference.TargetUnitId] = candidate;
            }
        }
        if (target.Declaration.Scope != reference.TargetScope)
        {
            throw InvalidReferenceJoin(
                join,
                $"persists target scope {target.Declaration.Scope} but the reference requires {reference.TargetScope}; schema history is inconsistent");
        }

        ValidateScope(target.Declaration, access);
        if (!string.Equals(join.SourceTable.Value, source.Name, StringComparison.Ordinal) ||
            !string.Equals(join.TargetTable.Value, target.Declaration.Name, StringComparison.Ordinal) ||
            !reference.Columns.SequenceEqual(
                join.ColumnPairs.Select(pair => pair.Source.Name),
                StringComparer.Ordinal) ||
            !target.Declaration.Key.Columns.SequenceEqual(
                join.ColumnPairs.Select(pair => pair.Target.Name),
                StringComparer.Ordinal))
        {
            throw InvalidReferenceJoin(join, "does not match the applied reference and complete target key");
        }
        if (!CollectionExists(target.CollectionName))
            throw InvalidReferenceJoin(join, "targets a storage unit whose primary collection is absent");
        _ = MongoSchemaCoordinator.EnsureAdmission(this, target, access);
        RegisterScope(target, access);
        return target;
    }

    private static QueryRenderException InvalidReferenceJoin(ReferenceJoin join, string reason) =>
        new(
            "GW-QUERY-032",
            $"Declared reference join '{join.ReferenceName}' {reason}; MongoDB refused it before issuing a query command.");

    private static void EnsureSameDeclaration(StorageUnit applied, StorageUnit requested)
    {
        if (!string.Equals(SchemaIdentity.Fingerprint(applied), SchemaIdentity.Fingerprint(requested), StringComparison.Ordinal))
        {
            throw new MongoSchemaConflictException(
                $"Storage unit '{requested.Name}' differs from the applied MongoDB schema, including its folded search-key algorithm identity. Apply the exact schema and rebuild the derived search-key column before opening a session.");
        }
    }

    internal MongoAppliedUnit Remember(PhysicalSchemaTarget target)
    {
        schemaSessions.Publish(target);
        var snapshot = MongoDeclarationSnapshot.Clone(target.Subject.Definition);
        var applied = new MongoAppliedUnit(snapshot, snapshot.Name, target.Fingerprint);
        lock (gate)
            units[snapshot.Id] = applied;
        return applied;
    }

    internal SchemaSessionLease CaptureSchemaSession(MongoAppliedUnit applied)
    {
        var fingerprint = applied.TargetFingerprint ?? throw new InvalidOperationException(
            $"MongoDB applied unit '{applied.Declaration.Id.Value}' is missing its authoritative target fingerprint.");
        return schemaSessions.Capture(
            new PhysicalSchemaTargetIdentity(applied.Declaration.Id, MongoSchemaTargets.Provider.Name),
            fingerprint);
    }

    internal bool TryGet(StorageUnitId id, out MongoAppliedUnit applied)
    {
        lock (gate)
            return units.TryGetValue(id, out applied!);
    }

    internal PhysicalSchemaAppliedState? ReadAppliedState(StorageUnitId id)
    {
        var target = new PhysicalSchemaTargetIdentity(id, MongoSchemaTargets.Provider.Name);
        var historyId = "history:" + target;
        var document = Metadata.Find(new BsonDocument("_id", historyId)).FirstOrDefault();
        if (document is null || !document.TryGetValue("stateJson", out var stateJson) || !stateJson.IsString)
            return null;

        var applied = PhysicalSchemaAppliedStateSerializer.Deserialize(stateJson.AsString);
        if (applied.TargetIdentity != target ||
            !string.Equals(applied.Provider.Name, MongoSchemaTargets.Provider.Name, StringComparison.Ordinal))
        {
            throw new MongoSchemaConflictException(
                $"Applied MongoDB schema history for '{id.Value}' belongs to '{applied.TargetIdentity}'.");
        }

        return applied;
    }

    internal bool CollectionExists(string name) => Context.Database.ListCollectionNames(
        new ListCollectionNamesOptions { Filter = new BsonDocument("name", name) }).Any();

    internal void RegisterScope(MongoAppliedUnit applied, MongoStorageAccess access)
    {
        if (applied.Declaration.Scope != ScopePolicy.Scoped || access.Scope is null)
            return;
        var scope = access.Scope.Value;
        var token = CrossScopeQueryMaterializer.ScopeToken(access.Scope);
        var document = new BsonDocument
        {
            ["_id"] = "scope:" + applied.Declaration.Id.Value + ":" + token,
            ["kind"] = "scope",
            ["unit"] = applied.Declaration.Id.Value,
            ["scope"] = scope,
            ["token"] = token,
            ["collection"] = MongoSchemaCoordinator.CollectionName(applied, access)
        };
        Metadata.ReplaceOne(
            new BsonDocument("_id", document["_id"]),
            document,
            new ReplaceOptions { IsUpsert = true });
    }

    internal IReadOnlyList<MongoScopeRegistration> ReadScopes(MongoAppliedUnit applied)
    {
        var filter = new BsonDocument
        {
            ["kind"] = "scope",
            ["unit"] = applied.Declaration.Id.Value
        };
        return Metadata.Find(filter)
            .Sort(new BsonDocument("token", 1))
            .ToList()
            .Select(document =>
            {
                var scope = new StorageScope(document["scope"].AsString);
                var token = document["token"].AsString;
                var collection = document["collection"].AsString;
                var expectedToken = CrossScopeQueryMaterializer.ScopeToken(scope);
                var expectedCollection = MongoSchemaCoordinator.CollectionName(
                    applied,
                    MongoStorageAccess.Scoped(scope));
                if (!string.Equals(token, expectedToken, StringComparison.Ordinal) ||
                    !string.Equals(collection, expectedCollection, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"GW-ACCESS-006: MongoDB scope registry drift was detected for storage unit '{applied.Declaration.Name}'. Reopen the affected scoped session to rebuild its provider-owned registration.");
                }
                return new MongoScopeRegistration(scope, token, collection);
            })
            .ToArray();
    }

    internal static void ValidateScope(StorageUnit unit, MongoStorageAccess access)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (unit.Scope != access.Policy)
        {
            throw new InvalidOperationException(
                $"Storage unit '{unit.Name}' requires {unit.Scope} access, but {access.Policy} was supplied.");
        }
    }
}

internal sealed record MongoAppliedUnit(
    StorageUnit Declaration,
    string CollectionName,
    string? TargetFingerprint = null);

internal sealed record MongoScopeRegistration(StorageScope Scope, string Token, string CollectionName);

internal sealed class MongoProviderCatalog(MongoProviderState state) : IMongoProviderCatalog
{
    public IReadOnlyList<MongoProviderIndex> ReadIndexes(StorageUnitId storageUnitId)
    {
        var persisted = state.ReadAppliedState(storageUnitId);
        var collectionName = persisted?.Snapshot.Subject.Name ?? storageUnitId.Value;
        var expected = persisted?.Snapshot.Subject.Indexes;
        var providerOwned = persisted?.Snapshot.ProviderDefinitions
            .Where(definition => string.Equals(
                definition.Kind,
                MongoSchemaTargets.DeclaredKeyIndexDefinitionKind,
                StringComparison.Ordinal))
            .Select(definition => definition.SubjectIdentity)
            .ToHashSet(StringComparer.Ordinal) ?? [];
        return ReadIndexes(collectionName, expected)
            .Where(index => !providerOwned.Contains(index.Name))
            .ToArray();
    }

    internal IReadOnlyList<MongoProviderIndex> ReadIndexes(
        string collectionName,
        IReadOnlyList<IndexDefinition>? expectedDefinitions = null)
    {
        if (!state.CollectionExists(collectionName))
            return [];

        var expected = expectedDefinitions?.ToDictionary(index => index.Name, StringComparer.Ordinal) ?? [];
        return state.Context.Database.GetCollection<BsonDocument>(collectionName)
            .Indexes.List().ToList()
            .Where(item => item["name"].AsString != "_id_")
            .Select(item =>
            {
                var columns = item["key"].AsBsonDocument
                    .Select(term => new MongoProviderIndexColumn(term.Name,
                        term.Value.ToInt32() < 0 ? KernelSortDirection.Descending : KernelSortDirection.Ascending))
                    .ToArray();
                var missing = item.Contains("partialFilterExpression")
                    ? MissingValueBehavior.Excluded
                    : MissingValueBehavior.Included;
                var name = item["name"].AsString;
                return new MongoProviderIndex(
                    name,
                    columns,
                    item.TryGetValue("unique", out var unique) && unique.ToBoolean(),
                    missing,
                    expected.TryGetValue(name, out var definition) ? definition.SchemaVersion : 1);
            })
            .OrderBy(index => index.Name, StringComparer.Ordinal)
            .ToArray();
    }
}

internal sealed class MongoSchemaCoordinator(MongoProviderState state) : IMongoSchemaCoordinator
{
    private readonly MongoSchemaExecutor executor = new(state.Context);
    private readonly object applicationGate = new();

    public GroundworkRuntimeSchemaAdmissionResult InspectRuntimeAdmission(
        StorageUnit desired,
        GroundworkRuntimeSchemaAdmissionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(desired);
        SchemaCapabilityAdmission.EnsureSupported(desired, MongoDbProviderConnection.ConstraintCapabilities);
        lock (applicationGate)
        {
            var target = MongoSchemaTargets.Compile(desired);
            var inspection = executor.InspectHistory(target);
            var stableRefusal = MongoDeclarationRules.StableDeclarationRefusals(
                inspection.History.AppliedState?.Snapshot.Subject.Definition,
                target.Subject.Definition).FirstOrDefault();
            if (stableRefusal is not null)
            {
                return new GroundworkRuntimeSchemaAdmissionResult(
                    inspection,
                    PhysicalSchemaDiffPlan.Invalid(target, DateTimeOffset.UtcNow, [stableRefusal]));
            }

            GroundworkRuntimeSchemaAdmissionResult result;
            try
            {
                result = GroundworkRuntimeSchemaAdmission.InspectRuntimeAdmission(
                    executor,
                    target,
                    options,
                    inspected: inspection,
                    inspectAfterApplication: () => executor.InspectHistory(target));
            }
            catch (MongoSchemaConflictException exception) when (exception.Refusal is { } refusal)
            {
                // An external history edit cannot normally pass the lease fence, but if the executor
                // observes one between inspection and apply, return the same unit-level Blocked
                // verdict rather than turning a provider guard into a hosting failure.
                var current = executor.InspectHistory(target);
                return new GroundworkRuntimeSchemaAdmissionResult(
                    current,
                    PhysicalSchemaDiffPlan.Invalid(target, DateTimeOffset.UtcNow, [refusal]));
            }

            if (result.Application?.Outcome is PhysicalSchemaApplicationOutcome.Applied or
                PhysicalSchemaApplicationOutcome.NoChanges)
                state.Remember(target);
            return result;
        }
    }

    public SchemaDiff Diff(StorageUnit desired)
    {
        SchemaCapabilityAdmission.EnsureSupported(desired, MongoDbProviderConnection.ConstraintCapabilities);
        var target = MongoSchemaTargets.Compile(desired);
        using var applicationLock = executor.AcquireApplicationLock(target.Identity);
        var history = executor.ReadHistory(target.Identity, applicationLock);
        MongoDeclarationRules.ThrowIfStableDeclarationChanged(
            history.AppliedState?.Snapshot.Subject.Definition,
            target.Subject.Definition);
        var plan = PhysicalSchemaDiffPlanner.Plan(target, history, DateTimeOffset.UtcNow);
        return new SchemaDiff(SchemaChangeMapping.Describe(plan, target.Subject.Definition));
    }

    public SchemaApplyResult Apply(StorageUnit desired)
    {
        SchemaCapabilityAdmission.EnsureSupported(desired, MongoDbProviderConnection.ConstraintCapabilities);
        lock (applicationGate)
        {
            var target = MongoSchemaTargets.Compile(desired);
            if (target.Subject.Columns.Any(column => column.Generation == ColumnGeneration.ProviderSequence))
                state.Context.RequireTransactions("ProviderSequence");
            var result = PhysicalSchemaApplication.ApplyRecoverableWork(target, executor);
            if (result.Outcome == PhysicalSchemaApplicationOutcome.Rejected)
                MongoDeclarationRules.ThrowIfStableDeclarationChanged(
                    result.Plan.PreviousDefinition,
                    target.Subject.Definition);
            if (result.Outcome is PhysicalSchemaApplicationOutcome.Applied or PhysicalSchemaApplicationOutcome.NoChanges)
                state.Remember(target);
            return new SchemaApplyResult(
                new SchemaDiff(SchemaChangeMapping.Describe(result.Plan, target.Subject.Definition)),
                result.Outcome is PhysicalSchemaApplicationOutcome.Applied or PhysicalSchemaApplicationOutcome.NoChanges);
        }
    }

    internal static BsonDocument BuildBackfillFilter(
        BsonDocument document,
        IReadOnlyList<DerivedColumnDefinition> pending)
    {
        var filter = new BsonDocument("_id", document.GetValue("_id"));
        foreach (var source in pending.Select(derived => derived.SourceColumn).Distinct(StringComparer.Ordinal))
        {
            filter[source] = !document.TryGetValue(source, out var value)
                ? new BsonDocument("$exists", false)
                : value.IsBsonNull
                    ? new BsonDocument("$type", 10)
                    : value;
        }
        return filter;
    }

    internal static IMongoCollection<BsonDocument> EnsureAdmission(
        MongoProviderState state,
        MongoAppliedUnit applied,
        MongoStorageAccess access)
    {
        var report = InspectAdmission(state, applied, access);
        if (!report.IsProcessReady)
        {
            var declaredKeyIndexDrift = report.ColumnDrift
                .Where(refusal => refusal.Path.StartsWith("indexes.", StringComparison.Ordinal))
                .ToArray();
            if (declaredKeyIndexDrift.Length != 0)
            {
                throw new InvalidOperationException(
                    $"Storage unit '{applied.Declaration.Name}' is not admitted because the MongoDB index that serves declared-key coverage is missing or differs from the applied declaration. " +
                    $"Restore the index to the applied declaration before opening a session. " +
                    $"[{string.Join("; ", declaredKeyIndexDrift.Select(refusal => refusal.Code + " at " + refusal.Path + ": " + refusal.Message))}]");
            }

            var algorithmDrift = report.ColumnDrift
                .Where(refusal => refusal.Path.EndsWith(".searchKeyAlgorithm", StringComparison.Ordinal))
                .ToArray();
            if (algorithmDrift.Length != 0)
            {
                throw new InvalidOperationException(
                    $"Storage unit '{applied.Declaration.Name}' is not admitted because its persisted folded search-key algorithm differs from the declaration. " +
                    $"Rebuild the derived search-key column before opening a session. " +
                    $"[{string.Join("; ", algorithmDrift.Select(refusal => refusal.Code + " at " + refusal.Path + ": " + refusal.Message))}]");
            }

            var name = CollectionName(applied, access);
            var commands = string.Join("; ", applied.Declaration.Columns.Select(column =>
                $"db.getCollection('{Escape(name)}').updateMany(" +
                $"{{ \"{Escape(column.Name)}\": {{ $exists: false }} }}, " +
                $"{{ $set: {{ \"{Escape(column.Name)}\": {(column.IsNullable ? "null" : "<backfill-value>")} }} }});"));
            throw new InvalidOperationException(
                $"Storage unit '{applied.Declaration.Name}' is not admitted: existing documents are missing declared columns. " +
                $"Backfill before opening it, for example: {commands} " +
                $"[{string.Join("; ", report.ColumnDrift.Select(refusal => refusal.Code + " at " + refusal.Path + ": " + refusal.Message))}]");
        }

        var collection = state.Context.Database.GetCollection<BsonDocument>(CollectionName(applied, access));
        EnsureLedgerIndexes(state, applied.Declaration.AppendIdempotency?.LedgerName);
        EnsureLedgerIndexes(state, applied.Declaration.RetentionIdempotency?.LedgerName);
        return collection;
    }

    /// <summary>
    /// Reads Mongo's actual collection/index catalog. Ordinary index drift remains inspect-only;
    /// the index that makes provider-neutral declared-key coverage true is process-required.
    /// </summary>
    internal static MongoSchemaAdmissionReport InspectAdmission(
        MongoProviderState state,
        MongoAppliedUnit applied,
        MongoStorageAccess access)
    {
        var name = CollectionName(applied, access);
        EnsureCollection(state, applied, name);
        var collection = state.Context.Database.GetCollection<BsonDocument>(name);
        var columnDrift = new List<SchemaRefusal>();
        foreach (var column in applied.Declaration.Columns)
        {
            if (MongoDocumentMapper.IsSystemOwnedToken(applied.Declaration, column))
                continue;
            if (string.Equals(column.Name, "_id", StringComparison.Ordinal))
                continue;

            var missing = collection.Find(new BsonDocument(column.Name,
                    new BsonDocument("$exists", false)))
                .Limit(1)
                .Any();
            if (missing)
            {
                columnDrift.Add(new SchemaRefusal(
                    "GW-RUNTIME-001",
                    $"Physical MongoDB collection contains a document missing declared column '{column.Name}'.",
                    $"columns.{column.Name}"));
                continue;
            }

            var acceptedValues = new BsonArray
            {
                new BsonDocument(column.Name,
                    new BsonDocument(
                        "$type",
                        new BsonArray(MongoValueCodec.GetAcceptedBsonTypeNames(column))))
            };
            var wrongType = collection.Find(new BsonDocument("$and", new BsonArray
                {
                    new BsonDocument(column.Name, new BsonDocument("$exists", true)),
                    new BsonDocument("$nor", acceptedValues)
                }))
                .Limit(1)
                .Any();
            if (wrongType)
            {
                var acceptedTypeDescription = MongoValueCodec.GetAcceptedBsonTypeDescription(column);
                columnDrift.Add(new SchemaRefusal(
                    "GW-RUNTIME-001",
                    $"Physical MongoDB column '{column.Name}' contains a value whose BSON type does not match {acceptedTypeDescription}.",
                    $"columns.{column.Name}.type"));
            }
        }

        if (applied.Declaration.DerivedColumns.Count != 0)
        {
            var persisted = state.ReadAppliedState(applied.Declaration.Id)?.Snapshot.Subject.DerivedColumns
                .ToDictionary(column => column.Name, column => column.AlgorithmId ?? string.Empty, StringComparer.Ordinal)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var expected in applied.Declaration.DerivedColumns)
            {
                var algorithm = ProjectionAlgorithmId(expected);
                if (!persisted.TryGetValue(expected.Name, out var actual) ||
                    !string.Equals(actual, algorithm, StringComparison.Ordinal))
                {
                    columnDrift.Add(new SchemaRefusal(
                        "GW-RUNTIME-001",
                        $"Persisted MongoDB search-key algorithm for derived column '{expected.Name}' differs from '{algorithm}'.",
                        $"columns.{expected.Name}.searchKeyAlgorithm"));
                }
            }
        }

        var actualIndexes = new MongoProviderCatalog(state).ReadIndexes(name, applied.Declaration.Indexes);
        var declaredKeyIndex = MongoSchemaTargets.DeclaredKeyIndex(applied.Declaration);
        var indexDrift = new List<SchemaRefusal>();
        foreach (var expected in applied.Declaration.Indexes)
        {
            var actual = actualIndexes.FirstOrDefault(index =>
                string.Equals(index.Name, expected.Name, StringComparison.Ordinal));
            if (actual is null)
            {
                var refusal = new SchemaRefusal(
                    string.Equals(expected.Name, declaredKeyIndex.Name, StringComparison.Ordinal)
                        ? "GW-RUNTIME-001"
                        : "GW-RUNTIME-002",
                    $"Physical MongoDB collection is missing declared index '{expected.Name}'.",
                    $"indexes.{expected.Name}");
                (string.Equals(expected.Name, declaredKeyIndex.Name, StringComparison.Ordinal)
                    ? columnDrift
                    : indexDrift).Add(refusal);
                continue;
            }

            var keysMatch = actual.Columns.Count == expected.Columns.Count &&
                actual.Columns.Zip(expected.Columns)
                    .All(pair => string.Equals(pair.First.Column, pair.Second.Column, StringComparison.Ordinal) &&
                                 pair.First.Direction == pair.Second.Direction);
            if (actual.IsUnique != expected.IsUnique ||
                actual.MissingValues != expected.MissingValues ||
                !keysMatch)
            {
                var refusal = new SchemaRefusal(
                    string.Equals(expected.Name, declaredKeyIndex.Name, StringComparison.Ordinal)
                        ? "GW-RUNTIME-001"
                        : "GW-RUNTIME-002",
                    $"Physical MongoDB index '{expected.Name}' differs in key order, direction, uniqueness, or partial filter.",
                    $"indexes.{expected.Name}");
                (string.Equals(expected.Name, declaredKeyIndex.Name, StringComparison.Ordinal)
                    ? columnDrift
                    : indexDrift).Add(refusal);
            }
        }

        return new MongoSchemaAdmissionReport(applied.Declaration.Id, columnDrift, indexDrift);
    }

    internal static string ProjectionAlgorithmId(DerivedColumnDefinition definition) => definition.AlgorithmId ?? definition.Projection switch
    {
        PortableProjection.UnicodeFold => PortableStringComparison.UnicodeOrdinalIgnoreCaseAlgorithmId,
        PortableProjection.BoundarySearchKey => PortableStringComparison.SearchKeyAlgorithmId,
        PortableProjection.LocaleSortKey => throw new InvalidOperationException(
            $"Locale sort-key projection '{definition.Name}' requires an explicit algorithm identity."),
        PortableProjection.ElementBoundarySearchKey => throw new InvalidOperationException(
            $"Element search-key projection '{definition.Name}' requires an explicit algorithm identity."),
        PortableProjection.Sha256 => PortableStringComparison.LookupHashAlgorithmId,
        _ => throw new ArgumentOutOfRangeException(nameof(definition), definition.Projection, null)
    };

    private static void EnsureCollection(MongoProviderState state, MongoAppliedUnit applied, string name)
    {
        if (!state.CollectionExists(name))
        {
            state.Context.Database.CreateCollection(name);
            CreateDeclaredIndexes(state.Context.Database.GetCollection<BsonDocument>(name), applied.Declaration);
        }
    }

    /// <summary>
    /// A scoped collection is materialized lazily when its first session opens. Give it the same
    /// declared indexes as the primary collection so a later scope is covered from its first read.
    /// This is runtime materialization, not schema diff/apply logic; planned index evolution still
    /// belongs to <see cref="MongoSchemaExecutor"/>.
    /// </summary>
    private static void CreateDeclaredIndexes(
        IMongoCollection<BsonDocument> collection,
        StorageUnit unit)
    {
        foreach (var index in unit.Indexes)
        {
            var specification = new MongoIndexSpecification(index, unit.Columns);
            var keys = new BsonDocument(specification.Terms.Select(term =>
                new BsonElement(term.Column, term.Direction == KernelSortDirection.Ascending ? 1 : -1)));
            collection.Indexes.CreateOne(new CreateIndexModel<BsonDocument>(keys,
                new CreateIndexOptions<BsonDocument>
                {
                    Name = specification.Name,
                    Unique = specification.IsUnique,
                    PartialFilterExpression = specification.PartialFilter
                }));
        }
    }

    private static void EnsureLedgerIndexes(MongoProviderState state, string? ledgerName)
    {
        if (ledgerName is null)
            return;

        var ledger = state.Operations(ledgerName);
        ledger.Indexes.CreateOne(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("unit").Ascending("committed_at"),
            new CreateIndexOptions { Name = "__groundwork_ledger_cleanup" }));
    }

    internal static string CollectionName(MongoAppliedUnit applied, MongoStorageAccess access)
    {
        if (applied.Declaration.Scope == ScopePolicy.Global)
            return applied.CollectionName;
        var scope = access.Scope?.Value ?? throw new InvalidOperationException(
            "A scoped storage unit requires a scope value.");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
        var collectionName = applied.CollectionName + "__scope__" + hash;
        PortabilityValidator.EnsurePhysicalIdentifier(
            collectionName,
            "scopedCollection.name",
            maximumByteLength: 255);
        return collectionName;
    }

    private static string Escape(string value) => value.Replace("'", "\\'", StringComparison.Ordinal);
}

/// <summary>
/// The declaration rules every MongoDB schema path enforces, whichever entry point compiled the
/// declaration: the in-process coordinator and the deployment tool's target compiler both call this
/// so a declaration MongoDB refuses is refused identically either way.
/// </summary>
internal static class MongoDeclarationRules
{
    internal static void Validate(StorageUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ConcurrencyDeclaration.ValidateDeclaration(unit);
        ArgumentException.ThrowIfNullOrWhiteSpace(unit.Name);
        ArgumentNullException.ThrowIfNull(unit.Columns);
        ArgumentNullException.ThrowIfNull(unit.Key);
        ArgumentNullException.ThrowIfNull(unit.Key.Columns);
        ArgumentNullException.ThrowIfNull(unit.DerivedColumns);
        ArgumentNullException.ThrowIfNull(unit.Indexes);
        var portability = PortabilityValidator.Validate(unit);
        if (!portability.IsPortable)
        {
            var refusal = portability.Refusals[0];
            throw new InvalidOperationException($"{refusal.Code} at {refusal.Path}: {refusal.Message}");
        }
        unit.AppendIdempotency?.Validate(unit);
        unit.RetentionIdempotency?.Validate(unit);
        if (unit.Columns.Count == 0)
            throw new ArgumentException("A MongoDB storage unit must declare at least one column.", nameof(unit));
        if (unit.Key.Columns.Count == 0)
            throw new ArgumentException("A MongoDB storage unit must declare at least one key column.", nameof(unit));
        if (unit.Columns.Any(column => column is null))
            throw new ArgumentException("MongoDB storage columns cannot contain null definitions.", nameof(unit));
        if (unit.Indexes.Any(index => index is null))
            throw new ArgumentException("MongoDB indexes cannot contain null definitions.", nameof(unit));
        if (unit.Indexes.Any(index => index.Columns is null || index.Columns.Any(column => column is null)))
            throw new ArgumentException("MongoDB index columns cannot be null.", nameof(unit));
        var names = unit.Columns.Select(column => column.Name).ToHashSet(StringComparer.Ordinal);
        if (names.Count != unit.Columns.Count)
            throw new ArgumentException("MongoDB storage columns must have unique names.", nameof(unit));
        if (names.Contains("_id"))
            throw new ArgumentException("The '_id' field is reserved by MongoDB and cannot be a declared column.", nameof(unit));
        foreach (var key in unit.Key.Columns)
            if (!names.Contains(key))
                throw new ArgumentException($"Key column '{key}' is not declared.", nameof(unit));
        foreach (var index in unit.Indexes)
        foreach (var column in index.Columns)
            if (!names.Contains(column.Column))
                throw new ArgumentException($"Index column '{column.Column}' is not declared.", nameof(unit));
        foreach (var column in unit.Columns.Where(column => column.Type == PortableType.Decimal))
            if (column.Precision is not (>= 1 and <= 34) || column.Scale is not (>= 0) || column.Scale > column.Precision)
                throw new ArgumentException($"MongoDB Decimal128 requires Decimal column '{column.Name}' to declare Precision 1..34 and Scale 0..Precision.", nameof(unit));
    }

    internal static ImmutableArray<SchemaRefusal> StableDeclarationRefusals(
        StorageUnit? previous,
        StorageUnit desired)
    {
        if (previous is null)
            return [];
        var previousColumns = previous.Columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
        var desiredColumns = desired.Columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
        var previousLogicalKey = previous.Key.Columns.Select(column => previousColumns[column].LogicalId);
        var desiredLogicalKey = desired.Key.Columns.Select(column => desiredColumns[column].LogicalId);
        if (previousLogicalKey.SequenceEqual(desiredLogicalKey, StringComparer.Ordinal) &&
            !previous.Key.Columns.SequenceEqual(desired.Key.Columns, StringComparer.Ordinal))
        {
            return
            [
                new SchemaRefusal(
                    "GW-PORT-008",
                    $"GW-PORT-008 at key.columns: Mongo key field names changed from " +
                    $"[{string.Join(", ", previous.Key.Columns)}] to [{string.Join(", ", desired.Key.Columns)}]. " +
                    "The native _id field layout is part of the route and cannot be renamed in place.",
                    "key.columns")
            ];
        }

        return [];
    }

    internal static void ThrowIfStableDeclarationChanged(StorageUnit? previous, StorageUnit desired)
    {
        var refusal = StableDeclarationRefusals(previous, desired).FirstOrDefault();
        if (refusal is null)
            return;
        ThrowStableDeclarationRefusal(refusal);
    }

    internal static void ThrowStableDeclarationRefusal(SchemaRefusal refusal)
        => throw new MongoSchemaConflictException(refusal);
}

internal interface IMongoSchemaBoundSession
{
    void EnsureSchemaCurrent();
}

internal sealed partial class MongoStorageSession : IMongoStorageSession, IMongoCompareAndDeleteStorageSession, IMongoExactAppendStorageSession, IBatchedStorageSession, IRetentionStorageSession, IStorageInspectionSession, IExactRetentionStorageSession, IExactRetentionAffectedKeysStorageSession, ISetMutationStorageSession, IMongoSchemaBoundSession
{
    private const string HighWaterValue = "high_water";
    private readonly MongoProviderState state;
    private readonly MongoAppliedUnit applied;
    private readonly IMongoCollection<BsonDocument> collection;
    private readonly IClientSessionHandle? transactionSession;
    private readonly MongoUnitOfWork? unitOfWork;
    private readonly SchemaSessionLease schemaSession;
    private bool disposed;

    // A wrapper-owned transaction must let transient failures escape so the wrapper can
    // replay the complete body. Explicit unit-of-work sessions have no body replay boundary
    // here, so they normalize the provider error at the write; direct writes preserve an
    // unknown transient infrastructure failure rather than misclassifying it as uniqueness.
    private bool ShouldNormalizeTransientWriteConflict =>
        unitOfWork is not null;

    internal MongoStorageSession(
        MongoProviderState state,
        MongoAppliedUnit applied,
        MongoStorageAccess access,
        IMongoCollection<BsonDocument> collection,
        IClientSessionHandle? transactionSession,
        MongoUnitOfWork? unitOfWork = null,
        IProviderCommandObserver? observer = null)
    {
        commandObserver = observer;
        this.state = state;
        this.applied = applied;
        this.collection = collection;
        this.transactionSession = transactionSession;
        this.unitOfWork = unitOfWork;
        schemaSession = state.CaptureSchemaSession(applied);
        Access = access;
        Unit = MongoDeclarationSnapshot.Clone(applied.Declaration);
    }

    /// <summary>
    /// Counts every provider command this session issues. It belongs to the session because the session is
    /// what issues commands; it used to be read off an individual write's options, so a batch observed only
    /// whatever happened to be staged first.
    /// </summary>
    private readonly IProviderCommandObserver? commandObserver;

    public StorageUnit Unit { get; }

    public MongoStorageAccess Access { get; }

    public QueryMaterializedResult Query(QueryRequest request, QueryRenderOptions? options = null) =>
        QueryCore(request, options, MongoExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask<QueryMaterializedResult> QueryAsync(
        QueryRequest request,
        QueryRenderOptions? options = null,
        CancellationToken cancellationToken = default) =>
        QueryCore(request, options, MongoExecution.Asynchronous(cancellationToken));

    private async ValueTask<QueryMaterializedResult> QueryCore(
        QueryRequest request,
        QueryRenderOptions? options,
        MongoExecution mode)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        if (Access.IsPrivilegedAcrossScopes)
            throw new InvalidOperationException(
                "GW-ACCESS-004: privileged cross-scope sessions must use QueryAcrossScopes so every row retains its scope.");
        if (!string.Equals(request.Table.Value, Unit.Name, StringComparison.Ordinal))
            throw new ArgumentException($"Query table '{request.Table.Value}' does not match session unit '{Unit.Name}'.", nameof(request));
        if (request.Join is not null &&
            request.Result is not ResultShape.Reduction &&
            request.Projection.AllColumns)
        {
            throw new QueryRenderException(
                "GW-QUERY-032",
                "Joined row materialization requires an explicit projection so source and target fields remain unambiguous.");
        }
        var targetApplied = request.Join is null
            ? null
            : state.ResolveReferenceTarget(Unit, request.Join, Access);
        var suppliedOptions = options ?? QueryRenderOptions.Default;
        var executionSource = Access.Policy == ScopePolicy.Scoped
            ? QueryRequestExecution.WithProviderPredicate(request, request.Where,
                QueryRequestExecution.ScopeBindingDiscriminator(Access.Scope!.Value))
            : request;
        var renderOptions = suppliedOptions.WithIdentityTieBreaks(Unit.Key.Columns.Select(QueryColumn).Where(column => column is not null)!.Select(column => column!)) with
        {
            Indexes = SearchKeyQueryMappings.RetargetIndexes(Unit, suppliedOptions.Indexes)
                .Select(index => index.WithColumnTypes(Unit.Columns.ToDictionary(column => column.Name, column => QueryTypeOf(column.Type), StringComparer.Ordinal))).ToImmutableArray(),
            PhysicalIndexNames = suppliedOptions.PhysicalIndexNames
                .Concat(MongoSchemaTargets.PhysicalIndexNames(Unit)
                    .Where(pair => !suppliedOptions.PhysicalIndexNames.ContainsKey(pair.Key)))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            SearchKeyColumns = SearchKeyQueryMappings.For(Unit, suppliedOptions.FindSelectedIndex()?.Name),
            ElementSearchKeyColumns = SearchKeyQueryMappings.ElementFor(Unit)
        };
        var executionRequest = QueryRequestExecution.ForPage(executionSource, renderOptions);
        var reduction = executionSource.Result as ResultShape.Reduction;
        var reductionColumn = reduction is null ? null : ResolveReductionColumn(reduction, targetApplied);
        var renderer = new MongoQueryRenderer();
        var command = targetApplied is null
            ? renderer.Render(executionRequest, renderOptions, collection.CollectionNamespace.CollectionName)
            : renderer.Render(
                executionRequest,
                renderOptions,
                collection.CollectionNamespace.CollectionName,
                MongoSchemaCoordinator.CollectionName(targetApplied, Access));
        commandObserver?.Observe(new ProviderCommandEvent("mongodb.query", "MongoDB.Aggregate(page)", ProviderCommandKind.Read, IsProbe: false));
        List<BsonDocument> documents;
        long? facetTotalCount = null;
        if (command.Pipeline.Length != 0)
        {
            var unionIndex = command.Pipeline
                .Select((stage, index) => (stage, index))
                .FirstOrDefault(item => item.stage.Contains("$unionWith")).index;
            if (transactionSession is not null && unionIndex != 0)
            {
                // MongoDB forbids $unionWith inside a transaction. Execute the data and
                // count branches separately on the same session, preserving the transaction
                // snapshot while retaining streaming results outside transactions.
                var dataPipeline = PipelineDefinition<BsonDocument, BsonDocument>.Create(command.Pipeline.Take(unionIndex).ToArray());
                var union = command.Pipeline[unionIndex]["$unionWith"].AsBsonDocument;
                var countPipeline = PipelineDefinition<BsonDocument, BsonDocument>.Create(
                    union["pipeline"].AsBsonArray.Select(value => value.AsBsonDocument).ToArray());
                documents = await mode.Aggregate(collection, transactionSession, dataPipeline,
                    new AggregateOptions { Hint = command.Hint }).ConfigureAwait(false);
                documents.AddRange(await mode.Aggregate(collection, transactionSession, countPipeline,
                    new AggregateOptions { Hint = command.Hint }).ConfigureAwait(false));
            }
            else
            {
                var pipeline = PipelineDefinition<BsonDocument, BsonDocument>.Create(command.Pipeline);
                documents = await mode.Aggregate(collection, transactionSession, pipeline,
                    new AggregateOptions { Hint = command.Hint }).ConfigureAwait(false);
            }
            if (command.IncludesTotalCount && documents.Count == 1 && documents[0].Contains("metadata") && documents[0].Contains("data"))
            {
                var envelope = documents[0];
                var metadata = envelope["metadata"].AsBsonArray;
                facetTotalCount = metadata.Count == 0 ? 0L : metadata[0].AsBsonDocument.GetValue("__groundwork_total_count", 0).ToInt64();
                documents = envelope["data"].AsBsonArray.Select(value => value.AsBsonDocument).ToList();
            }
        }
        else
        {
            var findOptions = new FindOptions<BsonDocument>
            {
                Sort = command.Sort.ElementCount == 0 ? null : command.Sort,
                Projection = command.Projection.ElementCount == 0 ? null : command.Projection,
                Skip = command.Skip,
                Limit = command.Limit,
                Hint = command.Hint
            };
            documents = await mode.Find(collection, transactionSession, command.Filter, findOptions)
                .ConfigureAwait(false);
        }

        var rows = documents.Select(document =>
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (reductionColumn is not null &&
                document.TryGetValue(reduction!.Column.Name, out var reducedValue))
            {
                row[reduction.Column.Name] = reduction is ResultShape.Sum { Column.Type: QueryType.Int32 }
                    ? (reducedValue.IsBsonNull ? null : reducedValue.ToInt64())
                    : MongoValueCodec.Decode(reducedValue, reductionColumn);
            }
            else if (reductionColumn is null && targetApplied is not null)
            {
                foreach (var column in executionRequest.Projection.Columns)
                {
                    var definition = ResolveResultColumn(column, targetApplied);
                    BsonValue? value = null;
                    if (string.Equals(column.Table.Value, Unit.Name, StringComparison.Ordinal))
                    {
                        document.TryGetValue(column.Name, out value);
                    }
                    else if (document.TryGetValue(MongoQueryRenderer.TargetOutputField, out var targetValue) &&
                             targetValue.IsBsonDocument)
                    {
                        targetValue.AsBsonDocument.TryGetValue(column.Name, out value);
                    }

                    if (value is not null)
                        row[QueryRequestExecution.ResultFieldName(executionSource, column)] =
                            MongoValueCodec.Decode(value, definition);
                }

                var effectiveOrder = renderOptions.GetEffectiveOrder(executionSource);
                for (var index = 0; index < effectiveOrder.Length; index++)
                {
                    var alias = QueryRequestExecution.ContinuationFieldName(index);
                    if (document.TryGetValue(alias, out var value))
                    {
                        row[alias] = MongoValueCodec.Decode(
                            value,
                            ResolveResultColumn(effectiveOrder[index].Column, targetApplied));
                    }
                }
            }
            else if (reductionColumn is null)
            {
                foreach (var column in Unit.Columns)
                {
                    if (MongoDocumentMapper.IsSystemOwnedToken(Unit, column))
                        continue;
                    if (document.TryGetValue(column.Name, out var value))
                        row[column.Name] = MongoValueCodec.Decode(value, column);
                }
            }
            if (document.TryGetValue("__groundwork_total_count", out var count))
                row["__groundwork_total_count"] = count.ToInt64();
            if (document.TryGetValue("__groundwork_count_only", out var marker))
                row["__groundwork_count_only"] = marker.ToInt64();
            return (IReadOnlyDictionary<string, object?>)row;
        }).ToArray();
        if (facetTotalCount is long count)
        {
            if (rows.Length == 0)
            {
                rows =
                [
                    new Dictionary<string, object?>
                    {
                        ["__groundwork_total_count"] = count,
                        ["__groundwork_count_only"] = 1L
                    }
                ];
            }
            else
            {
                var first = new Dictionary<string, object?>(rows[0], StringComparer.Ordinal)
                {
                    ["__groundwork_total_count"] = count
                };
                rows[0] = first;
            }
        }
        if (command.IncludesTotalCount && facetTotalCount is null && rows.Length == 0)
        {
            rows =
            [
                new Dictionary<string, object?>
                {
                    ["__groundwork_total_count"] = 0L,
                    ["__groundwork_count_only"] = 1L
                }
            ];
        }
        await AssertExplainPlan(command, renderOptions, mode).ConfigureAwait(false);
        return QueryResultMaterializer.Materialize(
            executionSource,
            renderOptions,
            rows,
            command.ExpectedIndex,
            command.Hint is not null,
            sourceIncludesRequestedOffset: true,
            sourceIncludesContinuation: true,
            sourceIncludesDistinct: true);
    }

    private ColumnDefinition ResolveReductionColumn(
        ResultShape.Reduction reduction,
        MongoAppliedUnit? targetApplied)
        => ResolveResultColumn(reduction.Column, targetApplied);

    private ColumnDefinition ResolveResultColumn(
        ColumnRef queryColumn,
        MongoAppliedUnit? targetApplied)
    {
        StorageUnit declaration;
        if (string.Equals(queryColumn.Table.Value, Unit.Name, StringComparison.Ordinal))
        {
            declaration = Unit;
        }
        else if (targetApplied is not null &&
                 string.Equals(
                     queryColumn.Table.Value,
                     targetApplied.Declaration.Name,
                     StringComparison.Ordinal))
        {
            declaration = targetApplied.Declaration;
        }
        else
        {
            throw new QueryRenderException(
                "GW-QUERY-032",
                $"Result column '{queryColumn}' does not belong to the applied source or joined target.");
        }

        var column = declaration.Columns.SingleOrDefault(candidate =>
            string.Equals(candidate.Name, queryColumn.Name, StringComparison.Ordinal));
        if (column is null || QueryTypeOf(column.Type) != queryColumn.Type)
        {
            throw new QueryRenderException(
                "GW-QUERY-032",
                $"Result column '{queryColumn}' does not match its applied storage declaration.");
        }
        return column;
    }

    public CrossScopeQueryResult QueryAcrossScopes(
        QueryRequest request,
        QueryRenderOptions? options = null) =>
        QueryAcrossScopesCore(request, options, MongoExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask<CrossScopeQueryResult> QueryAcrossScopesAsync(
        QueryRequest request,
        QueryRenderOptions? options = null,
        CancellationToken cancellationToken = default) =>
        QueryAcrossScopesCore(request, options, MongoExecution.Asynchronous(cancellationToken));

    private async ValueTask<CrossScopeQueryResult> QueryAcrossScopesCore(
        QueryRequest request,
        QueryRenderOptions? options,
        MongoExecution mode)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        if (!Access.IsPrivilegedAcrossScopes)
            throw new InvalidOperationException(
                "GW-ACCESS-001: cross-scope queries require explicit privileged across-scope access.");
        if (!string.Equals(request.Table.Value, Unit.Name, StringComparison.Ordinal))
            throw new ArgumentException(
                $"Query table '{request.Table.Value}' does not match session unit '{Unit.Name}'.",
                nameof(request));
        if (request.Join is not null)
        {
            throw new InvalidOperationException(
                "GW-ACCESS-003: privileged cross-scope queries refuse joins because one audited query cannot bind a single same-scope target collection.");
        }
        var audit = StorageAccessValidation.BeginPrivilegedQuery(
            StorageAccess.PrivilegedAcrossScopes(Access.Audit!),
            Unit);

        CrossScopeQueryResult result;
        try
        {
            var suppliedOptions = options ?? QueryRenderOptions.Default;
            if (suppliedOptions.FindPinnedIndex() is not null)
                throw new NotSupportedException(
                    "GW-ACCESS-005: MongoDB cross-scope queries cannot pin one physical index across multiple scope collections.");
            var scopeToken = new ColumnRef(
                new TableId(Unit.Name),
                CrossScopeQueryMaterializer.ScopeTokenColumn,
                QueryType.String,
                isNullable: false);
            var renderOptions = suppliedOptions.WithIdentityTieBreaks(
                new[] { scopeToken }
                    .Concat(Unit.Key.Columns
                        .Select(QueryColumn)
                        .Where(column => column is not null)
                        .Select(column => column!))) with
            {
                Indexes = ImmutableArray<QueryIndexDeclaration>.Empty,
                PhysicalIndexNames = new Dictionary<string, string>(StringComparer.Ordinal),
                SearchKeyColumns = SearchKeyQueryMappings.For(Unit),
                ElementSearchKeyColumns = SearchKeyQueryMappings.ElementFor(Unit),
                LatestPartitionColumns = [scopeToken]
            };
            var executionSource = QueryRequestExecution.WithProviderPredicate(
                request,
                request.Where,
                CrossScopeQueryMaterializer.BindingDiscriminator(
                    StorageAccess.PrivilegedAcrossScopes(Access.Audit!)));
            var executionRequest = EnsureCrossScopeProjection(
                QueryRequestExecution.ForPage(executionSource, renderOptions));
            var sourcePrefix = CrossScopeSourcePrefix(state.ReadScopes(applied));
            var command = new MongoQueryRenderer().Render(
                executionRequest,
                renderOptions,
                collection.CollectionNamespace.CollectionName,
                sourcePrefix);
            var pipeline = PipelineDefinition<BsonDocument, BsonDocument>.Create(command.Pipeline);
            commandObserver?.Observe(new ProviderCommandEvent("mongodb.query-across-scopes", "MongoDB.Aggregate(cross-scope)", ProviderCommandKind.Read, IsProbe: false));
            var documents = await mode.Aggregate(collection, session: null, pipeline).ConfigureAwait(false);
            var rows = documents.Select(ToCrossScopeQueryRow).ToArray();
            var materialized = QueryResultMaterializer.Materialize(
                executionSource,
                renderOptions,
                rows,
                selectedIndex: null,
                indexHintApplied: false,
                sourceIncludesRequestedOffset: true,
                sourceIncludesContinuation: true);
            result = CrossScopeQueryMaterializer.FromNativePage(
                materialized,
                rows,
                CrossScopeQueryMaterializer.RawScopeColumn);
        }
        catch (Exception exception)
        {
            audit.Failure(exception);
            throw;
        }

        audit.Success();
        return result;
    }

    private static IReadOnlyList<BsonDocument> CrossScopeSourcePrefix(
        IReadOnlyList<MongoScopeRegistration> scopes)
    {
        var stages = new List<BsonDocument>
        {
            new("$match", new BsonDocument("_id", new BsonDocument("$exists", false)))
        };
        foreach (var scope in scopes)
        {
            stages.Add(new BsonDocument("$unionWith", new BsonDocument
            {
                ["coll"] = scope.CollectionName,
                ["pipeline"] = new BsonArray
                {
                    new BsonDocument("$set", new BsonDocument
                    {
                        [CrossScopeQueryMaterializer.RawScopeColumn] = scope.Scope.Value,
                        [CrossScopeQueryMaterializer.ScopeTokenColumn] = scope.Token
                    })
                }
            }));
        }
        return stages;
    }

    private QueryRequest EnsureCrossScopeProjection(QueryRequest request)
    {
        if (request.Projection.AllColumns || request.Projection.Columns.Any(column =>
                string.Equals(column.Name, CrossScopeQueryMaterializer.RawScopeColumn, StringComparison.Ordinal)))
            return request;
        var rawScope = new ColumnRef(
            new TableId(Unit.Name),
            CrossScopeQueryMaterializer.RawScopeColumn,
            QueryType.String,
            isNullable: false);
        return QueryRequestExecution.WithProjection(
            request,
            Projection.ColumnsOnly([.. request.Projection.Columns, rawScope]));
    }

    private IReadOnlyDictionary<string, object?> ToCrossScopeQueryRow(BsonDocument document)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var column in Unit.Columns)
        {
            if (MongoDocumentMapper.IsSystemOwnedToken(Unit, column))
                continue;
            if (document.TryGetValue(column.Name, out var value))
                row[column.Name] = MongoValueCodec.Decode(value, column);
        }
        foreach (var internalColumn in new[]
                 {
                     CrossScopeQueryMaterializer.RawScopeColumn,
                     CrossScopeQueryMaterializer.ScopeTokenColumn
                 })
        {
            if (document.TryGetValue(internalColumn, out var value))
                row[internalColumn] = value.AsString;
        }
        if (document.TryGetValue("__groundwork_total_count", out var count))
            row["__groundwork_total_count"] = count.ToInt64();
        if (document.TryGetValue("__groundwork_count_only", out var marker))
            row["__groundwork_count_only"] = marker.ToInt64();
        return row;
    }

    public AggregationResult Aggregate(AggregationQuery query) =>
        AggregateCore(query, MongoExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask<AggregationResult> AggregateAsync(
        AggregationQuery query,
        CancellationToken cancellationToken = default) =>
        AggregateCore(query, MongoExecution.Asynchronous(cancellationToken));

    private ValueTask<AggregationResult> AggregateCore(AggregationQuery query, MongoExecution mode)
    {
        RefusePrivilegedOperation("aggregate");
        ArgumentNullException.ThrowIfNull(query);
        ThrowIfDisposed();
        var profile = AggregationProfileValidator.ResolveOrThrow(Unit, query);
        AggregationProfileValidator.Validate(Unit, profile);
        return ExecuteNativeAggregation(profile, query, mode);
    }

    private async ValueTask AssertExplainPlan(MongoQueryCommand query, QueryRenderOptions options, MongoExecution mode)
    {
        var logicalIndex = query.ExpectedIndex;
        if (query.IsMatchNone || !ExplainAssertionMode.ShouldAssert(logicalIndex)) return;
        if (transactionSession is not null)
            throw new InvalidOperationException(
                "MongoDB explain-assert cannot run inside a transaction; execute the differential query outside a unit of work.");

        var native = query.Pipeline.Length == 0
            ? new BsonDocument
            {
                { "find", collection.CollectionNamespace.CollectionName },
                { "filter", query.Filter },
                { "sort", query.Sort, query.Sort.ElementCount != 0 },
                { "projection", query.Projection, query.Projection.ElementCount != 0 },
                { "skip", query.Skip.GetValueOrDefault(), query.Skip.HasValue },
                { "limit", query.Limit.GetValueOrDefault(), query.Limit.HasValue },
                { "hint", query.Hint ?? string.Empty, query.Hint is not null }
            }
            : new BsonDocument
            {
                { "aggregate", collection.CollectionNamespace.CollectionName },
                { "pipeline", new BsonArray(query.Pipeline) },
                { "cursor", new BsonDocument() },
                { "hint", query.Hint ?? string.Empty, query.Hint is not null }
            };
        var explainCommand = new BsonDocument
        {
            { "explain", native },
            { "verbosity", "executionStats" }
        };
        var explain = await mode.Run(
            token => state.Context.Database.RunCommandAsync(new BsonDocumentCommand<BsonDocument>(explainCommand), cancellationToken: token),
            () => state.Context.Database.RunCommand(new BsonDocumentCommand<BsonDocument>(explainCommand)))
            .ConfigureAwait(false);
        var rawPlan = explain.ToJson(new JsonWriterSettings { Indent = true });
        var physicalIndex = options.ResolvePhysicalIndexName(logicalIndex!);
        ExplainAssertionMode.AssertChosenIndex(
            "MongoDB", logicalIndex!, physicalIndex, query.Hint is not null, rawPlan,
            MongoExplainPlanInspector.ChoseIndex(explain, physicalIndex));
    }

    private ColumnRef? QueryColumn(string name)
    {
        var column = Unit.Columns.Single(item => item.Name == name);
        if (MongoDocumentMapper.IsSystemOwnedToken(Unit, column))
            return null;
        return column.Type switch
        {
            PortableType.Boolean => new ColumnRef(new TableId(Unit.Name), name, QueryType.Boolean, column.IsNullable),
            PortableType.Int32 => new ColumnRef(new TableId(Unit.Name), name, QueryType.Int32, column.IsNullable),
            PortableType.Int64 => new ColumnRef(new TableId(Unit.Name), name, QueryType.Int64, column.IsNullable),
            PortableType.Decimal => new ColumnRef(new TableId(Unit.Name), name, QueryType.Decimal, column.IsNullable, null,
                column.Precision is int precision ? checked((byte)precision) : null,
                column.Scale is int scale ? checked((byte)scale) : null),
            PortableType.String => new ColumnRef(new TableId(Unit.Name), name, QueryType.String, column.IsNullable, column.MaxLength),
            PortableType.DateTimeOffset => new ColumnRef(new TableId(Unit.Name), name, QueryType.DateTimeOffset, column.IsNullable),
            PortableType.Guid => new ColumnRef(new TableId(Unit.Name), name, QueryType.Guid, column.IsNullable),
            PortableType.Binary => new ColumnRef(new TableId(Unit.Name), name, QueryType.Binary, column.IsNullable, column.MaxLength),
            _ => null
        };
    }

    private static QueryType? QueryTypeOf(PortableType type) => type switch
    {
        PortableType.Boolean => QueryType.Boolean,
        PortableType.Int32 => QueryType.Int32,
        PortableType.Int64 => QueryType.Int64,
        PortableType.Decimal => QueryType.Decimal,
        PortableType.String => QueryType.String,
        PortableType.DateTimeOffset => QueryType.DateTimeOffset,
        PortableType.Guid => QueryType.Guid,
        PortableType.Binary => QueryType.Binary,
        _ => null
    };

    public MongoStoredEntry? Read(MongoStorageKey key) =>
        ReadCore(key, MongoExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask<MongoStoredEntry?> ReadAsync(MongoStorageKey key, CancellationToken cancellationToken = default) =>
        ReadCore(key, MongoExecution.Asynchronous(cancellationToken));

    private async ValueTask<MongoStoredEntry?> ReadCore(MongoStorageKey key, MongoExecution mode)
    {
        RefusePrivilegedOperation("read");
        ArgumentNullException.ThrowIfNull(key);
        ThrowIfDisposed();
        var identity = MongoDocumentMapper.EncodeKey(Unit, key.Values);
        var document = await FindOne(identity, mode, "mongodb.read", isProbe: false).ConfigureAwait(false);
        return document is null
            ? null
            : MongoDocumentMapper.DecodeEntry(Unit, document,
                await Version(identity, mode, document).ConfigureAwait(false));
    }

    public MongoWriteOutcome Insert(MongoStorageValues values, MongoWriteOptions? options = null) =>
        InsertAsync(values, options, MongoExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask<MongoWriteOutcome> InsertAsync(
        MongoStorageValues values,
        MongoWriteOptions? options = null,
        CancellationToken cancellationToken = default) =>
        InsertAsync(values, options, MongoExecution.Asynchronous(cancellationToken));

    private ValueTask<MongoWriteOutcome> InsertAsync(MongoStorageValues values, MongoWriteOptions? options, MongoExecution mode)
    {
        RefusePrivilegedOperation("insert");
        WritePreconditionValidator.Validate(Unit, WriteOperation.Insert, ToStoreOptions(options));
        WritePreconditionValidator.ValidateWrittenValues(Unit, values.Values);
        return Mutate(values, options, MutationKind.Insert, mode);
    }

    public MongoWriteOutcome Update(MongoStorageValues values, MongoWriteOptions? options = null) =>
        UpdateAsync(values, options, MongoExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask<MongoWriteOutcome> UpdateAsync(
        MongoStorageValues values,
        MongoWriteOptions? options = null,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(values, options, MongoExecution.Asynchronous(cancellationToken));

    private ValueTask<MongoWriteOutcome> UpdateAsync(MongoStorageValues values, MongoWriteOptions? options, MongoExecution mode)
    {
        RefusePrivilegedOperation("update");
        WritePreconditionValidator.Validate(Unit, WriteOperation.Update, ToStoreOptions(options));
        WritePreconditionValidator.ValidateWrittenValues(Unit, values.Values);
        return Mutate(values, options, MutationKind.Update, mode);
    }

    public MongoWriteOutcome Upsert(MongoStorageValues values, MongoWriteOptions? options = null) =>
        UpsertAsync(values, options, MongoExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask<MongoWriteOutcome> UpsertAsync(
        MongoStorageValues values,
        MongoWriteOptions? options = null,
        CancellationToken cancellationToken = default) =>
        UpsertAsync(values, options, MongoExecution.Asynchronous(cancellationToken));

    private ValueTask<MongoWriteOutcome> UpsertAsync(MongoStorageValues values, MongoWriteOptions? options, MongoExecution mode)
    {
        RefusePrivilegedOperation("upsert");
        WritePreconditionValidator.Validate(Unit, WriteOperation.Upsert, ToStoreOptions(options));
        WritePreconditionValidator.ValidateWrittenValues(Unit, values.Values);
        return Mutate(values, options, MutationKind.Upsert, mode);
    }

    public MongoWriteOutcome ConditionalUpsert(MongoStorageValues values, MongoWriteOptions? options = null) =>
        ConditionalUpsertAsync(values, options, MongoExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask<MongoWriteOutcome> ConditionalUpsertAsync(
        MongoStorageValues values,
        MongoWriteOptions? options = null,
        CancellationToken cancellationToken = default) =>
        ConditionalUpsertAsync(values, options, MongoExecution.Asynchronous(cancellationToken));

    private ValueTask<MongoWriteOutcome> ConditionalUpsertAsync(
        MongoStorageValues values,
        MongoWriteOptions? options,
        MongoExecution mode)
    {
        RefusePrivilegedOperation("conditional upsert");
        WritePreconditionValidator.Validate(Unit, WriteOperation.ConditionalUpsert, ToStoreOptions(options));
        WritePreconditionValidator.ValidateWrittenValues(Unit, values.Values);
        return ConditionalUpsertCore(values, options, mode);
    }

    public IReadOnlyList<RowWriteOutcome> ApplyBatch(IReadOnlyList<RowWrite> writes)
        => ApplyBatch(writes, exactOutcomes: false);

    public IReadOnlyList<RowWriteOutcome> ApplyBatch(IReadOnlyList<RowWrite> writes, bool exactOutcomes) =>
        ApplyBatchAsync(writes, exactOutcomes, MongoExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask<IReadOnlyList<RowWriteOutcome>> ApplyBatchAsync(
        IReadOnlyList<RowWrite> writes,
        bool exactOutcomes,
        CancellationToken cancellationToken = default) =>
        ApplyBatchAsync(writes, exactOutcomes, MongoExecution.Asynchronous(cancellationToken));

    private async ValueTask<IReadOnlyList<RowWriteOutcome>> ApplyBatchAsync(
        IReadOnlyList<RowWrite> writes,
        bool exactOutcomes,
        MongoExecution mode)
    {
        RefusePrivilegedOperation("batch write");
        ArgumentNullException.ThrowIfNull(writes);
        ThrowIfDisposed();
        var nativeOnAppend = IsNativeAppendBatch(writes);
        var outcomes = await ExecuteWithTransactionIfNeeded(
            transactional => transactional.ApplyBatchCore(writes, exactOutcomes, mode), mode).ConfigureAwait(false);
        if (nativeOnAppend &&
            !outcomes.Any(item => IsBatchAbortingOutcome(item.Outcome)) &&
            OnAppendRetentionCoordinator.ContainsAppend(outcomes))
            await ApplyOnAppendRetention(mode).ConfigureAwait(false);
        return outcomes;
    }

    private bool IsNativeAppendBatch(IReadOnlyList<RowWrite> writes) =>
        Unit.Retention?.Trigger == RetentionTrigger.OnAppend &&
        writes.Count != 0 &&
        Unit.Columns.All(column => column.Generation != ColumnGeneration.ProviderSequence) &&
        writes.All(write => write.Mode == RowWriteMode.Upsert &&
                            write.Options.Precondition.Kind == WritePreconditionKind.Unconditional &&
                            write.Values is not null);

    private async ValueTask<IReadOnlyList<RowWriteOutcome>> ApplyBatchCore(
        IReadOnlyList<RowWrite> writes,
        bool exactOutcomes,
        MongoExecution mode)
    {
        if (writes.Count == 0)
            return [];
        if (writes.Any(write => write.Mode != RowWriteMode.Upsert ||
                               write.Options.Precondition.Kind != WritePreconditionKind.Unconditional ||
                               write.Values is null ||
                               Unit.Columns.Any(column => column.Generation == ColumnGeneration.ProviderSequence)))
            return await ApplyBatchFallback(writes, mode).ConfigureAwait(false);

        // Keep the logical RowWrite for outcome correlation and physicalize exactly once for
        // the native command. Fallback and exact paths delegate to single-row methods, which
        // perform their own physicalization.
        var physicalWrites = writes.Select(write => write.PopulateSearchKeyValues(Unit)).ToArray();

        // BulkWrite can acknowledge each model but cannot identify whether each
        // upsert inserted or updated. CommitWithOutcomes requests that exact evidence;
        // use the native single-row conditional primitive in that mode.
        if (exactOutcomes)
        {
            var exact = new List<RowWriteOutcome>(writes.Count);
            for (var index = 0; index < writes.Count; index++)
            {
                var outcome = new RowWriteOutcome(writes[index], ToStore(await ExactOutcomeUpsert(
                    new MongoStorageValues(physicalWrites[index].Values!.Values),
                    ToNative(writes[index].Options),
                    mode).ConfigureAwait(false)));
                exact.Add(outcome);
                if (IsBatchAbortingOutcome(outcome.Outcome))
                    return await CompleteBatchOutcomes(exact, mode).ConfigureAwait(false);
            }
            return exact;
        }

        var models = new List<WriteModel<BsonDocument>>(writes.Count);
        var incompleteWrites = new List<ColumnDefinition?>();
        foreach (var write in physicalWrites)
        {
            var identity = MongoDocumentMapper.EncodeKey(Unit, write.Values!.Values);
            var missingRequired = MissingRequiredColumn(write.Values.Values);
            var canInsert = missingRequired is null;
            incompleteWrites.Add(missingRequired);
            var document = canInsert
                ? await MongoDocumentMapper.EncodeDocument(
                    Unit, write.Values.Values, identity, existing: null, _ =>
                        throw new InvalidOperationException("ProviderSequence must use the fallback batch path."))
                    .ConfigureAwait(false)
                : null;
            var set = new BsonDocument();
            var setOnInsert = new BsonDocument();
            foreach (var column in Unit.Columns)
            {
                if (MongoDocumentMapper.IsSystemOwnedToken(Unit, column))
                    continue;
                if (Unit.Key.Columns.Contains(column.Name, StringComparer.Ordinal))
                {
                    // Mongo's _id is the lookup identity, not a substitute for the
                    // declared key fields. Persist the key fields on insert so schema
                    // admission and subsequent reads see the complete declaration.
                    if (canInsert)
                        setOnInsert[column.Name] = document![column.Name];
                    continue;
                }
                if (column.Name != "createdAt" && write.Values.Values.ContainsKey(column.Name))
                    set[column.Name] = document?[column.Name] ??
                        MongoValueCodec.Encode(write.Values.Values[column.Name], column);
                else if (canInsert)
                    setOnInsert[column.Name] = document![column.Name];
            }
            if (!canInsert && set.ElementCount == 0)
                AddKeyOnlyNoOp(set, write.Values.Values);
            var update = new BsonDocument();
            if (set.ElementCount != 0)
                update["$set"] = set;
            if (setOnInsert.ElementCount != 0)
                update["$setOnInsert"] = setOnInsert;
            if (Unit.Concurrency.IsOptimistic)
                update["$inc"] = new BsonDocument(MongoDocumentMapper.VersionField, 1L);
            models.Add(new UpdateOneModel<BsonDocument>(new BsonDocument("_id", identity), update)
            {
                IsUpsert = canInsert
            });
        }

        commandObserver?.Observe(new ProviderCommandEvent(
            "mongodb.batch-write",
            "MongoDB.BulkWrite(UpdateOne upsert:eligible-per-row ordered:false)",
            ProviderCommandKind.Write,
            IsProbe: false));
        try
        {
            var bulkOptions = new BulkWriteOptions { IsOrdered = false };
            var result = await mode.Run(
                token => transactionSession is null
                    ? collection.BulkWriteAsync(models, bulkOptions, token)
                    : collection.BulkWriteAsync(transactionSession, models, bulkOptions, token),
                () => transactionSession is null
                    ? collection.BulkWrite(models, bulkOptions)
                    : collection.BulkWrite(transactionSession, models, bulkOptions)).ConfigureAwait(false);
            ThrowIfIncompleteUpsertWasNotApplied(result, incompleteWrites, []);
            return await CompleteBatchOutcomes(
                writes.Select(write => new RowWriteOutcome(write,
                    new WriteOutcome(WriteOutcomeStatus.Upserted))).ToArray(),
                mode).ConfigureAwait(false);
        }
        catch (MongoBulkWriteException<BsonDocument> exception)
        {
            var failures = exception.WriteErrors.ToDictionary(error => error.Index, error => error);
            ThrowIfIncompleteUpsertWasNotApplied(exception.Result, incompleteWrites, failures.Keys);
            return await CompleteBatchOutcomes(writes.Select((write, index) =>
                new RowWriteOutcome(write, failures.TryGetValue(index, out var error)
                    ? new WriteOutcome(WriteOutcomeStatus.UniqueViolation, null, ExtractIndexName(error.Message))
                    : new WriteOutcome(WriteOutcomeStatus.Upserted))).ToArray(), mode).ConfigureAwait(false);
        }
    }

    private static void ThrowIfIncompleteUpsertWasNotApplied(
        BulkWriteResult<BsonDocument> result,
        IReadOnlyList<ColumnDefinition?> incompleteWrites,
        IEnumerable<int> failedIndexes)
    {
        if (!result.IsAcknowledged)
            return;
        var failures = failedIndexes.ToHashSet();
        var expectedApplied = incompleteWrites.Count - failures.Count;
        if (result.MatchedCount + result.Upserts.Count == expectedApplied)
            return;
        var missing = incompleteWrites
            .Where((column, index) => column is not null && !failures.Contains(index))
            .FirstOrDefault();
        if (missing is not null)
            throw new InvalidOperationException($"Column '{missing.Name}' is required.");
        throw new InvalidOperationException("MongoDB did not apply every acknowledged aggregate batch write.");
    }

    private async ValueTask<IReadOnlyList<RowWriteOutcome>> ApplyBatchFallback(IReadOnlyList<RowWrite> writes, MongoExecution mode)
    {
        var outcomes = new List<RowWriteOutcome>(writes.Count);
        foreach (var write in writes)
        {
            var outcome = new RowWriteOutcome(write, ToStore(await (write.Mode switch
            {
                RowWriteMode.Insert => InsertAsync(new MongoStorageValues(write.Values!.Values), ToNative(write.Options), mode),
                RowWriteMode.Update => UpdateAsync(new MongoStorageValues(write.Values!.Values), ToNative(write.Options), mode),
                RowWriteMode.Upsert when write.Options.Precondition.Kind == WritePreconditionKind.IfVersion =>
                    ConditionalUpsertAsync(new MongoStorageValues(write.Values!.Values), ToNative(write.Options), mode),
                RowWriteMode.Upsert => UpsertAsync(new MongoStorageValues(write.Values!.Values), ToNative(write.Options), mode),
                RowWriteMode.ConditionalUpsert => ConditionalUpsertAsync(new MongoStorageValues(write.Values!.Values), ToNative(write.Options), mode),
                RowWriteMode.Delete => DeleteAsync(new MongoStorageKey(write.Key!.Values), ToNative(write.Options), mode),
                RowWriteMode.CompareAndDelete => CompareAndDeleteAsync(new MongoStorageKey(write.Key!.Values), write.ExpectedValues, ToNative(write.Options), mode),
                _ => throw new ArgumentOutOfRangeException(nameof(writes), write.Mode, null)
            }).ConfigureAwait(false)));
            outcomes.Add(outcome);
            if (IsBatchAbortingOutcome(outcome.Outcome))
                return await CompleteBatchOutcomes(outcomes, mode).ConfigureAwait(false);
        }
        return outcomes;
    }

    private async ValueTask<IReadOnlyList<RowWriteOutcome>> CompleteBatchOutcomes(
        IReadOnlyList<RowWriteOutcome> outcomes,
        MongoExecution mode)
    {
        if (!outcomes.Any(item => IsBatchAbortingOutcome(item.Outcome)))
            return outcomes;

        // An explicit unit of work has no outer operation replay boundary. A conflict that aborts
        // its transaction must therefore be surfaced as the documented provider-neutral batch
        // failure, after making the unit terminal. The exception carries only attributed failures;
        // successful rows are rolled back and are not reported as committed outcomes.
        if (transactionSession is not null && unitOfWork is not null)
        {
            try { await Abort(transactionSession, mode).ConfigureAwait(false); }
            catch (MongoException) { }
            unitOfWork.Poison();
            var failures = outcomes
                .Where(item => IsBatchAbortingOutcome(item.Outcome))
                .ToArray();
            throw new BatchWriteException(
                "A MongoDB batch write conflict aborted the whole unit of work; retry the complete unit of work.",
                failures);
        }

        return outcomes;
    }

    private static bool IsBatchAbortingOutcome(WriteOutcome outcome) =>
        outcome.Status is WriteOutcomeStatus.UniqueViolation or WriteOutcomeStatus.ConcurrencyConflict;

    private static MongoWriteOptions? ToNative(WriteOptions options) =>
        new() { Precondition = options.Precondition };

    private static WriteOutcome ToStore(MongoWriteOutcome outcome) =>
        new((WriteOutcomeStatus)outcome.Status, outcome.Version, outcome.UniqueIndexName, outcome.GeneratedValues);

    public MongoWriteOutcome Delete(MongoStorageKey key, MongoWriteOptions? options = null) =>
        DeleteAsync(key, options, MongoExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask<MongoWriteOutcome> DeleteAsync(
        MongoStorageKey key,
        MongoWriteOptions? options = null,
        CancellationToken cancellationToken = default) =>
        DeleteAsync(key, options, MongoExecution.Asynchronous(cancellationToken));

    private ValueTask<MongoWriteOutcome> DeleteAsync(MongoStorageKey key, MongoWriteOptions? options, MongoExecution mode)
    {
        RefusePrivilegedOperation("delete");
        WritePreconditionValidator.Validate(Unit, WriteOperation.Delete, ToStoreOptions(options));
        ArgumentNullException.ThrowIfNull(key);
        ThrowIfDisposed();
        return ExecuteWithTransactionIfNeeded(transactional => transactional.DeleteCore(key, options, mode), mode);
    }

    public SetMutationResult UpdateWhere(Predicate where, IReadOnlyDictionary<string, object?> assignments) =>
        UpdateWhereCore(where, assignments, MongoExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask<SetMutationResult> UpdateWhereAsync(
        Predicate where,
        IReadOnlyDictionary<string, object?> assignments,
        CancellationToken cancellationToken = default) =>
        UpdateWhereCore(where, assignments, MongoExecution.Asynchronous(cancellationToken));

    public SetMutationResult DeleteWhere(Predicate where) =>
        DeleteWhereCore(where, MongoExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask<SetMutationResult> DeleteWhereAsync(
        Predicate where,
        CancellationToken cancellationToken = default) =>
        DeleteWhereCore(where, MongoExecution.Asynchronous(cancellationToken));

    /// <summary>
    /// Applies one <c>updateMany</c>. Scope needs no filter term: a scoped unit lives in its own
    /// collection on MongoDB, and this session holds that collection.
    /// </summary>
    private async ValueTask<SetMutationResult> UpdateWhereCore(
        Predicate where,
        IReadOnlyDictionary<string, object?> assignments,
        MongoExecution mode)
    {
        ArgumentNullException.ThrowIfNull(where);
        SetMutationExecutionAdmission.Require(where);
        var physical = SetMutationValidation.ValidateAndPhysicalizeAssignments(Unit, assignments);
        ThrowIfDisposed();
        RefusePrivilegedOperation("update-where");
        var filter = new MongoQueryRenderer().RenderAggregationSourcePredicate(
            where,
            Unit.Name,
            QueryRenderOptions.Default with
            {
                SearchKeyColumns = SearchKeyQueryMappings.For(Unit),
                ElementSearchKeyColumns = SearchKeyQueryMappings.ElementFor(Unit)
            });
        var set = new BsonDocument();
        foreach (var column in physical.Keys.OrderBy(column => column, StringComparer.Ordinal))
            set[column] = MongoValueCodec.Encode(physical[column], Unit.Columns.First(definition => definition.Name == column));
        var update = new BsonDocument { ["$set"] = set };
        if (Unit.Concurrency.IsOptimistic)
            update["$inc"] = new BsonDocument(MongoDocumentMapper.VersionField, 1L);
        // Observer text is diagnostic metadata, not a command recorder: it never carries filter or
        // assignment values, which may hold PII.
        commandObserver?.Observe(new ProviderCommandEvent(
            "mongodb.update-where",
            $"MongoDB.UpdateMany(filter=predicate; update=$set{(Unit.Concurrency.IsOptimistic ? "/$inc" : string.Empty)})",
            ProviderCommandKind.Write,
            IsProbe: false));
        var result = await mode.Run(
            token => transactionSession is null
                ? collection.UpdateManyAsync(filter, update, cancellationToken: token)
                : collection.UpdateManyAsync(transactionSession, filter, update, cancellationToken: token),
            () => transactionSession is null
                ? collection.UpdateMany(filter, update)
                : collection.UpdateMany(transactionSession, filter, update)).ConfigureAwait(false);
        // MatchedCount, not ModifiedCount: matched is the count every provider reports the same
        // way. ModifiedCount excludes documents whose assigned values were already equal, and no
        // relational provider can distinguish those.
        return new SetMutationResult(result.MatchedCount);
    }

    private async ValueTask<SetMutationResult> DeleteWhereCore(Predicate where, MongoExecution mode)
    {
        ArgumentNullException.ThrowIfNull(where);
        SetMutationExecutionAdmission.Require(where);
        ThrowIfDisposed();
        RefusePrivilegedOperation("delete-where");
        var filter = new MongoQueryRenderer().RenderAggregationSourcePredicate(
            where,
            Unit.Name,
            QueryRenderOptions.Default with
            {
                SearchKeyColumns = SearchKeyQueryMappings.For(Unit),
                ElementSearchKeyColumns = SearchKeyQueryMappings.ElementFor(Unit)
            });
        commandObserver?.Observe(new ProviderCommandEvent(
            "mongodb.delete-where",
            "MongoDB.DeleteMany(filter=predicate)",
            ProviderCommandKind.Write,
            IsProbe: false));
        var result = await mode.Run(
            token => transactionSession is null
                ? collection.DeleteManyAsync(filter, cancellationToken: token)
                : collection.DeleteManyAsync(transactionSession, filter, cancellationToken: token),
            () => transactionSession is null
                ? collection.DeleteMany(filter)
                : collection.DeleteMany(transactionSession, filter)).ConfigureAwait(false);
        return new SetMutationResult(result.DeletedCount);
    }

    public RetentionResult ApplyRetention(RetentionExecutionOptions? options = null) =>
        ApplyRetentionCore(options, MongoExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask<RetentionResult> ApplyRetentionAsync(RetentionExecutionOptions? options = null) =>
        ApplyRetentionCore(options,
            MongoExecution.Asynchronous(options?.CancellationToken ?? CancellationToken.None));

    private async ValueTask<RetentionResult> ApplyRetentionCore(RetentionExecutionOptions? options, MongoExecution mode)
    {
        RefusePrivilegedOperation("retention");
        options ??= new RetentionExecutionOptions();
        if (options.MaxRowsPerBatch <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.MaxRowsPerBatch));
        var declaration = Unit.Retention ??
            throw new InvalidOperationException($"Storage unit '{Unit.Name}' does not declare retention.");
        var keepNewest = RetentionSessionExtensions.EffectiveKeepNewest(Unit, options);
        var deleted = 0;
        var batches = 0;

        // A capped collection is intentionally not used: caps apply to the whole collection,
        // cannot keep N rows per partition, and reject document growth/index updates. Walk the
        // sorted partition projection with a bounded cursor, then compute each partition's
        // watermark through skip/limit and submit only that bounded id set to deleteMany.
        // No stage accumulates every row identity for a partition in a server or client array.
        if (declaration.PartitionColumns.Count == 0)
        {
            await DrainPartition(new BsonDocument()).ConfigureAwait(false);
            return new RetentionResult(deleted, batches);
        }

        var projection = new BsonDocument(declaration.PartitionColumns.Select(column =>
            new BsonElement(column, 1)));
        projection["_id"] = 0;
        var partitionSort = new BsonDocument(declaration.PartitionColumns.Select(column =>
            new BsonElement(column, 1)));
        foreach (var key in Unit.Key.Columns)
            partitionSort[key] = 1;
        var partitions = Find(new BsonDocument())
            .Project(projection)
            .Sort(partitionSort);
        partitions.Options.BatchSize = Math.Max(1, Math.Min(options.MaxRowsPerBatch, 512));
        partitions.Options.AllowDiskUse = true;
        using var cursor = await mode.ToCursor(partitions, options.CancellationToken).ConfigureAwait(false);
        BsonDocument? previous = null;
        while (await mode.MoveNext(cursor, options.CancellationToken).ConfigureAwait(false))
        {
            foreach (var document in cursor.Current)
            {
                options.CancellationToken.ThrowIfCancellationRequested();
                var partition = new BsonDocument(declaration.PartitionColumns.Select(column =>
                    new BsonElement(column, document.GetValue(column, BsonNull.Value))));
                if (partition.Equals(previous))
                    continue;
                previous = partition;
                await DrainPartition(partition).ConfigureAwait(false);
            }
        }
        return new RetentionResult(deleted, batches);

        async ValueTask DrainPartition(BsonDocument partitionFilter)
        {
            while (true)
            {
                options.CancellationToken.ThrowIfCancellationRequested();
                var victimsQuery = Find(partitionFilter)
                    .Sort(RetentionSort(Unit, declaration, includePartitions: false))
                    .Skip(keepNewest)
                    .Limit(options.MaxRowsPerBatch)
                    .Project(new BsonDocument("_id", 1));
                victimsQuery.Options.BatchSize = options.MaxRowsPerBatch;
                victimsQuery.Options.AllowDiskUse = true;
                commandObserver?.Observe(new ProviderCommandEvent(
                    "mongodb.retention-watermark-find",
                    $"MongoDB.Find(sort:order-desc+key-asc; skip:{keepNewest}; limit:{options.MaxRowsPerBatch}; projection:_id)",
                    ProviderCommandKind.Read,
                    IsProbe: false));
                var victims = await mode.ToList(victimsQuery, options.CancellationToken).ConfigureAwait(false);
                if (victims.Count == 0)
                    return;

                var ids = new BsonArray(victims.Select(document => document["_id"]));
                var filter = new BsonDocument("_id", new BsonDocument("$in", ids));
                var result = await mode.Run(
                    token => transactionSession is null
                        ? collection.DeleteManyAsync(filter, token)
                        : collection.DeleteManyAsync(transactionSession, filter, cancellationToken: token),
                    () => transactionSession is null
                        ? collection.DeleteMany(filter, options.CancellationToken)
                        : collection.DeleteMany(transactionSession, filter, cancellationToken: options.CancellationToken))
                    .ConfigureAwait(false);
                var affected = checked((int)result.DeletedCount);
                commandObserver?.Observe(new ProviderCommandEvent(
                    "mongodb.retention-delete-many",
                    $"MongoDB.DeleteMany(ids<=:{options.MaxRowsPerBatch})",
                    ProviderCommandKind.Write,
                    IsProbe: false));
                deleted += affected;
                batches++;
                if (affected == 0 || victims.Count < options.MaxRowsPerBatch)
                    return;
            }
        }

        IFindFluent<BsonDocument, BsonDocument> Find(BsonDocument filter) => transactionSession is null
            ? collection.Find(filter)
            : collection.Find(transactionSession, filter);
    }

    public StorageInspection Inspect() =>
        InspectCore(MongoExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask<StorageInspection> InspectAsync(CancellationToken cancellationToken = default) =>
        InspectCore(MongoExecution.Asynchronous(cancellationToken));

    private async ValueTask<StorageInspection> InspectCore(MongoExecution mode)
    {
        RefusePrivilegedOperation("inspect");
        StorageInspectionSessionExtensions.EnsureProviderSequence(Unit);
        ThrowIfDisposed();
        var filter = new BsonDocument("_id", HighWaterId());
        var document = await mode.FirstOrDefault(transactionSession is null
            ? state.Metadata.Find(filter)
            : state.Metadata.Find(transactionSession, filter)).ConfigureAwait(false);
        if (document is null || !document.TryGetValue(HighWaterValue, out var value) || value.IsBsonNull)
            return new StorageInspection(null);
        return new StorageInspection(value.ToInt64());
    }

    public RetentionOperationResult ApplyRetention(OperationId operationId, RetentionExecutionOptions? options = null) =>
        ApplyExactRetention(operationId, options, MongoExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask<RetentionOperationResult> ApplyRetentionAsync(
        OperationId operationId,
        RetentionExecutionOptions? options = null) =>
        ApplyExactRetention(operationId, options,
            MongoExecution.Asynchronous(options?.CancellationToken ?? CancellationToken.None));

    private async ValueTask<RetentionOperationResult> ApplyExactRetention(
        OperationId operationId,
        RetentionExecutionOptions? options,
        MongoExecution mode)
    {
        RefusePrivilegedOperation("retention");
        var declaration = Unit.RetentionIdempotency ?? throw new InvalidOperationException(
            $"Storage unit '{Unit.Name}' does not declare retention idempotency; declare RetentionIdempotency before using operation-identified retention.");
        declaration.Validate(Unit);
        options ??= new RetentionExecutionOptions();
        RetentionSessionExtensions.ValidateExecutionOptions(options);
        RetentionOperationCodec.ValidateOperation(operationId);
        RetentionAffectedKeys.Validate(Unit, options);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await ExecuteWithTransactionIfNeeded(
                    transactional => transactional.ApplyExactRetentionCore(operationId, declaration, options, mode), mode)
                    .ConfigureAwait(false);
            }
            catch (MongoLedgerConflictException) when (attempt == 0)
            {
                // A concurrent upsert can surface as a duplicate-key error after the other
                // transaction commits. A standalone exact-retention call can safely retry its
                // whole transaction and then replay the winner. An explicit unit of work must
                // retry the complete unit of work because the duplicate-key error aborts it.
                if (transactionSession is not null)
                {
                    try { await Abort(transactionSession, mode).ConfigureAwait(false); }
                    catch (MongoException) { }
                    unitOfWork?.Poison();
                    throw new MongoUnitOfWorkConflictException(
                        "A concurrent retention nonce conflict aborted the whole MongoDB unit of work; retry the complete unit of work.");
                }
            }
        }
    }

    private async ValueTask<RetentionOperationResult> ApplyExactRetentionCore(
        OperationId operationId,
        RetentionIdempotencyDeclaration declaration,
        RetentionExecutionOptions options,
        MongoExecution mode)
    {
        var scope = Access.Scope?.Value ?? string.Empty;
        var ledger = state.Operations(declaration.LedgerName);
        var fingerprint = RetentionOperationCodec.Fingerprint(Unit, operationId, scope, options);
        var cutoffExpression = new BsonDocument("$dateSubtract", new BsonDocument
        {
            ["startDate"] = "$$NOW",
            ["unit"] = "millisecond",
            ["amount"] = Math.Max(1L, checked((long)Math.Ceiling(declaration.Window.TotalMilliseconds)))
        });
        var identity = new BsonDocument
        {
            ["unit"] = Unit.Id.Value,
            ["scope"] = scope,
            ["nonce"] = operationId.Nonce
        };
        var valid = new BsonDocument
        {
            ["_id"] = identity,
            ["$expr"] = new BsonDocument("$gt", new BsonArray { "$committed_at", cutoffExpression })
        };
        var existing = await mode.FirstOrDefault(transactionSession is null
            ? ledger.Find(valid)
            : ledger.Find(transactionSession, valid)).ConfigureAwait(false);
        if (existing is not null)
            return ReadExistingRetention(existing, operationId, scope, fingerprint);

        var expired = await mode.FirstOrDefault(transactionSession is null
            ? ledger.Find(new BsonDocument("_id", identity))
            : ledger.Find(transactionSession, new BsonDocument("_id", identity))).ConfigureAwait(false);
        if (expired is not null)
        {
            await mode.Run(
                token => transactionSession is null
                    ? ledger.DeleteOneAsync(new BsonDocument("_id", identity), token)
                    : ledger.DeleteOneAsync(transactionSession, new BsonDocument("_id", identity), cancellationToken: token),
                () =>
                {
                    if (transactionSession is null)
                        ledger.DeleteOne(new BsonDocument("_id", identity));
                    else
                        ledger.DeleteOne(transactionSession, new BsonDocument("_id", identity));
                }).ConfigureAwait(false);
        }

        var ledgerSet = new BsonDocument
        {
            ["unit"] = MissingOrLiteral("unit", Unit.Id.Value),
            ["scope"] = MissingOrLiteral("scope", scope),
            ["nonce"] = MissingOrLiteral("nonce", operationId.Nonce),
            ["committed_at"] = new BsonDocument("$cond", new BsonArray
            {
                new BsonDocument("$eq", new BsonArray { new BsonDocument("$type", "$committed_at"), "missing" }), "$$NOW", "$committed_at"
            }),
            ["input_fingerprint"] = MissingOrLiteral("input_fingerprint", fingerprint),
            ["exact_result"] = new BsonDocument("$cond", new BsonArray
            {
                new BsonDocument("$eq", new BsonArray { new BsonDocument("$type", "$exact_result"), "missing" }), string.Empty, "$exact_result"
            })
        };
        var ledgerUpdate = Builders<BsonDocument>.Update.Pipeline(
            new EmptyPipelineDefinition<BsonDocument>()
                .AppendStage<BsonDocument, BsonDocument, BsonDocument>(new BsonDocument("$set", ledgerSet)));
        var ledgerOptions = new FindOneAndUpdateOptions<BsonDocument>
        {
            IsUpsert = true,
            ReturnDocument = ReturnDocument.Before
        };
        BsonDocument? previous;
        try
        {
            previous = await mode.Run(
                token => transactionSession is null
                    ? ledger.FindOneAndUpdateAsync(new BsonDocument("_id", identity), ledgerUpdate, ledgerOptions, token)
                    : ledger.FindOneAndUpdateAsync(transactionSession, new BsonDocument("_id", identity), ledgerUpdate, ledgerOptions, token),
                () => transactionSession is null
                    ? ledger.FindOneAndUpdate(new BsonDocument("_id", identity), ledgerUpdate, ledgerOptions)
                    : ledger.FindOneAndUpdate(transactionSession, new BsonDocument("_id", identity), ledgerUpdate, ledgerOptions))
                .ConfigureAwait(false);
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Code == 11000)
        {
            throw new MongoLedgerConflictException();
        }
        if (previous is not null)
            return ReadExistingRetention(previous, operationId, scope, fingerprint);

        // This method is called through ExecuteWithTransactionIfNeeded. A cancellation or
        // provider failure aborts the transaction, so no delete batch can outlive its ledger result.
        var affectedKeys = options.AffectedKeyProjection is { } projection
            ? await ReadAffectedKeys(projection, options, mode).ConfigureAwait(false)
            : Array.Empty<object?>();
        var retention = await ApplyRetentionCore(options, mode).ConfigureAwait(false);
        var result = new RetentionOperationResult(RetentionOperationStatus.Executed, retention.DeletedRows, retention.Batches, retention.Completed)
        {
            AffectedKeys = Array.AsReadOnly(affectedKeys.ToArray())
        };
        var completed = Builders<BsonDocument>.Update.Set("exact_result", RetentionOperationCodec.SerializeResult(result));
        await mode.Run(
            token => transactionSession is null
                ? ledger.UpdateOneAsync(new BsonDocument("_id", identity), completed, cancellationToken: token)
                : ledger.UpdateOneAsync(transactionSession, new BsonDocument("_id", identity), completed, cancellationToken: token),
            () =>
            {
                if (transactionSession is null)
                    ledger.UpdateOne(new BsonDocument("_id", identity), completed);
                else
                    ledger.UpdateOne(transactionSession, new BsonDocument("_id", identity), completed);
            }).ConfigureAwait(false);
        return result;
    }

    private async ValueTask<IReadOnlyList<object?>> ReadAffectedKeys(
        RetentionAffectedKeyProjection projection,
        RetentionExecutionOptions options,
        MongoExecution mode)
    {
        var declaration = Unit.Retention ?? throw new InvalidOperationException(
            $"Storage unit '{Unit.Name}' does not declare retention.");
        var sort = new BsonDocument
        {
            [declaration.OrderColumn] = -1
        };
        foreach (var key in Unit.Key.Columns.Where(key =>
                     !string.Equals(key, declaration.OrderColumn, StringComparison.Ordinal)))
            sort[key] = 1;
        var window = new BsonDocument
        {
            ["sortBy"] = sort,
            ["output"] = new BsonDocument("__groundwork_retention_rank", new BsonDocument("$documentNumber", new BsonDocument()))
        };
        if (declaration.PartitionColumns.Count != 0)
        {
            var partition = new BsonDocument();
            foreach (var column in declaration.PartitionColumns)
                partition[column] = "$" + column;
            window["partitionBy"] = partition;
        }

        var stages = new List<BsonDocument>
        {
            new("$setWindowFields", window),
            new("$match", new BsonDocument("__groundwork_retention_rank",
                new BsonDocument("$gt", RetentionSessionExtensions.EffectiveKeepNewest(Unit, options)))),
            new("$group", new BsonDocument("_id", "$" + projection.Column)),
            new("$sort", new BsonDocument("_id", 1)),
            new("$limit", checked(projection.MaxDistinctValues + 1)),
            new("$project", new BsonDocument { ["_id"] = 0, ["value"] = "$_id" })
        };
        var pipeline = PipelineDefinition<BsonDocument, BsonDocument>.Create(stages);
        var documents = await mode.Aggregate(collection, transactionSession, pipeline).ConfigureAwait(false);
        commandObserver?.Observe(new ProviderCommandEvent(
            "mongodb.retention-affected-keys",
            $"MongoDB.Aggregate(retention affected distinct; limit:{projection.MaxDistinctValues + 1})",
            ProviderCommandKind.Read,
            IsProbe: false));
        var decodeColumn = Unit.Columns.Single(candidate => candidate.Name == projection.Column);
        var values = documents.Select(document =>
            document.GetValue("value", BsonNull.Value).IsBsonNull
                ? null
                : MongoValueCodec.Decode(document["value"], decodeColumn));
        return RetentionAffectedKeys.DistinctAndOrderValues(
            values,
            projection,
            projection.MaxDistinctValues);
    }

    private RetentionOperationResult ReadExistingRetention(
        BsonDocument existing,
        OperationId operationId,
        string scope,
        string fingerprint)
    {
        var storedFingerprint = existing.TryGetValue("input_fingerprint", out var fingerprintValue) && !fingerprintValue.IsBsonNull
            ? fingerprintValue.AsString
            : null;
        var storedResult = existing.TryGetValue("exact_result", out var resultValue) && !resultValue.IsBsonNull
            ? resultValue.AsString
            : null;
        if (string.IsNullOrEmpty(storedFingerprint) || string.IsNullOrEmpty(storedResult))
            throw new InvalidOperationException("GW-RETENTION-002: an existing exact retention ledger entry has no exact result; use a new operation nonce.");
        if (!string.Equals(storedFingerprint, fingerprint, StringComparison.Ordinal))
            throw new RetentionIdempotencyConflictException(Unit.Id.Value, scope, operationId.Nonce, storedFingerprint, fingerprint);
        return RetentionOperationCodec.DeserializeResult(storedResult) with { Status = RetentionOperationStatus.Replayed };
    }

    private static BsonDocument RetentionSort(
        StorageUnit unit,
        RetentionDeclaration declaration,
        bool includePartitions = true)
    {
        var sort = new BsonDocument();
        if (includePartitions)
            foreach (var partition in declaration.PartitionColumns)
                sort[partition] = 1;
        sort[declaration.OrderColumn] = -1;
        foreach (var key in unit.Key.Columns.Where(key =>
                     !string.Equals(key, declaration.OrderColumn, StringComparison.Ordinal)))
            sort[key] = 1;
        return sort;
    }

    public MongoWriteOutcome Append(OperationId operationId, IReadOnlyList<MongoStorageValues> values) =>
        AppendAsync(operationId, values, MongoExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask<MongoWriteOutcome> AppendAsync(
        OperationId operationId,
        IReadOnlyList<MongoStorageValues> values,
        CancellationToken cancellationToken = default) =>
        AppendAsync(operationId, values, MongoExecution.Asynchronous(cancellationToken));

    private async ValueTask<MongoWriteOutcome> AppendAsync(
        OperationId operationId,
        IReadOnlyList<MongoStorageValues> values,
        MongoExecution mode)
    {
        RefusePrivilegedOperation("append");
        ThrowIfDisposed();
        var declaration = Unit.AppendIdempotency ?? throw new InvalidOperationException(
            $"Storage unit '{Unit.Name}' does not declare append idempotency.");
        declaration.Validate(Unit);
        if (string.IsNullOrWhiteSpace(operationId.Nonce))
            throw new ArgumentException("An operation id requires a non-empty nonce.", nameof(operationId));
        if (operationId.Nonce.Length > 256)
            throw new ArgumentException("An operation nonce cannot exceed 256 UTF-16 code units.", nameof(operationId));
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0 || values.Any(value => value is null))
            throw new ArgumentException("An append batch must contain at least one non-null row.", nameof(values));
        IdempotencyRules.ValidateOperation(
            Unit,
            operationId,
            values.Select(value => new StorageValues(value.Values)).ToArray());
        foreach (var value in values)
            WritePreconditionValidator.ValidateWrittenValues(Unit, value.Values);
        MongoWriteOutcome outcome;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                outcome = await ExecuteWithTransactionIfNeeded(async transactional => (await transactional
                    .AppendCore(operationId, values, declaration, exactOutcomes: false, mode)
                    .ConfigureAwait(false)).ToStatusOutcome(), mode).ConfigureAwait(false);
                break;
            }
            catch (MongoLedgerConflictException) when (attempt == 0)
            {
                // A concurrent upsert can surface as a duplicate-key error after the other
                // transaction commits. A standalone append is retried by its outer transaction
                // wrapper. An explicit unit of work may already contain other writes, so its
                // transaction is aborted as a whole and the caller must retry the whole unit of
                // work; restarting only the append would silently lose those earlier writes.
                if (transactionSession is not null)
                {
                    try { await Abort(transactionSession, mode).ConfigureAwait(false); }
                    catch (MongoException) { }
                    unitOfWork?.Poison();
                    throw new MongoUnitOfWorkConflictException(
                        "A concurrent idempotency nonce conflict aborted the whole MongoDB unit of work; retry the complete unit of work.");
                }
            }
        }
        if (Unit.Retention?.Trigger == RetentionTrigger.OnAppend &&
            outcome.Status is MongoWriteOutcomeStatus.Inserted or MongoWriteOutcomeStatus.Replayed)
            await ApplyOnAppendRetention(mode).ConfigureAwait(false);
        return outcome;
    }

    public MongoAppendOutcomeReport AppendWithOutcomes(OperationId operationId, IReadOnlyList<MongoStorageValues> values) =>
        AppendWithOutcomesAsync(operationId, values, MongoExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask<MongoAppendOutcomeReport> AppendWithOutcomesAsync(
        OperationId operationId,
        IReadOnlyList<MongoStorageValues> values,
        CancellationToken cancellationToken = default) =>
        AppendWithOutcomesAsync(operationId, values, MongoExecution.Asynchronous(cancellationToken));

    private async ValueTask<MongoAppendOutcomeReport> AppendWithOutcomesAsync(
        OperationId operationId,
        IReadOnlyList<MongoStorageValues> values,
        MongoExecution mode)
    {
        RefusePrivilegedOperation("append");
        ThrowIfDisposed();
        var declaration = Unit.AppendIdempotency ?? throw new InvalidOperationException(
            $"Storage unit '{Unit.Name}' does not declare append idempotency.");
        declaration.Validate(Unit);
        if (string.IsNullOrWhiteSpace(operationId.Nonce))
            throw new ArgumentException("An operation id requires a non-empty nonce.", nameof(operationId));
        if (operationId.Nonce.Length > 256)
            throw new ArgumentException("An operation nonce cannot exceed 256 UTF-16 code units.", nameof(operationId));
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0 || values.Any(value => value is null))
            throw new ArgumentException("An append batch must contain at least one non-null row.", nameof(values));
        IdempotencyRules.ValidateOperation(
            Unit,
            operationId,
            values.Select(value => new StorageValues(value.Values)).ToArray());
        foreach (var value in values)
            WritePreconditionValidator.ValidateWrittenValues(Unit, value.Values);

        MongoAppendOutcomeReport report;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                report = await ExecuteWithTransactionIfNeeded(async transactional => (await transactional
                    .AppendCore(operationId, values, declaration, exactOutcomes: true, mode)
                    .ConfigureAwait(false)).ToReport(), mode).ConfigureAwait(false);
                break;
            }
            catch (MongoLedgerConflictException) when (attempt == 0)
            {
                if (transactionSession is not null)
                {
                    try { await Abort(transactionSession, mode).ConfigureAwait(false); }
                    catch (MongoException) { }
                    unitOfWork?.Poison();
                    throw new MongoUnitOfWorkConflictException(
                        "A concurrent idempotency nonce conflict aborted the whole MongoDB unit of work; retry the complete unit of work.");
                }
            }
        }
        if (Unit.Retention?.Trigger == RetentionTrigger.OnAppend &&
            report.Status is MongoWriteOutcomeStatus.Inserted or MongoWriteOutcomeStatus.Replayed)
            await ApplyOnAppendRetention(mode).ConfigureAwait(false);
        return report;
    }

    private async ValueTask<MongoAppendExecution> AppendCore(
        OperationId operationId,
        IReadOnlyList<MongoStorageValues> values,
        AppendIdempotencyDeclaration declaration,
        bool exactOutcomes,
        MongoExecution mode)
    {
        var scope = Access.Scope?.Value ?? string.Empty;
        var ledger = state.Operations(declaration.LedgerName);
        var fingerprint = ExactAppendCodec.Fingerprint(
            Unit,
            values.Select(value => new StorageValues(value.Values)).ToArray());
        var cutoffExpression = new BsonDocument("$dateSubtract", new BsonDocument
        {
            ["startDate"] = "$$NOW",
            ["unit"] = "millisecond",
            ["amount"] = Math.Max(1L, checked((long)Math.Ceiling(declaration.Window.TotalMilliseconds)))
        });
        var expired = await mode.ToList(ledger.Find(
                transactionSession,
                new BsonDocument("$expr", new BsonDocument("$and", new BsonArray
                {
                    new BsonDocument("$eq", new BsonArray { "$unit", Unit.Id.Value }),
                    new BsonDocument("$lte", new BsonArray { "$committed_at", cutoffExpression })
                })))
            .Limit(128)
            .Project(new BsonDocument("_id", 1))).ConfigureAwait(false);
        if (expired.Count != 0)
        {
            var ids = expired.Select(document => document["_id"]).ToArray();
            var deleteFilter = Builders<BsonDocument>.Filter.In("_id", ids);
            await mode.Run(
                token => transactionSession is null
                    ? ledger.DeleteManyAsync(deleteFilter, token)
                    : ledger.DeleteManyAsync(transactionSession, deleteFilter, cancellationToken: token),
                () =>
                {
                    if (transactionSession is null)
                        ledger.DeleteMany(deleteFilter);
                    else
                        ledger.DeleteMany(transactionSession, deleteFilter);
                }).ConfigureAwait(false);
        }

        var identity = new BsonDocument
        {
            ["unit"] = Unit.Id.Value,
            ["scope"] = scope,
            ["nonce"] = operationId.Nonce
        };
        var validIdentity = new BsonDocument
        {
            ["_id"] = identity,
            ["$expr"] = new BsonDocument("$gt", new BsonArray { "$committed_at", cutoffExpression })
        };
        var existing = await mode.FirstOrDefault(transactionSession is null
            ? ledger.Find(validIdentity)
            : ledger.Find(transactionSession, validIdentity)).ConfigureAwait(false);
        if (existing is not null)
            return ReadExistingAppend(existing, operationId, scope, fingerprint, exactOutcomes);

        var expiredExisting = await mode.FirstOrDefault(transactionSession is null
            ? ledger.Find(new BsonDocument("_id", identity))
            : ledger.Find(transactionSession, new BsonDocument("_id", identity))).ConfigureAwait(false);
        if (expiredExisting is not null)
        {
            await mode.Run(
                token => transactionSession is null
                    ? ledger.DeleteOneAsync(new BsonDocument("_id", identity), token)
                    : ledger.DeleteOneAsync(transactionSession, new BsonDocument("_id", identity), cancellationToken: token),
                () =>
                {
                    if (transactionSession is null)
                        ledger.DeleteOne(new BsonDocument("_id", identity));
                    else
                        ledger.DeleteOne(transactionSession, new BsonDocument("_id", identity));
                }).ConfigureAwait(false);
        }

        // The pipeline keeps provider time (rather than client clock) as the
        // ledger timestamp. The initial identity lookup returns committed legacy
        // rows before this upsert, while $cond makes concurrent insert races
        // initialize only missing fields. Literal wrapping prevents a caller's
        // nonce/scope beginning with '$' from becoming an aggregation path.
        var ledgerSet = new BsonDocument
        {
            ["unit"] = MissingOrLiteral("unit", Unit.Id.Value),
            ["scope"] = MissingOrLiteral("scope", scope),
            ["nonce"] = MissingOrLiteral("nonce", operationId.Nonce),
            ["committed_at"] = new BsonDocument("$cond", new BsonArray
            {
                new BsonDocument("$eq", new BsonArray { new BsonDocument("$type", "$committed_at"), "missing" }),
                "$$NOW",
                "$committed_at"
            }),
            ["input_fingerprint"] = MissingOrLiteral("input_fingerprint", fingerprint),
            ["exact_result"] = new BsonDocument("$cond", new BsonArray
            {
                new BsonDocument("$eq", new BsonArray { new BsonDocument("$type", "$exact_result"), "missing" }),
                BsonNull.Value,
                "$exact_result"
            })
        };
        var ledgerUpdate = Builders<BsonDocument>.Update.Pipeline(
            new EmptyPipelineDefinition<BsonDocument>()
                .AppendStage<BsonDocument, BsonDocument, BsonDocument>(new BsonDocument("$set", ledgerSet)));
        var ledgerOptions = new FindOneAndUpdateOptions<BsonDocument>
        {
            IsUpsert = true,
            ReturnDocument = ReturnDocument.Before
        };
        BsonDocument? previous;
        try
        {
            previous = await mode.Run(
                token => transactionSession is null
                    ? ledger.FindOneAndUpdateAsync(new BsonDocument("_id", identity), ledgerUpdate, ledgerOptions, token)
                    : ledger.FindOneAndUpdateAsync(transactionSession, new BsonDocument("_id", identity), ledgerUpdate, ledgerOptions, token),
                () => transactionSession is null
                    ? ledger.FindOneAndUpdate(new BsonDocument("_id", identity), ledgerUpdate, ledgerOptions)
                    : ledger.FindOneAndUpdate(transactionSession, new BsonDocument("_id", identity), ledgerUpdate, ledgerOptions))
                .ConfigureAwait(false);
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Code == 11000)
        {
            throw new MongoLedgerConflictException();
        }
        catch (MongoCommandException exception) when (
            exception.Code == 112 || exception.HasErrorLabel("TransientTransactionError"))
        {
            // WiredTiger reports a concurrent insert into the same ledger identity as a
            // transaction write conflict on some server versions rather than duplicate key.
            // Both outcomes mean this transaction lost the nonce race and can retry safely.
            throw new MongoLedgerConflictException();
        }
        if (previous is not null)
            return ReadExistingAppend(previous, operationId, scope, fingerprint, exactOutcomes);

        var outcomes = new List<MongoWriteOutcome>(values.Count);
        foreach (var value in values)
        {
            var outcome = await MutateCore(value, MongoWriteOptions.Unconditional, MutationKind.Insert, mode)
                .ConfigureAwait(false);
            if (!outcome.Succeeded)
                throw new InvalidOperationException("An idempotent append payload row was not accepted; the ledger and payload were rolled back.");
            await RecordHighWater(outcome.GeneratedValues, mode).ConfigureAwait(false);
            outcomes.Add(outcome);
        }
        var serializedResult = ExactAppendCodec.SerializeOutcomes(
            outcomes.Select(outcome => new WriteOutcome(
                (WriteOutcomeStatus)outcome.Status,
                outcome.Version,
                generatedValues: outcome.GeneratedValues)).ToArray());
        var completed = Builders<BsonDocument>.Update.Set("exact_result", serializedResult);
        await mode.Run(
            token => transactionSession is null
                ? ledger.UpdateOneAsync(new BsonDocument("_id", identity), completed, cancellationToken: token)
                : ledger.UpdateOneAsync(transactionSession, new BsonDocument("_id", identity), completed, cancellationToken: token),
            () =>
            {
                if (transactionSession is null)
                    ledger.UpdateOne(new BsonDocument("_id", identity), completed);
                else
                    ledger.UpdateOne(transactionSession, new BsonDocument("_id", identity), completed);
            }).ConfigureAwait(false);
        return new MongoAppendExecution(MongoWriteOutcomeStatus.Inserted, outcomes);
    }

    private static BsonDocument MissingOrLiteral(string field, string value) =>
        new("$cond", new BsonArray
        {
            new BsonDocument("$eq", new BsonArray { new BsonDocument("$type", "$" + field), "missing" }),
            new BsonDocument("$literal", value),
            "$" + field
        });

    private MongoAppendExecution ReadExistingAppend(
        BsonDocument existing,
        OperationId operationId,
        string scope,
        string fingerprint,
        bool exactOutcomes)
    {
        var storedFingerprint = existing.TryGetValue("input_fingerprint", out var storedValue) &&
                                !storedValue.IsBsonNull && storedValue.IsString
            ? storedValue.AsString
            : null;
        if (storedFingerprint is null)
        {
            if (exactOutcomes)
                throw new InvalidOperationException(
                    "GW-APPEND-002: an existing append ledger entry has no exact result; use a new operation nonce.");
            return new MongoAppendExecution(MongoWriteOutcomeStatus.Replayed, null);
        }
        if (!exactOutcomes)
            return new MongoAppendExecution(MongoWriteOutcomeStatus.Replayed, null);
        if (!string.Equals(storedFingerprint, fingerprint, StringComparison.Ordinal))
            throw new AppendIdempotencyConflictException(Unit.Id.Value, scope, operationId.Nonce, storedFingerprint, fingerprint);
        if (!existing.TryGetValue("exact_result", out var resultValue) || resultValue.IsBsonNull || !resultValue.IsString)
            throw new InvalidOperationException(
                "GW-APPEND-002: an existing append ledger entry has no exact result; use a new operation nonce.");
        var decoded = ExactAppendCodec.DeserializeOutcomes(resultValue.AsString)
            .Select(outcome => new MongoWriteOutcome(
                (MongoWriteOutcomeStatus)outcome.Status,
                outcome.Version,
                generatedValues: outcome.GeneratedValues))
            .ToArray();
        return new MongoAppendExecution(MongoWriteOutcomeStatus.Replayed, decoded);
    }

    private sealed record MongoAppendExecution(
        MongoWriteOutcomeStatus Status,
        IReadOnlyList<MongoWriteOutcome>? Outcomes)
    {
        internal MongoWriteOutcome ToStatusOutcome() => new(Status);

        internal MongoAppendOutcomeReport ToReport()
        {
            if (Outcomes is null || Outcomes.Count == 0)
                throw new InvalidOperationException(
                    "GW-APPEND-002: an existing append ledger entry has no exact result; use a new operation nonce.");
            return new MongoAppendOutcomeReport(Status, Outcomes);
        }
    }

    private static WriteOptions? ToStoreOptions(MongoWriteOptions? options) => options is null
        ? null
        : new WriteOptions { Precondition = options.Precondition };

    internal void Close() => disposed = true;

    private async ValueTask<MongoWriteOutcome> Mutate(
        MongoStorageValues values,
        MongoWriteOptions? options,
        MutationKind kind,
        MongoExecution mode,
        bool exactOutcome = false)
    {
        ArgumentNullException.ThrowIfNull(values);
        ThrowIfDisposed();
        var outcome = await ExecuteWithTransactionIfNeeded(async transactional =>
        {
            var result = await transactional.MutateCore(values, options, kind, mode, exactOutcome).ConfigureAwait(false);
            await transactional.RecordHighWater(result.GeneratedValues, mode).ConfigureAwait(false);
            return result;
        }, mode).ConfigureAwait(false);
        if (outcome.Succeeded && Unit.Retention?.Trigger == RetentionTrigger.OnAppend &&
            kind is MutationKind.Insert or MutationKind.Upsert)
        {
            // Cleanup starts only after the sequence/write transaction commits, so a coalesced
            // dirty signal always represents a row visible to the active retention owner.
            await ApplyOnAppendRetention(mode).ConfigureAwait(false);
        }
        return outcome;
    }

    private ValueTask ApplyOnAppendRetention(MongoExecution mode)
    {
        async ValueTask Cleanup() =>
            await ApplyRetentionCore(new RetentionExecutionOptions(), mode).ConfigureAwait(false);
        return transactionSession is null
            ? OnAppendRetentionCoordinator.Run(state, Unit, Access.Scope?.Value, Cleanup)
            : Cleanup();
    }

    private async ValueTask<MongoWriteOutcome> MutateCore(
        MongoStorageValues values,
        MongoWriteOptions? options,
        MutationKind kind,
        MongoExecution mode,
        bool exactOutcome = false)
    {
        values = new MongoStorageValues(SearchKeyProjection.Populate(Unit, values.Values));
        var sequence = Unit.Columns.FirstOrDefault(column =>
            column.Generation == ColumnGeneration.ProviderSequence);
        var generatedValues = new Dictionary<string, object?>(StringComparer.Ordinal);
        var keyValues = values.Values;
        var hasSequenceLocator = sequence is not null && keyValues.ContainsKey(sequence.Name);
        if (hasSequenceLocator && kind == MutationKind.Insert)
            throw new ArgumentException(
                $"ProviderSequence column '{sequence!.Name}' is assigned by MongoDB and cannot be supplied for Insert.",
                nameof(values));
        if (sequence is not null && !hasSequenceLocator && (kind is MutationKind.Insert or MutationKind.Upsert))
        {
            var copied = keyValues.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var generated = await NextSequence(sequence, mode).ConfigureAwait(false);
            copied[sequence.Name] = generated;
            values = new MongoStorageValues(copied);
            keyValues = values.Values;
            generatedValues[sequence.Name] = generated;
        }
        var identity = MongoDocumentMapper.EncodeKey(Unit, keyValues);
        if (!Unit.Concurrency.IsOptimistic)
        {
            return await MutateNoneCore(
                values,
                options,
                kind,
                identity,
                hasSequenceLocator,
                generatedValues,
                mode).ConfigureAwait(false);
        }

        var existing = await FindOne(identity, mode).ConfigureAwait(false);
        var existingVersion = await Version(identity, mode, existing).ConfigureAwait(false);

        if (kind == MutationKind.Insert && existing is not null)
            return new MongoWriteOutcome(MongoWriteOutcomeStatus.UniqueViolation, existingVersion);
        if (kind == MutationKind.Update && existing is null)
            return new MongoWriteOutcome(MongoWriteOutcomeStatus.NotFound);
        if (kind == MutationKind.Upsert && hasSequenceLocator && existing is null)
            return new MongoWriteOutcome(MongoWriteOutcomeStatus.NotFound);
        if (!ConcurrencyAllows(existing, existingVersion, options, kind))
            return new MongoWriteOutcome(MongoWriteOutcomeStatus.ConcurrencyConflict, existingVersion);

        var nextVersion = NextVersion(existingVersion);
        var document = await MongoDocumentMapper.EncodeDocument(
            Unit,
            keyValues,
            identity,
            existing,
            column => sequence is not null && column.Name == sequence.Name && generatedValues.TryGetValue(column.Name, out var generated)
                ? new ValueTask<long>(Convert.ToInt64(generated, System.Globalization.CultureInfo.InvariantCulture))
                : NextSequence(column, mode),
            preserveCreatedAt: exactOutcome,
            generatedValues: generatedValues).ConfigureAwait(false);
        if (nextVersion is not null)
            document[MongoDocumentMapper.VersionField] = nextVersion.Value;
        var inserted = kind == MutationKind.Insert;
        try
        {
            var filter = ConcurrencyFilter(identity, existingVersion);
            if (kind == MutationKind.Insert)
                await InsertOne(document, mode).ConfigureAwait(false);
            else if (kind == MutationKind.Update)
            {
                var result = await ReplaceOne(filter, document, isUpsert: false, mode).ConfigureAwait(false);
                if (Unit.Concurrency.IsOptimistic && result.MatchedCount == 0)
                    return new MongoWriteOutcome(MongoWriteOutcomeStatus.ConcurrencyConflict,
                        await Version(identity, mode).ConfigureAwait(false));
            }
            else
            {
                if (exactOutcome && Unit.Concurrency.IsOptimistic && existing is null)
                {
                    // Use insert rather than an upsert when the caller observed no row. An
                    // upsert can match a row inserted after that observation and would then
                    // misclassify the update and reset its version token.
                    await InsertOne(document, mode).ConfigureAwait(false);
                    inserted = true;
                }
                else
                {
                    var result = await ReplaceOne(filter, document,
                        isUpsert: !hasSequenceLocator &&
                                  (!Unit.Concurrency.IsOptimistic || existing is null), mode).ConfigureAwait(false);
                    inserted = result.UpsertedId is not null;
                    if (Unit.Concurrency.IsOptimistic &&
                        result.MatchedCount == 0 &&
                        result.UpsertedId is null)
                        return new MongoWriteOutcome(MongoWriteOutcomeStatus.ConcurrencyConflict,
                            await Version(identity, mode).ConfigureAwait(false));
                }
            }
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Code == 11000)
        {
            return new MongoWriteOutcome(
                exactOutcome && Unit.Concurrency.IsOptimistic
                    ? MongoWriteOutcomeStatus.ConcurrencyConflict
                    : MongoWriteOutcomeStatus.UniqueViolation,
                existingVersion);
        }
        catch (MongoCommandException exception) when (
            ShouldNormalizeTransientWriteConflict && IsTransientWriteConflict(exception))
        {
            // A concurrent transactional insert into the same identity can surface as
            // WiredTiger code 112 rather than duplicate key 11000. Treat both outcomes
            // identically so an exact unit of work reports its portable conflict result
            // after the transaction is rolled back instead of leaking a driver exception.
            return new MongoWriteOutcome(
                exactOutcome && Unit.Concurrency.IsOptimistic
                    ? MongoWriteOutcomeStatus.ConcurrencyConflict
                    : MongoWriteOutcomeStatus.UniqueViolation,
                existingVersion);
        }

        await PersistVersion(identity, nextVersion, mode).ConfigureAwait(false);
        var status = kind switch
        {
            MutationKind.Insert => MongoWriteOutcomeStatus.Inserted,
            MutationKind.Update => MongoWriteOutcomeStatus.Updated,
            MutationKind.Upsert when hasSequenceLocator => MongoWriteOutcomeStatus.Updated,
            _ => MongoWriteOutcomeStatus.Upserted
        };
        if (exactOutcome && kind == MutationKind.Upsert)
        {
            status = inserted
                ? MongoWriteOutcomeStatus.Inserted
                : MongoWriteOutcomeStatus.Updated;
        }
        return new MongoWriteOutcome(status, nextVersion, generatedValues: generatedValues);
    }

    private async ValueTask<MongoWriteOutcome> MutateNoneCore(
        MongoStorageValues values,
        MongoWriteOptions? options,
        MutationKind kind,
        BsonValue identity,
        bool hasSequenceLocator,
        IReadOnlyDictionary<string, object?> generatedValues,
        MongoExecution mode)
    {
        var missingRequired = Unit.Columns.FirstOrDefault(column =>
            column.Generation == ColumnGeneration.Supplied &&
            !SearchKeyProjection.IsProviderOwnedColumn(column.Name) &&
            !column.IsNullable &&
            column.Default is null &&
            !values.Values.ContainsKey(column.Name));
        var canInsert = missingRequired is null && !hasSequenceLocator;
        var document = kind == MutationKind.Insert || canInsert
            ? await MongoDocumentMapper.EncodeDocument(
                Unit,
                values.Values,
                identity,
                existing: null,
                column => NextSequence(column, mode),
                generatedValues: generatedValues).ConfigureAwait(false)
            : null;
        commandObserver?.Observe(new ProviderCommandEvent(
            "mongodb.none-write",
            kind switch
            {
                MutationKind.Insert => "MongoDB.InsertOne",
                MutationKind.Update => "MongoDB.UpdateOne(upsert:false)",
                _ => $"MongoDB.UpdateOne(upsert:{canInsert.ToString().ToLowerInvariant()})"
            },
            ProviderCommandKind.Write,
            IsProbe: false));
        try
        {
            var filter = new BsonDocument("_id", identity);
            if (kind == MutationKind.Insert)
            {
                await InsertOne(document!, mode).ConfigureAwait(false);
                return new MongoWriteOutcome(
                    MongoWriteOutcomeStatus.Inserted,
                    generatedValues: generatedValues);
            }

            var set = new BsonDocument();
            var setOnInsert = new BsonDocument();
            foreach (var column in Unit.Columns)
            {
                if (Unit.Key.Columns.Contains(column.Name, StringComparer.Ordinal) || column.Name == "createdAt")
                {
                    if (canInsert && document!.TryGetValue(column.Name, out var insertValue))
                        setOnInsert[column.Name] = insertValue;
                    continue;
                }
                if (values.Values.ContainsKey(column.Name))
                    set[column.Name] = document?[column.Name] ?? MongoValueCodec.Encode(values.Values[column.Name], column);
                else if (canInsert && document!.TryGetValue(column.Name, out var defaultValue))
                    setOnInsert[column.Name] = defaultValue;
            }
            if (set.ElementCount == 0)
            {
                var key = Unit.Key.Columns[0];
                var definition = Unit.Columns.Single(column => column.Name == key);
                set[key] = MongoValueCodec.Encode(values.Values[key], definition);
            }
            var update = new BsonDocument("$set", set);
            if (setOnInsert.ElementCount != 0)
                update["$setOnInsert"] = setOnInsert;
            var isUpsert = kind == MutationKind.Upsert && canInsert && !hasSequenceLocator;
            var updateOptions = new UpdateOptions { IsUpsert = isUpsert };
            var result = await mode.Run(
                token => transactionSession is null
                    ? collection.UpdateOneAsync(filter, update, updateOptions, token)
                    : collection.UpdateOneAsync(transactionSession, filter, update, updateOptions, token),
                () => transactionSession is null
                    ? collection.UpdateOne(filter, update, updateOptions)
                    : collection.UpdateOne(transactionSession, filter, update, updateOptions)).ConfigureAwait(false);
            if (result.MatchedCount == 0 && result.UpsertedId is null)
            {
                if (kind == MutationKind.Update || hasSequenceLocator)
                    return new MongoWriteOutcome(MongoWriteOutcomeStatus.NotFound);
                throw new InvalidOperationException($"Column '{missingRequired!.Name}' is required.");
            }
            var status = kind == MutationKind.Update || hasSequenceLocator
                ? MongoWriteOutcomeStatus.Updated
                : kind == MutationKind.Insert
                    ? MongoWriteOutcomeStatus.Inserted
                    : MongoWriteOutcomeStatus.Upserted;
            return new MongoWriteOutcome(status, generatedValues: generatedValues);
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Code == 11000)
        {
            return new MongoWriteOutcome(
                MongoWriteOutcomeStatus.UniqueViolation,
                null,
                ExtractIndexName(exception.WriteError?.Message));
        }
        catch (MongoCommandException exception) when (
            ShouldNormalizeTransientWriteConflict && IsTransientWriteConflict(exception))
        {
            return new MongoWriteOutcome(MongoWriteOutcomeStatus.UniqueViolation);
        }
    }

    private async ValueTask<MongoWriteOutcome> ConditionalUpsertCore(
        MongoStorageValues values,
        MongoWriteOptions? options,
        MongoExecution mode)
    {
        ArgumentNullException.ThrowIfNull(values);
        values = new MongoStorageValues(SearchKeyProjection.Populate(Unit, values.Values));
        ThrowIfDisposed();

        // A provider sequence is allocated by a separate FindOneAndUpdate command.
        // ConditionalUpsertOne is deliberately a one-command primitive, so accepting
        // this declaration here would silently violate its round-trip contract (and
        // would require a transaction for correctness).  Keep the refusal before the
        // transaction wrapper so the rejected operation emits no Mongo command.
        var sequence = Unit.Columns.FirstOrDefault(column =>
            column.Generation == ColumnGeneration.ProviderSequence);
        if (sequence is not null)
        {
            throw new NotSupportedException(
                $"MongoDB conditional upsert cannot use ProviderSequence column '{sequence.Name}': sequence allocation requires a separate MongoDB command and transaction. Use Insert/Upsert or remove ProviderSequence for this one-command operation.");
        }

        var outcome = await ExecuteWithTransactionIfNeeded(transactional =>
            transactional.ConditionalUpsertOne(values, options, mode), mode).ConfigureAwait(false);
        if (outcome.Status == MongoWriteOutcomeStatus.Inserted &&
            Unit.Retention?.Trigger == RetentionTrigger.OnAppend)
            await ApplyOnAppendRetention(mode).ConfigureAwait(false);
        return outcome;
    }

    private async ValueTask<MongoWriteOutcome> ConditionalUpsertOne(
        MongoStorageValues values,
        MongoWriteOptions? options,
        MongoExecution mode)
    {
        var identity = MongoDocumentMapper.EncodeKey(Unit, values.Values);
        var missingRequired = MissingRequiredColumn(values.Values);
        var canInsert = missingRequired is null;
        var document = canInsert
            ? await MongoDocumentMapper.EncodeDocument(
                Unit,
                values.Values,
                identity,
                existing: null,
                column => NextSequence(column, mode)).ConfigureAwait(false)
            : null;
        var filter = new BsonDocument("_id", identity);
        var optimistic = Unit.Concurrency.IsOptimistic;
        if (optimistic && options?.Precondition.Kind == WritePreconditionKind.IfVersion)
        {
            filter[MongoDocumentMapper.VersionField] = new BsonInt64(options.Precondition.Version!.Value);
        }
        else if (optimistic && options?.Precondition.Kind == WritePreconditionKind.CreateOnly)
        {
            filter[MongoDocumentMapper.VersionField] = new BsonDocument("$exists", false);
        }

        var set = new BsonDocument();
        foreach (var column in Unit.Columns)
        {
            if (MongoDocumentMapper.IsSystemOwnedToken(Unit, column))
                continue;
            if (!values.Values.ContainsKey(column.Name) ||
                Unit.Key.Columns.Contains(column.Name, StringComparer.Ordinal) ||
                column.Name == "createdAt" ||
                column.Generation == ColumnGeneration.ProviderSequence)
                continue;
            set[column.Name] = document?[column.Name] ??
                MongoValueCodec.Encode(values.Values[column.Name], column);
        }
        if (!canInsert && set.ElementCount == 0)
            AddKeyOnlyNoOp(set, values.Values);

        var setOnInsert = new BsonDocument();
        foreach (var element in document ?? [])
        {
            if (element.Name != "_id" && !set.Contains(element.Name))
                setOnInsert[element.Name] = element.Value;
        }
        var update = new BsonDocument();
        if (set.ElementCount != 0)
            update["$set"] = set;
        if (optimistic)
            update["$inc"] = new BsonDocument(MongoDocumentMapper.VersionField, 1L);
        if (setOnInsert.ElementCount != 0)
            update["$setOnInsert"] = setOnInsert;

        var precondition = options?.Precondition ?? WritePrecondition.Unconditional;
        var isUpsert = canInsert && precondition.Kind != WritePreconditionKind.IfVersion;
        // Observer text is diagnostic metadata, not a command recorder. Never include
        // identity, filter, or document values because they may contain PII/secrets.
        var commandDescription =
            $"MongoDB.UpdateOne(upsert:{isUpsert.ToString().ToLowerInvariant()}; filter=identity+version; update=$set/$inc/$setOnInsert)";
        commandObserver?.Observe(new ProviderCommandEvent("mongodb.conditional-upsert", commandDescription, ProviderCommandKind.Write, IsProbe: false));
        try
        {
            var updateOptions = new UpdateOptions { IsUpsert = isUpsert };
            var result = await mode.Run(
                token => transactionSession is null
                    ? collection.UpdateOneAsync(filter, update, updateOptions, token)
                    : collection.UpdateOneAsync(transactionSession, filter, update, updateOptions, token),
                () => transactionSession is null
                    ? collection.UpdateOne(filter, update, updateOptions)
                    : collection.UpdateOne(transactionSession, filter, update, updateOptions)).ConfigureAwait(false);
            if (result.UpsertedId is not null)
                return new MongoWriteOutcome(MongoWriteOutcomeStatus.Inserted, optimistic ? 1 : null);
            if (result.MatchedCount != 0)
            {
                return new MongoWriteOutcome(
                    MongoWriteOutcomeStatus.Updated,
                    optimistic && precondition.Version is { } expectedVersion
                        ? checked(expectedVersion + 1)
                        : null);
            }
            if (precondition.Kind is WritePreconditionKind.IfVersion or WritePreconditionKind.CreateOnly)
                return new MongoWriteOutcome(MongoWriteOutcomeStatus.ConcurrencyConflict);
            throw new InvalidOperationException($"Column '{missingRequired!.Name}' is required.");
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Code == 11000)
        {
            var indexName = ExtractIndexName(exception.WriteError?.Message);
            return new MongoWriteOutcome(
                optimistic && IsIdentityIndex(indexName)
                    ? MongoWriteOutcomeStatus.ConcurrencyConflict
                    : MongoWriteOutcomeStatus.UniqueViolation,
                null,
                indexName);
        }
        catch (MongoCommandException exception) when (
            ShouldNormalizeTransientWriteConflict && IsTransientWriteConflict(exception))
        {
            return new MongoWriteOutcome(
                optimistic
                    ? MongoWriteOutcomeStatus.ConcurrencyConflict
                    : MongoWriteOutcomeStatus.UniqueViolation);
        }
    }

    private async ValueTask<MongoWriteOutcome> ExactOutcomeUpsert(
        MongoStorageValues values,
        MongoWriteOptions? options,
        MongoExecution mode)
    {
        var identity = MongoDocumentMapper.EncodeKey(Unit, values.Values);
        var missingRequired = MissingRequiredColumn(values.Values);
        var canInsert = missingRequired is null;
        var document = canInsert
            ? await MongoDocumentMapper.EncodeDocument(
                Unit,
                values.Values,
                identity,
                existing: null,
                column => NextSequence(column, mode)).ConfigureAwait(false)
            : null;
        var set = new BsonDocument();
        foreach (var column in Unit.Columns)
        {
            if (MongoDocumentMapper.IsSystemOwnedToken(Unit, column))
                continue;
            if (!values.Values.ContainsKey(column.Name) ||
                Unit.Key.Columns.Contains(column.Name, StringComparer.Ordinal) ||
                column.Name == "createdAt" ||
                column.Generation == ColumnGeneration.ProviderSequence)
                continue;
            set[column.Name] = document?[column.Name] ??
                MongoValueCodec.Encode(values.Values[column.Name], column);
        }

        // A partial upsert that cannot form a valid insert is still a valid update. Keep
        // it non-upserting and make a key-only update observable without touching source
        // or provider-owned search-key values.
        if (!canInsert && set.ElementCount == 0)
            AddKeyOnlyNoOp(set, values.Values);

        var setOnInsert = new BsonDocument();
        foreach (var element in document ?? [])
        {
            if (element.Name != "_id" && !set.Contains(element.Name))
                setOnInsert[element.Name] = element.Value;
        }
        var update = new BsonDocument();
        if (set.ElementCount != 0)
            update["$set"] = set;
        if (Unit.Concurrency.IsOptimistic)
            update["$inc"] = new BsonDocument(MongoDocumentMapper.VersionField, 1L);
        if (setOnInsert.ElementCount != 0)
            update["$setOnInsert"] = setOnInsert;

        commandObserver?.Observe(new ProviderCommandEvent(
            "mongodb.exact-batch-upsert",
            $"MongoDB.FindOneAndUpdate(upsert:{canInsert.ToString().ToLowerInvariant()}; return=before)",
            ProviderCommandKind.Write,
            IsProbe: false));
        try
        {
            var filter = new BsonDocument("_id", identity);
            var findOptions = new FindOneAndUpdateOptions<BsonDocument>
            {
                IsUpsert = canInsert,
                ReturnDocument = ReturnDocument.Before
            };
            var before = await mode.Run(
                token => transactionSession is null
                    ? collection.FindOneAndUpdateAsync(filter, update, findOptions, token)
                    : collection.FindOneAndUpdateAsync(transactionSession, filter, update, findOptions, token),
                () => transactionSession is null
                    ? collection.FindOneAndUpdate(filter, update, findOptions)
                    : collection.FindOneAndUpdate(transactionSession, filter, update, findOptions)).ConfigureAwait(false);
            if (before is null && missingRequired is not null)
                throw new InvalidOperationException($"Column '{missingRequired.Name}' is required.");
            var version = Unit.Concurrency.IsOptimistic
                ? before is null
                    ? 1L
                    : checked(before.GetValue(MongoDocumentMapper.VersionField, 0L).ToInt64() + 1)
                : (long?)null;
            return new MongoWriteOutcome(
                before is null ? MongoWriteOutcomeStatus.Inserted : MongoWriteOutcomeStatus.Updated,
                version);
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Code == 11000)
        {
            var indexName = ExtractIndexName(exception.WriteError?.Message);
            return new MongoWriteOutcome(MongoWriteOutcomeStatus.UniqueViolation, null, indexName);
        }
        catch (MongoCommandException exception) when (
            ShouldNormalizeTransientWriteConflict && IsTransientWriteConflict(exception))
        {
            return new MongoWriteOutcome(MongoWriteOutcomeStatus.UniqueViolation);
        }
    }

    private ColumnDefinition? MissingRequiredColumn(IReadOnlyDictionary<string, object?> values) =>
        Unit.Columns.FirstOrDefault(column =>
            column.Generation == ColumnGeneration.Supplied &&
            !SearchKeyProjection.IsProviderOwnedColumn(column.Name) &&
            !column.IsNullable &&
            column.Default is null &&
            !values.ContainsKey(column.Name));

    private void AddKeyOnlyNoOp(BsonDocument set, IReadOnlyDictionary<string, object?> values)
    {
        var key = Unit.Key.Columns[0];
        var definition = Unit.Columns.Single(column => column.Name == key);
        set[key] = MongoValueCodec.Encode(values[key], definition);
    }

    private static bool IsIdentityIndex(string? indexName) =>
        string.Equals(indexName, "_id_", StringComparison.Ordinal) ||
        string.Equals(indexName, "_id", StringComparison.Ordinal);

    private static bool IsTransientWriteConflict(MongoCommandException exception) =>
        exception.Code == 112 || exception.HasErrorLabel("TransientTransactionError");

    private static bool IsTransientTransactionBodyFailure(MongoException exception) =>
        exception.HasErrorLabel("TransientTransactionError") ||
        exception is MongoCommandException { Code: 112 };

    private static string? ExtractIndexName(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;
        var marker = " index: ";
        var start = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return null;
        start += marker.Length;
        var end = message.IndexOf(' ', start);
        return (end < 0 ? message[start..] : message[start..end]).Trim('"', '\'', '{', '}');
    }

    private async ValueTask<MongoWriteOutcome> DeleteCore(MongoStorageKey key, MongoWriteOptions? options, MongoExecution mode)
    {
        var identity = MongoDocumentMapper.EncodeKey(Unit, key.Values);
        if (!Unit.Concurrency.IsOptimistic)
        {
            commandObserver?.Observe(new ProviderCommandEvent("mongodb.none-delete", "MongoDB.DeleteOne", ProviderCommandKind.Write, IsProbe: false));
            var result = await DeleteOne(new BsonDocument("_id", identity), mode).ConfigureAwait(false);
            return result.DeletedCount == 0
                ? new MongoWriteOutcome(MongoWriteOutcomeStatus.NotFound)
                : new MongoWriteOutcome(MongoWriteOutcomeStatus.Deleted);
        }
        var existing = await FindOne(identity, mode).ConfigureAwait(false);
        var existingVersion = await Version(identity, mode, existing).ConfigureAwait(false);
        if (existing is null)
            return new MongoWriteOutcome(MongoWriteOutcomeStatus.NotFound);
        if (!ConcurrencyAllows(existing, existingVersion, options, MutationKind.Delete))
            return new MongoWriteOutcome(MongoWriteOutcomeStatus.ConcurrencyConflict, existingVersion);

        await DeleteOne(new BsonDocument("_id", identity), mode).ConfigureAwait(false);
        await RemoveVersion(identity, mode).ConfigureAwait(false);
        return new MongoWriteOutcome(MongoWriteOutcomeStatus.Deleted, Unit.Concurrency.IsOptimistic ? existingVersion : null);
    }

    public MongoWriteOutcome CompareAndDelete(
        MongoStorageKey key,
        IReadOnlyDictionary<string, object?> expectedValues,
        MongoWriteOptions? options = null) =>
        CompareAndDeleteAsync(key, expectedValues, options, MongoExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask<MongoWriteOutcome> CompareAndDeleteAsync(
        MongoStorageKey key,
        IReadOnlyDictionary<string, object?> expectedValues,
        MongoWriteOptions? options = null,
        CancellationToken cancellationToken = default) =>
        CompareAndDeleteAsync(key, expectedValues, options, MongoExecution.Asynchronous(cancellationToken));

    private ValueTask<MongoWriteOutcome> CompareAndDeleteAsync(
        MongoStorageKey key,
        IReadOnlyDictionary<string, object?> expectedValues,
        MongoWriteOptions? options,
        MongoExecution mode)
    {
        RefusePrivilegedOperation("compare-and-delete");
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(expectedValues);
        WritePreconditionValidator.Validate(Unit, WriteOperation.CompareAndDelete, ToStoreOptions(options));
        var canonicalKey = CompareAndDeleteValidation.CanonicalizeKey(Unit, new StorageKey(key.Values));
        var validated = CompareAndDeleteValidation.Validate(
            Unit,
            canonicalKey,
            expectedValues,
            ToStoreOptions(options));
        ThrowIfDisposed();
        return ExecuteWithTransactionIfNeeded(
            transactional => transactional.CompareAndDeleteCore(new MongoStorageKey(canonicalKey.Values), validated, options, mode),
            mode,
            requireTransaction: true);
    }

    private async ValueTask<MongoWriteOutcome> CompareAndDeleteCore(
        MongoStorageKey key,
        IReadOnlyDictionary<string, object?> expectedValues,
        MongoWriteOptions? options,
        MongoExecution mode)
    {
        var identity = MongoDocumentMapper.EncodeKey(Unit, key.Values);
        var existing = Unit.Concurrency.IsOptimistic
            ? await FindOne(identity, mode, "mongodb.compare-and-delete-read").ConfigureAwait(false)
            : null;
        if (Unit.Concurrency.IsOptimistic && existing is null)
            return new MongoWriteOutcome(MongoWriteOutcomeStatus.NotFound);
        var existingVersion = await Version(identity, mode, existing).ConfigureAwait(false);
        var filter = new BsonDocument("_id", identity);
        foreach (var pair in expectedValues)
        {
            var definition = Unit.Columns.FirstOrDefault(column => column.Name == pair.Key)
                ?? throw new ArgumentException($"Comparison column '{pair.Key}' is not declared by '{Unit.Name}'.", nameof(expectedValues));
            filter[pair.Key] = MongoValueCodec.Encode(pair.Value, definition);
        }
        if (Unit.Concurrency.IsOptimistic && options?.Precondition.Kind == WritePreconditionKind.IfVersion)
            filter[MongoDocumentMapper.VersionField] = options.Precondition.Version!.Value;

        commandObserver?.Observe(new ProviderCommandEvent("mongodb.compare-and-delete", "MongoDB.DeleteOne", ProviderCommandKind.Write, IsProbe: false));
        var result = await DeleteOne(filter, mode).ConfigureAwait(false);
        if (result.DeletedCount != 0)
        {
            await RemoveVersion(identity, mode).ConfigureAwait(false);
            return new MongoWriteOutcome(MongoWriteOutcomeStatus.Deleted, existingVersion);
        }

        existing ??= await FindOne(identity, mode).ConfigureAwait(false);
        if (existing is null)
            return new MongoWriteOutcome(MongoWriteOutcomeStatus.NotFound);
        existingVersion ??= await Version(identity, mode, existing).ConfigureAwait(false);
        if (options?.Precondition.Kind == WritePreconditionKind.IfVersion &&
            options.Precondition.Version != existingVersion)
            return new MongoWriteOutcome(MongoWriteOutcomeStatus.ConcurrencyConflict, existingVersion);
        return MatchesExpected(existing, expectedValues)
            ? new MongoWriteOutcome(MongoWriteOutcomeStatus.ConcurrencyConflict, existingVersion)
            : new MongoWriteOutcome(MongoWriteOutcomeStatus.ComparisonMismatch, existingVersion);
    }

    private bool MatchesExpected(
        BsonDocument existing,
        IReadOnlyDictionary<string, object?> expectedValues) =>
        expectedValues.All(pair =>
        {
            var definition = Unit.Columns.Single(column => column.Name == pair.Key);
            var actual = existing.TryGetValue(pair.Key, out var stored) ? stored : BsonNull.Value;
            return actual.Equals(MongoValueCodec.Encode(pair.Value, definition));
        });

    private bool ConcurrencyAllows(
        BsonDocument? existing,
        long? currentVersion,
        MongoWriteOptions? options,
        MutationKind kind)
    {
        var precondition = options?.Precondition ?? WritePrecondition.Unconditional;
        if (!Unit.Concurrency.IsOptimistic)
            return true;
        if (precondition.Kind == WritePreconditionKind.Unconditional)
            return true;
        if (precondition.Kind == WritePreconditionKind.CreateOnly)
            return existing is null && kind is (MutationKind.Insert or MutationKind.Upsert);
        return existing is not null && precondition.Version == currentVersion;
    }

    private BsonDocument ConcurrencyFilter(BsonValue identity, long? currentVersion)
    {
        var filter = new BsonDocument("_id", identity);
        if (!Unit.Concurrency.IsOptimistic || currentVersion is null)
            return filter;

        // Older P1 documents kept the version in metadata only. Accept that shape once,
        // while every successful replacement writes the atomic per-document token.
        filter["$or"] = new BsonArray
        {
            new BsonDocument(MongoDocumentMapper.VersionField, currentVersion.Value),
            new BsonDocument(MongoDocumentMapper.VersionField,
                new BsonDocument("$exists", false))
        };
        return filter;
    }

    private long? NextVersion(long? current) =>
        Unit.Concurrency.IsOptimistic
            ? checked((current ?? 0) + 1)
            : null;

    private async ValueTask<long> NextSequence(ColumnDefinition column, MongoExecution mode)
    {
        // Keep sequence allocation visible to the same diagnostic seam as the write.
        // ConditionalUpsert rejects this path before it can occur, preserving its
        // one-command contract; ordinary generated writes still report the extra
        // provider command instead of hiding it from accounting.
        commandObserver?.Observe(new ProviderCommandEvent(
            "mongodb.provider-sequence",
            "MongoDB.FindOneAndUpdate(sequence)",
            ProviderCommandKind.Write,
            IsProbe: false));
        var filter = new BsonDocument("_id", Unit.Id.Value + ":" + column.Name);
        var update = Builders<BsonDocument>.Update.Inc("value", 1);
        var options = new FindOneAndUpdateOptions<BsonDocument>
        {
            IsUpsert = true,
            ReturnDocument = ReturnDocument.After
        };
        var allocated = await mode.Run(
            token => state.Sequences.FindOneAndUpdateAsync(transactionSession, filter, update, options, token),
            () => state.Sequences.FindOneAndUpdate(transactionSession, filter, update, options)).ConfigureAwait(false);
        return allocated!["value"].ToInt64();
    }

    private ValueTask PersistVersion(BsonValue identity, long? version, MongoExecution mode)
    {
        if (!Unit.Concurrency.IsOptimistic || version is null)
            return default;
        var filter = new BsonDocument("_id", MetadataId(identity));
        var document = new BsonDocument { ["_id"] = MetadataId(identity), ["version"] = version.Value };
        var options = new ReplaceOptions { IsUpsert = true };
        return mode.Run(
            token => transactionSession is null
                ? state.Metadata.ReplaceOneAsync(filter, document, options, token)
                : state.Metadata.ReplaceOneAsync(transactionSession, filter, document, options, token),
            () =>
            {
                if (transactionSession is null)
                    state.Metadata.ReplaceOne(filter, document, options);
                else
                    state.Metadata.ReplaceOne(transactionSession, filter, document, options);
            });
    }

    private async ValueTask<long?> Version(BsonValue identity, MongoExecution mode, BsonDocument? document = null)
    {
        if (!Unit.Concurrency.IsOptimistic)
            return null;
        if (document is not null && document.TryGetValue(MongoDocumentMapper.VersionField, out var version))
            return version.ToInt64();
        var filter = new BsonDocument("_id", MetadataId(identity));
        var metadata = await mode.FirstOrDefault(transactionSession is null
            ? state.Metadata.Find(filter)
            : state.Metadata.Find(transactionSession, filter)).ConfigureAwait(false);
        return metadata is null ? null : metadata.GetValue("version", 0).ToInt64();
    }

    private ValueTask RemoveVersion(BsonValue identity, MongoExecution mode)
    {
        if (!Unit.Concurrency.IsOptimistic)
            return default;
        var filter = new BsonDocument("_id", MetadataId(identity));
        commandObserver?.Observe(new ProviderCommandEvent("mongodb.compare-and-delete-version-delete", "MongoDB.DeleteOne(metadata)", ProviderCommandKind.Write, IsProbe: false));
        return mode.Run(
            token => transactionSession is null
                ? state.Metadata.DeleteOneAsync(filter, token)
                : state.Metadata.DeleteOneAsync(transactionSession, filter, cancellationToken: token),
            () =>
            {
                if (transactionSession is null)
                    state.Metadata.DeleteOne(filter);
                else
                    state.Metadata.DeleteOne(transactionSession, filter);
            });
    }

    private ValueTask<BsonDocument?> FindOne(
        BsonValue identity,
        MongoExecution mode,
        string operation = "mongodb.write-probe",
        bool isProbe = true)
    {
        if (commandObserver is not null)
        {
            var commandText = operation == "mongodb.read" && !isProbe
                ? PointReadCommandText(identity)
                : "MongoDB.FindOne";
            commandObserver.Observe(new ProviderCommandEvent(operation, commandText, ProviderCommandKind.Read, IsProbe: isProbe));
        }
        return mode.FirstOrDefault(transactionSession is null
            ? collection.Find(new BsonDocument("_id", identity))
            : collection.Find(transactionSession, new BsonDocument("_id", identity)))!;
    }

    private string PointReadCommandText(BsonValue identity)
    {
        var command = new BsonDocument
        {
            ["collection"] = collection.CollectionNamespace.CollectionName,
            ["filter"] = new BsonDocument("_id", new BsonDocument("$eq", RedactIdentity(identity))),
            ["limit"] = 1
        };
        return command.ToJson();
    }

    private static BsonValue RedactIdentity(BsonValue identity)
    {
        if (identity is BsonDocument document)
        {
            var redacted = new BsonDocument();
            foreach (var element in document)
                redacted.Add(element.Name, RedactIdentity(element.Value));
            return redacted;
        }

        if (identity is BsonArray array)
        {
            var redacted = new BsonArray();
            foreach (var value in array)
                redacted.Add(RedactIdentity(value));
            return redacted;
        }

        return new BsonString("<redacted>");
    }

    private ValueTask InsertOne(BsonDocument document, MongoExecution mode) =>
        mode.Run(
            token => transactionSession is null
                ? collection.InsertOneAsync(document, cancellationToken: token)
                : collection.InsertOneAsync(transactionSession, document, cancellationToken: token),
            () =>
            {
                if (transactionSession is null)
                    collection.InsertOne(document);
                else
                    collection.InsertOne(transactionSession, document);
            });

    private ValueTask<ReplaceOneResult> ReplaceOne(
        BsonDocument filter,
        BsonDocument document,
        bool isUpsert,
        MongoExecution mode)
    {
        var options = new ReplaceOptions { IsUpsert = isUpsert };
        return mode.Run(
            token => transactionSession is null
                ? collection.ReplaceOneAsync(filter, document, options, token)
                : collection.ReplaceOneAsync(transactionSession, filter, document, options, token),
            () => transactionSession is null
                ? collection.ReplaceOne(filter, document, options)
                : collection.ReplaceOne(transactionSession, filter, document, options));
    }

    private ValueTask<DeleteResult> DeleteOne(BsonDocument filter, MongoExecution mode) =>
        mode.Run(
            token => transactionSession is null
                ? collection.DeleteOneAsync(filter, token)
                : collection.DeleteOneAsync(transactionSession, filter, cancellationToken: token),
            () => transactionSession is null
                ? collection.DeleteOne(filter)
                : collection.DeleteOne(transactionSession, filter));

    private BsonValue MetadataId(BsonValue identity) => new BsonDocument
    {
        ["unit"] = Unit.Id.Value,
        ["scope"] = Access.Scope?.Value ?? "<global>",
        ["key"] = identity
    };

    private BsonValue HighWaterId() => new BsonDocument
    {
        ["unit"] = Unit.Id.Value,
        ["scope"] = Access.Scope?.Value ?? "<global>",
        ["kind"] = "sequence-high-water"
    };

    private ValueTask RecordHighWater(IReadOnlyDictionary<string, object?> generatedValues, MongoExecution mode)
    {
        var sequence = Unit.Columns.FirstOrDefault(column => column.Generation == ColumnGeneration.ProviderSequence);
        if (sequence is null || !generatedValues.TryGetValue(sequence.Name, out var generated) || generated is null)
            return default;
        var filter = new BsonDocument("_id", HighWaterId());
        var update = Builders<BsonDocument>.Update
            .SetOnInsert("unit", Unit.Id.Value)
            .SetOnInsert("scope", Access.Scope?.Value ?? "<global>")
            .Max(HighWaterValue, Convert.ToInt64(generated, CultureInfo.InvariantCulture));
        var options = new UpdateOptions { IsUpsert = true };
        return mode.Run(
            token => transactionSession is null
                ? state.Metadata.UpdateOneAsync(filter, update, options, token)
                : state.Metadata.UpdateOneAsync(transactionSession, filter, update, options, token),
            () =>
            {
                if (transactionSession is null)
                    state.Metadata.UpdateOne(filter, update, options);
                else
                    state.Metadata.UpdateOne(transactionSession, filter, update, options);
            });
    }

    private async ValueTask<T> ExecuteWithTransactionIfNeeded<T>(
        Func<MongoStorageSession, ValueTask<T>> operation,
        MongoExecution mode,
        bool requireTransaction = false)
    {
        if (transactionSession is not null)
            return await operation(this).ConfigureAwait(false);
        if (!requireTransaction &&
            !Unit.Columns.Any(column => column.Generation == ColumnGeneration.ProviderSequence) &&
            Unit.AppendIdempotency is null &&
            Unit.RetentionIdempotency is null)
            return await operation(this).ConfigureAwait(false);

        var transactionReason = requireTransaction
            ? "CompareAndDelete"
            : Unit.AppendIdempotency is null
                ? Unit.RetentionIdempotency is null ? "ProviderSequence" : "ExactRetention"
                : "AppendIdempotency";
        state.Context.RequireTransactions(transactionReason);
        MongoException? lastTransientFailure = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var session = await mode.Run(
                token => state.Context.StartSessionAsync(cancellationToken: token),
                () => state.Context.StartSession()).ConfigureAwait(false);
            session.StartTransaction();
            var transactional = new MongoStorageSession(state, applied, Access, collection, session, observer: commandObserver);
            var operationCompleted = false;
            try
            {
                var result = await operation(transactional).ConfigureAwait(false);
                if (result is IReadOnlyList<RowWriteOutcome> batchResult &&
                    batchResult.Any(item => IsBatchAbortingOutcome(item.Outcome)))
                {
                    // A row-level conflict can abort the transaction before the fallback has
                    // finished walking its rows. Do not attempt the next row or commit the
                    // already-aborted transaction; return the positional prefix so the shared
                    // batch layer can raise BatchWriteException with the attributed failure.
                    try { await Abort(session, mode).ConfigureAwait(false); }
                    catch (MongoException) { }
                    operationCompleted = true;
                    return result;
                }
                if (result is MongoWriteOutcome
                    {
                        Status: MongoWriteOutcomeStatus.UniqueViolation or
                            MongoWriteOutcomeStatus.ConcurrencyConflict
                    })
                {
                    // A duplicate-key or transient-conflict write aborts the Mongo transaction
                    // immediately. Return the provider-neutral outcome without attempting
                    // commitTransaction on the already-aborted transaction; callers must be
                    // able to continue their conformance sequence with a fresh write transaction.
                    try { await Abort(session, mode).ConfigureAwait(false); }
                    catch (MongoException) { }
                    operationCompleted = true;
                    return result;
                }
                operationCompleted = true;
                await CommitTransactionWithRetry(session, mode).ConfigureAwait(false);
                return result;
            }
            catch (MongoException exception) when (
                IsTransientTransactionBodyFailure(exception) && attempt < 4)
            {
                lastTransientFailure = exception;
            }
            finally
            {
                if (!operationCompleted && session.IsInTransaction)
                {
                    try { await Abort(session, mode).ConfigureAwait(false); }
                    catch (MongoException) { }
                }
                transactional.Close();
            }
        }

        if (lastTransientFailure is not null)
            throw lastTransientFailure;
        throw new InvalidOperationException($"MongoDB {transactionReason} transaction retries were exhausted.");
    }

    internal static ValueTask Abort(IClientSessionHandle session, MongoExecution mode) =>
        mode.Run(token => session.AbortTransactionAsync(token), () => session.AbortTransaction());

    internal static async ValueTask CommitTransactionWithRetry(IClientSessionHandle session, MongoExecution mode)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await mode.Run(token => session.CommitTransactionAsync(token), () => session.CommitTransaction())
                    .ConfigureAwait(false);
                return;
            }
            catch (MongoException exception) when (
                exception.HasErrorLabel("UnknownTransactionCommitResult") && attempt < 4)
            {
                // The transaction body must not be replayed when only the commit result is
                // unknown. Retrying commit is the MongoDB-prescribed resolution.
            }
        }
    }

    private sealed class MongoLedgerConflictException : Exception
    {
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(MongoStorageSession));
        schemaSession.EnsureCurrent();
    }

    void IMongoSchemaBoundSession.EnsureSchemaCurrent() => ThrowIfDisposed();

    private void RefusePrivilegedOperation(string operation)
    {
        if (Access.IsPrivilegedAcrossScopes)
        {
            throw new InvalidOperationException(
                $"GW-ACCESS-003: privileged cross-scope access is query-only; '{operation}' requires an ordinary session with an explicit scope.");
        }
    }
}

internal sealed class MongoUnitOfWork : IMongoUnitOfWork, IMongoUnitOfWorkState
{
    private readonly IProviderCommandObserver? commandObserver;
    private readonly MongoProviderState state;
    private readonly IReadOnlyDictionary<StorageUnitId, (MongoAppliedUnit Applied, IMongoCollection<BsonDocument> Collection)> units;
    private readonly MongoStorageAccess access;
    private readonly IClientSessionHandle session;
    private readonly List<MongoStorageSession> sessions = [];
    private bool terminal;

    internal MongoUnitOfWork(
        MongoProviderState state,
        IReadOnlyList<MongoAppliedUnit> applied,
        IReadOnlyList<IMongoCollection<BsonDocument>> collections,
        MongoStorageAccess access,
        IProviderCommandObserver? observer = null)
    {
        commandObserver = observer;
        this.state = state;
        this.access = access;
        units = applied.Select((unit, index) => (unit, collections[index]))
            .ToDictionary(pair => pair.unit.Declaration.Id,
                pair => (pair.unit, pair.Item2));
        state.Context.RequireTransactions("Mongo unit of work");
        session = state.Context.StartSession();
        session.StartTransaction();
    }

    public IMongoStorageSession OpenSession(StorageUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ThrowIfTerminal();
        if (!units.TryGetValue(unit.Id, out var applied))
            throw new InvalidOperationException($"Storage unit '{unit.Id.Value}' was not declared for this unit of work.");
        var session = new MongoStorageSession(state, applied.Applied, access, applied.Collection, this.session, this, commandObserver);
        sessions.Add(session);
        return session;
    }

    public void Commit() => CommitCore(MongoExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask CommitAsync(CancellationToken cancellationToken = default) =>
        CommitCore(MongoExecution.Asynchronous(cancellationToken));

    private async ValueTask CommitCore(MongoExecution mode)
    {
        ThrowIfTerminal();
        try
        {
            await MongoStorageSession.CommitTransactionWithRetry(session, mode).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            // A failed commit still ends this unit and still disposes the native session. It must
            // become terminal in the same step, or a caller's Dispose rolls back through a disposed
            // session and replaces the commit failure with a lifecycle error.
            WriteFailureCleanup.Run(failure, Complete);
            throw;
        }
        Complete();
    }

    public void Rollback()
    {
        ThrowIfTerminal();
        try
        {
            if (session.IsInTransaction)
                session.AbortTransaction();
        }
        finally
        {
            Complete();
        }
    }

    private void Complete()
    {
        terminal = true;
        CloseSessions();
        session.Dispose();
    }

    public void Dispose()
    {
        if (!terminal)
            Rollback();
    }

    private void CloseSessions()
    {
        foreach (var storageSession in sessions)
            storageSession.Close();
    }

    private void ThrowIfTerminal()
    {
        if (terminal)
            throw new InvalidOperationException("The unit of work is already terminal.");
    }

    public bool IsActive => !terminal;

    public void EnsureActive() => ThrowIfTerminal();

    internal void Poison()
    {
        if (terminal)
            return;
        Complete();
    }
}

internal enum MutationKind
{
    Insert,
    Update,
    Upsert,
    Delete
}

internal static class MongoDocumentMapper
{
    internal const string VersionField = "__groundwork_version";

    internal static bool IsSystemOwnedToken(StorageUnit unit, ColumnDefinition column) =>
        unit.Concurrency.IsOptimistic && string.Equals(
            unit.Concurrency.TokenColumn, column.Name, StringComparison.Ordinal);

    internal static BsonValue EncodeKey(StorageUnit unit, IReadOnlyDictionary<string, object?> values)
    {
        var key = new BsonDocument();
        foreach (var name in unit.Key.Columns)
        {
            if (!values.TryGetValue(name, out var value) || value is null)
                throw new ArgumentException($"Key column '{name}' is required and cannot be null.", nameof(values));
            var column = unit.Columns.Single(item => item.Name == name);
            key.Add(name, MongoValueCodec.Encode(value, column));
        }
        return key.ElementCount == 1 ? key[0] : key;
    }

    internal static async ValueTask<BsonDocument> EncodeDocument(
        StorageUnit unit,
        IReadOnlyDictionary<string, object?> values,
        BsonValue identity,
        BsonDocument? existing,
        Func<ColumnDefinition, ValueTask<long>> nextSequence,
        bool preserveCreatedAt = false,
        IReadOnlyDictionary<string, object?>? generatedValues = null)
    {
        var known = unit.Columns.Select(column => column.Name).ToHashSet(StringComparer.Ordinal);
        var unknown = values.Keys.FirstOrDefault(key => !known.Contains(key));
        if (unknown is not null)
            throw new ArgumentException($"Column '{unknown}' is not declared by '{unit.Name}'.", nameof(values));

        var document = new BsonDocument("_id", identity);
        foreach (var column in unit.Columns)
        {
            if (IsSystemOwnedToken(unit, column))
                continue;
            var isPresent = values.TryGetValue(column.Name, out var value);
            if (column.Generation == ColumnGeneration.ProviderSequence)
            {
                var generatedInternally = generatedValues is not null && generatedValues.ContainsKey(column.Name);
                if (isPresent && existing is null && !generatedInternally)
                    throw new ArgumentException($"ProviderSequence column '{column.Name}' is assigned by MongoDB and cannot be supplied.", nameof(values));
                var generated = existing?.GetValue(column.Name, BsonNull.Value) ??
                    (generatedInternally
                        ? MongoValueCodec.Encode(generatedValues![column.Name], column)
                        : new BsonInt64(await nextSequence(column).ConfigureAwait(false)));
                if (isPresent && existing is not null && !generatedInternally &&
                    !MongoValueCodec.Encode(value, column).Equals(generated))
                {
                    throw new ArgumentException(
                        $"ProviderSequence column '{column.Name}' is assigned by MongoDB and cannot be changed.",
                        nameof(values));
                }
                document.Add(column.Name, generated);
            }
            else
            {
                document.Add(column.Name,
                    preserveCreatedAt && existing is not null && column.Name == "createdAt" &&
                    existing.TryGetValue(column.Name, out var priorCreatedAt)
                        ? priorCreatedAt
                    : !isPresent && existing is not null && existing.TryGetValue(column.Name, out var previous)
                        ? previous
                        : MongoValueCodec.Encode(value, column, isPresent));
            }
        }
        return document;
    }

    internal static MongoStoredEntry DecodeEntry(StorageUnit unit, BsonDocument document, long? version)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var column in unit.Columns)
        {
            if (SearchKeyProjection.IsProviderOwnedColumn(column.Name) || IsSystemOwnedToken(unit, column))
                continue;
            values[column.Name] = document.TryGetValue(column.Name, out var value)
                ? MongoValueCodec.Decode(value, column)
                : null;
        }
        return new MongoStoredEntry(new MongoStorageValues(values), version);
    }
}

internal static class SchemaIdentity
{
    internal static string Fingerprint(StorageUnit unit) => string.Join("||",
        unit.Id.Value,
        unit.Name,
        string.Join(",", unit.Key.Columns),
        unit.Scope,
        unit.Concurrency,
        unit.Timestamps,
        RetentionCanonicalization.Canonicalize(unit.Retention),
        unit.SchemaVersion,
        unit.AppendIdempotency is null
            ? "idempotency:none"
            : string.Join("|", "idempotency", unit.AppendIdempotency.Window.Ticks, unit.AppendIdempotency.LedgerName),
        unit.RetentionIdempotency is null
            ? "retention-idempotency:none"
            : string.Join("|", "retention-idempotency", unit.RetentionIdempotency.Window.Ticks, unit.RetentionIdempotency.LedgerName),
        string.Join("|", unit.Columns.Select(Column)),
        string.Join("|", unit.DerivedColumns.Select(column =>
            string.Join("|", column.Name, column.SourceColumn, column.Projection, column.AlgorithmId))),
        // Indexes and aggregation profiles are sets, not sequences, exactly as SchemaSubject
        // treats them: naming the same ones in a different order describes the same unit. A
        // schema document canonicalizes their order and the fluent builder does not, so an
        // order-sensitive hash here would refuse a declaration the deployment tool just applied.
        string.Join("|", unit.Indexes.Select(Index).OrderBy(canonical => canonical, StringComparer.Ordinal)),
        SchemaFingerprint.Canonicalize(unit.AggregationProfiles.Select(AggregationProfile)
            .OrderBy(canonical => canonical, StringComparer.Ordinal)));

    internal static bool ColumnEquals(ColumnDefinition left, ColumnDefinition right) =>
        string.Equals(Column(left), Column(right), StringComparison.Ordinal);

    internal static bool IndexEquals(IndexDefinition left, IndexDefinition right) =>
        string.Equals(Index(left), Index(right), StringComparison.Ordinal);

    internal static bool RetentionEquals(RetentionDeclaration? left, RetentionDeclaration? right) =>
        string.Equals(
            RetentionCanonicalization.Canonicalize(left),
            RetentionCanonicalization.Canonicalize(right),
            StringComparison.Ordinal);

    internal static bool IdempotencyEquals(
        AppendIdempotencyDeclaration? left,
        AppendIdempotencyDeclaration? right) =>
        left?.Window == right?.Window &&
        string.Equals(left?.LedgerName, right?.LedgerName, StringComparison.Ordinal);

    internal static bool RetentionIdempotencyEquals(
        RetentionIdempotencyDeclaration? left,
        RetentionIdempotencyDeclaration? right) =>
        left?.Window == right?.Window &&
        string.Equals(left?.LedgerName, right?.LedgerName, StringComparison.Ordinal);

    private static string Column(ColumnDefinition column) => string.Join("|",
        column.Name, column.Type, column.IsNullable, column.MaxLength, column.Precision,
        column.Scale,
        column.Type == PortableType.String && (column.Collation is null or PortableCollation.Ordinal)
            ? PortableCollation.Ordinal
            : column.Collation,
        column.Generation,
        column.Default is null ? "default:absent" : "default:present:" + column.Default.Value) +
        (column.ElementSearchKey is null
            ? string.Empty
            : "|element-search-key:" + column.ElementSearchKey.Collation + ":" +
              (column.ElementSearchKey.MaximumElementCodeUnits?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unbounded"));

    private static string Index(IndexDefinition index) => string.Join("|",
        index.Name, index.IsUnique, index.MissingValues, index.SchemaVersion,
        index.UseOrdinalIdentities,
        string.Join(",", index.Columns.Select(column => column.Column + ":" + column.Direction)),
        string.Join(",", (index.IncludedColumns ?? []).OrderBy(column => column, StringComparer.Ordinal)));

    private static string AggregationProfile(AggregationProfile profile) =>
        AggregationProfileCanonicalization.Canonicalize(profile);

}

internal static class MongoDeclarationSnapshot
{
    internal static StorageUnit Clone(StorageUnit unit) => unit with
    {
        Columns = unit.Columns.Select(column => column with
        {
            Default = column.Default is null ? null : new PortableDefault(CloneValue(column.Default.Value))
        }).ToArray(),
        Key = unit.Key with { Columns = unit.Key.Columns.ToArray() },
        DerivedColumns = unit.DerivedColumns.Select(column => column with { }).ToArray(),
        Indexes = unit.Indexes.Select(index => index with
        {
            Columns = index.Columns.Select(column => column with { }).ToArray(),
            IncludedColumns = index.IncludedColumns?.ToArray()
        }).ToArray(),
        AggregationProfiles = unit.AggregationProfiles.Select(AggregationProfileSnapshot.Capture).ToArray(),
        Retention = unit.Retention is null ? null : unit.Retention with
        {
            PartitionColumns = unit.Retention.PartitionColumns.ToArray()
        }
    };

    private static object? CloneValue(object? value) => value switch
    {
        null => null,
        byte[] bytes => bytes.ToArray(),
        JsonNode node => node.DeepClone(),
        JsonElement element => element.Clone(),
        JsonDocument document => document.RootElement.Clone(),
        IReadOnlyDictionary<string, object?> dictionary => new ReadOnlyDictionary<string, object?>(
            dictionary.ToDictionary(pair => pair.Key, pair => CloneValue(pair.Value), StringComparer.Ordinal)),
        IEnumerable sequence when value is not string => sequence.Cast<object?>().Select(CloneValue).ToArray(),
        _ when value.GetType().IsValueType || value is string => value,
        _ => throw new ArgumentException($"Cannot snapshot mutable default of type '{value.GetType().FullName}'.")
    };
}
