using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Groundwork.Kernel;
using Groundwork.SqlServer;
using Groundwork.Testing;
using Groundwork.Store;
using Xunit;

namespace Groundwork.SqlServer.Tests;

[Collection("SQL Server provider")]
public sealed class SqlServerProviderTests(SqlServerFixture fixture)
{
    [Fact]
    public void Provider_passes_provider_neutral_conformance()
    {
        fixture.Reset();
        using var connection = new SqlServerProviderFactory().Create(fixture.ConnectionString);
        var report = ConformanceSuite.Run(new SqlServerProviderFactory(), fixture.ConnectionString);
        Assert.True(report.Passed, string.Join(Environment.NewLine,
            report.Checks.Where(check => !check.Passed).Select(check => $"{check.Name}: {check.Failure}")));
    }

    [Fact]
    public void Live_compare_and_delete_preserves_revision_cas_and_exact_rollback()
    {
        fixture.Reset();
        using var connection = new SqlServerProviderFactory().Create(fixture.ConnectionString);
        var name = "compare_delete_" + Guid.NewGuid().ToString("N");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId(name),
            Name = name,
            Columns =
            [
                new ColumnDefinition { Name = "id", Type = PortableType.String, MaxLength = 100, IsNullable = false },
                new ColumnDefinition { Name = "owner", Type = PortableType.String, MaxLength = 100 },
                new ColumnDefinition { Name = "fence", Type = PortableType.Int64, IsNullable = false },
                new ColumnDefinition { Name = "amount", Type = PortableType.Decimal, Precision = 12, Scale = 2 }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Concurrency = ConcurrencyDeclaration.Optimistic()
        };
        Assert.True(connection.Schema.Apply(unit).Applied);
        var marker = new StorageUnit
        {
            Id = new StorageUnitId(name + "_marker"),
            Name = name + "_marker",
            Columns =
            [
                new ColumnDefinition { Name = "id", Type = PortableType.String, MaxLength = 100, IsNullable = false },
                new ColumnDefinition { Name = "value", Type = PortableType.String, MaxLength = 100, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] }
        };
        Assert.True(connection.Schema.Apply(marker).Applied);
        Assert.Contains(connection.Capabilities, capability => capability.Id == BatchWriteCapabilities.CompareAndDelete);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        Assert.Equal(1L, session.Insert(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "claim-1", ["owner"] = "worker-a", ["fence"] = 7L
        })).Version);
        session.Insert(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "claim-decimal", ["owner"] = "worker-a", ["fence"] = 7L, ["amount"] = 7m
        }));
        Assert.Equal(WriteOutcomeStatus.Deleted,
            session.CompareAndDelete(new StorageKey(new Dictionary<string, object?> { ["id"] = "claim-decimal" }),
                new Dictionary<string, object?> { ["amount"] = 7 }).Status);

        Assert.Equal(WriteOutcomeStatus.ComparisonMismatch,
            session.CompareAndDelete(new StorageKey(new Dictionary<string, object?> { ["id"] = "claim-1" }),
                new Dictionary<string, object?> { ["owner"] = "worker-b", ["fence"] = 7L }).Status);
        session.Insert(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "claim-trailing", ["owner"] = "worker-a", ["fence"] = 7L
        }));
        Assert.Equal(WriteOutcomeStatus.ComparisonMismatch,
            session.CompareAndDelete(new StorageKey(new Dictionary<string, object?> { ["id"] = "claim-trailing" }),
                new Dictionary<string, object?> { ["owner"] = "worker-a ", ["fence"] = 7L }).Status);
        Assert.NotNull(session.Read(new StorageKey(new Dictionary<string, object?> { ["id"] = "claim-trailing" })));
        session.Insert(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "claim-key", ["owner"] = "worker-a", ["fence"] = 7L
        }));
        Assert.Equal(WriteOutcomeStatus.NotFound,
            session.CompareAndDelete(new StorageKey(new Dictionary<string, object?> { ["id"] = "claim-key " }),
                new Dictionary<string, object?> { ["owner"] = "worker-a", ["fence"] = 7L }).Status);
        Assert.NotNull(session.Read(new StorageKey(new Dictionary<string, object?> { ["id"] = "claim-key" })));
        Assert.Equal(2L, session.Update(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "claim-1", ["owner"] = "worker-a", ["fence"] = 7L
        }), WriteOptions.IfVersion(1)).Version);
        var deleted = session.CompareAndDelete(new StorageKey(new Dictionary<string, object?> { ["id"] = "claim-1" }),
            new Dictionary<string, object?> { ["owner"] = "worker-a", ["fence"] = 7L });
        Assert.Equal(WriteOutcomeStatus.Deleted, deleted.Status);
        Assert.Equal(2L, deleted.Version);
        Assert.Equal(WriteOutcomeStatus.NotFound,
            session.CompareAndDelete(new StorageKey(new Dictionary<string, object?> { ["id"] = "claim-1" }),
                new Dictionary<string, object?> { ["owner"] = "worker-a" }).Status);

        session.Insert(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "claim-null", ["owner"] = null, ["fence"] = 7L
        }));
        Assert.Equal(WriteOutcomeStatus.Deleted,
            session.CompareAndDelete(new StorageKey(new Dictionary<string, object?> { ["id"] = "claim-null" }),
                new Dictionary<string, object?> { ["owner"] = null }).Status);

        session.Insert(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "claim-omitted", ["fence"] = 7L
        }));
        Assert.Equal(WriteOutcomeStatus.Deleted,
            session.CompareAndDelete(new StorageKey(new Dictionary<string, object?> { ["id"] = "claim-omitted" }),
                new Dictionary<string, object?> { ["owner"] = null }).Status);

        session.Insert(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "claim-2", ["owner"] = "worker-a", ["fence"] = 7L
        }));
        var claimed = session.Read(new StorageKey(new Dictionary<string, object?> { ["id"] = "claim-2" }))!;
        var reclaimer = connection.OpenSession(unit, StorageAccess.Global);
        Assert.Equal(2L, reclaimer.Update(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "claim-2", ["owner"] = "worker-b", ["fence"] = 8L
        }), WriteOptions.IfVersion(claimed.Version!.Value)).Version);
        using var work = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, unit, marker);
        work.Stage(RowWrite.Insert(marker, new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "marker", ["value"] = "must-rollback"
        })));
        var compare = RowWrite.CompareAndDelete(unit,
            new StorageKey(new Dictionary<string, object?> { ["id"] = "claim-2" }),
            new Dictionary<string, object?> { ["owner"] = "worker-a", ["fence"] = 7L });
        work.Stage(compare);
        var exception = Assert.Throws<BatchWriteException>(() => work.CommitWithOutcomes());
        var outcome = Assert.Single(exception.Outcomes);
        Assert.Same(compare, outcome.Write);
        Assert.Equal(WriteOutcomeStatus.ComparisonMismatch, outcome.Outcome.Status);
        var reclaimed = session.Read(new StorageKey(new Dictionary<string, object?> { ["id"] = "claim-2" }))!;
        Assert.Equal("worker-b", reclaimed.Values.Values["owner"]);
        Assert.Equal(8L, reclaimed.Values.Values["fence"]);
        Assert.Equal(2L, reclaimed.Version);
        Assert.Null(connection.OpenSession(marker, StorageAccess.Global).Read(
            new StorageKey(new Dictionary<string, object?> { ["id"] = "marker" })));
    }

    [Fact]
    public void W2_concurrency_harness_holds_every_named_invariant_for_the_full_matrix()
    {
        foreach (var (keyCount, includePartialUniqueIndex, optimistic) in W2Shapes())
        {
            fixture.Reset();
            var report = ConcurrencyHarness.Run(
                new StorageProviderConcurrencyFactory("sqlserver", new SqlServerProviderFactory()),
                fixture.ConnectionString,
                new ConcurrencyProbeOptions
                {
                    WriterCount = 32,
                    KeyCount = keyCount,
                    RepeatCount = 2,
                    Seed = 5245 + (keyCount == 1000 ? 1000 : 0) +
                        (includePartialUniqueIndex ? 100 : 0) + (optimistic ? 10 : 0),
                    Concurrency = optimistic ? ConcurrencyKind.Optimistic : ConcurrencyKind.None,
                    IncludePartialUniqueIndex = includePartialUniqueIndex
                });

            var shape = $"M={keyCount}, partial={includePartialUniqueIndex}, optimistic={optimistic}";
            Assert.True(report.Passed, $"{shape}{Environment.NewLine}{Describe(report)}");
            Assert.All(report.Scenarios.SelectMany(scenario => scenario.Invariants), invariant =>
                Assert.True(invariant.Passed, $"{shape}: {invariant.Name}: {invariant.Detail}"));
        }
    }

    private static IEnumerable<(int KeyCount, bool IncludePartialUniqueIndex, bool Optimistic)> W2Shapes()
    {
        foreach (var keyCount in new[] { 1, 1000 })
        foreach (var includePartialUniqueIndex in new[] { false, true })
        foreach (var optimistic in new[] { false, true })
            yield return (keyCount, includePartialUniqueIndex, optimistic);
    }

    [Fact]
    public void Customer_email_320_is_a_native_unique_index()
    {
        fixture.Reset();
        using var connection = new SqlServerProviderFactory().Create(fixture.ConnectionString);
        var name = "customer_" + Guid.NewGuid().ToString("N");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId(name), Name = name,
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, MaxLength = 450, IsNullable = false },
                new() { Name = "email", Type = PortableType.String, MaxLength = 320, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Indexes = [new IndexDefinition { Name = "by_email", Columns = [new IndexColumn("email")], IsUnique = true }]
        };

        Assert.True(connection.Schema.Apply(unit).Applied);
        var indexes = connection.Catalog.ReadIndexes(unit.Id);
        var email = Assert.Single(indexes, index => index.Name == "by_email");
        Assert.True(email.IsUnique);
        Assert.Equal("email", Assert.Single(email.Columns).Column);
    }

    [Fact]
    public void A_63_byte_storage_unit_name_applies_without_provider_rewriting()
    {
        fixture.Reset();
        using var connection = new SqlServerProviderFactory().Create(fixture.ConnectionString);
        var name = new string('a', PortabilityValidator.MaximumPortableIdentifierLength);
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("logical.boundary.id"),
            Name = name,
            Columns = [new ColumnDefinition { Name = "id", Type = PortableType.Int32, IsNullable = false }],
            Key = new KeyDefinition { Columns = ["id"] }
        };

        Assert.True(connection.Schema.Apply(unit).Applied);
        Assert.True(connection.Schema.Diff(unit).IsEmpty);
    }

    [Fact]
    public void Exact_batch_writes_with_an_unconstrained_logical_id_use_the_validated_physical_type_name()
    {
        fixture.Reset();
        using var connection = new SqlServerProviderFactory().Create(fixture.ConnectionString);
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("logical.id/with spaces/" + new string('x', 80)),
            Name = "sqlserver_batch_boundary",
            Columns =
            [
                new() { Name = "id", Type = PortableType.Int32, IsNullable = false },
                new() { Name = "payload", Type = PortableType.String, MaxLength = 64, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] }
        };

        Assert.True(connection.Schema.Apply(unit).Applied);
        using var work = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, unit);
        work.Stage(RowWrite.Insert(unit, new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = 1,
            ["payload"] = "one"
        })));
        work.Stage(RowWrite.Insert(unit, new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = 2,
            ["payload"] = "two"
        })));

        var report = work.CommitWithOutcomes();

        Assert.Equal(2, report.Summary.Succeeded);
        Assert.All(report.Outcomes, outcome => Assert.Equal(WriteOutcomeStatus.Inserted, outcome.Outcome.Status));
    }

    [Fact]
    public void Lifecycle_identity_columns_use_binary_collation_and_preserve_case_distinct_scopes_and_nonces()
    {
        fixture.Reset();
        using var connection = new SqlServerProviderFactory().Create(fixture.ConnectionString);
        var name = "s7_sqlserver_lifecycle_" + Guid.NewGuid().ToString("N");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId(name),
            Name = name,
            Scope = ScopePolicy.Scoped,
            Columns =
            [
                new() { Name = "sequence", Type = PortableType.Int64, IsNullable = false, Generation = ColumnGeneration.ProviderSequence },
                new() { Name = "payload", Type = PortableType.String, MaxLength = 100, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["sequence"] },
            Retention = new RetentionDeclaration
            {
                KeepNewest = 1,
                OrderColumn = "sequence",
                Trigger = RetentionTrigger.Explicit
            },
            RetentionIdempotency = new RetentionIdempotencyDeclaration { Window = TimeSpan.FromMinutes(10) },
            AppendIdempotency = new AppendIdempotencyDeclaration { Window = TimeSpan.FromMinutes(10) }
        };

        Assert.True(connection.Schema.Apply(unit).Applied);
        var upper = connection.OpenSession(unit, StorageAccess.Scoped(new StorageScope("A")));
        var lower = connection.OpenSession(unit, StorageAccess.Scoped(new StorageScope("a")));
        upper.Insert(new StorageValues(new Dictionary<string, object?> { ["payload"] = "upper-1" }));
        upper.Insert(new StorageValues(new Dictionary<string, object?> { ["payload"] = "upper-2" }));
        lower.Insert(new StorageValues(new Dictionary<string, object?> { ["payload"] = "lower-1" }));
        lower.Insert(new StorageValues(new Dictionary<string, object?> { ["payload"] = "lower-2" }));

        Assert.Equal(2L, upper.Inspect().LifetimeCommittedSequenceHighWater);
        Assert.Equal(4L, lower.Inspect().LifetimeCommittedSequenceHighWater);
        Assert.Equal(WriteOutcomeStatus.Inserted,
            upper.Append(new OperationId(DateTimeOffset.UtcNow, "AppendCase"),
                [new StorageValues(new Dictionary<string, object?> { ["payload"] = "append-upper" })]).Status);
        Assert.Equal(WriteOutcomeStatus.Inserted,
            upper.Append(new OperationId(DateTimeOffset.UtcNow, "appendcase"),
                [new StorageValues(new Dictionary<string, object?> { ["payload"] = "append-lower" })]).Status);
        Assert.Equal(RetentionOperationStatus.Executed,
            upper.ApplyRetention(new OperationId(DateTimeOffset.UtcNow, "CaseNonce")).Status);
        Assert.Equal(RetentionOperationStatus.Executed,
            upper.ApplyRetention(new OperationId(DateTimeOffset.UtcNow, "casenonce")).Status);
        Assert.Equal(RetentionOperationStatus.Executed,
            lower.ApplyRetention(new OperationId(DateTimeOffset.UtcNow, "CaseNonce")).Status);

        using var sql = new SqlConnection(fixture.ConnectionString);
        sql.Open();
        using var command = sql.CreateCommand();
        command.CommandText = """
            SELECT t.name, c.name, c.collation_name
            FROM sys.tables t
            JOIN sys.columns c ON c.object_id = t.object_id
            WHERE t.name IN (N'__groundwork_sequence_high_waters', N'__groundwork_operations', N'__groundwork_retention_operations')
              AND c.name IN (N'unit', N'scope', N'nonce');
            """;
        using var reader = command.ExecuteReader();
        var collations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
            collations[$"{reader.GetString(0)}.{reader.GetString(1)}"] = reader.GetString(2);

        Assert.Equal("Latin1_General_100_BIN2", collations["__groundwork_sequence_high_waters.unit"]);
        Assert.Equal("Latin1_General_100_BIN2", collations["__groundwork_sequence_high_waters.scope"]);
        Assert.Equal("Latin1_General_100_BIN2", collations["__groundwork_retention_operations.unit"]);
        Assert.Equal("Latin1_General_100_BIN2", collations["__groundwork_retention_operations.scope"]);
        Assert.Equal("Latin1_General_100_BIN2", collations["__groundwork_retention_operations.nonce"]);
        Assert.Equal("Latin1_General_100_BIN2", collations["__groundwork_operations.unit"]);
        Assert.Equal("Latin1_General_100_BIN2", collations["__groundwork_operations.scope"]);
        Assert.Equal("Latin1_General_100_BIN2", collations["__groundwork_operations.nonce"]);
    }

    [Fact]
    public void Existing_lifecycle_table_with_legacy_collation_is_refused_with_migration_guidance()
    {
        fixture.Reset();
        using var connection = new SqlServerProviderFactory().Create(fixture.ConnectionString);
        var name = "s7_sqlserver_legacy_lifecycle_" + Guid.NewGuid().ToString("N");
        var ledger = "s7_legacy_retention_" + Guid.NewGuid().ToString("N");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId(name),
            Name = name,
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, MaxLength = 100, IsNullable = false },
                new() { Name = "payload", Type = PortableType.String, MaxLength = 100, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Retention = new RetentionDeclaration { KeepNewest = 1, OrderColumn = "id", Trigger = RetentionTrigger.Explicit },
            RetentionIdempotency = new RetentionIdempotencyDeclaration
            {
                Window = TimeSpan.FromMinutes(10),
                LedgerName = ledger
            }
        };

        Assert.True(connection.Schema.Apply(unit).Applied);
        using (var sql = new SqlConnection(fixture.ConnectionString))
        {
            sql.Open();
            using var create = sql.CreateCommand();
            create.CommandText = $"CREATE TABLE [{ledger}] (unit nvarchar(450) NOT NULL, scope nvarchar(128) NOT NULL, nonce nvarchar(256) NOT NULL, committed_at nvarchar(64) NOT NULL, input_fingerprint nvarchar(128) NULL, exact_result nvarchar(max) NULL, PRIMARY KEY NONCLUSTERED (unit, scope, nonce));";
            create.ExecuteNonQuery();
        }

        var refusal = Assert.Throws<InvalidOperationException>(() =>
            connection.OpenSession(unit, StorageAccess.Global).ApplyRetention(
                new OperationId(DateTimeOffset.UtcNow, "legacy")));
        Assert.StartsWith("GW-SQLSERVER-LIFECYCLE-001", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Recreate or migrate", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unbounded_primary_string_is_refused_before_connection_open()
    {
        using var connection = new SqlServerProviderFactory().Create(
            "Server=invalid-host.invalid,1433;Database=master;User Id=sa;Password=Groundwork!2026;Encrypt=False;TrustServerCertificate=True");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("unbounded-key"), Name = "unbounded_key",
            Columns = [new ColumnDefinition { Name = "id", Type = PortableType.String, IsNullable = false }],
            Key = new KeyDefinition { Columns = ["id"] }
        };

        var exception = Assert.Throws<SqlServerKeyBudgetException>(() => connection.Schema.Diff(unit));
        Assert.Contains("bounded String key column", exception.Message, StringComparison.Ordinal);
    }

    private static string Describe(ConcurrencyHarnessReport report) =>
        string.Join(Environment.NewLine, report.Scenarios.SelectMany(scenario =>
            scenario.Invariants.Select(invariant =>
                $"seed={scenario.Seed} {invariant.Name}: {invariant.Passed} ({invariant.Detail})")));

}

