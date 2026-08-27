using System.Collections.Immutable;
using System.Globalization;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Substrate.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Groundwork.MongoDb;

/// <summary>
/// MongoDB's provider execution boundary for one exact schema target: the applied schema ledger the
/// planner reasons from, the application lock that serializes deployment, and the collection, index,
/// document and metadata work each planned operation names.
/// </summary>
/// <remarks>
/// <para>
/// The ledger lives in <c>__groundwork_metadata</c> beside the provider catalog the runtime already
/// keeps there, so a MongoDB deployment carries one metadata collection rather than two. It holds
/// exactly the canonical JSON <see cref="PhysicalSchemaAppliedStateSerializer"/> produces for every
/// other provider — the plan, its operations and its fingerprints are provider-neutral values, and
/// nothing about them is re-spelled here.
/// </para>
/// <para>
/// Unlike the relational executor, a batch is not one transaction. MongoDB cannot run
/// <c>drop</c> or <c>renameCollection</c> inside a multi-document transaction, so a batch that mixed
/// them would have to be split anyway. Every operation is instead individually idempotent — create
/// if absent, set where missing, drop if present — and the applied state is published only after all
/// of them succeed. An interrupted apply therefore replans from the ledger it never wrote and
/// re-runs the same operations onto the state it left.
/// </para>
/// </remarks>
public sealed class MongoSchemaExecutor
    : IPhysicalSchemaExecutor, IPhysicalSchemaHistoryInspector, IPhysicalSchemaCatalogInspector
{
    internal const string MetadataCollection = "__groundwork_metadata";
    private const string HistoryPrefix = "history:";
    private const string LockPrefix = "lock:";
    private const string SchemaPrefix = "schema:";
    private const string ScopeCollectionSeparator = "__scope__";

    /// <summary>
    /// How long an unrefreshed application lock stays held. A deployment that outruns it does not
    /// corrupt the ledger: the fence is asserted inside the publish transaction, so a lease another
    /// process has since taken loses the publish rather than overwriting it.
    /// </summary>
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(15);

    private static readonly TimeSpan AcquisitionTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Documents rewritten per bulk write while a column is added, backfilled, or altered.</summary>
    private const int DocumentBatchSize = 512;

    private readonly MongoClientContext context;

    public MongoSchemaExecutor(MongoClientContext context) =>
        this.context = context ?? throw new ArgumentNullException(nameof(context));

    private IMongoCollection<BsonDocument> Metadata =>
        context.Database.GetCollection<BsonDocument>(MetadataCollection);

    public IPhysicalSchemaApplicationLock AcquireApplicationLock(PhysicalSchemaTargetIdentity target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var id = LockPrefix + target;
        var owner = Guid.NewGuid().ToString("N");
        var deadline = DateTimeOffset.UtcNow + AcquisitionTimeout;
        while (true)
        {
            var now = DateTimeOffset.UtcNow;
            var filter = new BsonDocument
            {
                ["_id"] = id,
                ["$or"] = new BsonArray
                {
                    new BsonDocument("owner", BsonNull.Value),
                    new BsonDocument("expiresAt", new BsonDocument("$lte", Instant(now)))
                }
            };
            var update = new BsonDocument
            {
                ["$set"] = new BsonDocument
                {
                    ["kind"] = "schema-lock",
                    ["owner"] = owner,
                    ["expiresAt"] = Instant(now + LeaseDuration)
                },
                ["$inc"] = new BsonDocument("fence", 1L)
            };
            try
            {
                var claimed = Metadata.FindOneAndUpdate<BsonDocument>(
                    filter,
                    update,
                    new FindOneAndUpdateOptions<BsonDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After });
                return new MongoApplicationLock(this, target, id, owner, claimed["fence"].ToInt64());
            }
            catch (MongoCommandException exception) when (exception.Code == 11000)
            {
                // Held by an unexpired lease: the upsert could not insert over the existing _id.
            }
            catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
            {
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new SchemaToolProviderException(
                    $"Another deployment holds the MongoDB schema application lock for '{target}'.");
            }
            Thread.Sleep(200);
        }
    }

    public PhysicalSchemaHistoryState ReadHistory(
        PhysicalSchemaTargetIdentity target,
        IPhysicalSchemaApplicationLock applicationLock)
    {
        var lease = RequireLock(target, applicationLock);
        lease.Verify();
        return ReadHistory(target);
    }

    public PhysicalSchemaOperationAcknowledgement ApplyOperation(
        PhysicalSchemaTargetIdentity target,
        PhysicalSchemaOperation operation,
        IPhysicalSchemaApplicationLock applicationLock)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var lease = RequireLock(target, applicationLock);
        lease.Verify();
        Execute(operation);
        return new PhysicalSchemaOperationAcknowledgement(
            operation.Identity,
            operation.Fingerprint,
            DateTimeOffset.UtcNow);
    }

    public void PublishAppliedState(
        PhysicalSchemaAppliedState state,
        string? expectedAppliedTargetFingerprint,
        IPhysicalSchemaApplicationLock applicationLock)
    {
        ArgumentNullException.ThrowIfNull(state);
        var lease = RequireLock(state.TargetIdentity, applicationLock);
        var id = HistoryPrefix + state.TargetIdentity;
        using var session = context.StartSession();
        session.StartTransaction();
        try
        {
            lease.Assert(session);
            var current = Metadata
                .Find(session, new BsonDocument("_id", id))
                .FirstOrDefault()?
                .GetValue("targetFingerprint", BsonNull.Value);
            var actual = current is null || current.IsBsonNull ? null : current.AsString;
            if (!string.Equals(actual, expectedAppliedTargetFingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException($"MongoDB schema history CAS failed for '{state.TargetIdentity}'.");

            Metadata.ReplaceOne(
                session,
                new BsonDocument("_id", id),
                new BsonDocument
                {
                    ["_id"] = id,
                    ["kind"] = "schema-history",
                    ["subjectId"] = state.TargetIdentity.SubjectId.Value,
                    ["providerName"] = state.TargetIdentity.ProviderName,
                    ["targetFingerprint"] = state.TargetFingerprint,
                    ["appliedAt"] = Instant(state.AppliedAt),
                    ["stateJson"] = PhysicalSchemaAppliedStateSerializer.Serialize(state)
                },
                new ReplaceOptions { IsUpsert = true });
            PublishProviderCatalog(session, state);
            lease.Assert(session);
            session.CommitTransaction();
        }
        catch
        {
            if (session.IsInTransaction)
                session.AbortTransaction();
            throw;
        }
    }

    /// <summary>
    /// Rewrites the runtime provider's own <c>__groundwork_metadata</c> record for this subject, in
    /// the same transaction that publishes the ledger. A collection the deployment tool applied is
    /// therefore openable by <c>MongoProviderState.Resolve</c> without a second in-process apply:
    /// the fingerprint the runtime compares against is the one the tool just deployed.
    /// </summary>
    private void PublishProviderCatalog(IClientSessionHandle session, PhysicalSchemaAppliedState state)
    {
        var unit = state.Snapshot.Subject.Definition;
        var id = SchemaPrefix + unit.Id.Value;
        if (state.Snapshot.Subject.Evolution.RetiresPrimaryStorage)
        {
            Metadata.DeleteOne(session, new BsonDocument("_id", id));
            Metadata.DeleteMany(session, new BsonDocument
            {
                ["kind"] = "scope",
                ["unit"] = unit.Id.Value
            });
            return;
        }

        Metadata.ReplaceOne(
            session,
            new BsonDocument("_id", id),
            new BsonDocument
            {
                ["_id"] = id,
                ["collection"] = unit.Name,
                ["key"] = new BsonArray(unit.Key.Columns),
                ["fingerprint"] = SchemaIdentity.Fingerprint(unit),
                ["derived"] = new BsonArray(unit.DerivedColumns.Select(column => new BsonDocument
                {
                    ["name"] = column.Name,
                    ["algorithmId"] = MongoSchemaCoordinator.ProjectionAlgorithmId(column)
                }))
            },
            new ReplaceOptions { IsUpsert = true });
    }

    public PhysicalSchemaInspectionResult InspectHistory(PhysicalSchemaTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var history = ReadHistory(target.Identity);
        if (history.AppliedState is not { } applied)
            return new PhysicalSchemaInspectionResult(history, IsAppliedSchemaValid: true);

        var appliedTarget = new PhysicalSchemaTarget(
            applied.Snapshot.Subject,
            applied.Provider,
            applied.Snapshot.ProviderDefinitions);
        return Compare(appliedTarget, history);
    }

    /// <summary>
    /// Compares the deployed collection set to an exact compiled target under the caller's
    /// application lock, consulting no history. This is the proof <c>groundwork adopt</c> rests on.
    /// </summary>
    public PhysicalSchemaInspectionResult InspectDeployedCatalog(
        PhysicalSchemaTarget target,
        IPhysicalSchemaApplicationLock applicationLock)
    {
        ArgumentNullException.ThrowIfNull(target);
        var lease = RequireLock(target.Identity, applicationLock);
        lease.Verify();
        return Compare(target, PhysicalSchemaHistoryState.Empty);
    }

    private PhysicalSchemaHistoryState ReadHistory(PhysicalSchemaTargetIdentity target)
    {
        var document = Metadata.Find(new BsonDocument("_id", HistoryPrefix + target)).FirstOrDefault();
        return document is null
            ? PhysicalSchemaHistoryState.Empty
            : PhysicalSchemaHistoryState.FromApplied(
                PhysicalSchemaAppliedStateSerializer.Deserialize(document["stateJson"].AsString));
    }

    // ---- catalog comparison ------------------------------------------------------------------

    /// <summary>
    /// Compares every collection this subject owns — the primary one and each per-scope collection
    /// derived from it — to the declared columns, folded search-key algorithms, and indexes.
    /// </summary>
    /// <remarks>
    /// MongoDB publishes no column catalog, so a declared column's evidence is the documents: a
    /// document missing the field, or storing it under a BSON type the declaration does not name,
    /// is drift. A field the declaration does not mention is deliberately not classified through
    /// <see cref="ForeignColumnAdmission"/>: MongoDB declares no columns, so an undeclared field is
    /// not a foreign column that could refuse a write the way an undefaulted NOT NULL column can.
    /// Reporting one as drift would be inventing a rule the deployment cannot enforce.
    /// </remarks>
    private PhysicalSchemaInspectionResult Compare(
        PhysicalSchemaTarget target,
        PhysicalSchemaHistoryState history)
    {
        var subject = target.Subject;
        if (subject.Evolution.RetiresPrimaryStorage)
            return new PhysicalSchemaInspectionResult(history, IsAppliedSchemaValid: true);

        var unit = subject.Definition;
        if (!CollectionExists(unit.Name))
        {
            return new PhysicalSchemaInspectionResult(
                history,
                IsAppliedSchemaValid: false,
                ColumnDrift: [new SchemaRefusal(
                    "GW-RUNTIME-001",
                    $"MongoDB collection '{unit.Name}' does not exist.",
                    "table")]);
        }

        var columnDrift = new List<SchemaRefusal>();
        var indexDrift = new List<SchemaRefusal>();
        foreach (var name in DeployedCollections(unit.Name))
        {
            CompareDocuments(name, unit, columnDrift);
            CompareIndexes(name, unit, indexDrift);
        }
        CompareSearchKeyAlgorithms(unit, columnDrift);
        return new PhysicalSchemaInspectionResult(
            history,
            columnDrift.Count == 0 && indexDrift.Count == 0,
            [.. columnDrift],
            [.. indexDrift]);
    }

    private void CompareDocuments(string collectionName, StorageUnit unit, List<SchemaRefusal> drift)
    {
        var collection = context.Database.GetCollection<BsonDocument>(collectionName);
        foreach (var column in unit.Columns)
        {
            if (MongoDocumentMapper.IsSystemOwnedToken(unit, column))
                continue;

            if (collection.Find(new BsonDocument(column.Name, new BsonDocument("$exists", false))).Limit(1).Any())
            {
                drift.Add(new SchemaRefusal(
                    "GW-RUNTIME-001",
                    $"MongoDB collection '{collectionName}' contains a document missing declared column '{column.Name}'.",
                    $"columns.{column.Name}"));
                continue;
            }

            var expectedType = MongoValueCodec.GetBsonTypeName(column);
            var accepted = new BsonArray { new BsonDocument(column.Name, new BsonDocument("$type", expectedType)) };
            if (column.IsNullable)
                accepted.Add(new BsonDocument(column.Name, BsonNull.Value));
            var wrongType = collection.Find(new BsonDocument("$and", new BsonArray
            {
                new BsonDocument(column.Name, new BsonDocument("$exists", true)),
                new BsonDocument("$nor", accepted)
            })).Limit(1).Any();
            if (wrongType)
            {
                drift.Add(new SchemaRefusal(
                    "GW-RUNTIME-001",
                    column.IsNullable
                        ? $"MongoDB column '{collectionName}.{column.Name}' contains a value whose BSON type is not '{expectedType}'."
                        : $"MongoDB column '{collectionName}.{column.Name}' is declared required and contains a null or a " +
                          $"value whose BSON type is not '{expectedType}'.",
                    $"columns.{column.Name}.type"));
            }
        }
    }

    private void CompareIndexes(string collectionName, StorageUnit unit, List<SchemaRefusal> drift)
    {
        var actual = ReadIndexes(collectionName);
        foreach (var expected in unit.Indexes)
        {
            var specification = new MongoIndexSpecification(expected, unit.Columns);
            if (!actual.TryGetValue(expected.Name, out var deployed))
            {
                drift.Add(new SchemaRefusal(
                    "GW-RUNTIME-002",
                    $"MongoDB collection '{collectionName}' is missing declared index '{expected.Name}'.",
                    $"indexes.{expected.Name}"));
                continue;
            }

            if (!Matches(deployed, specification))
            {
                drift.Add(new SchemaRefusal(
                    "GW-RUNTIME-002",
                    $"MongoDB index '{collectionName}.{expected.Name}' differs in key order, direction, uniqueness, " +
                    "or partial filter.",
                    $"indexes.{expected.Name}"));
            }
        }
    }

    /// <summary>
    /// A folded column's algorithm identity is Groundwork's own record, not something MongoDB
    /// stores. A collection Groundwork never applied to therefore cannot show that its search-key
    /// contents were produced by the declared algorithm, and adoption refuses by name.
    /// </summary>
    private void CompareSearchKeyAlgorithms(StorageUnit unit, List<SchemaRefusal> drift)
    {
        if (unit.DerivedColumns.Count == 0)
            return;
        var document = Metadata.Find(new BsonDocument("_id", SchemaPrefix + unit.Id.Value)).FirstOrDefault();
        var persisted = document is not null && document.TryGetValue("derived", out var derived) && derived.IsBsonArray
            ? derived.AsBsonArray.OfType<BsonDocument>().ToDictionary(
                item => item["name"].AsString,
                item => item["algorithmId"].AsString,
                StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var expected in unit.DerivedColumns)
        {
            var algorithm = MongoSchemaCoordinator.ProjectionAlgorithmId(expected);
            if (!persisted.TryGetValue(expected.Name, out var actual) ||
                !string.Equals(actual, algorithm, StringComparison.Ordinal))
            {
                drift.Add(new SchemaRefusal(
                    "GW-RUNTIME-001",
                    $"Persisted MongoDB search-key algorithm for derived column '{expected.Name}' is " +
                    $"'{actual ?? "<missing>"}' rather than '{algorithm}'.",
                    $"columns.{expected.Name}.searchKeyAlgorithm"));
            }
        }
    }

    // ---- operation execution ------------------------------------------------------------------

    private void Execute(PhysicalSchemaOperation operation)
    {
        switch (operation)
        {
            case CreatePrimaryStorageOperation create:
                CreateCollection(create.Subject.Name);
                break;
            case AddColumnOperation add:
                foreach (var name in DeployedCollections(add.Subject.Name))
                    AddField(name, add.Subject.Definition, add.Column);
                break;
            case BackfillColumnOperation backfill:
                foreach (var name in DeployedCollections(backfill.Subject.Name))
                    Backfill(name, backfill);
                break;
            // MongoDB stores no per-field nullability to switch on, so finalizing a required
            // column has nothing to do here. What proves the documents honour the declaration is
            // ValidatePhysicalSchema at the end of the plan, which names the column and refuses to
            // publish. A null check here would be either redundant with that — FinalizeColumn is
            // only planned for a newly added column, and a plan that adds a required one has
            // already supplied a portable default the backfill writes, or been refused by
            // GW-SCHEMA-005 — or wrong, because the remaining case defers population to a data
            // migration that runs after the DDL.
            case FinalizeColumnOperation:
                break;
            case CreatePhysicalIndexOperation create:
                foreach (var name in DeployedCollections(create.Subject.Name))
                    CreateIndex(name, create.Subject.Definition, create.Index);
                break;
            case RebuildPhysicalIndexOperation rebuild:
                foreach (var name in DeployedCollections(rebuild.Subject.Name))
                {
                    DropIndex(name, rebuild.Index.Name);
                    CreateIndex(name, rebuild.Subject.Definition, rebuild.Index);
                }
                break;
            case DropPhysicalIndexOperation drop:
                foreach (var name in DeployedCollections(drop.Subject.Name))
                    DropIndex(name, drop.Index.Name);
                break;
            case RenameColumnOperation rename:
                foreach (var name in DeployedCollections(rename.Subject.Name))
                {
                    context.Database.GetCollection<BsonDocument>(name).UpdateMany(
                        new BsonDocument(rename.FromName, new BsonDocument("$exists", true)),
                        new BsonDocument("$rename", new BsonDocument(rename.FromName, rename.Column.Name)));
                }
                break;
            case AlterColumnOperation alter:
                foreach (var name in DeployedCollections(alter.Subject.Name))
                    Alter(name, alter);
                break;
            case DropColumnOperation drop:
                foreach (var name in DeployedCollections(drop.Subject.Name))
                {
                    context.Database.GetCollection<BsonDocument>(name).UpdateMany(
                        new BsonDocument(drop.Column.Name, new BsonDocument("$exists", true)),
                        new BsonDocument("$unset", new BsonDocument(drop.Column.Name, string.Empty)));
                }
                break;
            case RenamePrimaryStorageOperation rename:
                RenameCollections(rename);
                break;
            case DropPrimaryStorageOperation drop:
                foreach (var name in DeployedCollections(drop.Name))
                    context.Database.DropCollection(name);
                break;
            case ApplyProviderPhysicalSchemaDefinitionOperation apply:
                ApplyProviderDefinition(apply.Definition);
                break;
            // A supersession marker is a durable ledger fact, not physical work.
            case ColumnSupersessionOperation:
                break;
            case ValidatePhysicalSchemaOperation validate:
                ValidateTarget(validate.Target);
                break;
            case PublishAppliedStateOperation:
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(operation), operation.Kind, "Unsupported MongoDB schema operation.");
        }
    }

    private void ValidateTarget(PhysicalSchemaTarget target)
    {
        var inspection = Compare(target, PhysicalSchemaHistoryState.Empty);
        if (!inspection.HasColumnDrift && !inspection.HasIndexDrift)
            return;
        var refusal = inspection.HasColumnDrift ? inspection.ColumnDrift[0] : inspection.IndexDrift[0];
        throw new InvalidOperationException(refusal.Message);
    }

    /// <summary>
    /// Creates the collection unless it is already there, which is what makes an interrupted
    /// deployment re-runnable: the ledger is published only at the end, so the next plan derives
    /// this operation again against the collection the last attempt left behind.
    /// </summary>
    private void CreateCollection(string name)
    {
        if (!CollectionExists(name))
            context.Database.CreateCollection(name);
    }

    /// <summary>
    /// Creates the declared index, or refuses by name when the collection already carries a
    /// different index under that name. A plan derived from empty history — an interrupted
    /// deployment, or an adoption candidate — creates every declared index, and MongoDB answers a
    /// conflicting redefinition with a driver error that names nothing an operator can act on.
    /// </summary>
    private void CreateIndex(string collectionName, StorageUnit unit, IndexDefinition index)
    {
        var specification = new MongoIndexSpecification(index, unit.Columns);
        if (ReadIndexes(collectionName).TryGetValue(index.Name, out var deployed))
        {
            if (Matches(deployed, specification))
                return;
            throw new InvalidOperationException(
                $"MongoDB collection '{collectionName}' already carries an index named '{index.Name}' whose key " +
                "order, direction, uniqueness, or partial filter differs from the declared one. Apply the " +
                "declaration against its recorded schema history, which plans that as a rebuild.");
        }

        var keys = new BsonDocument(specification.Terms.Select(term => new BsonElement(
            term.Column,
            term.Direction == Groundwork.Kernel.SortDirection.Ascending ? 1 : -1)));
        context.Database.GetCollection<BsonDocument>(collectionName).Indexes.CreateOne(
            new CreateIndexModel<BsonDocument>(keys, new CreateIndexOptions<BsonDocument>
            {
                Name = specification.Name,
                Unique = specification.IsUnique,
                PartialFilterExpression = specification.PartialFilter
            }));
    }

    /// <summary>
    /// Drops the index where it is present. A per-scope collection materialized by an application
    /// whose declaration predates the index never had it, so a rebuild that spans every collection
    /// has to tolerate its absence in one of them rather than failing the whole deployment.
    /// </summary>
    private void DropIndex(string collectionName, string indexName)
    {
        if (ReadIndexes(collectionName).ContainsKey(indexName))
            context.Database.GetCollection<BsonDocument>(collectionName).Indexes.DropOne(indexName);
    }

    /// <summary>
    /// Materializes a declared field on documents that predate it. A column with a portable default
    /// is written with that default and a nullable one with an explicit null, because MongoDB's
    /// runtime admission treats a missing field as drift; the planner has already refused a
    /// required column that offers neither.
    /// </summary>
    private void AddField(string collectionName, StorageUnit unit, ColumnDefinition column)
    {
        if (MongoDocumentMapper.IsSystemOwnedToken(unit, column))
            return;
        var value = column.Default is { } portable
            ? MongoValueCodec.Encode(portable.Value, column)
            : BsonNull.Value;
        context.Database.GetCollection<BsonDocument>(collectionName).UpdateMany(
            new BsonDocument(column.Name, new BsonDocument("$exists", false)),
            new BsonDocument("$set", new BsonDocument(column.Name, value)));
    }

    private void Backfill(string collectionName, BackfillColumnOperation operation)
    {
        var unit = operation.Subject.Definition;
        if (operation.Derived is { } derived)
        {
            Project(collectionName, unit, derived);
            return;
        }
        if (operation.Column.Default is not { } portable)
            return;
        context.Database.GetCollection<BsonDocument>(collectionName).UpdateMany(
            new BsonDocument(operation.Column.Name, BsonNull.Value),
            new BsonDocument("$set", new BsonDocument(
                operation.Column.Name,
                MongoValueCodec.Encode(portable.Value, operation.Column))));
    }

    /// <summary>
    /// Recomputes one derived search key over every document, through the same host-process
    /// transform the resumable data-migration runner drives, so a search key written by a schema
    /// apply and one written by a migration are one definition.
    /// </summary>
    private void Project(string collectionName, StorageUnit unit, DerivedColumnDefinition derived)
    {
        var collection = context.Database.GetCollection<BsonDocument>(collectionName);
        var transform = new DerivedColumnTransform(unit, [derived]);
        var columns = unit.Columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
        var batch = new List<WriteModel<BsonDocument>>(DocumentBatchSize);
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
                new BsonDocument("_id", document.GetValue("_id")),
                new BsonDocument("$set", updates)));
            if (batch.Count < DocumentBatchSize)
                continue;
            collection.BulkWrite(batch, new BulkWriteOptions { IsOrdered = false });
            batch.Clear();
        }

        if (batch.Count != 0)
            collection.BulkWrite(batch, new BulkWriteOptions { IsOrdered = false });
    }

    /// <summary>
    /// Redefines a stored column. Where the declaration's BSON representation is unchanged — a
    /// widened string, a relaxed nullability — nothing stored has to move and the alteration is the
    /// ledger fact alone. Where it changes, every stored value is decoded under the applied
    /// definition and re-encoded under the declared one, which refuses by name on a value the new
    /// definition cannot hold rather than truncating it.
    /// </summary>
    private void Alter(string collectionName, AlterColumnOperation operation)
    {
        if (MongoDocumentMapper.IsSystemOwnedToken(operation.Subject.Definition, operation.Column))
            return;
        if (string.Equals(
                MongoValueCodec.GetBsonTypeName(operation.From),
                MongoValueCodec.GetBsonTypeName(operation.Column),
                StringComparison.Ordinal) &&
            operation.From.Precision == operation.Column.Precision &&
            operation.From.Scale == operation.Column.Scale)
        {
            return;
        }

        var name = operation.Column.Name;
        var collection = context.Database.GetCollection<BsonDocument>(collectionName);
        var batch = new List<WriteModel<BsonDocument>>(DocumentBatchSize);
        foreach (var document in collection
                     .Find(new BsonDocument(name, new BsonDocument("$ne", BsonNull.Value)))
                     .Project<BsonDocument>(new BsonDocument { ["_id"] = 1, [name] = 1 })
                     .ToEnumerable())
        {
            var value = MongoValueCodec.Decode(document[name], operation.From);
            batch.Add(new UpdateOneModel<BsonDocument>(
                new BsonDocument("_id", document.GetValue("_id")),
                new BsonDocument("$set", new BsonDocument(name, MongoValueCodec.Encode(value, operation.Column)))));
            if (batch.Count < DocumentBatchSize)
                continue;
            collection.BulkWrite(batch, new BulkWriteOptions { IsOrdered = false });
            batch.Clear();
        }

        if (batch.Count != 0)
            collection.BulkWrite(batch, new BulkWriteOptions { IsOrdered = false });
    }

    private void RenameCollections(RenamePrimaryStorageOperation operation)
    {
        foreach (var from in DeployedCollections(operation.FromName))
        {
            var to = operation.ToName + from[operation.FromName.Length..];
            if (CollectionExists(to))
                continue;
            context.Database.RenameCollection(from, to);
        }
        foreach (var superseded in operation.SupersededProviderDefinitions)
            DropProviderDefinition(superseded);
        // The scope registry names the collection each scope lives in, so a rename that left those
        // rows behind would make GW-ACCESS-006 fire on the next scoped session.
        foreach (var registration in Metadata
                     .Find(new BsonDocument { ["kind"] = "scope", ["unit"] = operation.Subject.Id.Value })
                     .ToList())
        {
            Metadata.UpdateOne(
                new BsonDocument("_id", registration["_id"]),
                new BsonDocument("$set", new BsonDocument(
                    "collection",
                    operation.ToName + ScopeCollectionSeparator + ScopeHash(registration))));
        }
    }

    private static string ScopeHash(BsonDocument registration)
    {
        var collection = registration["collection"].AsString;
        var separator = collection.LastIndexOf(ScopeCollectionSeparator, StringComparison.Ordinal);
        return separator < 0 ? string.Empty : collection[(separator + ScopeCollectionSeparator.Length)..];
    }

    private void ApplyProviderDefinition(ProviderPhysicalSchemaDefinition definition)
    {
        var id = SchemaPrefix + definition.SubjectId.Value;
        var column = MongoSchemaTargets.DerivedColumnName(definition);
        Metadata.UpdateOne(
            new BsonDocument("_id", id),
            new BsonDocument("$pull", new BsonDocument("derived", new BsonDocument("name", column))),
            new UpdateOptions { IsUpsert = true });
        Metadata.UpdateOne(
            new BsonDocument("_id", id),
            new BsonDocument("$push", new BsonDocument("derived", new BsonDocument
            {
                ["name"] = column,
                ["algorithmId"] = definition.CanonicalDefinition
            })),
            new UpdateOptions { IsUpsert = true });
    }

    private void DropProviderDefinition(ProviderPhysicalSchemaDefinition definition) =>
        Metadata.UpdateOne(
            new BsonDocument("_id", SchemaPrefix + definition.SubjectId.Value),
            new BsonDocument("$pull", new BsonDocument(
                "derived",
                new BsonDocument("name", MongoSchemaTargets.DerivedColumnName(definition)))));

    // ---- catalog access -----------------------------------------------------------------------

    private bool CollectionExists(string name) => context.Database.ListCollectionNames(
        new ListCollectionNamesOptions { Filter = new BsonDocument("name", name) }).Any();

    /// <summary>
    /// Every collection this subject owns: the primary one, and each per-scope collection a scoped
    /// unit has materialized. Index and field work spans all of them, so a scoped MongoDB unit is
    /// not half-deployed the way it would be if only the primary collection were touched.
    /// </summary>
    private IReadOnlyList<string> DeployedCollections(string primary)
    {
        var names = context.Database.ListCollectionNames().ToList()
            .Where(name => string.Equals(name, primary, StringComparison.Ordinal) ||
                           name.StartsWith(primary + ScopeCollectionSeparator, StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        return names;
    }

    private IReadOnlyDictionary<string, BsonDocument> ReadIndexes(string collectionName) =>
        context.Database.GetCollection<BsonDocument>(collectionName).Indexes.List().ToList()
            .Where(index => index["name"].AsString != "_id_")
            .ToDictionary(index => index["name"].AsString, index => index, StringComparer.Ordinal);

    private static bool Matches(BsonDocument deployed, MongoIndexSpecification expected)
    {
        var keys = deployed["key"].AsBsonDocument;
        if (keys.ElementCount != expected.Terms.Count)
            return false;
        for (var index = 0; index < expected.Terms.Count; index++)
        {
            var term = expected.Terms[index];
            var element = keys.GetElement(index);
            if (!string.Equals(element.Name, term.Column, StringComparison.Ordinal))
                return false;
            var descending = element.Value.ToInt32() < 0;
            if (descending != (term.Direction == Groundwork.Kernel.SortDirection.Descending))
                return false;
        }

        var unique = deployed.TryGetValue("unique", out var value) && value.ToBoolean();
        if (unique != expected.IsUnique)
            return false;
        var partial = deployed.TryGetValue("partialFilterExpression", out var filter) ? filter.AsBsonDocument : null;
        return expected.PartialFilter is null ? partial is null : partial is not null && partial == expected.PartialFilter;
    }

    private static string Instant(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private MongoApplicationLock RequireLock(
        PhysicalSchemaTargetIdentity target,
        IPhysicalSchemaApplicationLock applicationLock)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(applicationLock);
        if (applicationLock is not MongoApplicationLock lease || !ReferenceEquals(lease.Executor, this))
            throw new ArgumentException("The application lock was not issued by this MongoDB executor.", nameof(applicationLock));
        if (lease.Target != target)
            throw new InvalidOperationException($"Lock '{lease.Target}' does not match target '{target}'.");
        return lease;
    }

    /// <summary>
    /// One held MongoDB schema lease. <see cref="Verify"/> proves the lease is still this process's
    /// before work runs; <see cref="Assert"/> proves it again inside the publish transaction, so a
    /// lease that expired mid-deployment loses the publish rather than overwriting a ledger another
    /// deployment has since written.
    /// </summary>
    private sealed class MongoApplicationLock(
        MongoSchemaExecutor executor,
        PhysicalSchemaTargetIdentity target,
        string id,
        string owner,
        long fence) : IPhysicalSchemaApplicationLock
    {
        private bool released;

        internal MongoSchemaExecutor Executor { get; } = executor;

        public PhysicalSchemaTargetIdentity Target { get; } = target;

        internal void Verify()
        {
            if (released)
                throw new InvalidOperationException($"The MongoDB schema application lock for '{Target}' was released.");
            if (!Held(null))
                throw new InvalidOperationException(
                    $"The MongoDB schema application lock for '{Target}' is no longer held by this deployment.");
        }

        internal void Assert(IClientSessionHandle session)
        {
            if (!Held(session))
                throw new InvalidOperationException(
                    $"The MongoDB schema application lock for '{Target}' is no longer held by this deployment.");
        }

        private bool Held(IClientSessionHandle? session)
        {
            var filter = new BsonDocument
            {
                ["_id"] = id,
                ["owner"] = owner,
                ["fence"] = fence
            };
            return session is null
                ? Executor.Metadata.Find(filter).Limit(1).Any()
                : Executor.Metadata.Find(session, filter).Limit(1).Any();
        }

        public void Dispose()
        {
            if (released)
                return;
            released = true;
            Executor.Metadata.UpdateOne(
                new BsonDocument { ["_id"] = id, ["owner"] = owner, ["fence"] = fence },
                new BsonDocument("$set", new BsonDocument
                {
                    ["owner"] = BsonNull.Value,
                    ["expiresAt"] = Instant(DateTimeOffset.UnixEpoch)
                }));
        }
    }
}
