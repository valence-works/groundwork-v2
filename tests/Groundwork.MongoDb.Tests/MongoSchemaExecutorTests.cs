using System.Collections.Immutable;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.LiveDatabases;
using Groundwork.Substrate.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Groundwork.MongoDb.Tests;

/// <summary>
/// The MongoDB schema executor against a live replica set, for the properties the deployment tool
/// rests on but does not itself exercise: the expand–contract workflow, the application lease, and
/// what happens to an apply the deployed documents do not admit.
/// </summary>
public sealed class MongoSchemaExecutorTests : IDisposable
{
    private const string MigrationId = "2026-08-widen-total";

    /// <summary>
    /// MongoDB was left out of expand–contract because it kept no applied schema ledger. It has one
    /// now, and both the schema ledger and the data-migration ledger it reads are MongoDB's own, so
    /// the exclusion no longer holds: the expand half adds the replacement and keeps the superseded
    /// column, the contract half refuses until the backfill is recorded, and then removes it.
    /// </summary>
    [SkippableFact]
    public void Expand_keeps_the_superseded_column_and_contract_removes_it_once_the_backfill_is_recorded()
    {
        var context = Context();
        var executor = new MongoSchemaExecutor(context);
        var migrations = new MongoDataMigrationExecutor(context);
        var table = Table();

        var before = Target(Before(table));
        Assert.Equal(
            PhysicalSchemaApplicationOutcome.Applied,
            PhysicalSchemaApplication.Apply(before, executor).Outcome);
        Collection(context, table).InsertOne(new BsonDocument
        {
            ["_id"] = "one",
            ["id"] = "one",
            ["total"] = new BsonDecimal128(12.34m)
        });

        var superseding = Target(After(table), new SchemaEvolutionMetadata(
            semanticMigrationId: MigrationId,
            supersessions: [new ColumnSupersession(TotalColumn, "total_amount")],
            dualPresenceWindow: TimeSpan.Zero));

        var expand = PhysicalSchemaApplication.Apply(
            superseding, executor, phase: SchemaEvolutionPhase.Expand, dataMigrationExecutor: migrations);
        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, expand.Outcome);
        var expanded = Collection(context, table).Find(new BsonDocument("_id", "one")).Single();
        Assert.True(expanded.Contains("total"));
        Assert.True(expanded.Contains("total_amount"));

        // Nothing records the backfill, so the contract half is gated shut and removes nothing.
        var gated = PhysicalSchemaApplication.Apply(
            superseding, executor, phase: SchemaEvolutionPhase.Contract, dataMigrationExecutor: migrations);
        Assert.Equal(PhysicalSchemaApplicationOutcome.Rejected, gated.Outcome);
        Assert.Equal("GW-EXPAND-002", Assert.Single(gated.Plan.Refusals).Code);
        Assert.True(Collection(context, table).Find(new BsonDocument("_id", "one")).Single().Contains("total"));

        migrations.WriteLedgerEntry(new DataMigrationLedgerEntry(
            superseding.Identity, MigrationId, table, "fingerprint",
            DataMigrationRunState.Completed, cursor: null,
            rowsScanned: 1, rowsChanged: 1, batches: 1,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        var contract = PhysicalSchemaApplication.Apply(
            superseding, executor, phase: SchemaEvolutionPhase.Contract, dataMigrationExecutor: migrations);
        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, contract.Outcome);
        Assert.Contains(
            PhysicalSchemaOperationKind.DropColumn,
            contract.Plan.Operations.Select(operation => operation.Kind));
        var contracted = Collection(context, table).Find(new BsonDocument("_id", "one")).Single();
        Assert.False(contracted.Contains("total"));
        Assert.True(contracted.Contains("total_amount"));
    }

