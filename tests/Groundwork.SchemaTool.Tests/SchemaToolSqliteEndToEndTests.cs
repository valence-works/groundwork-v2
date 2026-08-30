using System.Text.Json;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Sqlite;
using Groundwork.Store;
using Groundwork.Testing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Groundwork.SchemaTool.Tests;

public sealed class SchemaToolSqliteEndToEndTests : IDisposable
{
    [Fact]
    public async Task Interop_view_requires_exact_authorization_and_exposes_only_typed_application_and_scope_columns()
    {
        const string schemaDocument =
            """
            {"tables":[{"name":"reporting_orders","columns":[{"name":"id","type":"String","nullable":false,"length":64,"precision":null,"scale":null,"folding":"None","generation":"Supplied"},{"name":"total","type":"Decimal","nullable":false,"length":null,"precision":18,"scale":4,"folding":"None","generation":"Supplied"}],"key":["id"],"indexes":[],"scope":"Scoped","interopView":"reporting_orders_view"}]}
            """;
        var schema = harness.Temp("interop-view.json", schemaDocument);
        var database = Path.Combine(harness.Root, "interop-view.db");
        var connectionString = $"Data Source={database}";
        File.Create(database).Dispose();
        var unit = StorageUnit.Declare("reporting_orders", "reporting_orders")
            .String("id", 64, column => column.Required())
            .Decimal("total", 18, 4, column => column.Required())
            .Key("id")
            .Scoped()
            .InteropView("reporting_orders_view")
            .Build();

        using (var runtime = new SqliteProviderFactory().Create(connectionString))
        {
            var refusal = Assert.Throws<InvalidOperationException>(() => runtime.Schema.Apply(unit));
            Assert.Contains("GW-SCHEMA-010", refusal.Message, StringComparison.Ordinal);
        }

        var plan = await harness.RunAsync(["plan", "--schema", schema], connectionString);
        Assert.True(SchemaToolExitCodes.PendingChanges == plan.ExitCode, plan.Reason);
        Assert.Contains("ApplyProviderDefinition", PendingOperationKinds(plan));

        var safeOnly = await harness.RunAsync(["apply", "--schema", schema, "--safe"], connectionString);
        Assert.True(SchemaToolExitCodes.AuthorizationRequired == safeOnly.ExitCode, safeOnly.Reason);

        var apply = await harness.ApplyAuthorizedAsync(schema, connectionString);
        Assert.True(SchemaToolExitCodes.Success == apply.ExitCode, apply.Reason);

        using (var store = new SqliteProviderFactory().Create(connectionString))
        {
            Assert.Equal(WriteOutcomeStatus.Inserted, store
                .OpenSession(unit, StorageAccess.Scoped(new StorageScope("tenant-a")))
                .Insert(new StorageValues(new Dictionary<string, object?>
                {
                    ["id"] = "order-a",
                    ["total"] = 12.3400m
                })).Status);
            Assert.Equal(WriteOutcomeStatus.Inserted, store
                .OpenSession(unit, StorageAccess.Scoped(new StorageScope("tenant-b")))
                .Insert(new StorageValues(new Dictionary<string, object?>
                {
                    ["id"] = "order-b",
                    ["total"] = 98.7654m
                })).Status);
        }

        using var raw = new SqliteConnection(connectionString);
        raw.Open();
        RegisterReportingCollations(raw);
        using (var columns = raw.CreateCommand())
        {
            columns.CommandText = "SELECT name FROM pragma_table_info('reporting_orders_view') ORDER BY cid;";
            using var reader = columns.ExecuteReader();
            var names = new List<string>();
            while (reader.Read()) names.Add(reader.GetString(0));
            Assert.Equal(["id", "total", "__groundwork_scope"], names);
        }
        using (var rows = raw.CreateCommand())
        {
            rows.CommandText = "SELECT id,total,__groundwork_scope,typeof(total) FROM reporting_orders_view ORDER BY id;";
            using var reader = rows.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("order-a", reader.GetString(0));
            Assert.Equal(12.34d, reader.GetDouble(1), precision: 4);
            Assert.Equal("tenant-a", reader.GetString(2));
            Assert.Equal("real", reader.GetString(3));
            Assert.True(reader.Read());
            Assert.Equal("order-b", reader.GetString(0));
            Assert.Equal("tenant-b", reader.GetString(2));
            Assert.False(reader.Read());
        }
        using (var tamper = raw.CreateCommand())
        {
            tamper.CommandText =
                "DROP VIEW reporting_orders_view; " +
                "CREATE VIEW reporting_orders_view AS " +
                "SELECT id,total,__groundwork_scope FROM reporting_orders;";
            tamper.ExecuteNonQuery();
        }
        raw.Close();

        var drift = await harness.RunAsync(["status", "--schema", schema], connectionString);
        Assert.True(SchemaToolExitCodes.ValidationFailed == drift.ExitCode, drift.Reason);
        raw.Open();
        RegisterReportingCollations(raw);
        using (var simulateCommittedRemoval = raw.CreateCommand())
        {
            simulateCommittedRemoval.CommandText = "DROP VIEW reporting_orders_view;";
            simulateCommittedRemoval.ExecuteNonQuery();
        }
        raw.Close();

        var withoutView = harness.Temp(
            "interop-view-removed.json",
            schemaDocument.Replace(",\"interopView\":\"reporting_orders_view\"", string.Empty, StringComparison.Ordinal));
        var removalPlan = await harness.RunAsync(["plan", "--schema", withoutView], connectionString);
        Assert.True(SchemaToolExitCodes.PendingChanges == removalPlan.ExitCode, removalPlan.Reason);
        Assert.Contains("DropProviderDefinition", PendingOperationKinds(removalPlan));
        var remove = await harness.ApplyAuthorizedAsync(withoutView, connectionString);
        Assert.True(SchemaToolExitCodes.Success == remove.ExitCode, remove.Reason);

        using var catalog = new SqliteConnection(connectionString);
        catalog.Open();
        using var objects = catalog.CreateCommand();
        objects.CommandText =
            "SELECT type FROM sqlite_master WHERE name IN ('reporting_orders','reporting_orders_view') ORDER BY type;";
        using var objectReader = objects.ExecuteReader();
        Assert.True(objectReader.Read());
        Assert.Equal("table", objectReader.GetString(0));
        Assert.False(objectReader.Read());
        catalog.Close();

        var collisionDatabase = Path.Combine(harness.Root, "interop-view-collision.db");
        File.Create(collisionDatabase).Dispose();
        var collisionConnectionString = $"Data Source={collisionDatabase}";
        using var collisionCatalog = new SqliteConnection(collisionConnectionString);
        collisionCatalog.Open();
        using (var collision = collisionCatalog.CreateCommand())
        {
            collision.CommandText =
                "CREATE TABLE external_anchor (id TEXT); " +
                "CREATE INDEX reporting_orders_view ON external_anchor(id);";
            collision.ExecuteNonQuery();
        }
        collisionCatalog.Close();
        var collisionApply = await harness.ApplyAuthorizedAsync(schema, collisionConnectionString);
        Assert.Equal(SchemaToolExitCodes.ExecutionFailed, collisionApply.ExitCode);
        collisionCatalog.Open();
        using var collisionObjects = collisionCatalog.CreateCommand();
        collisionObjects.CommandText =
            "SELECT name,type FROM sqlite_master " +
            "WHERE name IN ('reporting_orders','reporting_orders_view') ORDER BY name;";
        using var collisionReader = collisionObjects.ExecuteReader();
        Assert.True(collisionReader.Read());
        Assert.Equal("reporting_orders_view", collisionReader.GetString(0));
        Assert.Equal("index", collisionReader.GetString(1));
        Assert.False(collisionReader.Read());
    }