[CollectionDefinition("SQL Server provider", DisableParallelization = true)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
}

public sealed class SqlServerFixture : IAsyncLifetime
{
    private MsSqlContainer? container;

    public string ConnectionString { get; private set; } = string.Empty;

    public void Reset()
    {
        using var connection = new SqlConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DECLARE @sql nvarchar(max) = N'';
            SELECT @sql += N'DROP TABLE ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + N';'
            FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
            WHERE t.name IN (N'conformance-global',N'conformance-scoped',N'__groundwork_schema_history',N'__groundwork_schema_fences',N'__groundwork_sequence_high_waters',N'__groundwork_operations',N'__groundwork_retention_operations')
               OR t.name LIKE N'customer[_]%'
               OR t.name LIKE N'w2_sqlserver[_]%'
               OR t.name LIKE N's7_sqlserver[_]%'
               OR t.name LIKE N's7_legacy_retention[_]%';
            IF @sql <> N'' EXEC sys.sp_executesql @sql;
            """;
        command.ExecuteNonQuery();
    }

    public async Task InitializeAsync()
    {
        ConnectionString = Environment.GetEnvironmentVariable("GROUNDWORK_SQLSERVER_CONNECTION") ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(ConnectionString)) return;

        container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU21-ubuntu-22.04")
            .WithPassword("Groundwork!2026")
            .Build();
        await container.StartAsync();
        ConnectionString = container.GetConnectionString();
    }

    public async Task DisposeAsync()
    {
        if (container is not null) await container.DisposeAsync();
    }
}
