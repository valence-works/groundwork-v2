using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Query.Model;
using Groundwork.Substrate.Mongo;
using Groundwork.Testing;
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

    public ProviderFit ProviderSequenceFit => state.Context.SupportsTransactions()
        ? new ProviderFit.Supported()
        : new ProviderFit.Unsupported([MongoCapabilities.ProviderSequence]);

    /// <summary>Provides read-only access to the native database for catalog/evidence tests.</summary>
    public IMongoDatabase Database => state.Context.Database;

    public MongoSchemaAdmissionReport InspectSchema(StorageUnit unit, MongoStorageAccess access)
    {
        ThrowIfDisposed();
        var applied = state.Resolve(unit, access);
        return MongoSchemaCoordinator.InspectAdmission(state, applied, access);
    }

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
        declaration = SearchKeyProjection.Expand(declaration);
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
                $"Storage unit '{declaration.Name}' differs from the applied MongoDB schema, including its folded search-key algorithm identity. Apply the exact schema and rebuild the derived search-key column before opening a session.");
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
                $"Storage unit '{requested.Name}' differs from the applied MongoDB schema, including its folded search-key algorithm identity. Apply the exact schema and rebuild the derived search-key column before opening a session.");
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
        desired = SearchKeyProjection.Expand(desired);
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
        desired = SearchKeyProjection.Expand(desired);
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
        var changes = DiffForApply(desired, current?.Declaration, actual, !exists);
        BackfillSearchKeys(collection, desired, current?.Declaration);
        // A new unique folded index must see the fully backfilled key values. Creating it
        // before projection would treat every legacy document as null and either admit an
        // invalid index or fail before the actual duplicate-fold collision is observable.
        CreateIndexes(collection, desired, actual, changes);
        PersistSchemaMetadata(desired);
        state.Remember(desired);
        return new MongoSchemaApplyResult(new MongoSchemaDiff(changes), changes.Count != 0);
    }

    private static void BackfillSearchKeys(
        IMongoCollection<BsonDocument> collection,
        StorageUnit desired,
        StorageUnit? previous)
    {
        var previousDerived = previous?.DerivedColumns.ToDictionary(column => column.Name, StringComparer.Ordinal) ?? [];
        var pending = desired.DerivedColumns.Where(column =>
            !previousDerived.TryGetValue(column.Name, out var prior) || prior != column).ToArray();
        if (pending.Length == 0)
            return;

        var columns = desired.Columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
        foreach (var document in collection.Find(new BsonDocument()).ToEnumerable())
        {
            var updates = new BsonDocument();
            foreach (var derived in pending)
            {
                var source = columns[derived.SourceColumn];
                var hidden = columns[derived.Name];
                var value = document.TryGetValue(source.Name, out var stored)
                    ? MongoValueCodec.Decode(stored, source)
                    : null;
                var projected = SearchKeyProjection.Populate(desired,
                    new Dictionary<string, object?>(StringComparer.Ordinal) { [source.Name] = value });
                projected.TryGetValue(derived.Name, out var searchKey);
                updates[hidden.Name] = MongoValueCodec.Encode(searchKey, hidden);
            }

            if (updates.ElementCount != 0)
                collection.UpdateOne(
                    BuildBackfillFilter(document, pending),
                    new BsonDocument("$set", updates));
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

        return state.Context.Database.GetCollection<BsonDocument>(CollectionName(applied, access));
    }

    /// <summary>
    /// Reads Mongo's actual collection/index catalog. This is deliberately inspect-only: missing
    /// or changed indexes do not make Mongo startup fatal, while missing/invalid declared fields do.
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

            var expectedType = MongoValueCodec.GetBsonTypeName(column);
            var acceptedValues = new BsonArray
            {
                new BsonDocument(column.Name,
                    new BsonDocument("$type", expectedType))
            };
            if (column.IsNullable)
                acceptedValues.Add(new BsonDocument(column.Name, BsonNull.Value));
            var wrongType = collection.Find(new BsonDocument("$and", new BsonArray
                {
                    new BsonDocument(column.Name, new BsonDocument("$exists", true)),
                    new BsonDocument("$nor", acceptedValues)
                }))
                .Limit(1)
                .Any();
            if (wrongType)
            {
                columnDrift.Add(new SchemaRefusal(
                    "GW-RUNTIME-001",
                    $"Physical MongoDB column '{column.Name}' contains a value whose BSON type does not match '{expectedType}'.",
                    $"columns.{column.Name}.type"));
            }
        }

        var metadata = state.Metadata.Find(new BsonDocument("_id", "schema:" + applied.Declaration.Id.Value))
            .FirstOrDefault();
        if (applied.Declaration.DerivedColumns.Count != 0)
        {
            var persisted = metadata is not null &&
                metadata.TryGetValue("derived", out var derived) && derived.IsBsonArray
                ? derived.AsBsonArray
                .OfType<BsonDocument>()
                .ToDictionary(item => item["name"].AsString,
                    item => item["algorithmId"].AsString,
                    StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
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
        var indexDrift = new List<SchemaRefusal>();
        foreach (var expected in applied.Declaration.Indexes)
        {
            var actual = actualIndexes.FirstOrDefault(index =>
                string.Equals(index.Name, expected.Name, StringComparison.Ordinal));
            if (actual is null)
            {
                indexDrift.Add(new SchemaRefusal(
                    "GW-RUNTIME-002",
                    $"Physical MongoDB collection is missing declared index '{expected.Name}'.",
                    $"indexes.{expected.Name}"));
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
                indexDrift.Add(new SchemaRefusal(
                    "GW-RUNTIME-002",
                    $"Physical MongoDB index '{expected.Name}' differs in key order, direction, uniqueness, or partial filter.",
                    $"indexes.{expected.Name}"));
            }
        }

        return new MongoSchemaAdmissionReport(applied.Declaration.Id, columnDrift, indexDrift);
    }

    private static string ProjectionAlgorithmId(DerivedColumnDefinition definition) => definition.AlgorithmId ?? definition.Projection switch
    {
        PortableProjection.UnicodeFold => PortableStringComparison.UnicodeOrdinalIgnoreCaseAlgorithmId,
        PortableProjection.BoundarySearchKey => PortableStringComparison.SearchKeyAlgorithmId,
        PortableProjection.Sha256 => PortableStringComparison.LookupHashAlgorithmId,
        _ => throw new ArgumentOutOfRangeException(nameof(definition), definition.Projection, null)
    };

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
        IReadOnlyList<MongoProviderIndex> actual,
        IReadOnlyList<MongoSchemaChange>? changes = null)
    {
        var rebuilds = (changes ?? [])
            .Where(change => change.Kind == MongoSchemaChangeKind.RebuildIndex)
            .Select(change => change.Identity)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var index in actual.Where(index => rebuilds.Contains(index.Name)))
            collection.Indexes.DropOne(index.Name);

        foreach (var index in unit.Indexes)
        {
            if (!rebuilds.Contains(index.Name) &&
                actual.Any(existing => string.Equals(existing.Name, index.Name, StringComparison.Ordinal)))
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
            {
                if (SearchKeyProjection.IsIndexRetarget(previous, index, desired.DerivedColumns))
                    changes.Add(new MongoSchemaChange(MongoSchemaChangeKind.RebuildIndex, index.Name));
                else
                    throw new MongoSchemaConflictException($"Index '{index.Name}' changed non-additively.");
            }
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
            ? [
                .. desired.Indexes
                    .Where(index => actual.Any(native => native.Name == index.Name &&
                        IsIndexRetarget(native, index, desired.DerivedColumns)))
                    .Select(index => new MongoSchemaChange(MongoSchemaChangeKind.RebuildIndex, index.Name)),
                .. desired.Indexes
                    .Where(index => actual.All(native => native.Name != index.Name))
                    .Select(index => new MongoSchemaChange(MongoSchemaChangeKind.CreateIndex, index.Name))
            ]
            : BuildChanges(desired, current, actual);
    }

    private static bool IsIndexRetarget(
        MongoProviderIndex actual,
        IndexDefinition desired,
        IReadOnlyList<DerivedColumnDefinition> derived)
    {
        var previous = new IndexDefinition
        {
            Name = actual.Name,
            Columns = actual.Columns
                .Select(column => new IndexColumn(column.Column, column.Direction))
                .ToArray(),
            IsUnique = actual.IsUnique,
            MissingValues = actual.MissingValues,
            SchemaVersion = actual.SchemaVersion
        };
        return SearchKeyProjection.IsIndexRetarget(previous, desired, derived);
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
            ["fingerprint"] = SchemaIdentity.Fingerprint(unit),
            ["derived"] = new BsonArray(unit.DerivedColumns.Select(column => new BsonDocument
            {
                ["name"] = column.Name,
                ["algorithmId"] = ProjectionAlgorithmId(column)
            }))
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
        ConcurrencyDeclaration.ValidateDeclaration(unit);
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

internal sealed partial class MongoStorageSession : IMongoStorageSession, IBatchedStorageSession
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

    public QueryMaterializedResult Query(QueryRequest request, QueryRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        if (!string.Equals(request.Table.Value, Unit.Name, StringComparison.Ordinal))
            throw new ArgumentException($"Query table '{request.Table.Value}' does not match session unit '{Unit.Name}'.", nameof(request));
        var suppliedOptions = options ?? QueryRenderOptions.Default;
        var executionSource = Access.Policy == ScopePolicy.Scoped
            ? QueryRequestExecution.WithProviderPredicate(request, request.Where,
                QueryRequestExecution.ScopeBindingDiscriminator(Access.Scope!.Value))
            : request;
        var renderOptions = suppliedOptions.WithIdentityTieBreaks(Unit.Key.Columns.Select(QueryColumn).Where(column => column is not null)!.Select(column => column!)) with
        {
            Indexes = SearchKeyQueryMappings.RetargetIndexes(Unit, suppliedOptions.Indexes)
                .Select(index => index.WithColumnTypes(Unit.Columns.ToDictionary(column => column.Name, column => QueryTypeOf(column.Type), StringComparer.Ordinal))).ToImmutableArray(),
            PhysicalIndexNames = Unit.Indexes.ToDictionary(index => index.Name, index => index.Name, StringComparer.Ordinal),
            SearchKeyColumns = SearchKeyQueryMappings.For(Unit)
        };
        var executionRequest = QueryRequestExecution.ForPage(executionSource, renderOptions);
        var command = new MongoQueryRenderer().Render(executionRequest, renderOptions, collection.CollectionNamespace.CollectionName);
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
                documents = collection.Aggregate(transactionSession, dataPipeline, new AggregateOptions { Hint = command.Hint }).ToList();
                documents.AddRange(collection.Aggregate(transactionSession, countPipeline, new AggregateOptions { Hint = command.Hint }).ToList());
            }
            else
            {
                var pipeline = PipelineDefinition<BsonDocument, BsonDocument>.Create(command.Pipeline);
                documents = (transactionSession is null
                    ? collection.Aggregate(pipeline, new AggregateOptions { Hint = command.Hint })
                    : collection.Aggregate(transactionSession, pipeline, new AggregateOptions { Hint = command.Hint })).ToList();
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
            documents = (transactionSession is null
                ? collection.FindSync(command.Filter, findOptions)
                : collection.FindSync(transactionSession, command.Filter, findOptions)).ToList();
        }

        var rows = documents.Select(document =>
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var column in Unit.Columns)
            {
                if (MongoDocumentMapper.IsSystemOwnedToken(Unit, column))
                    continue;
                if (document.TryGetValue(column.Name, out var value))
                    row[column.Name] = MongoValueCodec.Decode(value, column);
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
        AssertExplainPlan(command, renderOptions);
        return QueryResultMaterializer.Materialize(
            executionSource,
            renderOptions,
            rows,
            command.ExpectedIndex,
            command.Hint is not null,
            sourceIncludesRequestedOffset: true,
            sourceIncludesContinuation: true);
    }

    public AggregationResult Aggregate(AggregationQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ThrowIfDisposed();
        var profile = AggregationProfileValidator.ResolveOrThrow(Unit, query.ProfileName);
        AggregationProfileValidator.Validate(Unit, profile);
        if (Access.Policy != ScopePolicy.Global || query.PostPredicate is not null)
            return AggregationSessionExecutor.Execute(Unit, request => Query(request), query);
        return ExecuteNativeAggregation(profile, query);
    }

    private void AssertExplainPlan(MongoQueryCommand query, QueryRenderOptions options)
    {
        var logicalIndex = query.ExpectedIndex;
        if (query.IsMatchNone || !ExplainAssertTestMode.ShouldAssert(logicalIndex)) return;
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
        var explain = state.Context.Database.RunCommand(new BsonDocumentCommand<BsonDocument>(explainCommand));
        var rawPlan = explain.ToJson(new JsonWriterSettings { Indent = true });
        var physicalIndex = options.ResolvePhysicalIndexName(logicalIndex!);
        ExplainAssertTestMode.AssertChosenIndex(
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

    public MongoStoredEntry? Read(MongoStorageKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        ThrowIfDisposed();
        var identity = MongoDocumentMapper.EncodeKey(Unit, key.Values);
        var document = FindOne(identity);
        return document is null ? null : MongoDocumentMapper.DecodeEntry(Unit, document, Version(identity, document));
    }

    public MongoWriteOutcome Insert(MongoStorageValues values, MongoWriteOptions? options = null)
    {
        WritePreconditionValidator.Validate(Unit, WriteOperation.Insert, ToTestingOptions(options));
        WritePreconditionValidator.ValidateSystemOwnedValues(Unit, values.Values);
        return Mutate(values, options, MutationKind.Insert);
    }

    public MongoWriteOutcome Update(MongoStorageValues values, MongoWriteOptions? options = null)
    {
        WritePreconditionValidator.Validate(Unit, WriteOperation.Update, ToTestingOptions(options));
        WritePreconditionValidator.ValidateSystemOwnedValues(Unit, values.Values);
        return Mutate(values, options, MutationKind.Update);
    }

    public MongoWriteOutcome Upsert(MongoStorageValues values, MongoWriteOptions? options = null)
    {
        WritePreconditionValidator.Validate(Unit, WriteOperation.Upsert, ToTestingOptions(options));
        WritePreconditionValidator.ValidateSystemOwnedValues(Unit, values.Values);
        return Mutate(values, options, MutationKind.Upsert);
    }

    public MongoWriteOutcome ConditionalUpsert(MongoStorageValues values, MongoWriteOptions? options = null)
    {
        WritePreconditionValidator.Validate(Unit, WriteOperation.ConditionalUpsert, ToTestingOptions(options));
        WritePreconditionValidator.ValidateSystemOwnedValues(Unit, values.Values);
        return ConditionalUpsertCore(values, options);
    }

    public IReadOnlyList<RowWriteOutcome> ApplyBatch(IReadOnlyList<RowWrite> writes)
        => ApplyBatch(writes, exactOutcomes: false);

    public IReadOnlyList<RowWriteOutcome> ApplyBatch(IReadOnlyList<RowWrite> writes, bool exactOutcomes)
    {
        ArgumentNullException.ThrowIfNull(writes);
        ThrowIfDisposed();
        return ExecuteWithTransactionIfNeeded(transactional => transactional.ApplyBatchCore(writes, exactOutcomes));
    }

    private IReadOnlyList<RowWriteOutcome> ApplyBatchCore(IReadOnlyList<RowWrite> writes, bool exactOutcomes)
    {
        if (writes.Count == 0)
            return [];
        if (writes.Any(write => write.Mode != RowWriteMode.Upsert ||
                               write.Options.Precondition.Kind != WritePreconditionKind.Unconditional ||
                               write.Values is null ||
                               Unit.Columns.Any(column => column.Generation == ColumnGeneration.ProviderSequence)))
            return ApplyBatchFallback(writes);

        // Keep the logical RowWrite for outcome correlation and physicalize exactly once for
        // the native command. Fallback and exact paths delegate to single-row methods, which
        // perform their own physicalization.
        var physicalWrites = writes.Select(write => write.PopulateSearchKeyValues()).ToArray();

        // BulkWrite can acknowledge each model but cannot identify whether each
        // upsert inserted or updated. CommitWithOutcomes requests that exact evidence;
        // use the native single-row conditional primitive in that mode.
        if (exactOutcomes)
        {
            return writes.Zip(physicalWrites, (write, physical) =>
                new RowWriteOutcome(write, ToTesting(
                    ExactOutcomeUpsert(
                        new MongoStorageValues(physical.Values!.Values),
                        ToNative(write.Options))))).ToArray();
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
                ? MongoDocumentMapper.EncodeDocument(
                    Unit, write.Values.Values, identity, existing: null, _ =>
                        throw new InvalidOperationException("ProviderSequence must use the fallback batch path."))
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

        writes[0].Options.Observer?.Observe(new WritePathEvent(
            "mongodb.batch-write",
            "MongoDB.BulkWrite(UpdateOne upsert:eligible-per-row ordered:false)",
            IsProbe: false));
        try
        {
            var result = transactionSession is null
                ? collection.BulkWrite(models, new BulkWriteOptions { IsOrdered = false })
                : collection.BulkWrite(transactionSession, models, new BulkWriteOptions { IsOrdered = false });
            ThrowIfIncompleteUpsertWasNotApplied(result, incompleteWrites, []);
            return writes.Select(write => new RowWriteOutcome(write,
                new WriteOutcome(WriteOutcomeStatus.Upserted))).ToArray();
        }
        catch (MongoBulkWriteException<BsonDocument> exception)
        {
            var failures = exception.WriteErrors.ToDictionary(error => error.Index, error => error);
            ThrowIfIncompleteUpsertWasNotApplied(exception.Result, incompleteWrites, failures.Keys);
            return writes.Select((write, index) =>
                new RowWriteOutcome(write, failures.TryGetValue(index, out var error)
                    ? new WriteOutcome(WriteOutcomeStatus.UniqueViolation, null, ExtractIndexName(error.Message))
                    : new WriteOutcome(WriteOutcomeStatus.Upserted))).ToArray();
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

    private IReadOnlyList<RowWriteOutcome> ApplyBatchFallback(IReadOnlyList<RowWrite> writes) =>
        writes.Select(write => new RowWriteOutcome(write, write.Mode switch
        {
            RowWriteMode.Insert => ToTesting(Insert(new MongoStorageValues(write.Values!.Values), ToNative(write.Options))),
            RowWriteMode.Update => ToTesting(Update(new MongoStorageValues(write.Values!.Values), ToNative(write.Options))),
            RowWriteMode.Upsert when write.Options.Precondition.Kind == WritePreconditionKind.IfVersion => ToTesting(ConditionalUpsert(new MongoStorageValues(write.Values!.Values), ToNative(write.Options))),
            RowWriteMode.Upsert => ToTesting(Upsert(new MongoStorageValues(write.Values!.Values), ToNative(write.Options))),
            RowWriteMode.ConditionalUpsert => ToTesting(ConditionalUpsert(new MongoStorageValues(write.Values!.Values), ToNative(write.Options))),
            RowWriteMode.Delete => ToTesting(Delete(new MongoStorageKey(write.Key!.Values), ToNative(write.Options))),
            _ => throw new ArgumentOutOfRangeException(nameof(write.Mode), write.Mode, null)
        })).ToArray();

    private static MongoWriteOptions? ToNative(WriteOptions options) =>
        new() { Precondition = options.Precondition, Observer = options.Observer };

    private static WriteOutcome ToTesting(MongoWriteOutcome outcome) =>
        new((WriteOutcomeStatus)outcome.Status, outcome.Version, outcome.UniqueIndexName, outcome.GeneratedValues);

    public MongoWriteOutcome Delete(MongoStorageKey key, MongoWriteOptions? options = null)
    {
        WritePreconditionValidator.Validate(Unit, WriteOperation.Delete, ToTestingOptions(options));
        ArgumentNullException.ThrowIfNull(key);
        ThrowIfDisposed();
        return ExecuteWithTransactionIfNeeded(transactional => transactional.DeleteCore(key, options));
    }

    private static WriteOptions? ToTestingOptions(MongoWriteOptions? options) => options is null
        ? null
        : new WriteOptions { Precondition = options.Precondition, Observer = options.Observer };

    internal void Close() => disposed = true;

    private MongoWriteOutcome Mutate(
        MongoStorageValues values,
        MongoWriteOptions? options,
        MutationKind kind,
        bool exactOutcome = false)
    {
        ArgumentNullException.ThrowIfNull(values);
        ThrowIfDisposed();
        return ExecuteWithTransactionIfNeeded(transactional =>
            transactional.MutateCore(values, options, kind, exactOutcome));
    }

    private MongoWriteOutcome MutateCore(
        MongoStorageValues values,
        MongoWriteOptions? options,
        MutationKind kind,
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
            var generated = NextSequence(sequence, options?.Observer);
            copied[sequence.Name] = generated;
            values = new MongoStorageValues(copied);
            keyValues = values.Values;
            generatedValues[sequence.Name] = generated;
        }
        var identity = MongoDocumentMapper.EncodeKey(Unit, keyValues);
        if (!Unit.Concurrency.IsOptimistic)
            return MutateNoneCore(
                values,
                options,
                kind,
                identity,
                hasSequenceLocator,
                generatedValues);

        var existing = FindOne(identity);
        var existingVersion = Version(identity, existing);

        if (kind == MutationKind.Insert && existing is not null)
            return new MongoWriteOutcome(MongoWriteOutcomeStatus.UniqueViolation, existingVersion);
        if (kind == MutationKind.Update && existing is null)
            return new MongoWriteOutcome(MongoWriteOutcomeStatus.NotFound);
        if (kind == MutationKind.Upsert && hasSequenceLocator && existing is null)
            return new MongoWriteOutcome(MongoWriteOutcomeStatus.NotFound);
        if (!ConcurrencyAllows(existing, existingVersion, options, kind))
            return new MongoWriteOutcome(MongoWriteOutcomeStatus.ConcurrencyConflict, existingVersion);

        var nextVersion = NextVersion(existingVersion);
        var document = MongoDocumentMapper.EncodeDocument(
            Unit,
            keyValues,
            identity,
            existing,
            column => sequence is not null && column.Name == sequence.Name && generatedValues.TryGetValue(column.Name, out var generated)
                ? Convert.ToInt64(generated, System.Globalization.CultureInfo.InvariantCulture)
                : NextSequence(column, options?.Observer),
            preserveCreatedAt: exactOutcome,
            generatedValues: generatedValues);
        if (nextVersion is not null)
            document[MongoDocumentMapper.VersionField] = nextVersion.Value;
        var inserted = kind == MutationKind.Insert;
        try
        {
            var filter = ConcurrencyFilter(identity, existingVersion);
            if (kind == MutationKind.Insert)
                InsertOne(document);
            else if (kind == MutationKind.Update)
            {
                var result = ReplaceOne(filter, document, isUpsert: false);
                if (Unit.Concurrency.IsOptimistic && result.MatchedCount == 0)
                    return new MongoWriteOutcome(MongoWriteOutcomeStatus.ConcurrencyConflict, Version(identity));
            }
            else
            {
                if (exactOutcome && Unit.Concurrency.IsOptimistic && existing is null)
                {
                    // Use insert rather than an upsert when the caller observed no row. An
                    // upsert can match a row inserted after that observation and would then
                    // misclassify the update and reset its version token.
                    InsertOne(document);
                    inserted = true;
                }
                else
                {
                    var result = ReplaceOne(filter, document,
                        isUpsert: !hasSequenceLocator &&
                                  (!Unit.Concurrency.IsOptimistic || existing is null));
                    inserted = result.UpsertedId is not null;
                    if (Unit.Concurrency.IsOptimistic && result.MatchedCount == 0)
                        return new MongoWriteOutcome(MongoWriteOutcomeStatus.ConcurrencyConflict, Version(identity));
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

        PersistVersion(identity, nextVersion);
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

    private MongoWriteOutcome MutateNoneCore(
        MongoStorageValues values,
        MongoWriteOptions? options,
        MutationKind kind,
        BsonValue identity,
        bool hasSequenceLocator,
        IReadOnlyDictionary<string, object?> generatedValues)
    {
        var missingRequired = Unit.Columns.FirstOrDefault(column =>
            column.Generation == ColumnGeneration.Supplied &&
            !SearchKeyProjection.IsProviderOwnedColumn(column.Name) &&
            !column.IsNullable &&
            column.Default is null &&
            !values.Values.ContainsKey(column.Name));
        var canInsert = missingRequired is null && !hasSequenceLocator;
        var document = kind == MutationKind.Insert || canInsert
            ? MongoDocumentMapper.EncodeDocument(
                Unit,
                values.Values,
                identity,
                existing: null,
                column => NextSequence(column, options?.Observer),
                generatedValues: generatedValues)
            : null;
        options?.Observer?.Observe(new WritePathEvent(
            "mongodb.none-write",
            kind switch
            {
                MutationKind.Insert => "MongoDB.InsertOne",
                MutationKind.Update => "MongoDB.UpdateOne(upsert:false)",
                _ => $"MongoDB.UpdateOne(upsert:{canInsert.ToString().ToLowerInvariant()})"
            },
            IsProbe: false));
        try
        {
            var filter = new BsonDocument("_id", identity);
            if (kind == MutationKind.Insert)
            {
                InsertOne(document!);
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
            var result = transactionSession is null
                ? collection.UpdateOne(filter, update, updateOptions)
                : collection.UpdateOne(transactionSession, filter, update, updateOptions);
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
    }

    private MongoWriteOutcome ConditionalUpsertCore(
        MongoStorageValues values,
        MongoWriteOptions? options)
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

        return ExecuteWithTransactionIfNeeded(transactional =>
            transactional.ConditionalUpsertOne(values, options));
    }

    private MongoWriteOutcome ConditionalUpsertOne(
        MongoStorageValues values,
        MongoWriteOptions? options)
    {
        var identity = MongoDocumentMapper.EncodeKey(Unit, values.Values);
        var missingRequired = MissingRequiredColumn(values.Values);
        var canInsert = missingRequired is null;
        var document = canInsert
            ? MongoDocumentMapper.EncodeDocument(
                Unit,
                values.Values,
                identity,
                existing: null,
                column => NextSequence(column, options?.Observer))
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
        options?.Observer?.Observe(new WritePathEvent("mongodb.conditional-upsert", commandDescription, IsProbe: false));
        try
        {
            var result = transactionSession is null
                ? collection.UpdateOne(filter, update, new UpdateOptions { IsUpsert = isUpsert })
                : collection.UpdateOne(transactionSession, filter, update, new UpdateOptions { IsUpsert = isUpsert });
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
    }

    private MongoWriteOutcome ExactOutcomeUpsert(
        MongoStorageValues values,
        MongoWriteOptions? options)
    {
        var identity = MongoDocumentMapper.EncodeKey(Unit, values.Values);
        var missingRequired = MissingRequiredColumn(values.Values);
        var canInsert = missingRequired is null;
        var document = canInsert
            ? MongoDocumentMapper.EncodeDocument(
                Unit,
                values.Values,
                identity,
                existing: null,
                column => NextSequence(column, options?.Observer))
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

        options?.Observer?.Observe(new WritePathEvent(
            "mongodb.exact-batch-upsert",
            $"MongoDB.FindOneAndUpdate(upsert:{canInsert.ToString().ToLowerInvariant()}; return=before)",
            IsProbe: false));
        try
        {
            var filter = new BsonDocument("_id", identity);
            var findOptions = new FindOneAndUpdateOptions<BsonDocument>
            {
                IsUpsert = canInsert,
                ReturnDocument = ReturnDocument.Before
            };
            var before = transactionSession is null
                ? collection.FindOneAndUpdate(filter, update, findOptions)
                : collection.FindOneAndUpdate(transactionSession, filter, update, findOptions);
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

    private MongoWriteOutcome DeleteCore(MongoStorageKey key, MongoWriteOptions? options)
    {
        var identity = MongoDocumentMapper.EncodeKey(Unit, key.Values);
        if (!Unit.Concurrency.IsOptimistic)
        {
            options?.Observer?.Observe(new WritePathEvent("mongodb.none-delete", "MongoDB.DeleteOne", IsProbe: false));
            var result = DeleteOne(new BsonDocument("_id", identity));
            return result.DeletedCount == 0
                ? new MongoWriteOutcome(MongoWriteOutcomeStatus.NotFound)
                : new MongoWriteOutcome(MongoWriteOutcomeStatus.Deleted);
        }
        var existing = FindOne(identity);
        var existingVersion = Version(identity);
        if (existing is null)
            return new MongoWriteOutcome(MongoWriteOutcomeStatus.NotFound);
        if (!ConcurrencyAllows(existing, existingVersion, options, MutationKind.Delete))
            return new MongoWriteOutcome(MongoWriteOutcomeStatus.ConcurrencyConflict, existingVersion);

        DeleteOne(new BsonDocument("_id", identity));
        RemoveVersion(identity);
        return new MongoWriteOutcome(MongoWriteOutcomeStatus.Deleted, Unit.Concurrency.IsOptimistic ? existingVersion : null);
    }

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

    private long NextSequence(ColumnDefinition column, IWritePathObserver? observer = null)
    {
        // Keep sequence allocation visible to the same diagnostic seam as the write.
        // ConditionalUpsert rejects this path before it can occur, preserving its
        // one-command contract; ordinary generated writes still report the extra
        // provider command instead of hiding it from accounting.
        observer?.Observe(new WritePathEvent(
            "mongodb.provider-sequence",
            "MongoDB.FindOneAndUpdate(sequence)",
            IsProbe: false));
        return state.Sequences.FindOneAndUpdate(
            transactionSession,
            new BsonDocument("_id", Unit.Id.Value + ":" + column.Name),
            Builders<BsonDocument>.Update.Inc("value", 1),
            new FindOneAndUpdateOptions<BsonDocument>
            {
                IsUpsert = true,
                ReturnDocument = ReturnDocument.After
            })!["value"].ToInt64();
    }

    private void PersistVersion(BsonValue identity, long? version)
    {
        if (!Unit.Concurrency.IsOptimistic || version is null)
            return;
        var filter = new BsonDocument("_id", MetadataId(identity));
        var document = new BsonDocument { ["_id"] = MetadataId(identity), ["version"] = version.Value };
        if (transactionSession is null)
            state.Metadata.ReplaceOne(filter, document, new ReplaceOptions { IsUpsert = true });
        else
            state.Metadata.ReplaceOne(transactionSession, filter, document, new ReplaceOptions { IsUpsert = true });
    }

    private long? Version(BsonValue identity, BsonDocument? document = null)
    {
        if (!Unit.Concurrency.IsOptimistic)
            return null;
        if (document is not null && document.TryGetValue(MongoDocumentMapper.VersionField, out var version))
            return version.ToInt64();
        var filter = new BsonDocument("_id", MetadataId(identity));
        var metadata = transactionSession is null
            ? state.Metadata.Find(filter).FirstOrDefault()
            : state.Metadata.Find(transactionSession, filter).FirstOrDefault();
        return metadata is null ? null : metadata.GetValue("version", 0).ToInt64();
    }

    private void RemoveVersion(BsonValue identity)
    {
        if (Unit.Concurrency.IsOptimistic)
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

    private ReplaceOneResult ReplaceOne(BsonDocument filter, BsonDocument document, bool isUpsert)
    {
        var options = new ReplaceOptions { IsUpsert = isUpsert };
        if (transactionSession is null)
            return collection.ReplaceOne(filter, document, options);
        return collection.ReplaceOne(transactionSession, filter, document, options);
    }

    private DeleteResult DeleteOne(BsonDocument filter)
    {
        if (transactionSession is null)
            return collection.DeleteOne(filter);
        return collection.DeleteOne(transactionSession, filter);
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
        MongoException? lastTransientFailure = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var session = state.Context.StartSession();
            session.StartTransaction();
            var transactional = new MongoStorageSession(state, applied, Access, collection, session);
            var operationCompleted = false;
            try
            {
                var result = operation(transactional);
                operationCompleted = true;
                CommitTransactionWithRetry(session);
                return result;
            }
            catch (MongoException exception) when (
                exception.HasErrorLabel("TransientTransactionError") && attempt < 4)
            {
                lastTransientFailure = exception;
            }
            finally
            {
                if (!operationCompleted && session.IsInTransaction)
                {
                    try { session.AbortTransaction(); }
                    catch (MongoException) { }
                }
                transactional.Close();
            }
        }

        if (lastTransientFailure is not null)
            throw lastTransientFailure;
        throw new InvalidOperationException("MongoDB ProviderSequence transaction retries were exhausted.");
    }

    internal static void CommitTransactionWithRetry(IClientSessionHandle session)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                session.CommitTransaction();
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
            MongoStorageSession.CommitTransactionWithRetry(session);
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

    internal static BsonDocument EncodeDocument(
        StorageUnit unit,
        IReadOnlyDictionary<string, object?> values,
        BsonValue identity,
        BsonDocument? existing,
        Func<ColumnDefinition, long> nextSequence,
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
                    (generatedInternally ? MongoValueCodec.Encode(generatedValues![column.Name], column) : new BsonInt64(nextSequence(column)));
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
        unit.SchemaVersion,
        string.Join("|", unit.Columns.Select(Column)),
        string.Join("|", unit.DerivedColumns.Select(column =>
            string.Join("|", column.Name, column.SourceColumn, column.Projection, column.AlgorithmId))),
        string.Join("|", unit.Indexes.Select(Index)),
        string.Join("|", unit.AggregationProfiles.Select(AggregationProfile)));

    internal static bool ColumnEquals(ColumnDefinition left, ColumnDefinition right) =>
        string.Equals(Column(left), Column(right), StringComparison.Ordinal);

    internal static bool IndexEquals(IndexDefinition left, IndexDefinition right) =>
        string.Equals(Index(left), Index(right), StringComparison.Ordinal);

    private static string Column(ColumnDefinition column) => string.Join("|",
        column.Name, column.Type, column.IsNullable, column.MaxLength, column.Precision,
        column.Scale,
        column.Type == PortableType.String && (column.Collation is null or PortableCollation.Ordinal)
            ? PortableCollation.Ordinal
            : column.Collation,
        column.Generation,
        column.Default is null ? "default:absent" : "default:present:" + column.Default.Value);

    private static string Index(IndexDefinition index) => string.Join("|",
        index.Name, index.IsUnique, index.MissingValues, index.SchemaVersion,
        string.Join(",", index.Columns.Select(column => column.Column + ":" + column.Direction)));

    private static string AggregationProfile(AggregationProfile profile) => string.Join("|",
        profile.Name,
        string.Join(",", profile.GroupByColumns.OrderBy(column => column, StringComparer.Ordinal)),
        string.Join(",", profile.Aggregates.Select(Aggregate).OrderBy(value => value, StringComparer.Ordinal)),
        string.Join(",", profile.AllowedPredicates.Select(allowance => allowance.Alias + ":" +
            string.Join("+", allowance.SupportedPredicates.OrderBy(value => value)))),
        profile.MaxGroups,
        profile.MaxInputRows);

    private static string Aggregate(Groundwork.Kernel.Aggregate aggregate) => aggregate switch
    {
        Groundwork.Kernel.Aggregate.Min min => $"min:{min.Alias}:{min.Column}",
        Groundwork.Kernel.Aggregate.Max max => $"max:{max.Alias}:{max.Column}",
        Groundwork.Kernel.Aggregate.Sum sum => $"sum:{sum.Alias}:{sum.Column}",
        Groundwork.Kernel.Aggregate.SetUnion set => $"setUnion:{set.Alias}:{set.Column}:{set.MaxValues}",
        Groundwork.Kernel.Aggregate.FirstBy first => $"firstBy:{first.Alias}:{first.Column}:{first.OrderColumn}:{first.Direction}",
        _ => throw new ArgumentOutOfRangeException(nameof(aggregate))
    };
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
        }).ToArray(),
        AggregationProfiles = unit.AggregationProfiles.Select(AggregationProfileSnapshot.Capture).ToArray()
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
