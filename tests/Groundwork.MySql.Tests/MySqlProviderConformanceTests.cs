using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.LiveDatabases;
using Groundwork.Store;
using Groundwork.Substrate.Relational;
using Groundwork.Testing;
using MySqlConnector;
using Xunit;

namespace Groundwork.MySql.Tests;

public sealed class MySqlProviderConformanceTests
{
    [SkippableFact]
    public async Task Provider_passes_the_shipped_conformance_suite_on_both_surfaces()
    {
        using var database = LiveMySqlDatabase.OpenOrSkip();

        var synchronous = ConformanceSuite.Run(new MySqlProviderFactory(), database.ConnectionString);
        Assert.True(synchronous.Passed, Describe(synchronous));

        var asynchronous = await ConformanceSuite.RunAsync(
            new MySqlProviderFactory(),
            database.ConnectionString);
        Assert.True(asynchronous.Passed, Describe(asynchronous));
    }

    [SkippableFact]
    public void Schema_preserves_no_pad_keys_checks_and_expression_defaults()
    {
        using var database = LiveMySqlDatabase.OpenOrSkip();
        const string defaultText = "slash\\newline\nquote'control\u001a";
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("mysql-schema-contract"),
            Name = "mysql_schema_contract",
            Columns =
            [
                new ColumnDefinition { Name = "id", Type = PortableType.String, MaxLength = 32, IsNullable = false },
                new ColumnDefinition { Name = "quantity", Type = PortableType.Int32, IsNullable = false, Default = new PortableDefault(1) },
                new ColumnDefinition { Name = "note", Type = PortableType.String, IsNullable = false, Default = new PortableDefault(defaultText) }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            CheckConstraints =
            [
                new CheckConstraintDefinition
                {
                    Name = "ck_mysql_quantity",
                    Column = "quantity",
                    Operator = CheckConstraintOperator.GreaterThan,
                    Value = new PortableDefault(0)
                }
            ]
        };

        using var provider = new MySqlProviderFactory().Create(database.ConnectionString);
        Assert.True(provider.Schema.Apply(unit).Applied);
        using var session = provider.OpenOwnedSession(unit, StorageAccess.Global);
        Assert.True(session.Insert(new StorageValues(new Dictionary<string, object?> { ["id"] = "a" })).Succeeded);
        Assert.True(session.Insert(new StorageValues(new Dictionary<string, object?> { ["id"] = "a " })).Succeeded);
        var stored = Assert.IsType<StoredEntry>(session.Read(new StorageKey(new Dictionary<string, object?> { ["id"] = "a" })));
        Assert.Equal(defaultText, stored.Values.Values["note"]);
        Assert.Throws<MySqlException>(() => session.Insert(new StorageValues(
            new Dictionary<string, object?> { ["id"] = "invalid", ["quantity"] = 0 })));
    }

    [SkippableFact]
    public void Schema_replay_skips_ddl_committed_before_a_later_batch_failure()
    {
        using var database = LiveMySqlDatabase.OpenOrSkip();
        var legacy = new ColumnDefinition
        {
            Name = "legacy",
            Type = PortableType.Int32,
            IsNullable = true
        };
        var initial = new StorageUnit
        {
            Id = new StorageUnitId("mysql-schema-replay"),
            Name = "mysql_schema_replay",
            Columns =
            [
                new ColumnDefinition { Name = "id", Type = PortableType.Int32, IsNullable = false },
                legacy
            ],
            Key = new KeyDefinition { Columns = ["id"] }
        };
        using (var provider = new MySqlProviderFactory().Create(database.ConnectionString))
            Assert.True(provider.Schema.Apply(initial).Applied);

        var added = new ColumnDefinition { Name = "payload", Type = PortableType.String, MaxLength = 40, IsNullable = true };
        var evolved = initial with
        {
            Columns =
            [
                initial.Columns[0],
                legacy with { Name = "renamed", Id = legacy.LogicalId },
                added
            ]
        };
        var target = new PhysicalSchemaTarget(
            new SchemaSubject(evolved),
            new ProviderIdentity("MySQL/MariaDB", "1.0"));
        var executor = new RelationalSchemaExecutor(
            () => new MySqlConnection(database.ConnectionString),
            new MySqlDialect());

        using var lease = executor.AcquireApplicationLock(target.Identity);
        var history = executor.ReadHistory(target.Identity, lease);
        var plan = PhysicalSchemaDiffPlanner.Plan(target, history, DateTimeOffset.UtcNow);
        var add = Assert.Single(plan.Operations.OfType<AddColumnOperation>());
        var rename = Assert.Single(plan.Operations.OfType<RenameColumnOperation>());

        // Simulate external drift after planning. MySQL commits the first ALTER even though the
        // second fails, so the replay must acknowledge the already-present column rather than
        // issuing the same ADD COLUMN again.
        using (var drift = new MySqlConnection(database.ConnectionString))
        {
            drift.Open();
            using var command = drift.CreateCommand();
            command.CommandText = "ALTER TABLE `mysql_schema_replay` DROP COLUMN `legacy`;";
            command.ExecuteNonQuery();
        }

        Assert.Throws<MySqlException>(() => executor.ApplyOperationBatch(target.Identity, [add, rename], lease));
        Assert.Single(executor.ApplyOperationBatch(target.Identity, [add], lease));

        using var connection = new MySqlConnection(database.ConnectionString);
        connection.Open();
        Assert.Contains("payload", new MySqlDialect().ReadColumns(connection, null, initial.Name).Keys);
    }

