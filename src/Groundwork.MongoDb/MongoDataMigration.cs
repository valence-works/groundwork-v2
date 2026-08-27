using System.Globalization;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Substrate.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Groundwork.MongoDb;

/// <summary>
/// MongoDB data-migration execution. The provider is not relational, so it advertises only what it
/// can actually honour: keyset progress over <c>_id</c> and a durable ledger always, atomic chunk
/// progress only on a deployment that can start a transaction, and never the relational
/// set-based batch update — Mongo has no multi-document update that carries a different value per
/// document, so a chunk is one <c>bulkWrite</c> command of per-document updates instead.
/// </summary>
public sealed class MongoDataMigrationExecutor : IDataMigrationExecutor
{
    /// <summary>Provider name recorded in every MongoDB data-migration ledger entry.</summary>
    public const string ProviderName = "mongodb";

    internal const string LedgerCollection = "__groundwork_data_migrations";

    private const string RunningState = "running";
    private const string CompletedState = "completed";

    private readonly MongoClientContext context;

    public MongoDataMigrationExecutor(MongoClientContext context) =>
        this.context = context ?? throw new ArgumentNullException(nameof(context));

    /// <summary>The ledger target identity MongoDB records for one storage unit.</summary>
    public static PhysicalSchemaTargetIdentity TargetFor(StorageUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return new PhysicalSchemaTargetIdentity(unit.Id, ProviderName);
    }

    public DataMigrationCapabilities Capabilities =>
        DataMigrationCapabilities.KeysetScan |
        DataMigrationCapabilities.AppliedLedger |
        (context.SupportsTransactions()
            ? DataMigrationCapabilities.AtomicChunkProgress
            : DataMigrationCapabilities.None);