    /// <summary>
    /// The lease is what makes two concurrent deployments safe. A lease another deployment has taken
    /// cannot publish: the fence is asserted before the ledger is written, so the stale holder loses
    /// rather than overwriting a ledger it never planned against.
    /// </summary>
    [SkippableFact]
    public void A_lease_another_deployment_has_taken_cannot_publish()
    {
        var context = Context();
        var executor = new MongoSchemaExecutor(context);
        var target = Target(Before(Table()));

        var stale = executor.AcquireApplicationLock(target.Identity);
        var plan = PhysicalSchemaDiffPlanner.Plan(
            target, executor.ReadHistory(target.Identity, stale), DateTimeOffset.UtcNow);
        var state = plan.Complete(
            [.. plan.Operations
                .Where(operation => operation.Kind != PhysicalSchemaOperationKind.PublishAppliedState)
                .Select(operation => new PhysicalSchemaOperationAcknowledgement(
                    operation.Identity, operation.Fingerprint, DateTimeOffset.UtcNow))],
            DateTimeOffset.UtcNow);

        // The lease expires and a second deployment claims it, which advances the fence.
        Expire(context, target.Identity);
        using var current = executor.AcquireApplicationLock(target.Identity);

        var refusedPublish = Assert.Throws<InvalidOperationException>(() =>
            executor.PublishAppliedState(state, null, stale));
        Assert.Contains("no longer held by this deployment", refusedPublish.Message, StringComparison.Ordinal);
        Assert.Null(executor.ReadHistory(target.Identity, current).AppliedState);

        // Nor can the stale holder go on executing operations under the lease it no longer has.
        var refusedOperation = Assert.Throws<InvalidOperationException>(() => executor.ApplyOperation(
            target.Identity, plan.Operations[0], stale));
        Assert.Contains("no longer held by this deployment", refusedOperation.Message, StringComparison.Ordinal);

        stale.Dispose();
        var refusedAfterRelease = Assert.Throws<InvalidOperationException>(() => executor.ApplyOperation(
            target.Identity, plan.Operations[0], stale));
        Assert.Contains("was released", refusedAfterRelease.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A required column that existing documents leave null is refused by name at target
    /// validation, so nothing is published: MongoDB stores no per-field nullability to enforce, and
    /// what makes the declaration true is the documents.
    /// </summary>
    [SkippableFact]
    public void An_apply_the_documents_do_not_admit_is_refused_by_name_and_publishes_nothing()
    {
        var context = Context();
        var executor = new MongoSchemaExecutor(context);
        var table = Table();
        var optional = Target(WithNote(table, required: false));
        Assert.Equal(
            PhysicalSchemaApplicationOutcome.Applied,
            PhysicalSchemaApplication.Apply(optional, executor).Outcome);
        Collection(context, table).InsertOne(new BsonDocument
        {
            ["_id"] = "one",
            ["id"] = "one",
            ["note"] = BsonNull.Value
        });

        var required = Target(WithNote(table, required: true));
        var refused = Assert.Throws<InvalidOperationException>(() =>
            PhysicalSchemaApplication.Apply(required, executor));

        Assert.Contains("note", refused.Message, StringComparison.Ordinal);
        Assert.Contains("declared required", refused.Message, StringComparison.Ordinal);
        using var applicationLock = executor.AcquireApplicationLock(required.Identity);
        Assert.Equal(
            optional.Fingerprint,
            executor.ReadHistory(required.Identity, applicationLock).AppliedState!.TargetFingerprint);
    }

    /// <summary>
    /// Renaming a subject's storage renames every collection it owns, including the per-scope ones,
    /// and rewrites the scope registry that names them. Leaving those rows behind would make the
    /// next scoped session report <c>GW-ACCESS-006</c> registry drift.
    /// </summary>
    [SkippableFact]
    public void Renaming_storage_carries_every_per_scope_collection_and_the_scope_registry()
    {
        var context = Context();
        var executor = new MongoSchemaExecutor(context);
        var id = "mongo_rename_" + Guid.NewGuid().ToString("N")[..12];
        var from = id + "_before";
        var to = id + "_after";
        Assert.Equal(
            PhysicalSchemaApplicationOutcome.Applied,
            PhysicalSchemaApplication.Apply(Target(Scoped(id, from)), executor).Outcome);

        // The runtime materializes the scope collection and registers it.
        var scope = new StorageScope("tenant-a");
        using (var store = new MongoDbProviderConnection(new MongoClientContext(ConnectionString(context))))
            store.OpenSession(Scoped(id, from), MongoStorageAccess.Scoped(scope));
        var scopedBefore = MongoSchemaExecutor.ScopedCollectionName(from, scope.Value);
        Assert.Contains(scopedBefore, context.Database.ListCollectionNames().ToList());

        var renamed = PhysicalSchemaApplication.Apply(Target(Scoped(id, to)), executor);

        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, renamed.Outcome);
        Assert.Contains(
            PhysicalSchemaOperationKind.RenamePrimaryStorage,
            renamed.Plan.Operations.Select(operation => operation.Kind));
        var collections = context.Database.ListCollectionNames().ToList();
        Assert.Contains(to, collections);
        Assert.Contains(MongoSchemaExecutor.ScopedCollectionName(to, scope.Value), collections);
        Assert.DoesNotContain(from, collections);
        Assert.DoesNotContain(scopedBefore, collections);
        Assert.Equal(
            MongoSchemaExecutor.ScopedCollectionName(to, scope.Value),
            context.Database.GetCollection<BsonDocument>("__groundwork_metadata")
                .Find(new BsonDocument { ["kind"] = "scope", ["unit"] = id })
                .Single()["collection"].AsString);
    }

    /// <summary>
    /// A rebuild is planned from the applied ledger, not from the catalog, and a subject owns more
    /// than one collection: a per-scope collection materialized by an application whose declaration
    /// predates the index never carried it. Redirecting the index must therefore tolerate its
    /// absence rather than failing the whole deployment on a driver error.
    /// </summary>
    [SkippableFact]
    public void A_rebuild_tolerates_a_collection_that_never_carried_the_index()
    {
        var context = Context();
        var executor = new MongoSchemaExecutor(context);
        var table = Table();
        Assert.Equal(
            PhysicalSchemaApplicationOutcome.Applied,
            PhysicalSchemaApplication.Apply(Target(WithIndex(table, descending: false)), executor).Outcome);
        Collection(context, table).Indexes.DropOne("by_owner");

        var rebuilt = PhysicalSchemaApplication.Apply(Target(WithIndex(table, descending: true)), executor);

        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, rebuilt.Outcome);
        Assert.Contains(
            PhysicalSchemaOperationKind.RebuildPhysicalIndex,
            rebuilt.Plan.Operations.Select(operation => operation.Kind));
        Assert.Contains(
            "by_owner",
            Collection(context, table).Indexes.List().ToList().Select(index => index["name"].AsString));
    }