    [Fact]
    public async Task Discovered_sqlite_factory_plans_applies_and_reports_status_against_a_real_file_database()
    {
        var schema = harness.Temp("schema.json", SchemaToolCliHarness.InitialSchema());
        var database = Path.Combine(harness.Root, "store.db");
        var connection = $"Data Source={database}";

        var missing = await harness.RunAsync(["plan", "--schema", schema], connection);
        Assert.True(SchemaToolExitCodes.ExecutionFailed == missing.ExitCode, missing.Reason);
        Assert.Contains("does not exist", missing.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(database));
        Assert.False(File.Exists(database + ".schema.lock"));

        var refused = await harness.RunAsync(
            ["apply", "--schema", schema, "--expected-plan", "not-the-current-plan"], connection);
        Assert.True(SchemaToolExitCodes.AuthorizationRequired == refused.ExitCode, refused.Reason);
        Assert.Equal("authorization-required", refused.Report.RootElement.GetProperty("outcome").GetString());
        Assert.False(File.Exists(database));
        Assert.False(File.Exists(database + ".schema.lock"));

        File.Create(database).Dispose();
        var plan = await harness.RunAsync(["plan", "--schema", schema], connection);
        Assert.True(SchemaToolExitCodes.PendingChanges == plan.ExitCode, plan.Reason);
        Assert.Equal("pending", plan.Report.RootElement.GetProperty("outcome").GetString());
        Assert.False(plan.Report.RootElement.GetProperty("targetMutated").GetBoolean());
        Assert.Equal(0, CountTables(connection));

        var apply = await harness.RunAsync(["apply", "--schema", schema, "--safe"], connection);
        Assert.True(SchemaToolExitCodes.Success == apply.ExitCode, apply.Reason);
        Assert.Equal("1", apply.Report.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal("applied", apply.Report.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("SQLite", apply.Report.RootElement.GetProperty("provider").GetProperty("name").GetString());
        Assert.Equal("1.0", apply.Report.RootElement.GetProperty("provider").GetProperty("version").GetString());
        Assert.True(apply.Report.RootElement.GetProperty("targetMutated").GetBoolean());
        Assert.True(File.Exists(database + ".schema.lock"));

        var status = await harness.RunAsync(["status", "--schema", schema], connection);
        Assert.True(SchemaToolExitCodes.Success == status.ExitCode, status.Reason);
        Assert.Equal("ready", status.Report.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(0, status.Report.RootElement.GetProperty("pendingOperations").GetArrayLength());
        Assert.True(status.Report.RootElement.GetProperty("appliedOperations").GetArrayLength() > 0);

        var evolved = harness.Temp("evolved.json", SchemaToolCliHarness.EvolvedSchema());
        var evolvedPlan = await harness.RunAsync(["plan", "--schema", evolved], connection);
        Assert.True(SchemaToolExitCodes.PendingChanges == evolvedPlan.ExitCode, evolvedPlan.Reason);
        Assert.True(evolvedPlan.Report.RootElement.GetProperty("pendingOperations").GetArrayLength() > 0);
        Assert.False(evolvedPlan.Report.RootElement
            .GetProperty("authorization").GetProperty("destructiveRequired").GetBoolean());
        var fingerprint = evolvedPlan.Report.RootElement.GetProperty("planFingerprint").GetString()!;

        var authorized = await harness.RunAsync(
            ["apply", "--schema", evolved, "--expected-plan", fingerprint], connection);
        Assert.True(SchemaToolExitCodes.Success == authorized.ExitCode, authorized.Reason);
        Assert.Equal("applied", authorized.Report.RootElement.GetProperty("outcome").GetString());

        var settled = await harness.RunAsync(["status", "--schema", evolved], connection);
        Assert.True(SchemaToolExitCodes.Success == settled.ExitCode, settled.Reason);
        Assert.Equal("ready", settled.Report.RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Sqlite_factory_honors_the_store_schema_lock_held_by_another_process()
    {
        var schema = harness.Temp("locked-schema.json", SchemaToolCliHarness.InitialSchema());
        var database = Path.Combine(harness.Root, "locked.db");
        var connection = $"Data Source={database}";
        var holder = new SqliteProviderFactory().Create(connection);
        try
        {
            var plan = await harness.RunAsync(["plan", "--schema", schema], connection);
            Assert.True(SchemaToolExitCodes.ExecutionFailed == plan.ExitCode, plan.Reason);
            Assert.Contains("GW-CLI-010", plan.Output, StringComparison.Ordinal);
            Assert.Contains("GW-SQLITE-LIFETIME-001", plan.Output, StringComparison.Ordinal);
        }
        finally
        {
            holder.Dispose();
            using var pooled = new SqliteConnection(connection);
            SqliteConnection.ClearPool(pooled);
        }
    }

    [Fact]
    public async Task Sqlite_factory_refuses_memory_data_sources_and_missing_connections_as_invocation_errors()
    {
        var schema = harness.Temp("refusal-schema.json", SchemaToolCliHarness.InitialSchema());

        var memory = await harness.RunAsync(["plan", "--schema", schema], "Data Source=:memory:");
        Assert.True(SchemaToolExitCodes.InvalidInvocation == memory.ExitCode, memory.Reason);
        Assert.Contains("GW-CLI-001", memory.Output, StringComparison.Ordinal);
        Assert.Contains("in-memory", memory.Output, StringComparison.Ordinal);

        var uriMemory = await harness.RunAsync(
            ["apply", "--schema", schema, "--safe"], "Data Source=file:refusal?mode=memory&cache=shared");
        Assert.True(SchemaToolExitCodes.InvalidInvocation == uriMemory.ExitCode, uriMemory.Reason);
        Assert.Contains("in-memory", uriMemory.Output, StringComparison.Ordinal);

        var unconnected = await harness.RunAsync(["plan", "--schema", schema]);
        Assert.True(SchemaToolExitCodes.InvalidInvocation == unconnected.ExitCode, unconnected.Reason);
        Assert.Contains("GW-CLI-001", unconnected.Output, StringComparison.Ordinal);
        Assert.Contains("--connection or --database", unconnected.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_catalog_from_an_earlier_schema_boundary_is_refused_by_name_not_by_exit_ten()
    {
        var schema = harness.Temp("boundary-schema.json", SchemaToolCliHarness.InitialSchema());
        var database = Path.Combine(harness.Root, "boundary.db");
        var connection = $"Data Source={database}";
        Assert.True(SchemaToolExitCodes.Success ==
            (await harness.RunAsync(["apply", "--schema", schema, "--safe"], connection)).ExitCode);

        // Rewrite the fingerprint the snapshot recorded, which is what an earlier build's
        // fingerprint boundary leaves behind.
        Execute(connection,
            """
            UPDATE __groundwork_schema_history SET state_json = replace(
                state_json,
                '"targetFingerprint":"' || target_fingerprint || '"',
                '"targetFingerprint":"stale-boundary"');
            """);

        var status = await harness.RunAsync(["status", "--schema", schema], connection);
        Assert.True(SchemaToolExitCodes.ValidationFailed == status.ExitCode, status.Reason);
        Assert.Contains(GroundworkSchemaBoundaryException.Code, status.Output, StringComparison.Ordinal);
        Assert.Contains("Discard that catalog", status.Output, StringComparison.Ordinal);

        var unit = StorageUnit.Declare("tickets", "tickets")
            .String("id", 64, column => column.Required())
            .Key("id")
            .Build();
        using var store = new SqliteProviderFactory().Create(connection);
        var failure = Assert.Throws<GroundworkSchemaBoundaryException>(() => store.Schema.Diff(unit));
        Assert.Contains("Discard that catalog", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_multi_target_schema_applies_under_every_target_plan_fingerprint()
    {
        var schema = harness.Temp("multi.json", SchemaToolCliHarness.MultiTargetSchema);
        var database = Path.Combine(harness.Root, "multi.db");
        var connection = $"Data Source={database}";
        File.Create(database).Dispose();

        var plan = await harness.RunAsync(["plan", "--schema", schema], connection);
        Assert.True(SchemaToolExitCodes.PendingChanges == plan.ExitCode, plan.Reason);
        Assert.Equal(2, plan.Report.RootElement.GetProperty("targets").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, plan.Report.RootElement.GetProperty("planFingerprint").ValueKind);

        var apply = await harness.ApplyAuthorizedAsync(schema, connection);
        Assert.True(SchemaToolExitCodes.Success == apply.ExitCode, apply.Reason);
        Assert.Equal("applied", apply.Report.RootElement.GetProperty("outcome").GetString());

        var status = await harness.RunAsync(["status", "--schema", schema], connection);
        Assert.True(SchemaToolExitCodes.Success == status.ExitCode, status.Reason);
        Assert.Equal("ready", status.Report.RootElement.GetProperty("outcome").GetString());
    }

    private static void Execute(string connectionString, string sql)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
        SqliteConnection.ClearPool(connection);
    }

    private static int CountTables(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table';";
        var count = Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        SqliteConnection.ClearPool(connection);
        return count;
    }

    private static string[] PendingOperationKinds(SchemaToolCliRun run) =>
        run.Report.RootElement.GetProperty("pendingOperations").EnumerateArray()
            .Select(operation => operation.GetProperty("kind").GetString()!)
            .ToArray();

    private static void RegisterReportingCollations(SqliteConnection connection)
    {
        connection.CreateCollation("GROUNDWORK_UTF16_ORDINAL", static (left, right) => string.CompareOrdinal(left, right));
        connection.CreateCollation("GROUNDWORK_DECIMAL_18_4", static (left, right) =>
            decimal.Parse(left, System.Globalization.CultureInfo.InvariantCulture)
                .CompareTo(decimal.Parse(right, System.Globalization.CultureInfo.InvariantCulture)));
    }

    private readonly SchemaToolCliHarness harness = new(
        static (arguments, output, error) => GroundworkSchemaCli.RunAsync(arguments, output, error),
        "sqlite",
        Path.Combine(AppContext.BaseDirectory, "Groundwork.Sqlite.dll"));

    public void Dispose() => harness.Dispose();
}
