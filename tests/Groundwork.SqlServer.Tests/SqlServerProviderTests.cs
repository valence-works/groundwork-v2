using Microsoft.Data.SqlClient;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.LiveDatabases;
using Groundwork.SqlServer;
using Groundwork.Substrate.Relational;
using Groundwork.Testing;
using Groundwork.Store;
using Xunit;

namespace Groundwork.SqlServer.Tests;

[Collection(SqlServerLiveDatabase.Name)]
public sealed class SqlServerProviderTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public void Physical_foreign_keys_and_checks_apply_as_native_sqlserver_constraints()
    {
        var connectionString = fixture.Reset();
        using var connection = new SqlServerProviderFactory().Create(connectionString);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var customer = StorageUnit.Declare("sql-customer-" + suffix, "customer_constraint_target_" + suffix)
            .Guid("id", column => column.Required())
            .Key("id")
            .Build();
        var order = StorageUnit.Declare("sql-order-" + suffix, "customer_constraint_source_" + suffix)
            .Guid("id", column => column.Required())
            .Guid("customer_id", column => column.Required())
            .Int32("quantity", column => column.Required())
            .Key("id")
            .Index("by_customer", "customer_id")
            .PhysicalReference("fk_order_customer", customer, "customer_id")
            .Check("ck_order_quantity", "quantity", CheckConstraintOperator.GreaterThan, 0)
            .Build();

        Assert.True(connection.Schema.Apply(customer).Applied);
        Assert.True(connection.Schema.Apply(order).Applied);
        Assert.True(connection.Schema.Diff(order).IsEmpty);

        using var raw = new SqlConnection(connectionString);
        raw.Open();
        using var command = raw.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.objects o JOIN sys.tables t ON t.object_id=o.parent_object_id WHERE t.name=@table AND o.name IN (N'fk_order_customer',N'ck_order_quantity');";
        command.Parameters.AddWithValue("@table", order.Name);
        Assert.Equal(2, Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }
    [SkippableFact]
    public void Owned_session_marker_matches_the_opening_path()
    {
        var connectionString = fixture.Reset();
        using var connection = new SqlServerProviderFactory().Create(connectionString);
        var name = "sql_session_ownership_" + Guid.NewGuid().ToString("N");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId(name),
            Name = name,
            Columns = [new ColumnDefinition { Name = "id", Type = PortableType.String, MaxLength = 64, IsNullable = false }],
            Key = new KeyDefinition { Columns = ["id"] }
        };
        Assert.True(connection.Schema.Apply(unit).Applied);

        Assert.False(connection.OpenSession(unit, StorageAccess.Global) is IOwnedStorageSession);
        using (var work = connection.BeginUnitOfWork(StorageAccess.Global, unit))
            Assert.False(work.OpenSession(unit) is IOwnedStorageSession);

        var owned = connection.OpenOwnedSession(unit, StorageAccess.Global);
        Assert.IsAssignableFrom<IOwnedStorageSession>(owned);
        owned.Dispose();
        Assert.Throws<ObjectDisposedException>(() => owned.Read(new StorageKey(
            new Dictionary<string, object?> { ["id"] = "after-release" })));
    }

    [SkippableFact]
    public void Owned_sessions_return_each_connection_to_a_bounded_pool()
    {
        var source = fixture.Reset();
        var builder = new SqlConnectionStringBuilder(source)
        {
            MaxPoolSize = 1,
            ConnectTimeout = 2
        };
        using var connection = new SqlServerProviderFactory().Create(builder.ConnectionString);
        var name = "sql_session_pool_" + Guid.NewGuid().ToString("N");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId(name),
            Name = name,
            Columns = [new ColumnDefinition { Name = "id", Type = PortableType.String, MaxLength = 64, IsNullable = false }],
            Key = new KeyDefinition { Columns = ["id"] }
        };
        Assert.True(connection.Schema.Apply(unit).Applied);

        for (var index = 0; index < 8; index++)
        {
            using var owned = connection.OpenOwnedSession(unit, StorageAccess.Global);
            Assert.IsAssignableFrom<IOwnedStorageSession>(owned);
        }
    }

    [SkippableFact]
    public async Task Provider_disposal_cannot_miss_a_registering_legacy_session()
    {
        var source = fixture.Reset();
        var pool = new SqlConnectionStringBuilder(source)
        {
            MaxPoolSize = 1,
            ConnectTimeout = 2
        };
        using var connection = new SqlServerProviderFactory().Create(pool.ConnectionString);
        var name = "sql_session_registration_" + Guid.NewGuid().ToString("N");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId(name),
            Name = name,
            Columns = [new ColumnDefinition { Name = "id", Type = PortableType.String, MaxLength = 64, IsNullable = false }],
            Key = new KeyDefinition { Columns = ["id"] }
        };
        Assert.True(connection.Schema.Apply(unit).Applied);
        using var observer = new RegistrationBarrierObserver();

        var opening = Task.Run(() => connection.OpenSession(unit, StorageAccess.Global, observer));
        Assert.True(observer.EligibilityChecked.Wait(TimeSpan.FromSeconds(5)), "Session registration did not reach the eligibility check.");
        var disposing = Task.Run(connection.Dispose);
        Assert.True(observer.DisposalAttempted.Wait(TimeSpan.FromSeconds(5)), "Provider disposal did not reach the registration boundary.");

        observer.Release.Set();
        var session = await opening;
        await disposing;

        Assert.Throws<ObjectDisposedException>(() => session.Read(new StorageKey(
            new Dictionary<string, object?> { ["id"] = "after-provider" })));
        using var returned = new SqlConnection(pool.ConnectionString);
        returned.Open();
    }

    [SkippableFact]
    public void Owned_deferred_conflict_detail_rejects_provider_disposal()
    {
        var connectionString = fixture.Reset();
        using var connection = new SqlServerProviderFactory().Create(connectionString);
        var name = "sql_owned_deferred_" + Guid.NewGuid().ToString("N");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId(name),
            Name = name,
            Columns =
            [
                new ColumnDefinition { Name = "id", Type = PortableType.String, MaxLength = 64, IsNullable = false },
                new ColumnDefinition { Name = "value", Type = PortableType.String, MaxLength = 64, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Concurrency = ConcurrencyDeclaration.Optimistic()
        };
        Assert.True(connection.Schema.Apply(unit).Applied);
        using var owned = connection.OpenOwnedSession(unit, StorageAccess.Global);
        var values = new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "one",
            ["value"] = "value"
        });
        Assert.Equal(WriteOutcomeStatus.Inserted, owned.Insert(values).Status);
        var stale = Assert.IsAssignableFrom<IConcurrencyStorageSession>(owned).ConditionalUpsert(
            values,
            new WriteOptions { Precondition = WritePrecondition.IfVersion(99) });
        Assert.Equal(WriteOutcomeStatus.ConcurrencyConflict, stale.Status);

        connection.Dispose();

        Assert.Throws<ObjectDisposedException>(() => { _ = stale.Detail; });
    }

    [SkippableFact]
    public async Task Provider_passes_provider_neutral_conformance_on_both_surfaces()
    {
        var connectionString = fixture.Reset();
        using (new SqlServerProviderFactory().Create(connectionString))
        {
            // Both surfaces run against the one live database, without a reset between them:
            // each proves the whole contract on its own storage units.
            var synchronous = ConformanceSuite.Run(new SqlServerProviderFactory(), connectionString);
            Assert.True(synchronous.Passed, Describe(synchronous));

            var asynchronous = await ConformanceSuite.RunAsync(new SqlServerProviderFactory(), connectionString);
            Assert.True(asynchronous.Passed, Describe(asynchronous));
        }
    }

    private static string Describe(ConformanceReport report) => string.Join(Environment.NewLine,
        report.Checks.Where(check => !check.Passed).Select(check => $"{check.Name}: {check.Failure}"));

    [SkippableFact]
    public async Task Batch_fallback_completes_without_reentering_the_connection_gate()
    {
        var connectionString = fixture.Reset();
        using var connection = new SqlServerProviderFactory().Create(connectionString);
        var name = "nested_write_" + Guid.NewGuid().ToString("N");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId(name),
            Name = name,
            Columns =
            [
                new ColumnDefinition { Name = "id", Type = PortableType.String, MaxLength = 64, IsNullable = false },
                new ColumnDefinition { Name = "value", Type = PortableType.String, MaxLength = 64, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Concurrency = ConcurrencyDeclaration.Optimistic()
        };
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        var batched = Assert.IsAssignableFrom<IBatchedStorageSession>(session);

        // A non-unconditional precondition takes the batch fallback. The fallback must reuse the
        // batch transaction without waiting for the non-reentrant connection gate it already holds.
        var write = RowWrite.Upsert(
            unit,
            new StorageValues(new Dictionary<string, object?> { ["id"] = "a", ["value"] = "nested" }),
            WriteOptions.CreateOnly);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var outcomes = await batched.ApplyBatchAsync([write], exactOutcomes: true, timeout.Token);
        Assert.Equal(WriteOutcomeStatus.Upserted, outcomes.Single().Outcome.Status);
    }

    [SkippableFact]
    public async Task Shared_session_serializes_reads_while_on_append_retention_runs()
    {
        var connectionString = fixture.Reset();
        using var connection = new SqlServerProviderFactory().Create(connectionString);
        var name = "on_append_gate_" + Guid.NewGuid().ToString("N");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId(name),
            Name = name,
            Columns =
            [
                new ColumnDefinition { Name = "id", Type = PortableType.String, MaxLength = 64, IsNullable = false },
                new ColumnDefinition { Name = "payload", Type = PortableType.String, MaxLength = 64, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Retention = new RetentionDeclaration
            {
                KeepNewest = 1,
                OrderColumn = "id",
                Trigger = RetentionTrigger.OnAppend
            }
        };
        Assert.True(connection.Schema.Apply(unit).Applied);

        using var observer = new BlockingRetentionObserver();
        var session = connection.OpenSession(unit, StorageAccess.Global, observer);
        var first = Task.Run(() => session.Insert(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "first", ["payload"] = "payload"
        })));
        Assert.True(observer.RetentionEntered.Wait(TimeSpan.FromSeconds(5)),
            "On-append retention did not start in time.");
        observer.CheckForOverlap.Set();

        using var secondStarted = new ManualResetEventSlim();
        var second = Task.Run(() =>
        {
            secondStarted.Set();
            return session.Read(new StorageKey(
                new Dictionary<string, object?> { ["id"] = "missing" }));
        });
        Assert.True(secondStarted.Wait(TimeSpan.FromSeconds(5)), "The second caller did not start in time.");
        await Task.Delay(TimeSpan.FromMilliseconds(150));
        Assert.False(observer.Overlapped,
            "A shared-session read reached SQL Server while retention held the session gate.");

        observer.Release.Set();
        Assert.True((await first).Succeeded);
        Assert.Null(await second);
    }

    [SkippableFact]
    public async Task Queued_async_read_honors_cancellation_while_shared_session_gate_is_held()
    {
        var connectionString = fixture.Reset();
        using var connection = new SqlServerProviderFactory().Create(connectionString);
        var name = "sql_read_cancel_" + Guid.NewGuid().ToString("N");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId(name),
            Name = name,
            Columns = [new ColumnDefinition { Name = "id", Type = PortableType.String, MaxLength = 64, IsNullable = false }],
            Key = new KeyDefinition { Columns = ["id"] }
        };
        Assert.True(connection.Schema.Apply(unit).Applied);

        using var observer = new BlockingReadObserver();
        var session = connection.OpenSession(unit, StorageAccess.Global, observer);
        var first = Task.Run(async () => await session.ReadAsync(new StorageKey(
            new Dictionary<string, object?> { ["id"] = "first" })));
        Assert.True(observer.ReadEntered.Wait(TimeSpan.FromSeconds(5)),
            "The first read did not reach the provider command in time.");

        using var cancellation = new CancellationTokenSource();
        var queued = session.ReadAsync(
            new StorageKey(new Dictionary<string, object?> { ["id"] = "queued" }), cancellation.Token).AsTask();
        cancellation.Cancel();
        var completed = await Task.WhenAny(queued, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(queued, completed);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await queued);

        observer.Release.Set();
        Assert.Null(await first);
    }

    [SkippableFact]
    public void Live_compare_and_delete_preserves_revision_cas_and_exact_rollback()
    {
        var connectionString = fixture.Reset();
        using var connection = new SqlServerProviderFactory().Create(connectionString);
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

    [SkippableFact]
    [Trait("Category", "Concurrency")]
    public void W2_concurrency_harness_holds_every_named_invariant_for_the_full_matrix()
    {
        foreach (var (keyCount, includePartialUniqueIndex, optimistic) in W2Shapes())
        {
            var connectionString = fixture.Reset();
            var report = ConcurrencyHarness.Run(
                new StorageProviderConcurrencyFactory("sqlserver", new SqlServerProviderFactory()),
                connectionString,
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

    [SkippableFact]
    public void Customer_email_320_is_a_native_unique_index()
    {
        var connectionString = fixture.Reset();
        using var connection = new SqlServerProviderFactory().Create(connectionString);
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

    [SkippableFact]
    public void A_63_byte_storage_unit_name_applies_without_provider_rewriting()
    {
        var connectionString = fixture.Reset();
        using var connection = new SqlServerProviderFactory().Create(connectionString);
        // A per-run GUID keeps the name unique across reruns while still landing exactly on the
        // boundary length the test exists to prove.
        var name = BoundaryName();
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

    [SkippableFact]
    public void Exact_batch_writes_with_an_unconstrained_logical_id_use_the_validated_physical_type_name()
    {
        var connectionString = fixture.Reset();
        using var connection = new SqlServerProviderFactory().Create(connectionString);
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("logical.id/with spaces/" + new string('x', 80)),
            Name = "sqlserver_batch_boundary_" + Guid.NewGuid().ToString("N"),
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

    [SkippableFact]
    public void Lifecycle_identity_columns_use_binary_collation_and_preserve_case_distinct_scopes_and_nonces()
    {
        var connectionString = fixture.Reset();
        using var connection = new SqlServerProviderFactory().Create(connectionString);
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

        using var sql = new SqlConnection(connectionString);
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

    [SkippableFact]
    public void Existing_lifecycle_table_with_legacy_collation_is_refused_with_migration_guidance()
    {
        var connectionString = fixture.Reset();
        using var connection = new SqlServerProviderFactory().Create(connectionString);
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
        using (var sql = new SqlConnection(connectionString))
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

    [SkippableFact]
    public void Live_dropped_column_on_a_plain_unit_is_fatal_at_session_open()
    {
        var connectionString = fixture.Reset();
        var name = "w2_sqlserver_admission_drop_" + Guid.NewGuid().ToString("N");
        var unit = AdmissionUnit(name);
        using (var connection = new SqlServerProviderFactory().Create(connectionString))
        {
            Assert.True(connection.Schema.Apply(unit).Applied);
        }

        using (var sql = new SqlConnection(connectionString))
        {
            sql.Open();
            using var alter = sql.CreateCommand();
            alter.CommandText = $"ALTER TABLE [{name}] DROP COLUMN [payload];";
            alter.ExecuteNonQuery();
        }

        using var reopened = new SqlServerProviderFactory().Create(connectionString);
        var failure = Assert.Throws<GroundworkRuntimeSchemaAdmissionException>(() => reopened.OpenSession(unit, StorageAccess.Global));
        Assert.Contains("GW-RUNTIME-001", failure.Message, StringComparison.Ordinal);
        Assert.Contains("payload", failure.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Live_admission_inspects_once_per_unit_per_connection()
    {
        var connectionString = fixture.Reset();
        var name = "w2_sqlserver_admission_cache_" + Guid.NewGuid().ToString("N");
        var unit = AdmissionUnit(name);
        using (var connection = new SqlServerProviderFactory().Create(connectionString))
        {
            Assert.True(connection.Schema.Apply(unit).Applied);
        }

        using var reopened = new SqlServerProviderFactory().Create(connectionString);
        var firstObserver = new ProviderCommandObserver();
        _ = reopened.OpenSession(unit, StorageAccess.Global, firstObserver);
        var admissionEvent = Assert.Single(firstObserver.Commands);
        Assert.Equal("sqlserver.schema-admission", admissionEvent.Operation);
        Assert.Equal(ProviderCommandKind.Read, admissionEvent.Kind);

        var secondObserver = new ProviderCommandObserver();
        _ = reopened.OpenSession(unit, StorageAccess.Global, secondObserver);
        Assert.Equal(0, secondObserver.RoundTrips);
    }

    /// <summary>
    /// A name landing exactly on <see cref="PortabilityValidator.MaximumPortableIdentifierLength"/>,
    /// unique per call so a rerun against the same database does not collide with a table an earlier
    /// run left behind.
    /// </summary>
    private static string BoundaryName()
    {
        var name = ("boundary_" + Guid.NewGuid().ToString("N")).PadRight(
            PortabilityValidator.MaximumPortableIdentifierLength, 'a');
        return name[..PortabilityValidator.MaximumPortableIdentifierLength];
    }

    private static StorageUnit AdmissionUnit(string name) => new()
    {
        Id = new StorageUnitId(name),
        Name = name,
        Columns =
        [
            new ColumnDefinition { Name = "id", Type = PortableType.String, MaxLength = 100, IsNullable = false },
            new ColumnDefinition { Name = "payload", Type = PortableType.String, MaxLength = 200 }
        ],
        Key = new KeyDefinition { Columns = ["id"] }
    };

    private static string Describe(ConcurrencyHarnessReport report) =>
        string.Join(Environment.NewLine, report.Scenarios.SelectMany(scenario =>
            scenario.Invariants.Select(invariant =>
                $"seed={scenario.Seed} {invariant.Name}: {invariant.Passed} ({invariant.Detail})")));

    private sealed class BlockingRetentionObserver : IProviderCommandObserver, IDisposable
    {
        private int overlapped;

        internal ManualResetEventSlim RetentionEntered { get; } = new();
        internal ManualResetEventSlim CheckForOverlap { get; } = new();
        internal ManualResetEventSlim Release { get; } = new();
        internal bool Overlapped => Volatile.Read(ref overlapped) != 0;

        public void Observe(ProviderCommandEvent command)
        {
            if (command.Operation.Contains("retention", StringComparison.OrdinalIgnoreCase))
            {
                RetentionEntered.Set();
                Release.Wait(TimeSpan.FromSeconds(5));
            }
            else if (CheckForOverlap.IsSet && !Release.IsSet &&
                     command.Kind == ProviderCommandKind.Read && !command.IsProbe)
            {
                Interlocked.Exchange(ref overlapped, 1);
            }
        }

        public void Dispose()
        {
            Release.Set();
            RetentionEntered.Dispose();
            CheckForOverlap.Dispose();
            Release.Dispose();
        }
    }

    private sealed class BlockingReadObserver : IProviderCommandObserver, IDisposable
    {
        internal ManualResetEventSlim ReadEntered { get; } = new();
        internal ManualResetEventSlim Release { get; } = new();

        public void Observe(ProviderCommandEvent command)
        {
            if (command.Operation == "sqlserver.read")
            {
                ReadEntered.Set();
                Release.Wait(TimeSpan.FromSeconds(5));
            }
        }

        public void Dispose()
        {
            Release.Set();
            ReadEntered.Dispose();
            Release.Dispose();
        }
    }

    private sealed class RegistrationBarrierObserver : ISessionRegistrationObserver, IDisposable
    {
        internal ManualResetEventSlim EligibilityChecked { get; } = new();
        internal ManualResetEventSlim DisposalAttempted { get; } = new();
        internal ManualResetEventSlim Release { get; } = new();

        public void Observe(ProviderCommandEvent command) { }

        public void OnSessionRegistrationEligibilityChecked()
        {
            EligibilityChecked.Set();
            Release.Wait(TimeSpan.FromSeconds(5));
        }

        public void OnProviderDisposalAttempted() => DisposalAttempted.Set();

        public void Dispose()
        {
            Release.Set();
            EligibilityChecked.Dispose();
            DisposalAttempted.Dispose();
            Release.Dispose();
        }
    }

}

/// <summary>
/// The live SQL Server database this test process owns, emptied of everything the suite creates.
/// </summary>
public sealed class SqlServerFixture
{
    /// <summary>
    /// The connection string for the live database, with the suite's tables dropped, and a skip
    /// for the calling test when no SQL Server is configured. Every live suite in the repository
    /// is dormant in a job that provisions no server; this one is no exception, and a job that
    /// wants these proofs names <c>GROUNDWORK_SQLSERVER_CONNECTION</c> and gets them all.
    /// </summary>
    public string Reset()
    {
        var connectionString = LiveSqlServer.Required();
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DECLARE @targets TABLE (object_id int PRIMARY KEY);
            INSERT INTO @targets (object_id)
            SELECT t.object_id
            FROM sys.tables t
            WHERE t.name IN (N'__groundwork_schema_history',N'__groundwork_schema_fences',N'__groundwork_sequence_high_waters',N'__groundwork_operations',N'__groundwork_retention_operations')
               OR t.name LIKE N'conformance[_]global%'
               OR t.name LIKE N'conformance[_]scoped%'
               OR t.name LIKE N'boundary[_]%'
               OR t.name LIKE N'sqlserver[_]batch[_]boundary[_]%'
               OR t.name LIKE N'customer[_]%'
               OR t.name LIKE N'w2_sqlserver[_]%'
               OR t.name LIKE N's7_sqlserver[_]%'
               OR t.name LIKE N's7_legacy_retention[_]%';

            DECLARE @sql nvarchar(max) = N'';
            SELECT @sql += N'ALTER TABLE ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name)
                + N' DROP CONSTRAINT ' + QUOTENAME(fk.name) + N';'
            FROM sys.foreign_keys fk
            JOIN sys.tables t ON t.object_id=fk.parent_object_id
            JOIN sys.schemas s ON s.schema_id=t.schema_id
            WHERE fk.parent_object_id IN (SELECT object_id FROM @targets)
               OR fk.referenced_object_id IN (SELECT object_id FROM @targets);
            IF @sql <> N'' EXEC sys.sp_executesql @sql;

            SET @sql = N'';
            SELECT @sql += N'DROP TABLE ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + N';'
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id=t.schema_id
            JOIN @targets target ON target.object_id=t.object_id;
            IF @sql <> N'' EXEC sys.sp_executesql @sql;
            """;
        command.ExecuteNonQuery();
        return connectionString;
    }
}
