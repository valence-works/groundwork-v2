using Groundwork.Kernel;
using Groundwork.Sqlite;
using Groundwork.Store;
using Xunit;

namespace Groundwork.Sqlite.Tests;

public sealed class SqliteCompareAndDeleteTests
{
    [Fact]
    public void Native_compare_and_delete_is_atomic_and_exact_uow_preserves_revision_cas()
    {
        var directory = Path.Combine(Path.GetTempPath(), "groundwork-compare-delete-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var unit = Unit();
            using var connection = new SqliteProviderFactory().Create($"Data Source={Path.Combine(directory, "store.db")}");
            connection.Schema.Apply(unit);
            Assert.Contains(connection.Capabilities, capability => capability.Id == BatchWriteCapabilities.CompareAndDelete);
            var session = connection.OpenSession(unit, StorageAccess.Global);
            session.Insert(Values("claim-1", "worker-a", 7L));
            session.Insert(new StorageValues(new Dictionary<string, object?>
            {
                ["id"] = "claim-decimal", ["owner"] = "worker-a", ["fence"] = 7L, ["amount"] = 7m
            }));
            Assert.Equal(WriteOutcomeStatus.Deleted,
                session.CompareAndDelete(Key("claim-decimal"), new Dictionary<string, object?> { ["amount"] = 7 }).Status);

            var mismatchObserver = new ProviderCommandObserver();
            Assert.Equal(WriteOutcomeStatus.ComparisonMismatch,
                session.CompareAndDelete(Key("claim-1"), new Dictionary<string, object?>
                {
                    ["owner"] = "worker-b",
                    ["fence"] = 7L
                }, new WriteOptions { Observer = mismatchObserver }).Status);
            Assert.Equal(2, mismatchObserver.RoundTrips);
            Assert.Contains(mismatchObserver.Commands, command => command.Operation == "sqlite.compare-and-delete-read");
            Assert.NotNull(session.Read(Key("claim-1")));

            Assert.Equal(2L, session.Update(Values("claim-1", "worker-a", 7L), WriteOptions.IfVersion(1)).Version);

            using (var work = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, unit))
            {
                work.Stage(RowWrite.CompareAndDelete(unit, Key("claim-1"), new Dictionary<string, object?>
                {
                    ["owner"] = "worker-a",
                    ["fence"] = 7L
                }));
                var report = work.CommitWithOutcomes();
                var outcome = Assert.Single(report.Outcomes).Outcome;
                Assert.Equal(WriteOutcomeStatus.Deleted, outcome.Status);
                Assert.Equal(2L, outcome.Version);
            }

            Assert.Equal(WriteOutcomeStatus.NotFound,
                session.CompareAndDelete(Key("claim-1"), new Dictionary<string, object?> { ["owner"] = "worker-a" }).Status);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Nullable_expected_values_use_portable_null_equality()
    {
        var directory = Path.Combine(Path.GetTempPath(), "groundwork-compare-delete-null-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var unit = Unit() with
            {
                Id = new StorageUnitId("compare-delete-sqlite-null"),
                Name = "compare_delete_sqlite_null",
                Columns =
                [
                    new ColumnDefinition { Name = "id", Type = PortableType.String, MaxLength = 64, IsNullable = false },
                    new ColumnDefinition { Name = "owner", Type = PortableType.String, MaxLength = 64 },
                    new ColumnDefinition { Name = "fence", Type = PortableType.Int64, IsNullable = false }
                ]
            };
            using var connection = new SqliteProviderFactory().Create($"Data Source={Path.Combine(directory, "store.db")}");
            connection.Schema.Apply(unit);
            var session = connection.OpenSession(unit, StorageAccess.Global);
            session.Insert(new StorageValues(new Dictionary<string, object?>
            {
                ["id"] = "claim-null", ["owner"] = null, ["fence"] = 7L
            }));

            Assert.Equal(WriteOutcomeStatus.Deleted,
                session.CompareAndDelete(Key("claim-null"), new Dictionary<string, object?>
                {
                    ["owner"] = null,
                    ["fence"] = 7L
                }).Status);

            session.Insert(new StorageValues(new Dictionary<string, object?>
            {
                ["id"] = "claim-omitted", ["fence"] = 7L
            }));
            Assert.Equal(WriteOutcomeStatus.Deleted,
                session.CompareAndDelete(Key("claim-omitted"), new Dictionary<string, object?>
                {
                    ["owner"] = null
                }).Status);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Exact_compare_failure_rolls_back_a_preceding_write_in_another_unit()
    {
        var directory = Path.Combine(Path.GetTempPath(), "groundwork-compare-delete-rollback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var unit = Unit();
            var marker = MarkerUnit();
            using var connection = new SqliteProviderFactory().Create($"Data Source={Path.Combine(directory, "store.db")}");
            connection.Schema.Apply(unit);
            connection.Schema.Apply(marker);
            var session = connection.OpenSession(unit, StorageAccess.Global);
            session.Insert(Values("claim-rollback", "worker-a", 7L));
            var claimed = session.Read(Key("claim-rollback"))!;
            var reclaimer = connection.OpenSession(unit, StorageAccess.Global);
            Assert.Equal(2L, reclaimer.Update(Values("claim-rollback", "worker-b", 8L),
                WriteOptions.IfVersion(claimed.Version!.Value)).Version);

            using var work = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, unit, marker);
            work.Stage(RowWrite.Insert(marker, new StorageValues(new Dictionary<string, object?>
            {
                ["id"] = "marker", ["value"] = "must-rollback"
            })));
            work.Stage(RowWrite.CompareAndDelete(unit, Key("claim-rollback"), new Dictionary<string, object?>
            {
                ["owner"] = "worker-a", ["fence"] = 7L
            }));

            var exception = Assert.Throws<BatchWriteException>(() => work.CommitWithOutcomes());
            Assert.Equal(WriteOutcomeStatus.ComparisonMismatch, Assert.Single(exception.Outcomes).Outcome.Status);
            var reclaimed = session.Read(Key("claim-rollback"))!;
            Assert.Equal("worker-b", reclaimed.Values.Values["owner"]);
            Assert.Equal(8L, reclaimed.Values.Values["fence"]);
            Assert.Equal(2L, reclaimed.Version);
            Assert.Null(connection.OpenSession(marker, StorageAccess.Global).Read(
                new StorageKey(new Dictionary<string, object?> { ["id"] = "marker" })));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Scoped_units_accept_logical_keys_and_compare_only_declared_values()
    {
        var directory = Path.Combine(Path.GetTempPath(), "groundwork-compare-delete-scoped-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var unit = Unit() with
            {
                Id = new StorageUnitId("compare-delete-sqlite-scoped"),
                Name = "compare_delete_sqlite_scoped",
                Scope = ScopePolicy.Scoped
            };
            using var connection = new SqliteProviderFactory().Create($"Data Source={Path.Combine(directory, "store.db")}");
            connection.Schema.Apply(unit);
            var session = connection.OpenSession(unit, StorageAccess.Scoped(new StorageScope("tenant-a")));
            session.Insert(Values("claim-scoped", "worker-a", 7L));

            Assert.Equal(WriteOutcomeStatus.Deleted,
                session.CompareAndDelete(Key("claim-scoped"), new Dictionary<string, object?>
                {
                    ["owner"] = "worker-a"
                }).Status);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Unsupported_compare_shapes_are_rejected_before_native_io()
    {
        var directory = Path.Combine(Path.GetTempPath(), "groundwork-compare-delete-admission-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var unit = Unit() with
            {
                Id = new StorageUnitId("compare-delete-sqlite-admission"),
                Name = "compare_delete_sqlite_admission"
            };
            using var connection = new SqliteProviderFactory().Create($"Data Source={Path.Combine(directory, "store.db")}");
            connection.Schema.Apply(unit);
            var session = connection.OpenSession(unit, StorageAccess.Global);
            session.Insert(Values("claim-admission", "worker-a", 7L));

            var undeclaredObserver = new ProviderCommandObserver();
            Assert.Throws<ArgumentException>(() => session.CompareAndDelete(
                Key("claim-admission"),
                new Dictionary<string, object?> { ["missing"] = "value" },
                new WriteOptions { Observer = undeclaredObserver }));
            Assert.Empty(undeclaredObserver.Commands);

            var wrongTypeObserver = new ProviderCommandObserver();
            Assert.Throws<ArgumentException>(() => session.CompareAndDelete(
                Key("claim-admission"),
                new Dictionary<string, object?> { ["fence"] = "seven" },
                new WriteOptions { Observer = wrongTypeObserver }));
            Assert.Empty(wrongTypeObserver.Commands);

            var decimalObserver = new ProviderCommandObserver();
            Assert.Throws<ArgumentException>(() => session.CompareAndDelete(
                Key("claim-admission"),
                new Dictionary<string, object?> { ["amount"] = 7.004m },
                new WriteOptions { Observer = decimalObserver }));
            Assert.Empty(decimalObserver.Commands);

            var jsonUnit = unit with
            {
                Id = new StorageUnitId("compare-delete-sqlite-json"),
                Name = "compare_delete_sqlite_json",
                Columns = [..unit.Columns, new ColumnDefinition { Name = "payload", Type = PortableType.Json }]
            };
            connection.Schema.Apply(jsonUnit);
            var jsonObserver = new ProviderCommandObserver();
            Assert.Throws<ArgumentException>(() => connection.OpenSession(jsonUnit, StorageAccess.Global).CompareAndDelete(
                Key("claim-admission"),
                new Dictionary<string, object?> { ["payload"] = "{\"a\":1}" },
                new WriteOptions { Observer = jsonObserver }));
            Assert.Empty(jsonObserver.Commands);
            Assert.NotNull(session.Read(Key("claim-admission")));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    private static StorageUnit Unit() => new()
    {
        Id = new StorageUnitId("compare-delete-sqlite"),
        Name = "compare_delete_sqlite",
        Columns =
        [
            new ColumnDefinition { Name = "id", Type = PortableType.String, MaxLength = 64, IsNullable = false },
            new ColumnDefinition { Name = "owner", Type = PortableType.String, MaxLength = 64, IsNullable = false },
            new ColumnDefinition { Name = "fence", Type = PortableType.Int64, IsNullable = false },
            new ColumnDefinition { Name = "amount", Type = PortableType.Decimal, Precision = 12, Scale = 2 }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        Concurrency = ConcurrencyDeclaration.Optimistic()
    };

    private static StorageUnit MarkerUnit() => new()
    {
        Id = new StorageUnitId("compare-delete-sqlite-marker"),
        Name = "compare_delete_sqlite_marker",
        Columns =
        [
            new ColumnDefinition { Name = "id", Type = PortableType.String, MaxLength = 64, IsNullable = false },
            new ColumnDefinition { Name = "value", Type = PortableType.String, MaxLength = 64, IsNullable = false }
        ],
        Key = new KeyDefinition { Columns = ["id"] }
    };

    private static StorageValues Values(string id, string owner, long fence) => new(new Dictionary<string, object?>
    {
        ["id"] = id, ["owner"] = owner, ["fence"] = fence
    });

    private static StorageKey Key(string id) => new(new Dictionary<string, object?> { ["id"] = id });
}
