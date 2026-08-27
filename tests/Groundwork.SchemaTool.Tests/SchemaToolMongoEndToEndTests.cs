using System.Text.Json;
using Groundwork.Kernel;
using Groundwork.LiveDatabases;
using Groundwork.MongoDb;
using Groundwork.Testing;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Groundwork.SchemaTool.Tests;

/// <summary>
/// <c>groundwork</c> driven against a live MongoDB replica set through the discovered
/// <c>mongodb</c> plug-in. These are the proofs behind "one declaration, four providers": the same
/// schema document, the same commands, and — where the vocabulary is provider-neutral — the same
/// operations and exit codes a relational target reports.
/// </summary>
public sealed class SchemaToolMongoEndToEndTests : IDisposable
{
    [SkippableFact]
    public async Task Discovered_mongodb_factory_plans_applies_and_reports_status_against_a_replica_set()
    {
        var connection = Database();
        var table = Table();
        var schema = harness.Temp("mongo-schema.json", SchemaToolCliHarness.InitialSchema(table));

        var plan = await harness.RunAsync(["plan", "--schema", schema], connection);
        Assert.True(SchemaToolExitCodes.PendingChanges == plan.ExitCode, plan.Reason);
        Assert.Equal("pending", plan.Report.RootElement.GetProperty("outcome").GetString());
        Assert.False(plan.Report.RootElement.GetProperty("targetMutated").GetBoolean());
        Assert.Empty(Collections(connection));

        var refused = await harness.RunAsync(
            ["apply", "--schema", schema, "--expected-plan", "not-the-current-plan"], connection);
        Assert.True(SchemaToolExitCodes.AuthorizationRequired == refused.ExitCode, refused.Reason);
        Assert.Equal("authorization-required", refused.Report.RootElement.GetProperty("outcome").GetString());
        Assert.Empty(Collections(connection));

        var apply = await harness.RunAsync(["apply", "--schema", schema, "--safe"], connection);
        Assert.True(SchemaToolExitCodes.Success == apply.ExitCode, apply.Reason);
        Assert.Equal("applied", apply.Report.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("mongodb", apply.Report.RootElement.GetProperty("provider").GetProperty("name").GetString());
        Assert.Equal("1.0", apply.Report.RootElement.GetProperty("provider").GetProperty("version").GetString());
        Assert.True(apply.Report.RootElement.GetProperty("targetMutated").GetBoolean());
        Assert.Contains(table, Collections(connection));

        var status = await harness.RunAsync(["status", "--schema", schema], connection);
        Assert.True(SchemaToolExitCodes.Success == status.ExitCode, status.Reason);
        Assert.Equal("ready", status.Report.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(0, status.Report.RootElement.GetProperty("pendingOperations").GetArrayLength());
        Assert.True(status.Report.RootElement.GetProperty("appliedOperations").GetArrayLength() > 0);

        var evolved = harness.Temp("mongo-evolved.json", SchemaToolCliHarness.EvolvedSchema(table));
        var evolvedPlan = await harness.RunAsync(["plan", "--schema", evolved], connection);
        Assert.True(SchemaToolExitCodes.PendingChanges == evolvedPlan.ExitCode, evolvedPlan.Reason);
        var fingerprint = evolvedPlan.Report.RootElement.GetProperty("planFingerprint").GetString()!;

        var authorized = await harness.RunAsync(
            ["apply", "--schema", evolved, "--expected-plan", fingerprint], connection);
        Assert.True(SchemaToolExitCodes.Success == authorized.ExitCode, authorized.Reason);
        Assert.Equal("applied", authorized.Report.RootElement.GetProperty("outcome").GetString());
        Assert.Contains("by_priority", IndexNames(connection, table));

        var settled = await harness.RunAsync(["status", "--schema", evolved], connection);
        Assert.True(SchemaToolExitCodes.Success == settled.ExitCode, settled.Reason);
        Assert.Equal("ready", settled.Report.RootElement.GetProperty("outcome").GetString());
    }

    /// <summary>
    /// One plan and report format across a table and a collection: the same schema document plans
    /// the same operation kinds under the same identities and authorization addresses, and the two
    /// commands exit with the same code. Only the target fingerprint differs, because it names the
    /// provider by construction.
    /// </summary>
    [SkippableFact]
    public async Task A_collection_and_a_table_report_the_same_plan_operations_identities_and_exit_codes()
    {
        var connection = Database();
        var mongoSchema = harness.Temp("parity-mongo.json", SchemaToolCliHarness.InitialSchema("parity_unit"));
        var sqliteFile = Path.Combine(harness.Root, "parity.db");
        File.Create(sqliteFile).Dispose();
        var sqliteSchema = sqlite.Temp("parity-sqlite.json", SchemaToolCliHarness.InitialSchema("parity_unit"));

        var mongoPlan = await harness.RunAsync(["plan", "--schema", mongoSchema], connection);
        var sqlitePlan = await sqlite.RunAsync(["plan", "--schema", sqliteSchema], $"Data Source={sqliteFile}");
        Assert.True(SchemaToolExitCodes.PendingChanges == mongoPlan.ExitCode, mongoPlan.Reason);
        Assert.Equal(sqlitePlan.ExitCode, mongoPlan.ExitCode);
        Assert.Equal(Operations(sqlitePlan, "pendingOperations"), Operations(mongoPlan, "pendingOperations"));
        Assert.Contains("ValidatePhysicalSchema", Kinds(mongoPlan, "pendingOperations"));
        Assert.Contains("PublishAppliedState", Kinds(mongoPlan, "pendingOperations"));
        Assert.Equal(
            sqlitePlan.Report.RootElement.GetProperty("outcome").GetString(),
            mongoPlan.Report.RootElement.GetProperty("outcome").GetString());
        Assert.NotEqual(
            sqlitePlan.Report.RootElement.GetProperty("targets")[0].GetProperty("fingerprint").GetString(),
            mongoPlan.Report.RootElement.GetProperty("targets")[0].GetProperty("fingerprint").GetString());

        var mongoApply = await harness.RunAsync(["apply", "--schema", mongoSchema, "--safe"], connection);
        var sqliteApply = await sqlite.RunAsync(["apply", "--schema", sqliteSchema, "--safe"], $"Data Source={sqliteFile}");
        Assert.True(SchemaToolExitCodes.Success == mongoApply.ExitCode, mongoApply.Reason);
        Assert.Equal(sqliteApply.ExitCode, mongoApply.ExitCode);
        Assert.Equal(Operations(sqliteApply, "appliedOperations"), Operations(mongoApply, "appliedOperations"));
    }

    /// <summary>
    /// The same destructive removal is authorized identically on a collection and on a table: the
    /// same readable address, the same refusal code, and the same exit code at every step.
    /// </summary>
    [SkippableFact]
    public async Task A_destructive_removal_is_authorized_identically_on_a_collection_and_on_a_table()
    {
        var mongoConnection = Database();
        var sqliteFile = Path.Combine(harness.Root, "destructive.db");
        File.Create(sqliteFile).Dispose();
        var sqliteConnection = $"Data Source={sqliteFile}";
        var declared = harness.Temp("orders.json", SchemaToolCliHarness.OrdersSchema());
        var reduced = harness.Temp("orders-reduced.json", SchemaToolCliHarness.OrdersSchema(includeLegacyTotal: false));

        var mongoSeed = await harness.RunAsync(["apply", "--schema", declared, "--safe"], mongoConnection);
        var sqliteSeed = await sqlite.RunAsync(["apply", "--schema", declared, "--safe"], sqliteConnection);
        Assert.True(SchemaToolExitCodes.Success == mongoSeed.ExitCode, mongoSeed.Reason);
        Assert.Equal(sqliteSeed.ExitCode, mongoSeed.ExitCode);

        var mongoPlan = await harness.RunAsync(["plan", "--schema", reduced], mongoConnection);
        var sqlitePlan = await sqlite.RunAsync(["plan", "--schema", reduced], sqliteConnection);
        Assert.True(SchemaToolExitCodes.PendingChanges == mongoPlan.ExitCode, mongoPlan.Reason);
        Assert.Equal(sqlitePlan.ExitCode, mongoPlan.ExitCode);
        Assert.Equal(Authorizations(sqlitePlan), Authorizations(mongoPlan));
        Assert.Equal(new[] { "drop-column:orders.legacy_total" }, Authorizations(mongoPlan));

        // --safe alone never authorizes a destructive plan, on either target.
        var mongoUnsafe = await harness.RunAsync(["apply", "--schema", reduced, "--safe"], mongoConnection);
        var sqliteUnsafe = await sqlite.RunAsync(["apply", "--schema", reduced, "--safe"], sqliteConnection);
        Assert.True(SchemaToolExitCodes.AuthorizationRequired == mongoUnsafe.ExitCode, mongoUnsafe.Reason);
        Assert.Equal(sqliteUnsafe.ExitCode, mongoUnsafe.ExitCode);
        Assert.Equal(Codes(sqliteUnsafe), Codes(mongoUnsafe));
        Assert.Equal(new[] { "GW-CLI-011" }, Codes(mongoUnsafe));

        var mongoApply = await harness.ApplyAuthorizedAsync(reduced, mongoConnection);
        var sqliteApply = await sqlite.ApplyAuthorizedAsync(reduced, sqliteConnection);
        Assert.True(SchemaToolExitCodes.Success == mongoApply.ExitCode, mongoApply.Reason);
        Assert.Equal(sqliteApply.ExitCode, mongoApply.ExitCode);
        Assert.Equal("applied", mongoApply.Report.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(Operations(sqliteApply, "appliedOperations"), Operations(mongoApply, "appliedOperations"));

        var status = await harness.RunAsync(["status", "--schema", reduced], mongoConnection);
        Assert.True(SchemaToolExitCodes.Success == status.ExitCode, status.Reason);
        Assert.Equal("ready", status.Report.RootElement.GetProperty("outcome").GetString());
    }

    /// <summary>
    /// The in-process coordinator and deployment tool both plan the rename against the applied
    /// ledger, so the stored field moves and keeps its value instead of a new field appearing beside
    /// the old one.
    /// </summary>
    [SkippableFact]
    public async Task A_column_rename_carries_its_logical_id_and_moves_the_stored_field()
    {
        var connection = Database();
        var declared = harness.Temp("rename-declared.json", SchemaToolCliHarness.OrdersSchema());
        var renamed = harness.Temp("rename-applied.json",
            SchemaToolCliHarness.OrdersSchema(customerColumn: "buyer"));
        Assert.True(SchemaToolExitCodes.Success ==
            (await harness.RunAsync(["apply", "--schema", declared, "--safe"], connection)).ExitCode);

        Collection(connection, "orders").InsertOne(new BsonDocument
        {
            ["_id"] = "order-1",
            ["id"] = "order-1",
            ["customer"] = "ada",
            ["legacy_total"] = BsonNull.Value
        });

        var plan = await harness.RunAsync(["plan", "--schema", renamed], connection);
        Assert.True(SchemaToolExitCodes.PendingChanges == plan.ExitCode, plan.Reason);
        Assert.Contains("RenameColumn", Kinds(plan, "pendingOperations"));
        Assert.DoesNotContain("DropColumn", Kinds(plan, "pendingOperations"));

        var apply = await harness.ApplyAuthorizedAsync(renamed, connection);
        Assert.True(SchemaToolExitCodes.Success == apply.ExitCode, apply.Reason);

        var stored = Collection(connection, "orders").Find(new BsonDocument("_id", "order-1")).Single();
        Assert.Equal("ada", stored["buyer"].AsString);
        Assert.False(stored.Contains("customer"));

        var status = await harness.RunAsync(["status", "--schema", renamed], connection);
        Assert.True(SchemaToolExitCodes.Success == status.ExitCode, status.Reason);
        Assert.Equal("ready", status.Report.RootElement.GetProperty("outcome").GetString());
    }

    /// <summary>
    /// Adoption over a collection set Groundwork has no record of applying, mirroring the relational
    /// proof: the ledger it publishes is the one an apply publishes, operation for operation.
    /// </summary>
    [SkippableFact]
    public async Task Adopt_records_an_existing_collection_set_and_refuses_one_that_differs()
    {
        var connection = Database();
        var table = Table();
        var schema = harness.Temp("adopt-mongo.json", SchemaToolCliHarness.EvolvedSchema(table));
        var apply = await harness.RunAsync(["apply", "--schema", schema, "--safe"], connection);
        Assert.True(SchemaToolExitCodes.Success == apply.ExitCode, apply.Reason);
        var appliedOperations = Operations(apply, "appliedOperations");
        ForgetHistory(connection);

        var unauthorized = await harness.RunAsync(["adopt", "--schema", schema], connection);
        Assert.True(SchemaToolExitCodes.InvalidInvocation == unauthorized.ExitCode, unauthorized.Reason);

        var adopt = await harness.RunAsync(["adopt", "--schema", schema, "--safe"], connection);
        Assert.True(SchemaToolExitCodes.Success == adopt.ExitCode, adopt.Reason);
        Assert.Equal("adopted", adopt.Report.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(appliedOperations, Operations(adopt, "appliedOperations"));
        Assert.Equal(
            apply.Report.RootElement.GetProperty("appliedTargetFingerprint").GetString(),
            adopt.Report.RootElement.GetProperty("appliedTargetFingerprint").GetString());

        var again = await harness.RunAsync(["adopt", "--schema", schema, "--safe"], connection);
        Assert.True(SchemaToolExitCodes.Success == again.ExitCode, again.Reason);
        Assert.Equal("ready", again.Report.RootElement.GetProperty("outcome").GetString());

        // A catalog that is not the declared one is refused by name, publishing nothing.
        ForgetHistory(connection);
        Collection(connection, table).Indexes.DropOne("by_priority");
        var refused = await harness.RunAsync(["adopt", "--schema", schema, "--safe"], connection);
        Assert.True(SchemaToolExitCodes.ValidationFailed == refused.ExitCode, refused.Reason);
        Assert.Equal("blocked", refused.Report.RootElement.GetProperty("outcome").GetString());
        Assert.Contains("GW-RUNTIME-002", refused.Output, StringComparison.Ordinal);
        Assert.Contains("by_priority", refused.Output, StringComparison.Ordinal);
        Assert.Null(HistoryJson(connection));
    }

    /// <summary>
    /// A scoped unit lives in one collection per scope. Index work spans every one of them, and
    /// adoption verifies every one of them, so a scoped collection set is not half-deployed and not
    /// half-verified.
    /// </summary>
    [SkippableFact]
    public async Task Index_work_and_adoption_span_every_per_scope_collection()
    {
        var connection = Database();
        var table = Table();
        var initial = harness.Temp("scoped-initial.json", ScopedSchema(table, withIndex: false));
        Assert.True(SchemaToolExitCodes.Success ==
            (await harness.RunAsync(["apply", "--schema", initial, "--safe"], connection)).ExitCode);

        // The runtime materializes the per-scope collections; the tool then deploys into them.
        using (var store = new MongoDbProviderFactory().Create(connection))
        {
            foreach (var scope in new[] { "tenant-a", "tenant-b" })
                store.OpenSession(ScopedDeclaration(table), MongoStorageAccess.Scoped(new StorageScope(scope)));
        }

        var scoped = Collections(connection)
            .Where(name => name.StartsWith(table + "__scope__", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, scoped.Length);

        var evolved = harness.Temp("scoped-evolved.json", ScopedSchema(table, withIndex: true));
        var apply = await harness.ApplyAuthorizedAsync(evolved, connection);
        Assert.True(SchemaToolExitCodes.Success == apply.ExitCode, apply.Reason);
        foreach (var name in scoped)
            Assert.Contains("by_owner", IndexNames(connection, name));

        // Adoption reads the same collection set: dropping the index from one scope alone refuses,
        // naming the scope collection that differs.
        ForgetHistory(connection);
        Collection(connection, scoped[0]).Indexes.DropOne("by_owner");
        var refused = await harness.RunAsync(["adopt", "--schema", evolved, "--safe"], connection);
        Assert.True(SchemaToolExitCodes.ValidationFailed == refused.ExitCode, refused.Reason);
        Assert.Contains(scoped[0], refused.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A collection the deployment tool applied opens at runtime with no in-process apply: the
    /// provider catalog the tool published in <c>__groundwork_metadata</c> is the one
    /// <c>MongoProviderState.Resolve</c> compares its declaration against.
    /// </summary>
    [SkippableFact]
    public async Task A_tool_applied_collection_opens_at_runtime_without_a_second_apply()
    {
        var connection = Database();
        var table = Table();
        var schema = harness.Temp("runtime-parity.json", SchemaToolCliHarness.ParitySchema(table));
        var apply = await harness.ApplyAuthorizedAsync(schema, connection);
        Assert.True(SchemaToolExitCodes.Success == apply.ExitCode, apply.Reason);

        var declaration = SchemaToolCliHarness.ParityDeclaration(table);
        Assert.Equal(
            MongoSchemaTargets.Compile(declaration).Fingerprint,
            apply.Report.RootElement.GetProperty("appliedTargetFingerprint").GetString());

        using var store = new MongoDbProviderFactory().Create(connection);
        var session = store.OpenSession(declaration, MongoStorageAccess.Scoped(new StorageScope("tenant-a")));
        session.Insert(new MongoStorageValues(new Dictionary<string, object?>
        {
            ["id"] = "row-1",
            ["customer"] = "Ada",
            ["status"] = "pending"
        }));

        Assert.True(store.Schema.Diff(declaration).IsEmpty);
    }

    private static string ScopedSchema(string table, bool withIndex) =>
        $$"""
        {"tables":[{"name":"{{table}}","columns":[{"name":"id","type":"String","nullable":false,"length":64,"precision":null,"scale":null,"folding":"None","generation":"Supplied"},{"name":"owner","type":"String","nullable":false,"length":64,"precision":null,"scale":null,"folding":"None","generation":"Supplied"}],"key":["id"],"indexes":[{{(withIndex ? """{"name":"by_owner","columns":[{"name":"owner","descending":false}],"includeNulls":true,"unique":false}""" : "")}}],"scope":"Scoped"}]}
        """;

    private static StorageUnit ScopedDeclaration(string table) =>
        StorageUnit.Declare(table, table)
            .String("id", 64, column => column.Required())
            .String("owner", 64, column => column.Required())
            .Key("id")
            .Scoped()
            .Build();

    /// <summary>
    /// Every operation the declaration itself names, as the report spells it. Operations over
    /// provider-owned columns are excluded because those are not declared: SQLite physicalizes an
    /// append-action column that MongoDB has no equivalent of, and comparing them would be
    /// comparing two physicalizations rather than one report format. The two target-scoped
    /// bookkeeping operations are excluded for the same reason: their identity is derived from the
    /// target fingerprint, which names the provider by construction.
    /// </summary>
    private static string[] Operations(SchemaToolCliRun run, string property) =>
        run.Report.RootElement.GetProperty(property).EnumerateArray()
            .Where(operation => !operation.GetProperty("subjectIdentity").GetString()!
                .StartsWith("__groundwork_", StringComparison.Ordinal))
            .Where(operation => operation.GetProperty("kind").GetString() is not
                ("ValidatePhysicalSchema" or "PublishAppliedState"))
            .Select(operation => operation.GetProperty("kind").GetString() + " " +
                                 operation.GetProperty("identity").GetString())
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToArray();

    private static string[] Kinds(SchemaToolCliRun run, string property) =>
        run.Report.RootElement.GetProperty(property).EnumerateArray()
            .Select(operation => operation.GetProperty("kind").GetString()!)
            .ToArray();

    private static string[] Authorizations(SchemaToolCliRun run) =>
        run.Report.RootElement.GetProperty("authorization")
            .GetProperty("destructiveOperationsRequired").EnumerateArray()
            .Select(value => value.GetString()!)
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToArray();

    private static string[] Codes(SchemaToolCliRun run) =>
        run.Report.RootElement.GetProperty("targets").EnumerateArray()
            .SelectMany(target => target.GetProperty("diagnostics").EnumerateArray())
            .Select(diagnostic => diagnostic.GetProperty("code").GetString()!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToArray();

    private void ForgetHistory(string connectionString) =>
        Metadata(connectionString).DeleteMany(new BsonDocument("kind", "schema-history"));

    private string? HistoryJson(string connectionString) =>
        Metadata(connectionString)
            .Find(new BsonDocument("kind", "schema-history"))
            .FirstOrDefault()?["stateJson"].AsString;

    private IMongoCollection<BsonDocument> Metadata(string connectionString) =>
        Collection(connectionString, "__groundwork_metadata");

    private IMongoCollection<BsonDocument> Collection(string connectionString, string name) =>
        Client(connectionString).GetDatabase(new MongoUrl(connectionString).DatabaseName)
            .GetCollection<BsonDocument>(name);

    private IReadOnlyList<string> Collections(string connectionString) =>
        Client(connectionString).GetDatabase(new MongoUrl(connectionString).DatabaseName)
            .ListCollectionNames().ToList();

    private IReadOnlyList<string> IndexNames(string connectionString, string collection) =>
        Collection(connectionString, collection).Indexes.List().ToList()
            .Select(index => index["name"].AsString)
            .ToArray();

    private MongoClient Client(string connectionString)
    {
        lock (clients)
        {
            if (!clients.TryGetValue(connectionString, out var client))
            {
                client = new MongoClient(connectionString);
                clients.Add(connectionString, client);
            }
            return client;
        }
    }

    /// <summary>
    /// A database of this test's own. Each proof deploys schema into it, so sharing one would let
    /// a collection created by one test change what another one plans.
    /// </summary>
    private string Database()
    {
        var url = new MongoUrlBuilder(LiveMongo.Required());
        url.DatabaseName = url.DatabaseName + "_tool_" + Guid.NewGuid().ToString("N")[..8];
        var connectionString = url.ToMongoUrl().ToString();
        databases.Add(connectionString);
        return connectionString;
    }

    private static string Table() => "mongo_tool_" + Guid.NewGuid().ToString("N")[..12];

    private readonly Dictionary<string, MongoClient> clients = new(StringComparer.Ordinal);
    private readonly List<string> databases = [];

    private readonly SchemaToolCliHarness harness = new(
        static (arguments, output, error) => GroundworkSchemaCli.RunAsync(arguments, output, error),
        "mongodb",
        Path.Combine(AppContext.BaseDirectory, "Groundwork.MongoDb.dll"));

    private readonly SchemaToolCliHarness sqlite = new(
        static (arguments, output, error) => GroundworkSchemaCli.RunAsync(arguments, output, error),
        "sqlite",
        Path.Combine(AppContext.BaseDirectory, "Groundwork.Sqlite.dll"));

    public void Dispose()
    {
        foreach (var connectionString in databases)
        {
            try
            {
                Client(connectionString).DropDatabase(new MongoUrl(connectionString).DatabaseName);
            }
            catch (MongoException)
            {
                // The server outliving the run is the container's business, not a test result.
            }
        }
        foreach (var client in clients.Values)
            client.Dispose();
        harness.Dispose();
        sqlite.Dispose();
    }
}
