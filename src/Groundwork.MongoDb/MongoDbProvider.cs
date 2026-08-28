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
        var applied = state.Resolve(unit, access);
        return MongoSchemaCoordinator.InspectAdmission(state, applied, access);
    }

    public IMongoStorageSession OpenSession(StorageUnit unit, MongoStorageAccess access, IProviderCommandObserver? observer = null)
    {
        ThrowIfDisposed();
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
        PortabilityValidator.EnsurePhysicalIdentifiers(declaration);
        ProviderOwnedColumns.ValidateLogicalDeclaration(declaration);
        declaration = SearchKeyProjection.Expand(declaration);
        AggregationProfileValidator.ValidateUnit(declaration);
        ValidateScope(declaration, access);
        lock (gate)
        {
            if (units.TryGetValue(declaration.Id, out var existing))
            {
                EnsureSameDeclaration(existing.Declaration, declaration);
                return existing;
            }
        }

        var applied = new MongoAppliedUnit(MongoDeclarationSnapshot.Clone(declaration), declaration.Name);
        _ = MongoSchemaCoordinator.CollectionName(applied, access);
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

internal sealed record MongoAppliedUnit(StorageUnit Declaration, string CollectionName);

internal sealed record MongoScopeRegistration(StorageScope Scope, string Token, string CollectionName);

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
        ProviderOwnedColumns.ValidateLogicalDeclaration(desired);
        desired = SearchKeyProjection.Expand(desired);
        AggregationProfileValidator.ValidateUnit(desired);
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
        ProviderOwnedColumns.ValidateLogicalDeclaration(desired);
        desired = SearchKeyProjection.Expand(desired);
        AggregationProfileValidator.ValidateUnit(desired);
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

    /// <summary>
    /// Rows per bulk write. MongoDB has no multi-document update that carries a different value per
    /// document, so a batch is one <c>bulkWrite</c> command rather than one command per document.
    /// </summary>
    private const int SearchKeyBackfillBatchSize = 512;

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

        // The same host-process transform the chunked data-migration runner drives, so a search key
        // written by a schema apply and one written by a resumable migration are one definition.
        var transform = new DerivedColumnTransform(desired, pending);
        var columns = desired.Columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
        var batch = new List<WriteModel<BsonDocument>>(SearchKeyBackfillBatchSize);
        foreach (var document in collection.Find(new BsonDocument()).ToEnumerable())
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var source in transform.SourceColumns)
            {
                row[source] = document.TryGetValue(source, out var stored) && !stored.IsBsonNull
                    ? MongoValueCodec.Decode(stored, columns[source])
                    : null;
            }

            var produced = transform.Transform(new DataMigrationRow(row));
            if (!produced.HasValues)
                continue;
            var updates = new BsonDocument();
            foreach (var pair in produced.Values!)
                updates[pair.Key] = MongoValueCodec.Encode(pair.Value, columns[pair.Key]);
            batch.Add(new UpdateOneModel<BsonDocument>(
                BuildBackfillFilter(document, pending),
                new BsonDocument("$set", updates)));
            if (batch.Count < SearchKeyBackfillBatchSize)
                continue;
            collection.BulkWrite(batch, new BulkWriteOptions { IsOrdered = false });
            batch.Clear();
        }

        if (batch.Count != 0)
            collection.BulkWrite(batch, new BulkWriteOptions { IsOrdered = false });
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

        var collection = state.Context.Database.GetCollection<BsonDocument>(CollectionName(applied, access));
        EnsureLedgerIndexes(state, applied.Declaration.AppendIdempotency?.LedgerName);
        EnsureLedgerIndexes(state, applied.Declaration.RetentionIdempotency?.LedgerName);
        return collection;
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

    internal static string ProjectionAlgorithmId(DerivedColumnDefinition definition) => definition.AlgorithmId ?? definition.Projection switch
    {
        PortableProjection.UnicodeFold => PortableStringComparison.UnicodeOrdinalIgnoreCaseAlgorithmId,
        PortableProjection.BoundarySearchKey => PortableStringComparison.SearchKeyAlgorithmId,
        PortableProjection.LocaleSortKey => throw new InvalidOperationException(
            $"Locale sort-key projection '{definition.Name}' requires an explicit algorithm identity."),
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

    private static void EnsureLedgerIndexes(MongoProviderState state, string? ledgerName)
    {
        if (ledgerName is null)
            return;

        var ledger = state.Operations(ledgerName);
        ledger.Indexes.CreateOne(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("unit").Ascending("committed_at"),
            new CreateIndexOptions { Name = "__groundwork_ledger_cleanup" }));
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
        var collectionName = applied.CollectionName + "__scope__" + hash;
        PortabilityValidator.EnsurePhysicalIdentifier(
            collectionName,
            "scopedCollection.name",
            maximumByteLength: 255);
        return collectionName;
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
            if (!SchemaIdentity.RetentionEquals(current.Retention, desired.Retention))
                throw new MongoSchemaConflictException(
                    $"Storage unit '{desired.Name}' changed its retention declaration non-additively.");
            if (!SchemaIdentity.RetentionIdempotencyEquals(current.RetentionIdempotency, desired.RetentionIdempotency))
                throw new MongoSchemaConflictException(
                    $"Storage unit '{desired.Name}' changed its retention idempotency declaration non-additively.");
            if (current.Scope != desired.Scope || current.Concurrency != desired.Concurrency ||
                current.Timestamps != desired.Timestamps || current.SchemaVersion != desired.SchemaVersion ||
                !SchemaIdentity.IdempotencyEquals(current.AppendIdempotency, desired.AppendIdempotency))
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

        var previousProfiles = current.AggregationProfiles.ToDictionary(profile => profile.Name, StringComparer.Ordinal);
        var desiredProfiles = desired.AggregationProfiles.ToDictionary(profile => profile.Name, StringComparer.Ordinal);
        foreach (var profile in desiredProfiles.Values)
        {
            if (!previousProfiles.TryGetValue(profile.Name, out var previous) ||
                !string.Equals(
                    AggregationProfileCanonicalization.Canonicalize(previous),
                    AggregationProfileCanonicalization.Canonicalize(profile),
                    StringComparison.Ordinal))
            {
                changes.Add(new MongoSchemaChange(MongoSchemaChangeKind.UpdateAggregationProfile, profile.Name));
            }
        }
        foreach (var previous in previousProfiles.Values)
        {
            if (!desiredProfiles.ContainsKey(previous.Name))
                changes.Add(new MongoSchemaChange(MongoSchemaChangeKind.UpdateAggregationProfile, previous.Name));
        }

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

    /// <summary>
    /// The declaration rules this in-process coordinator adds to the shared ones. The rename
    /// refusal is the only one that is specific to this path: it plans from the fingerprint in
    /// <c>__groundwork_metadata</c> rather than from the applied schema ledger, so it still cannot
    /// tell a renamed field from a new one.
    /// </summary>
    private static void ValidateDeclaration(StorageUnit unit)
    {
        MongoDeclarationRules.Validate(unit);
        // The deployment tool reads the applied schema ledger and plans a RenameColumn against it;
        // this path has no ledger to read, so declaring a diverged logical id here would read
        // documents that still store the old field as null. Deploy the rename with
        // 'groundwork apply', which carries the logical id across the rename.
        if (unit.Columns.FirstOrDefault(column =>
                !string.Equals(column.LogicalId, column.Name, StringComparison.Ordinal)) is { } renamed)
        {
            throw new InvalidOperationException(
                $"GW-SCHEMA-009 at schema.columns.{renamed.Name}.id: this in-process MongoDB schema apply does " +
                $"not read the applied schema ledger, so declaring '{renamed.Name}' under logical id " +
                $"'{renamed.LogicalId}' would read documents that still store the old field as null. Deploy the " +
                "rename with 'groundwork apply', which plans it against that ledger, or keep the physical name.");
        }
    }

    private static bool HasProviderSequence(StorageUnit unit) =>
        unit.Columns.Any(column => column.Generation == ColumnGeneration.ProviderSequence);

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
}

