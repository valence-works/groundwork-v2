using System.Collections;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Groundwork.Kernel;
using Groundwork.Substrate.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;
using KernelSortDirection = Groundwork.Kernel.SortDirection;

namespace Groundwork.MongoDb;

/// <summary>The capability declared by MongoDB for provider-assigned sequence columns.</summary>
public static class MongoCapabilities
{
    public static readonly CapabilityId ProviderSequence = new("groundwork.storage.provider-sequence");

    public static CapabilityDescriptor ProviderSequenceDescriptor { get; } = new(
        ProviderSequence,
        "Provider-assigned monotonic sequence",
        "MongoDB allocates a sequence in a counter collection and commits it with the row in a transaction-capable deployment.",
        EvidenceGatedByDefault: true,
        OwningModule: "groundwork-mongodb");
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

    /// <summary>Provides read-only access to the native database for catalog/evidence tests.</summary>
    public IMongoDatabase Database => state.Context.Database;

    public IMongoStorageSession OpenSession(StorageUnit unit, MongoStorageAccess access)
    {
        ThrowIfDisposed();
        var applied = state.Resolve(unit, access);
        var collection = MongoSchemaCoordinator.EnsureAdmission(state, applied, access);
        return new MongoStorageSession(state, applied, access, collection, null);
    }

    public IMongoUnitOfWork BeginUnitOfWork(MongoStorageAccess access, params StorageUnit[] units)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(units);
        if (units.Length == 0)
            throw new ArgumentException("A unit of work must declare at least one storage unit.", nameof(units));

        var applied = units.Select(unit => state.Resolve(unit, access)).ToArray();
        if (applied.Select(unit => unit.Declaration.Id).Distinct().Count() != applied.Length)
            throw new ArgumentException("A unit of work cannot list the same storage unit twice.", nameof(units));
        var collections = applied
            .Select(unit => MongoSchemaCoordinator.EnsureAdmission(state, unit, access))
            .ToArray();
        return new MongoUnitOfWork(state, applied, collections, access);
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

    internal MongoProviderState(MongoClientContext context) => Context = context;

    internal MongoClientContext Context { get; }

    internal IMongoCollection<BsonDocument> Metadata =>
        Context.Database.GetCollection<BsonDocument>("__groundwork_metadata");

    internal IMongoCollection<BsonDocument> Sequences =>
        Context.Database.GetCollection<BsonDocument>("__groundwork_sequences");

    internal MongoAppliedUnit Resolve(StorageUnit declaration, MongoStorageAccess access)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ValidateScope(declaration, access);
        lock (gate)
        {
            if (units.TryGetValue(declaration.Id, out var existing))
            {
                EnsureSameDeclaration(existing.Declaration, declaration);
                return existing;
            }
        }

        if (!CollectionExists(declaration.Name))
        {
            throw new InvalidOperationException(
                $"Storage unit '{declaration.Id.Value}' has not been applied to this provider.");
        }

        var persisted = Metadata.Find(new BsonDocument("_id", "schema:" + declaration.Id.Value))
            .FirstOrDefault();
        if (persisted is not null && persisted.TryGetValue("fingerprint", out var fingerprint) &&
            !string.Equals(fingerprint.AsString, SchemaIdentity.Fingerprint(declaration), StringComparison.Ordinal))
        {
            throw new MongoSchemaConflictException(
                $"Storage unit '{declaration.Name}' differs from the applied MongoDB schema. Apply the schema before opening it.");
        }

        var applied = new MongoAppliedUnit(MongoDeclarationSnapshot.Clone(declaration), declaration.Name);
        lock (gate)
        {
            if (units.TryGetValue(declaration.Id, out var raced))
            {
                EnsureSameDeclaration(raced.Declaration, declaration);
                return raced;
            }
            units.Add(declaration.Id, applied);
            return applied;
        }
    }

    private static void EnsureSameDeclaration(StorageUnit applied, StorageUnit requested)
    {
        if (!string.Equals(SchemaIdentity.Fingerprint(applied), SchemaIdentity.Fingerprint(requested), StringComparison.Ordinal))
        {
            throw new MongoSchemaConflictException(
                $"Storage unit '{requested.Name}' differs from the applied MongoDB schema. Apply the schema before opening it.");
        }
    }

    internal MongoAppliedUnit Remember(StorageUnit declaration)
    {
        var snapshot = MongoDeclarationSnapshot.Clone(declaration);
        var applied = new MongoAppliedUnit(snapshot, snapshot.Name);
        lock (gate)
            units[declaration.Id] = applied;
        return applied;
    }

    internal bool TryGet(StorageUnitId id, out MongoAppliedUnit applied)
    {
        lock (gate)
            return units.TryGetValue(id, out applied!);
    }

    internal bool CollectionExists(string name) => Context.Database.ListCollectionNames(
        new ListCollectionNamesOptions { Filter = new BsonDocument("name", name) }).Any();

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