    /// <summary>
    /// A plan derived from empty history creates every declared index. Where the collection already
    /// carries a different index under that name, the deployment is refused by a message that names
    /// the collection and the index, rather than by a raw driver error.
    /// </summary>
    [SkippableFact]
    public void An_index_that_already_exists_under_a_different_shape_is_refused_by_name()
    {
        var context = Context();
        var executor = new MongoSchemaExecutor(context);
        var table = Table();
        Assert.Equal(
            PhysicalSchemaApplicationOutcome.Applied,
            PhysicalSchemaApplication.Apply(Target(WithIndex(table, descending: false)), executor).Outcome);

        // The recorded history is gone, so the next plan is a full create over the deployed catalog.
        context.Database.GetCollection<BsonDocument>("__groundwork_metadata")
            .DeleteMany(new BsonDocument("kind", "schema-history"));

        var refused = Assert.Throws<InvalidOperationException>(() =>
            PhysicalSchemaApplication.Apply(Target(WithIndex(table, descending: true)), executor));

        Assert.Contains(table, refused.Message, StringComparison.Ordinal);
        Assert.Contains("by_owner", refused.Message, StringComparison.Ordinal);
        Assert.Contains("plans that as a rebuild", refused.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ fixtures

    private static readonly ColumnDefinition TotalColumn = new()
    {
        Name = "total",
        Type = PortableType.Decimal,
        IsNullable = true,
        Precision = 10,
        Scale = 2
    };

    private static StorageUnit Before(string table) => new()
    {
        Id = new StorageUnitId(table),
        Name = table,
        Columns =
        [
            new ColumnDefinition { Name = "id", Type = PortableType.String, MaxLength = 64, IsNullable = false },
            TotalColumn
        ],
        Key = new KeyDefinition { Columns = ["id"] }
    };

    private static StorageUnit After(string table) => new()
    {
        Id = new StorageUnitId(table),
        Name = table,
        Columns =
        [
            new ColumnDefinition { Name = "id", Type = PortableType.String, MaxLength = 64, IsNullable = false },
            new ColumnDefinition
            {
                Name = "total_amount",
                Type = PortableType.Decimal,
                IsNullable = true,
                Precision = 18,
                Scale = 4
            }
        ],
        Key = new KeyDefinition { Columns = ["id"] }
    };

    private static StorageUnit WithNote(string table, bool required) => new()
    {
        Id = new StorageUnitId(table),
        Name = table,
        Columns =
        [
            new ColumnDefinition { Name = "id", Type = PortableType.String, MaxLength = 64, IsNullable = false },
            new ColumnDefinition
            {
                Name = "note",
                Type = PortableType.String,
                MaxLength = 64,
                IsNullable = !required
            }
        ],
        Key = new KeyDefinition { Columns = ["id"] }
    };

    private static StorageUnit WithIndex(string table, bool descending) => new()
    {
        Id = new StorageUnitId(table),
        Name = table,
        Columns =
        [
            new ColumnDefinition { Name = "id", Type = PortableType.String, MaxLength = 64, IsNullable = false },
            new ColumnDefinition { Name = "owner", Type = PortableType.String, MaxLength = 64, IsNullable = false }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        Indexes =
        [
            new IndexDefinition
            {
                Name = "by_owner",
                Columns = [new IndexColumn("owner", descending ? Groundwork.Kernel.SortDirection.Descending : Groundwork.Kernel.SortDirection.Ascending)],
                MissingValues = MissingValueBehavior.Included
            }
        ]
    };

    private static StorageUnit Scoped(string id, string table) => new()
    {
        Id = new StorageUnitId(id),
        Name = table,
        Columns =
        [
            new ColumnDefinition { Name = "id", Type = PortableType.String, MaxLength = 64, IsNullable = false },
            new ColumnDefinition { Name = "owner", Type = PortableType.String, MaxLength = 64, IsNullable = false }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        Scope = ScopePolicy.Scoped
    };

    private static PhysicalSchemaTarget Target(StorageUnit unit, SchemaEvolutionMetadata? evolution = null) =>
        new(new SchemaSubject(MongoSchemaTargets.Physicalize(unit), evolution), MongoSchemaTargets.Provider);

    private static void Expire(MongoClientContext context, PhysicalSchemaTargetIdentity target) =>
        context.Database.GetCollection<BsonDocument>("__groundwork_metadata").UpdateOne(
            new BsonDocument("_id", "lock:" + target),
            new BsonDocument("$set", new BsonDocument("expiresAt", "0001-01-01T00:00:00.0000000+00:00")));

    private static IMongoCollection<BsonDocument> Collection(MongoClientContext context, string name) =>
        context.Database.GetCollection<BsonDocument>(name);

    private static string ConnectionString(MongoClientContext context) =>
        connectionStrings[context];

    private static string Table() => "mongo_exec_" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>A database of this test's own, dropped when the class finishes.</summary>
    private MongoClientContext Context()
    {
        var url = new MongoUrlBuilder(LiveMongo.Required());
        url.DatabaseName = url.DatabaseName + "_exec_" + Guid.NewGuid().ToString("N")[..8];
        var connectionString = url.ToMongoUrl().ToString();
        var context = new MongoClientContext(connectionString);
        connectionStrings[context] = connectionString;
        contexts.Add(context);
        return context;
    }

    private readonly List<MongoClientContext> contexts = [];

    private static readonly Dictionary<MongoClientContext, string> connectionStrings = [];

    public void Dispose()
    {
        foreach (var context in contexts)
        {
            try
            {
                context.Client.DropDatabase(context.Database.DatabaseNamespace.DatabaseName);
            }
            catch (MongoException)
            {
                // The server outliving the run is the container's business, not a test result.
            }
            context.Dispose();
        }
    }
}