    [SkippableFact]
    public void Scoped_provider_sequences_keep_point_writes_tenant_safe()
    {
        using var database = LiveMySqlDatabase.OpenOrSkip();
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("mysql-scoped-sequence"),
            Name = "mysql_scoped_sequence",
            Scope = ScopePolicy.Scoped,
            Columns =
            [
                new ColumnDefinition
                {
                    Name = "sequence",
                    Type = PortableType.Int64,
                    IsNullable = false,
                    Generation = ColumnGeneration.ProviderSequence
                },
                new ColumnDefinition { Name = "payload", Type = PortableType.String, MaxLength = 40 }
            ],
            Key = new KeyDefinition { Columns = ["sequence"] }
        };

        using var provider = new MySqlProviderFactory().Create(database.ConnectionString);
        Assert.True(provider.Schema.Apply(unit).Applied);
        using var first = provider.OpenOwnedSession(
            unit,
            StorageAccess.Scoped(new StorageScope("tenant-a")));
        using var second = provider.OpenOwnedSession(
            unit,
            StorageAccess.Scoped(new StorageScope("tenant-b")));
        var inserted = first.Insert(new StorageValues(
            new Dictionary<string, object?> { ["payload"] = "tenant-a" }));
        var sequence = inserted.GeneratedValue<long>("sequence");
        var values = new StorageValues(new Dictionary<string, object?>
        {
            ["sequence"] = sequence,
            ["payload"] = "tenant-b"
        });

        Assert.Equal(WriteOutcomeStatus.NotFound, second.Update(values).Status);
        Assert.Equal(WriteOutcomeStatus.NotFound, second.Upsert(values).Status);
        Assert.Equal(
            WriteOutcomeStatus.UniqueViolation,
            Assert.IsAssignableFrom<IConcurrencyStorageSession>(second).ConditionalUpsert(values).Status);
        Assert.Null(second.Read(new StorageKey(
            new Dictionary<string, object?> { ["sequence"] = sequence })));
        Assert.Equal(
            "tenant-a",
            first.Read(new StorageKey(
                new Dictionary<string, object?> { ["sequence"] = sequence }))!.Values.Values["payload"]);
    }

    [SkippableFact]
    public void Idempotency_ledgers_accept_the_full_operation_nonce_contract()
    {
        using var database = LiveMySqlDatabase.OpenOrSkip();
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("mysql-full-nonce"),
            Name = "mysql_full_nonce",
            Columns =
            [
                new ColumnDefinition { Name = "id", Type = PortableType.Int32, IsNullable = false },
                new ColumnDefinition { Name = "payload", Type = PortableType.String, MaxLength = 40 }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            AppendIdempotency = new AppendIdempotencyDeclaration { Window = TimeSpan.FromHours(1) }
        };

        using var provider = new MySqlProviderFactory().Create(database.ConnectionString);
        Assert.True(provider.Schema.Apply(unit).Applied);
        using var session = provider.OpenOwnedSession(unit, StorageAccess.Global);
        var result = session.Append(
            new OperationId(DateTimeOffset.UtcNow, new string('n', 256)),
            [new StorageValues(new Dictionary<string, object?> { ["id"] = 1, ["payload"] = "ok" })]);

        Assert.Equal(WriteOutcomeStatus.Inserted, result.Status);
    }

    [SkippableFact]
    public void Conditional_inserts_run_on_append_retention()
    {
        using var database = LiveMySqlDatabase.OpenOrSkip();
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("mysql-conditional-retention"),
            Name = "mysql_conditional_retention",
            Columns =
            [
                new ColumnDefinition { Name = "id", Type = PortableType.String, MaxLength = 32, IsNullable = false },
                new ColumnDefinition { Name = "ordering", Type = PortableType.Int64, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Retention = new RetentionDeclaration
            {
                KeepNewest = 2,
                OrderColumn = "ordering",
                Trigger = RetentionTrigger.OnAppend
            }
        };

        using var provider = new MySqlProviderFactory().Create(database.ConnectionString);
        Assert.True(provider.Schema.Apply(unit).Applied);
        using var session = provider.OpenOwnedSession(unit, StorageAccess.Global);
        var conditional = Assert.IsAssignableFrom<IConcurrencyStorageSession>(session);
        for (var index = 0; index < 5; index++)
        {
            Assert.Equal(
                WriteOutcomeStatus.Inserted,
                conditional.ConditionalUpsert(new StorageValues(new Dictionary<string, object?>
                {
                    ["id"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["ordering"] = (long)index
                })).Status);
        }

        for (var index = 0; index < 3; index++)
        {
            Assert.Null(session.Read(new StorageKey(
                new Dictionary<string, object?>
                {
                    ["id"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture)
                })));
        }
        for (var index = 3; index < 5; index++)
        {
            Assert.NotNull(session.Read(new StorageKey(
                new Dictionary<string, object?>
                {
                    ["id"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture)
                })));
        }
    }

    private static string Describe(ConformanceReport report) => string.Join(
        Environment.NewLine,
        report.Failures.Select(failure => $"{failure.Name}: {failure.Failure}"));
}