internal sealed record MongoAppliedUnit(StorageUnit Declaration, string CollectionName);

internal sealed class MongoProviderCatalog(MongoProviderState state) : IMongoProviderCatalog
{
    public IReadOnlyList<MongoProviderIndex> ReadIndexes(StorageUnitId storageUnitId)
    {
        state.TryGet(storageUnitId, out var applied);
        var collectionName = applied?.CollectionName ??
            state.Metadata.Find(new BsonDocument("_id", "schema:" + storageUnitId.Value))
                .FirstOrDefault()?.GetValue("collection", storageUnitId.Value).AsString ?? storageUnitId.Value;
        return ReadIndexes(collectionName, applied?.Declaration.Indexes);
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
    public MongoSchemaDiff Diff(StorageUnit desired)
    {
        ValidateDeclaration(desired);
        state.TryGet(desired.Id, out var current);
        var previousKeyOrder = current?.Declaration.Key.Columns ?? ReadSchemaKeyOrder(desired.Id);
        ValidateCompositeKeyOrder(desired, previousKeyOrder);
        if (current is null && !state.CollectionExists(desired.Name))
        {
            return new MongoSchemaDiff([
                new MongoSchemaChange(MongoSchemaChangeKind.CreateStorageUnit, desired.Name),
                .. desired.Columns.Select(column => new MongoSchemaChange(MongoSchemaChangeKind.AddColumn, column.Name)),
                .. desired.DerivedColumns.Select(column => new MongoSchemaChange(MongoSchemaChangeKind.AddDerivedColumn, column.Name)),
                .. desired.Indexes.Select(index => new MongoSchemaChange(MongoSchemaChangeKind.CreateIndex, index.Name))
            ]);
        }

        current ??= new MongoAppliedUnit(desired, desired.Name);
        return new MongoSchemaDiff(BuildChanges(desired, current.Declaration,
            new MongoProviderCatalog(state).ReadIndexes(desired.Name, desired.Indexes)));
    }

    public MongoSchemaApplyResult Apply(StorageUnit desired)
    {
        ValidateDeclaration(desired);
        var portability = PortabilityValidator.Validate(desired, new PortabilityValidationContext(["mongodb"]));
        if (!portability.IsPortable)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine,
                portability.Refusals.Select(refusal =>
                    $"{refusal.Code} at {refusal.Path}: {refusal.Message}")));
        }

        state.TryGet(desired.Id, out var current);
        var previousKeyOrder = current?.Declaration.Key.Columns ?? ReadSchemaKeyOrder(desired.Id);
        ValidateCompositeKeyOrder(desired, previousKeyOrder);

        if (HasProviderSequence(desired))
            state.Context.RequireTransactions("ProviderSequence");

        var exists = state.CollectionExists(desired.Name);
        if (!exists)
        {
            state.Context.Database.CreateCollection(desired.Name);
            current = null;
        }

        var actual = new MongoProviderCatalog(state).ReadIndexes(desired.Name, desired.Indexes);

        var collection = state.Context.Database.GetCollection<BsonDocument>(desired.Name);
        CreateIndexes(collection, desired, actual);

        var changes = DiffForApply(desired, current?.Declaration, actual, !exists);
        PersistSchemaMetadata(desired);
        state.Remember(desired);
        return new MongoSchemaApplyResult(new MongoSchemaDiff(changes), changes.Count != 0);
    }

    internal static IMongoCollection<BsonDocument> EnsureAdmission(
        MongoProviderState state,
        MongoAppliedUnit applied,
        MongoStorageAccess access)
    {
        var name = CollectionName(applied, access);
        EnsureCollection(state, applied, name);
        var collection = state.Context.Database.GetCollection<BsonDocument>(name);
        var missing = applied.Declaration.Columns
            .Where(column => !string.Equals(column.Name, "_id", StringComparison.Ordinal))
            .Select(column => new BsonDocument(column.Name,
                new BsonDocument("$exists", false)))
            .ToArray();
        if (missing.Length != 0 && collection.CountDocuments(new BsonDocument("$or", new BsonArray(missing))) > 0)
        {
            var commands = string.Join("; ", applied.Declaration.Columns.Select(column =>
                $"db.getCollection('{Escape(name)}').updateMany(" +
                $"{{ \"{Escape(column.Name)}\": {{ $exists: false }} }}, " +
                $"{{ $set: {{ \"{Escape(column.Name)}\": {(column.IsNullable ? "null" : "<backfill-value>")} }} }});"));
            throw new InvalidOperationException(
                $"Storage unit '{applied.Declaration.Name}' is not admitted: existing documents are missing declared columns. " +
                $"Backfill before opening it, for example: {commands}");
        }

        return collection;
    }

    private static void EnsureCollection(MongoProviderState state, MongoAppliedUnit applied, string name)
    {
        if (!state.CollectionExists(name))
        {
            state.Context.Database.CreateCollection(name);
            CreateIndexes(state.Context.Database.GetCollection<BsonDocument>(name), applied.Declaration, []);
        }
    }

    private static void CreateIndexes(
        IMongoCollection<BsonDocument> collection,
        StorageUnit unit,
        IReadOnlyList<MongoProviderIndex> actual)
    {
        foreach (var index in unit.Indexes)
        {
            if (actual.Any(existing => string.Equals(existing.Name, index.Name, StringComparison.Ordinal)))
                continue;
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

    internal static string CollectionName(MongoAppliedUnit applied, MongoStorageAccess access)
    {
        if (applied.Declaration.Scope == ScopePolicy.Global)
            return applied.CollectionName;
        var scope = access.Scope?.Value ?? throw new InvalidOperationException(
            "A scoped storage unit requires a scope value.");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
        return applied.CollectionName + "__scope__" + hash;
    }

    private static IReadOnlyList<MongoSchemaChange> BuildChanges(
        StorageUnit desired,
        StorageUnit? current,
        IReadOnlyList<MongoProviderIndex> actual)
    {
        if (current is not null)
        {
            if (!string.Equals(current.Name, desired.Name, StringComparison.Ordinal))
                throw new MongoSchemaConflictException($"Storage unit '{desired.Id.Value}' cannot change its name.");
            if (!current.Key.Columns.SequenceEqual(desired.Key.Columns, StringComparer.Ordinal))
                throw new MongoSchemaConflictException($"Storage unit '{desired.Name}' cannot change its key non-additively.");
            if (current.Scope != desired.Scope || current.Concurrency != desired.Concurrency ||
                current.Timestamps != desired.Timestamps || current.SchemaVersion != desired.SchemaVersion)
            {
                throw new MongoSchemaConflictException($"Storage unit '{desired.Name}' changed non-additive storage metadata.");
            }
        }

        if (current is null)
            return [
                new MongoSchemaChange(MongoSchemaChangeKind.CreateStorageUnit, desired.Name),
                .. desired.Columns.Select(column => new MongoSchemaChange(MongoSchemaChangeKind.AddColumn, column.Name)),
                .. desired.DerivedColumns.Select(column => new MongoSchemaChange(MongoSchemaChangeKind.AddDerivedColumn, column.Name)),
                .. desired.Indexes.Select(index => new MongoSchemaChange(MongoSchemaChangeKind.CreateIndex, index.Name))
            ];

        var changes = new List<MongoSchemaChange>();
        foreach (var column in desired.Columns)
        {
            var previous = current.Columns.FirstOrDefault(item => item.Name == column.Name);
            if (previous is null)
                changes.Add(new MongoSchemaChange(MongoSchemaChangeKind.AddColumn, column.Name));
            else if (!SchemaIdentity.ColumnEquals(previous, column))
                throw new MongoSchemaConflictException($"Column '{column.Name}' changed non-additively.");
        }
        foreach (var previous in current.Columns)
            if (!desired.Columns.Any(column => column.Name == previous.Name))
                throw new MongoSchemaConflictException($"Column '{previous.Name}' was removed non-additively.");

        foreach (var derived in desired.DerivedColumns)
        {
            var previous = current.DerivedColumns.FirstOrDefault(item => item.Name == derived.Name);
            if (previous is null)
                changes.Add(new MongoSchemaChange(MongoSchemaChangeKind.AddDerivedColumn, derived.Name));
            else if (previous != derived)
                throw new MongoSchemaConflictException($"Derived column '{derived.Name}' changed non-additively.");
        }

        foreach (var index in desired.Indexes)
        {
            var previous = current.Indexes.FirstOrDefault(item => item.Name == index.Name);
            var native = actual.FirstOrDefault(item => item.Name == index.Name);
            if (previous is null && native is null)
                changes.Add(new MongoSchemaChange(MongoSchemaChangeKind.CreateIndex, index.Name));
            else if (previous is not null && !SchemaIdentity.IndexEquals(previous, index))
                throw new MongoSchemaConflictException($"Index '{index.Name}' changed non-additively.");
        }
        foreach (var previous in current.Indexes)
            if (!desired.Indexes.Any(index => index.Name == previous.Name))
                throw new MongoSchemaConflictException($"Index '{previous.Name}' was removed non-additively.");

        return changes;
    }

    private static IReadOnlyList<MongoSchemaChange> DiffForApply(
        StorageUnit desired,
        StorageUnit? current,
        IReadOnlyList<MongoProviderIndex> actual,
        bool collectionWasCreated)
    {
        if (collectionWasCreated)
            return [
                new MongoSchemaChange(MongoSchemaChangeKind.CreateStorageUnit, desired.Name),
                .. desired.Columns.Select(column => new MongoSchemaChange(MongoSchemaChangeKind.AddColumn, column.Name)),
                .. desired.DerivedColumns.Select(column => new MongoSchemaChange(MongoSchemaChangeKind.AddDerivedColumn, column.Name)),
                .. desired.Indexes.Select(index => new MongoSchemaChange(MongoSchemaChangeKind.CreateIndex, index.Name))
            ];
        return current is null
            ? desired.Indexes
                .Where(index => actual.All(native => native.Name != index.Name))
                .Select(index => new MongoSchemaChange(MongoSchemaChangeKind.CreateIndex, index.Name))
                .ToArray()
            : BuildChanges(desired, current, actual);
    }

    private IReadOnlyList<string>? ReadSchemaKeyOrder(StorageUnitId id)
    {
        var document = state.Metadata.Find(new BsonDocument("_id", SchemaMetadataId(id))).FirstOrDefault();
        if (document is null)
            return null;
        return document.GetValue("key", new BsonArray()).AsBsonArray
            .Select(value => value.AsString)
            .ToArray();
    }

    private void PersistSchemaMetadata(StorageUnit unit)
    {
        var document = new BsonDocument
        {
            ["_id"] = SchemaMetadataId(unit.Id),
            ["collection"] = unit.Name,
            ["key"] = new BsonArray(unit.Key.Columns),
            ["fingerprint"] = SchemaIdentity.Fingerprint(unit)
        };
        state.Metadata.ReplaceOne(
            new BsonDocument("_id", SchemaMetadataId(unit.Id)),
            document,
            new ReplaceOptions { IsUpsert = true });
    }

    private static string SchemaMetadataId(StorageUnitId id) => "schema:" + id.Value;

    private static void ValidateCompositeKeyOrder(StorageUnit unit, IReadOnlyList<string>? previous)
    {
        if (previous is null || previous.Count < 2 || unit.Key.Columns.SequenceEqual(previous, StringComparer.Ordinal))
            return;
        throw new InvalidOperationException(
            $"GW-PORT-008 at key.columns: Mongo composite key column order changed from " +
            $"[{string.Join(", ", previous)}] to [{string.Join(", ", unit.Key.Columns)}]. " +
            "The native _id field order is part of the route and cannot be reordered.");
    }

    private static void ValidateDeclaration(StorageUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentException.ThrowIfNullOrWhiteSpace(unit.Name);
        ArgumentNullException.ThrowIfNull(unit.Columns);
        ArgumentNullException.ThrowIfNull(unit.Key);
        ArgumentNullException.ThrowIfNull(unit.Key.Columns);
        ArgumentNullException.ThrowIfNull(unit.DerivedColumns);
        ArgumentNullException.ThrowIfNull(unit.Indexes);
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

    private static bool HasProviderSequence(StorageUnit unit) =>
        unit.Columns.Any(column => column.Generation == ColumnGeneration.ProviderSequence);

    private static string Escape(string value) => value.Replace("'", "\\'", StringComparison.Ordinal);
}

internal sealed class MongoStorageSession : IMongoStorageSession
{
    private readonly MongoProviderState state;
    private readonly MongoAppliedUnit applied;
    private readonly IMongoCollection<BsonDocument> collection;
    private readonly IClientSessionHandle? transactionSession;
    private bool disposed;

    internal MongoStorageSession(
        MongoProviderState state,
        MongoAppliedUnit applied,
        MongoStorageAccess access,
        IMongoCollection<BsonDocument> collection,
        IClientSessionHandle? transactionSession)
    {
        this.state = state;
        this.applied = applied;
        this.collection = collection;
        this.transactionSession = transactionSession;
        Access = access;
        Unit = MongoDeclarationSnapshot.Clone(applied.Declaration);
    }

    public StorageUnit Unit { get; }

    public MongoStorageAccess Access { get; }

    public MongoStoredEntry? Read(MongoStorageKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        ThrowIfDisposed();
        var identity = MongoDocumentMapper.EncodeKey(Unit, key.Values);
        var document = FindOne(identity);
        return document is null ? null : MongoDocumentMapper.DecodeEntry(Unit, document, Version(identity));
    }

    public MongoWriteOutcome Insert(MongoStorageValues values, MongoWriteOptions? options = null) =>
        Mutate(values, options, MutationKind.Insert);

    public MongoWriteOutcome Update(MongoStorageValues values, MongoWriteOptions? options = null) =>
        Mutate(values, options, MutationKind.Update);

    public MongoWriteOutcome Upsert(MongoStorageValues values, MongoWriteOptions? options = null) =>
        Mutate(values, options, MutationKind.Upsert);

    public MongoWriteOutcome Delete(MongoStorageKey key, MongoWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        ThrowIfDisposed();
        return ExecuteWithTransactionIfNeeded(transactional => transactional.DeleteCore(key, options));
    }

    internal void Close() => disposed = true;

    private MongoWriteOutcome Mutate(MongoStorageValues values, MongoWriteOptions? options, MutationKind kind)
    {
        ArgumentNullException.ThrowIfNull(values);
        ThrowIfDisposed();
        return ExecuteWithTransactionIfNeeded(transactional => transactional.MutateCore(values, options, kind));
    }

    private MongoWriteOutcome MutateCore(MongoStorageValues values, MongoWriteOptions? options, MutationKind kind)
    {
        var keyValues = values.Values;
        var identity = MongoDocumentMapper.EncodeKey(Unit, keyValues);
        var existing = FindOne(identity);
        var existingVersion = Version(identity);

        if (kind == MutationKind.Insert && existing is not null)
            return new MongoWriteOutcome(MongoWriteOutcomeStatus.UniqueViolation, existingVersion);
        if (kind == MutationKind.Update && existing is null)
            return new MongoWriteOutcome(MongoWriteOutcomeStatus.NotFound);
        if (!ConcurrencyAllows(existing, existingVersion, options, kind))
            return new MongoWriteOutcome(MongoWriteOutcomeStatus.ConcurrencyConflict, existingVersion);

        var document = MongoDocumentMapper.EncodeDocument(
            Unit,
            values.Values,
            identity,
            existing,
            column => NextSequence(column));
        try
        {
            var filter = new BsonDocument("_id", identity);
            if (kind == MutationKind.Insert)
                InsertOne(document);
            else if (kind == MutationKind.Update)
                ReplaceOne(filter, document, isUpsert: false);
            else
                ReplaceOne(filter, document, isUpsert: true);
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Code == 11000)
        {
            return new MongoWriteOutcome(MongoWriteOutcomeStatus.UniqueViolation, existingVersion);
        }

        var version = NextVersion(existingVersion);
        PersistVersion(identity, version);
        var status = kind switch
        {
            MutationKind.Insert => MongoWriteOutcomeStatus.Inserted,
            MutationKind.Update => MongoWriteOutcomeStatus.Updated,
            _ => MongoWriteOutcomeStatus.Upserted
        };
        return new MongoWriteOutcome(status, version);
    }

    private MongoWriteOutcome DeleteCore(MongoStorageKey key, MongoWriteOptions? options)
    {
        var identity = MongoDocumentMapper.EncodeKey(Unit, key.Values);
        var existing = FindOne(identity);
        var existingVersion = Version(identity);
        if (existing is null)
            return new MongoWriteOutcome(MongoWriteOutcomeStatus.NotFound);
        if (!ConcurrencyAllows(existing, existingVersion, options, MutationKind.Delete))
            return new MongoWriteOutcome(MongoWriteOutcomeStatus.ConcurrencyConflict, existingVersion);

        DeleteOne(new BsonDocument("_id", identity));
        RemoveVersion(identity);
        return new MongoWriteOutcome(MongoWriteOutcomeStatus.Deleted, Unit.Concurrency == ConcurrencyDeclaration.Optimistic ? existingVersion : null);
    }

    private bool ConcurrencyAllows(
        BsonDocument? existing,
        long? currentVersion,
        MongoWriteOptions? options,
        MutationKind kind)
    {
        var expected = options?.ExpectedVersion;
        if (expected is not null && Unit.Concurrency == ConcurrencyDeclaration.None)
        {
            throw new InvalidOperationException(
                $"Storage unit '{Unit.Name}' does not declare version machinery.");
        }
        if (Unit.Concurrency != ConcurrencyDeclaration.Optimistic)
            return true;
        if (existing is null)
            return expected is null && kind is MutationKind.Insert or MutationKind.Upsert;
        return expected is not null && expected == currentVersion;
    }

    private long? NextVersion(long? current) =>
        Unit.Concurrency == ConcurrencyDeclaration.Optimistic
            ? checked((current ?? 0) + 1)
            : null;

    private long NextSequence(ColumnDefinition column) =>
        state.Sequences.FindOneAndUpdate(
            transactionSession,
            new BsonDocument("_id", Unit.Id.Value + ":" + column.Name),
            Builders<BsonDocument>.Update.Inc("value", 1),
            new FindOneAndUpdateOptions<BsonDocument>
            {
                IsUpsert = true,
                ReturnDocument = ReturnDocument.After
            })!["value"].ToInt64();

    private void PersistVersion(BsonValue identity, long? version)
    {
        if (Unit.Concurrency != ConcurrencyDeclaration.Optimistic || version is null)
            return;
        var filter = new BsonDocument("_id", MetadataId(identity));
        var document = new BsonDocument { ["_id"] = MetadataId(identity), ["version"] = version.Value };
        if (transactionSession is null)
            state.Metadata.ReplaceOne(filter, document, new ReplaceOptions { IsUpsert = true });
        else
            state.Metadata.ReplaceOne(transactionSession, filter, document, new ReplaceOptions { IsUpsert = true });
    }

    private long? Version(BsonValue identity)
    {
        if (Unit.Concurrency != ConcurrencyDeclaration.Optimistic)
            return null;
        var filter = new BsonDocument("_id", MetadataId(identity));
        var metadata = transactionSession is null
            ? state.Metadata.Find(filter).FirstOrDefault()
            : state.Metadata.Find(transactionSession, filter).FirstOrDefault();
        return metadata is null ? null : metadata.GetValue("version", 0).ToInt64();
    }

    private void RemoveVersion(BsonValue identity)
    {
        if (Unit.Concurrency == ConcurrencyDeclaration.Optimistic)
        {
            var filter = new BsonDocument("_id", MetadataId(identity));
            if (transactionSession is null)
                state.Metadata.DeleteOne(filter);
            else
                state.Metadata.DeleteOne(transactionSession, filter);
        }
    }

    private BsonDocument? FindOne(BsonValue identity) =>
        transactionSession is null
            ? collection.Find(new BsonDocument("_id", identity)).FirstOrDefault()
            : collection.Find(transactionSession, new BsonDocument("_id", identity)).FirstOrDefault();

    private void InsertOne(BsonDocument document)
    {
        if (transactionSession is null)
            collection.InsertOne(document);
        else
            collection.InsertOne(transactionSession, document);
    }

    private void ReplaceOne(BsonDocument filter, BsonDocument document, bool isUpsert)
    {
        var options = new ReplaceOptions { IsUpsert = isUpsert };
        if (transactionSession is null)
            collection.ReplaceOne(filter, document, options);
        else
            collection.ReplaceOne(transactionSession, filter, document, options);
    }

    private void DeleteOne(BsonDocument filter)
    {
        if (transactionSession is null)
            collection.DeleteOne(filter);
        else
            collection.DeleteOne(transactionSession, filter);
    }

    private BsonValue MetadataId(BsonValue identity) => new BsonDocument
    {
        ["unit"] = Unit.Id.Value,
        ["scope"] = Access.Scope?.Value ?? "<global>",
        ["key"] = identity
    };

    private T ExecuteWithTransactionIfNeeded<T>(Func<MongoStorageSession, T> operation)
    {
        if (transactionSession is not null)
            return operation(this);
        if (!Unit.Columns.Any(column => column.Generation == ColumnGeneration.ProviderSequence))
            return operation(this);

        state.Context.RequireTransactions("ProviderSequence");
        using var session = state.Context.StartSession();
        session.StartTransaction();
        var transactional = new MongoStorageSession(state, applied, Access, collection, session);
        try
        {
            var result = operation(transactional);
            session.CommitTransaction();
            transactional.Close();
            return result;
        }
        catch
        {
            session.AbortTransaction();
            transactional.Close();
            throw;
        }
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(MongoStorageSession));
    }
}

internal sealed class MongoUnitOfWork : IMongoUnitOfWork
{
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
        MongoStorageAccess access)
    {
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
        var session = new MongoStorageSession(state, applied.Applied, access, applied.Collection, this.session);
        sessions.Add(session);
        return session;
    }

    public void Commit()
    {
        ThrowIfTerminal();
        try
        {
            session.CommitTransaction();
            terminal = true;
            CloseSessions();
        }
        finally
        {
            session.Dispose();
        }
    }

    public void Rollback()
    {
        ThrowIfTerminal();
        try
        {
            session.AbortTransaction();
            terminal = true;
            CloseSessions();
        }
        finally
        {
            session.Dispose();
        }
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

    internal static BsonDocument EncodeDocument(
        StorageUnit unit,
        IReadOnlyDictionary<string, object?> values,
        BsonValue identity,
        BsonDocument? existing,
        Func<ColumnDefinition, long> nextSequence)
    {
        var known = unit.Columns.Select(column => column.Name).ToHashSet(StringComparer.Ordinal);
        var unknown = values.Keys.FirstOrDefault(key => !known.Contains(key));
        if (unknown is not null)
            throw new ArgumentException($"Column '{unknown}' is not declared by '{unit.Name}'.", nameof(values));

        var document = new BsonDocument("_id", identity);
        foreach (var column in unit.Columns)
        {
            var isPresent = values.TryGetValue(column.Name, out var value);
            if (column.Generation == ColumnGeneration.ProviderSequence)
            {
                if (isPresent && existing is null)
                    throw new ArgumentException($"ProviderSequence column '{column.Name}' is assigned by MongoDB and cannot be supplied.", nameof(values));
                var generated = existing?.GetValue(column.Name, BsonNull.Value) ?? new BsonInt64(nextSequence(column));
                if (isPresent && existing is not null &&
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
                    !isPresent && existing is not null && existing.TryGetValue(column.Name, out var previous)
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
            values[column.Name] = document.TryGetValue(column.Name, out var value)
                ? MongoValueCodec.Decode(value, column)
                : null;
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
        unit.SchemaVersion,
        string.Join("|", unit.Columns.Select(Column)),
        string.Join("|", unit.DerivedColumns.Select(column =>
            string.Join("|", column.Name, column.SourceColumn, column.Projection))),
        string.Join("|", unit.Indexes.Select(Index)));

    internal static bool ColumnEquals(ColumnDefinition left, ColumnDefinition right) =>
        string.Equals(Column(left), Column(right), StringComparison.Ordinal);

    internal static bool IndexEquals(IndexDefinition left, IndexDefinition right) =>
        string.Equals(Index(left), Index(right), StringComparison.Ordinal);

    private static string Column(ColumnDefinition column) => string.Join("|",
        column.Name, column.Type, column.IsNullable, column.MaxLength, column.Precision,
        column.Scale, column.Collation, column.Generation,
        column.Default is null ? "default:absent" : "default:present:" + column.Default.Value);

    private static string Index(IndexDefinition index) => string.Join("|",
        index.Name, index.IsUnique, index.MissingValues, index.SchemaVersion,
        string.Join(",", index.Columns.Select(column => column.Column + ":" + column.Direction)));
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
            Columns = index.Columns.Select(column => column with { }).ToArray()
        }).ToArray()
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