internal sealed partial class MongoStorageSession : IMongoStorageSession, IMongoCompareAndDeleteStorageSession, IMongoExactAppendStorageSession, IBatchedStorageSession, IRetentionStorageSession, IStorageInspectionSession, IExactRetentionStorageSession, ISetMutationStorageSession
{
    private const string HighWaterValue = "high_water";
    private readonly MongoProviderState state;
    private readonly MongoAppliedUnit applied;
    private readonly IMongoCollection<BsonDocument> collection;
    private readonly IClientSessionHandle? transactionSession;
    private readonly MongoUnitOfWork? unitOfWork;
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
        commandObserver?.Observe(new ProviderCommandEvent("mongodb.query", "MongoDB.Aggregate(page)", ProviderCommandKind.Read, IsProbe: false));
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
            sourceIncludesContinuation: true);
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
        StorageAccessValidation.ObservePrivilegedQuery(
            StorageAccess.PrivilegedAcrossScopes(Access.Audit!),
            Unit);

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
        return CrossScopeQueryMaterializer.FromNativePage(
            materialized,
            rows,
            CrossScopeQueryMaterializer.RawScopeColumn);
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
        var profile = AggregationProfileValidator.ResolveOrThrow(Unit, query.ProfileName);
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
        if (nativeOnAppend && OnAppendRetentionCoordinator.ContainsAppend(outcomes))
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
        var physicalWrites = writes.Select(write => write.PopulateSearchKeyValues()).ToArray();

        // BulkWrite can acknowledge each model but cannot identify whether each
        // upsert inserted or updated. CommitWithOutcomes requests that exact evidence;
        // use the native single-row conditional primitive in that mode.
        if (exactOutcomes)
        {
            var exact = new List<RowWriteOutcome>(writes.Count);
            for (var index = 0; index < writes.Count; index++)
            {
                exact.Add(new RowWriteOutcome(writes[index], ToStore(await ExactOutcomeUpsert(
                    new MongoStorageValues(physicalWrites[index].Values!.Values),
                    ToNative(writes[index].Options),
                    mode).ConfigureAwait(false))));
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

    private async ValueTask<IReadOnlyList<RowWriteOutcome>> ApplyBatchFallback(IReadOnlyList<RowWrite> writes, MongoExecution mode)
    {
        var outcomes = new List<RowWriteOutcome>(writes.Count);
        foreach (var write in writes)
        {
            outcomes.Add(new RowWriteOutcome(write, ToStore(await (write.Mode switch
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
            }).ConfigureAwait(false))));
        }
        return outcomes;
    }

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
        var physical = SetMutationValidation.ValidateAndPhysicalizeAssignments(Unit, assignments);
        ThrowIfDisposed();
        RefusePrivilegedOperation("update-where");
        var filter = new MongoQueryRenderer().RenderAggregationSourcePredicate(
            where,
            Unit.Name,
            QueryRenderOptions.Default with
            {
                SearchKeyColumns = SearchKeyQueryMappings.For(Unit)
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
        ThrowIfDisposed();
        RefusePrivilegedOperation("delete-where");
        var filter = new MongoQueryRenderer().RenderAggregationSourcePredicate(
            where,
            Unit.Name,
            QueryRenderOptions.Default with
            {
                SearchKeyColumns = SearchKeyQueryMappings.For(Unit)
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

    private ValueTask<RetentionOperationResult> ApplyExactRetention(
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
        return ExecuteWithTransactionIfNeeded(
            transactional => transactional.ApplyExactRetentionCore(operationId, declaration, options, mode), mode);
    }

    private async ValueTask<RetentionOperationResult> ApplyExactRetentionCore(
        OperationId operationId,
        RetentionIdempotencyDeclaration declaration,
        RetentionExecutionOptions options,
        MongoExecution mode)
    {
        var scope = Access.Scope?.Value ?? string.Empty;
        var ledger = state.Operations(declaration.LedgerName);
        var fingerprint = RetentionOperationCodec.Fingerprint(Unit, options);
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
        var retention = await ApplyRetentionCore(options, mode).ConfigureAwait(false);
        var result = new RetentionOperationResult(RetentionOperationStatus.Executed, retention.DeletedRows, retention.Batches, retention.Completed);
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
        commandObserver?.Observe(new ProviderCommandEvent(operation, "MongoDB.FindOne", ProviderCommandKind.Read, IsProbe: isProbe));
        return mode.FirstOrDefault(transactionSession is null
            ? collection.Find(new BsonDocument("_id", identity))
            : collection.Find(transactionSession, new BsonDocument("_id", identity)))!;
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
    }

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
        column.Default is null ? "default:absent" : "default:present:" + column.Default.Value);

    private static string Index(IndexDefinition index) => string.Join("|",
        index.Name, index.IsUnique, index.MissingValues, index.SchemaVersion,
        string.Join(",", index.Columns.Select(column => column.Column + ":" + column.Direction)));

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
            Columns = index.Columns.Select(column => column with { }).ToArray()
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