    public DataMigrationLedgerEntry? ReadLedgerEntry(PhysicalSchemaTargetIdentity target, string migrationId) =>
        ReadEntry(target, migrationId, MongoExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask<DataMigrationLedgerEntry?> ReadLedgerEntryAsync(
        PhysicalSchemaTargetIdentity target,
        string migrationId,
        CancellationToken cancellationToken = default) =>
        ReadEntry(target, migrationId, MongoExecution.Asynchronous(cancellationToken));

    public IReadOnlyList<DataMigrationLedgerEntry> ReadLedgerEntries(PhysicalSchemaTargetIdentity target) =>
        ReadEntries(target, MongoExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask<IReadOnlyList<DataMigrationLedgerEntry>> ReadLedgerEntriesAsync(
        PhysicalSchemaTargetIdentity target,
        CancellationToken cancellationToken = default) =>
        ReadEntries(target, MongoExecution.Asynchronous(cancellationToken));

    public void WriteLedgerEntry(DataMigrationLedgerEntry entry) =>
        WriteEntry(entry, null, MongoExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask WriteLedgerEntryAsync(DataMigrationLedgerEntry entry, CancellationToken cancellationToken = default) =>
        WriteEntry(entry, null, MongoExecution.Asynchronous(cancellationToken));

    public DataMigrationChunkOutcome ExecuteChunk(DataMigrationChunkRequest request) =>
        ExecuteChunkCore(request, MongoExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask<DataMigrationChunkOutcome> ExecuteChunkAsync(
        DataMigrationChunkRequest request,
        CancellationToken cancellationToken = default) =>
        ExecuteChunkCore(request, MongoExecution.Asynchronous(cancellationToken));

    private async ValueTask<DataMigrationChunkOutcome> ExecuteChunkCore(
        DataMigrationChunkRequest request,
        MongoExecution mode)
    {
        ArgumentNullException.ThrowIfNull(request);
        // A chunk must move rows and its resume cursor together, so it needs a transaction. A
        // standalone deployment cannot start one; refusing here is what the advertised capability
        // means, rather than writing the rows and hoping the progress write follows.
        if (!context.SupportsTransactions())
        {
            throw new DataMigrationRefusedException(
                DataMigrationCodes.MissingCapability,
                "this MongoDB deployment is standalone and cannot start a transaction, so a data-migration " +
                "chunk cannot commit its rows and its resume cursor together " +
                $"(capability {DataMigrationCapabilities.AtomicChunkProgress}).");
        }

        var unit = request.Unit;
        var collection = context.Database.GetCollection<BsonDocument>(unit.Name);
        var columns = unit.Columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
        var filter = request.Cursor is null
            ? new BsonDocument()
            : new BsonDocument("_id", new BsonDocument("$gt", EncodeCursor(unit, request.Cursor, columns)));
        var projection = new BsonDocument("_id", 1);
        foreach (var column in request.Projection)
            projection[column] = 1;

        using var session = mode.IsAsync
            ? await context.StartSessionAsync(mode.CancellationToken).ConfigureAwait(false)
            : context.StartSession();
        session.StartTransaction();
        try
        {
            var documents = await mode.Find(collection, session, filter, new FindOptions<BsonDocument>
            {
                Sort = new BsonDocument("_id", 1),
                Limit = request.MaxRows,
                Projection = projection
            }).ConfigureAwait(false);

            if (documents.Count == 0)
            {
                await Abort(session, mode).ConfigureAwait(false);
                return DataMigrationChunkOutcome.Exhausted(request.Entry);
            }

            var writes = new List<WriteModel<BsonDocument>>(documents.Count);
            IReadOnlyDictionary<string, object?>? lastRow = null;
            foreach (var document in documents)
            {
                var row = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var column in request.Projection)
                {
                    row[column] = document.TryGetValue(column, out var stored) && !stored.IsBsonNull
                        ? MongoValueCodec.Decode(stored, columns[column])
                        : null;
                }
                lastRow = row;
                if (request.Apply(row) is not { Count: > 0 } produced)
                    continue;
                var update = new BsonDocument();
                foreach (var pair in produced)
                    update[pair.Key] = MongoValueCodec.Encode(pair.Value, columns[pair.Key]);
                writes.Add(new UpdateOneModel<BsonDocument>(
                    new BsonDocument("_id", document.GetValue("_id")),
                    new BsonDocument("$set", update)));
            }

            var changed = 0L;
            if (writes.Count != 0)
            {
                var result = await mode.Run(
                    token => collection.BulkWriteAsync(session, writes, new BulkWriteOptions { IsOrdered = false }, token),
                    () => collection.BulkWrite(session, writes, new BulkWriteOptions { IsOrdered = false }))
                    .ConfigureAwait(false);
                changed = result.ModifiedCount;
            }

            var entry = request.Entry.Advance(
                DataMigrationCursor.After(unit, lastRow!),
                documents.Count,
                changed,
                DateTimeOffset.UtcNow);
            await WriteEntry(entry, session, mode).ConfigureAwait(false);
            await Commit(session, mode).ConfigureAwait(false);
            return documents.Count < request.MaxRows
                ? DataMigrationChunkOutcome.Exhausted(entry)
                : DataMigrationChunkOutcome.Advanced(entry);
        }
        catch
        {
            await Abort(session, mode).ConfigureAwait(false);
            throw;
        }
    }

    private static BsonValue EncodeCursor(
        StorageUnit unit,
        DataMigrationCursor cursor,
        IReadOnlyDictionary<string, ColumnDefinition> columns)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var index = 0; index < unit.Key.Columns.Count; index++)
            values[unit.Key.Columns[index]] = cursor.Values[index];
        var key = new BsonDocument();
        foreach (var name in unit.Key.Columns)
            key.Add(name, MongoValueCodec.Encode(values[name], columns[name]));
        return key.ElementCount == 1 ? key[0] : key;
    }

    private static ValueTask Commit(IClientSessionHandle session, MongoExecution mode) => mode.Run(
        token => session.CommitTransactionAsync(token),
        () => session.CommitTransaction());

    private static ValueTask Abort(IClientSessionHandle session, MongoExecution mode)
    {
        if (!session.IsInTransaction)
            return default;
        // An abort must still run when the caller already cancelled the work it undoes.
        return mode.IsAsync
            ? new(session.AbortTransactionAsync(CancellationToken.None))
            : Run();

        ValueTask Run()
        {
            session.AbortTransaction();
            return default;
        }
    }

    private async ValueTask<DataMigrationLedgerEntry?> ReadEntry(
        PhysicalSchemaTargetIdentity target,
        string migrationId,
        MongoExecution mode)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationId);
        var documents = await mode.Find(
            context.Database.GetCollection<BsonDocument>(LedgerCollection),
            null,
            new BsonDocument("_id", Identity(target, migrationId)),
            new FindOptions<BsonDocument> { Limit = 1 }).ConfigureAwait(false);
        return documents.Count == 0 ? null : Decode(target, documents[0]);
    }

    private async ValueTask<IReadOnlyList<DataMigrationLedgerEntry>> ReadEntries(
        PhysicalSchemaTargetIdentity target,
        MongoExecution mode)
    {
        ArgumentNullException.ThrowIfNull(target);
        var documents = await mode.Find(
            context.Database.GetCollection<BsonDocument>(LedgerCollection),
            null,
            new BsonDocument
            {
                ["subjectId"] = target.SubjectId.Value,
                ["providerName"] = target.ProviderName
            },
            new FindOptions<BsonDocument> { Sort = new BsonDocument("migrationId", 1) }).ConfigureAwait(false);
        return documents.Select(document => Decode(target, document)).ToArray();
    }

    private async ValueTask WriteEntry(DataMigrationLedgerEntry entry, IClientSessionHandle? session, MongoExecution mode)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var collection = context.Database.GetCollection<BsonDocument>(LedgerCollection);
        var filter = new BsonDocument("_id", Identity(entry.Target, entry.MigrationId));
        var document = new BsonDocument
        {
            ["_id"] = Identity(entry.Target, entry.MigrationId),
            ["subjectId"] = entry.Target.SubjectId.Value,
            ["providerName"] = entry.Target.ProviderName,
            ["migrationId"] = entry.MigrationId,
            ["unitName"] = entry.UnitName,
            ["requestFingerprint"] = entry.RequestFingerprint,
            ["state"] = entry.IsComplete ? CompletedState : RunningState,
            ["cursor"] = entry.Cursor is null ? BsonNull.Value : entry.Cursor,
            ["rowsScanned"] = entry.RowsScanned,
            ["rowsChanged"] = entry.RowsChanged,
            ["batches"] = entry.Batches,
            ["startedAt"] = Instant(entry.StartedAt),
            ["updatedAt"] = Instant(entry.UpdatedAt),
            ["completedAt"] = entry.CompletedAt is { } completed ? Instant(completed) : BsonNull.Value
        };
        var options = new ReplaceOptions { IsUpsert = true };
        await mode.Run(
            token => session is null
                ? collection.ReplaceOneAsync(filter, document, options, token)
                : collection.ReplaceOneAsync(session, filter, document, options, token),
            () => session is null
                ? collection.ReplaceOne(filter, document, options)
                : collection.ReplaceOne(session, filter, document, options)).ConfigureAwait(false);
    }

    private static DataMigrationLedgerEntry Decode(PhysicalSchemaTargetIdentity target, BsonDocument document)
    {
        var state = document.GetValue("state", BsonNull.Value).AsString;
        return new DataMigrationLedgerEntry(
            target,
            document["migrationId"].AsString,
            document["unitName"].AsString,
            document["requestFingerprint"].AsString,
            state switch
            {
                RunningState => DataMigrationRunState.Running,
                CompletedState => DataMigrationRunState.Completed,
                _ => throw new DataMigrationRefusedException(
                    DataMigrationCodes.LedgerCorrupt,
                    $"the MongoDB data-migration ledger records unknown state '{state}'.")
            },
            document["cursor"].IsBsonNull ? null : document["cursor"].AsString,
            document["rowsScanned"].ToInt64(),
            document["rowsChanged"].ToInt64(),
            document["batches"].ToInt32(),
            ParseInstant(document["startedAt"].AsString),
            ParseInstant(document["updatedAt"].AsString),
            document["completedAt"].IsBsonNull ? null : ParseInstant(document["completedAt"].AsString));
    }

    private static string Identity(PhysicalSchemaTargetIdentity target, string migrationId) =>
        SchemaFingerprint.Canonicalize([target.SubjectId.Value, target.ProviderName, migrationId]);

    private static string Instant(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseInstant(string value) =>
        DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
