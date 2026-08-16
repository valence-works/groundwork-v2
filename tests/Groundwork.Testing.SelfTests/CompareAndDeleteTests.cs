using System.Text.Json;
using Groundwork.Kernel;
using Groundwork.Store;
using Groundwork.Testing;

namespace Groundwork.Testing.SelfTests;

public sealed class CompareAndDeleteTests
{
    [Fact]
    public void Compare_and_delete_distinguishes_absence_mismatch_and_delete()
    {
        var unit = Unit();
        using var connection = new InMemoryProviderFactory().Create("memory://compare-delete-outcomes");
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        var key = Key("claim-1");
        session.Insert(Values("claim-1", "worker-a", 7));

        var mismatch = session.CompareAndDelete(key, new Dictionary<string, object?>
        {
            ["owner"] = "worker-b",
            ["fence"] = 7L
        });
        Assert.Equal(WriteOutcomeStatus.ComparisonMismatch, mismatch.Status);
        Assert.NotNull(session.Read(key));

        var deleted = session.CompareAndDelete(key, new Dictionary<string, object?>
        {
            ["owner"] = "worker-a",
            ["fence"] = 7L
        });
        Assert.Equal(WriteOutcomeStatus.Deleted, deleted.Status);

        var absent = session.CompareAndDelete(key, new Dictionary<string, object?>
        {
            ["owner"] = "worker-a"
        });
        Assert.Equal(WriteOutcomeStatus.NotFound, absent.Status);

        var missingUnit = unit with
        {
            Id = new StorageUnitId("compare-delete-missing"),
            Name = "compare_delete_missing",
            Columns = [..unit.Columns.Select(column => column.Name == "owner"
                ? column with { IsNullable = true }
                : column)]
        };
        connection.Schema.Apply(missingUnit);
        var missingSession = connection.OpenSession(missingUnit, StorageAccess.Global);
        missingSession.Insert(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "claim-missing", ["fence"] = 7L
        }));
        Assert.Equal(WriteOutcomeStatus.Deleted,
            missingSession.CompareAndDelete(Key("claim-missing"), new Dictionary<string, object?>
            {
                ["owner"] = null
            }).Status);
    }

    [Fact]
    public void Exact_unit_of_work_attributes_compare_failure_and_rolls_back_all_units()
    {
        var unit = Unit();
        var markerUnit = MarkerUnit();
        using var connection = new InMemoryProviderFactory().Create("memory://compare-delete-uow");
        connection.Schema.Apply(unit);
        connection.Schema.Apply(markerUnit);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        session.Insert(Values("claim-1", "worker-a", 7));

        using var work = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, unit, markerUnit);
        work.Stage(RowWrite.Insert(markerUnit, new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "marker", ["value"] = "must-rollback"
        })));
        var compare = RowWrite.CompareAndDelete(unit, Key("claim-1"), new Dictionary<string, object?>
        {
            ["owner"] = "worker-b",
            ["fence"] = 7L
        });
        work.Stage(compare);

        var exception = Assert.Throws<BatchWriteException>(() => work.CommitWithOutcomes());

        var outcome = Assert.Single(exception.Outcomes);
        Assert.Same(compare, outcome.Write);
        Assert.Equal(WriteOutcomeStatus.ComparisonMismatch, outcome.Outcome.Status);
        Assert.NotNull(session.Read(Key("claim-1")));
        Assert.Null(connection.OpenSession(markerUnit, StorageAccess.Global).Read(
            new StorageKey(new Dictionary<string, object?> { ["id"] = "marker" })));
    }

    [Fact]
    public void Reclaimed_successor_after_claim_read_is_mismatch_and_exact_uow_preserves_it()
    {
        var unit = Unit();
        using var connection = new InMemoryProviderFactory().Create("memory://compare-delete-reclaim");
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        session.Insert(Values("claim-reclaimed", "worker-a", 7));

        var claimed = session.Read(Key("claim-reclaimed"))!;
        var expected = new Dictionary<string, object?>
        {
            ["owner"] = claimed.Values.Values["owner"],
            ["fence"] = claimed.Values.Values["fence"]
        };
        Assert.Equal(WriteOutcomeStatus.Updated,
            session.Update(Values("claim-reclaimed", "worker-b", 8)).Status);

        using var work = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, unit);
        work.Stage(RowWrite.CompareAndDelete(unit, Key("claim-reclaimed"), expected));
        var exception = Assert.Throws<BatchWriteException>(() => work.CommitWithOutcomes());

        var outcome = Assert.Single(exception.Outcomes);
        Assert.Equal(WriteOutcomeStatus.ComparisonMismatch, outcome.Outcome.Status);
        var reclaimed = session.Read(Key("claim-reclaimed"))!;
        Assert.Equal("worker-b", reclaimed.Values.Values["owner"]);
        Assert.Equal(8L, reclaimed.Values.Values["fence"]);
    }

    [Fact]
    public void Compare_declaration_is_immutable_and_fingerprint_binds_columns_and_values()
    {
        var unit = Unit();
        var expected = new Dictionary<string, object?> { ["owner"] = "worker-a" };
        var write = RowWrite.CompareAndDelete(unit, Key("claim-1"), expected);
        expected["owner"] = "worker-b";

        Assert.Equal("worker-a", write.ExpectedValues["owner"]);
        var different = RowWrite.CompareAndDelete(unit, Key("claim-1"),
            new Dictionary<string, object?> { ["owner"] = "worker-b" });
        Assert.NotEqual(write.Fingerprint, different.Fingerprint);
    }

    [Fact]
    public void Decimal_integral_expected_values_are_canonicalized_before_provider_dispatch()
    {
        var unit = Unit() with
        {
            Id = new StorageUnitId("compare-delete-decimal"),
            Name = "compare_delete_decimal",
            Columns =
            [
                ..Unit().Columns,
                new ColumnDefinition
                {
                    Name = "amount",
                    Type = PortableType.Decimal,
                    IsNullable = false,
                    Precision = 12,
                    Scale = 2
                }
            ]
        };
        using var connection = new InMemoryProviderFactory().Create("memory://compare-delete-decimal");
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        session.Insert(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "claim-decimal", ["owner"] = "worker-a", ["fence"] = 7L, ["amount"] = 7m
        }));

        var expected = new Dictionary<string, object?> { ["amount"] = 7 };
        var write = RowWrite.CompareAndDelete(unit, Key("claim-decimal"), expected);
        Assert.Equal(7m, write.ExpectedValues["amount"]);
        Assert.Equal(WriteOutcomeStatus.Deleted,
            session.CompareAndDelete(Key("claim-decimal"), expected).Status);
    }

    [Fact]
    public void Fingerprint_is_injective_and_canonical_for_delimiters_json_and_numeric_types()
    {
        var unit = Unit() with
        {
            Columns =
            [
                ..Unit().Columns,
                new ColumnDefinition { Name = "payload", Type = PortableType.Json }
            ]
        };
        var key = Key("claim-1");
        var first = RowWrite.CompareAndDelete(unit, key, new Dictionary<string, object?>
        {
            ["owner"] = "worker-a",
            ["fence"] = 7
        });
        var equivalent = RowWrite.CompareAndDelete(unit, key, new Dictionary<string, object?>
        {
            ["owner"] = "worker-a",
            ["fence"] = 7L
        });

        Assert.Equal(first.Fingerprint, equivalent.Fingerprint);

        using var firstJson = JsonDocument.Parse("{\"b\":2,\"a\":1}");
        using var equivalentJson = JsonDocument.Parse("{\"a\":1,\"b\":2}");
        var firstPayload = ExactAppendCodec.Fingerprint(unit, [new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "claim-1", ["owner"] = "worker-a", ["fence"] = 7L, ["payload"] = firstJson
        })]);
        var equivalentPayload = ExactAppendCodec.Fingerprint(unit, [new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "claim-1", ["owner"] = "worker-a", ["fence"] = 7L, ["payload"] = equivalentJson
        })]);
        Assert.Equal(firstPayload, equivalentPayload);

        var twoPart = unit with
        {
            Id = new StorageUnitId("compare-delete-two-part"),
            Name = "compare_delete_two_part",
            Columns =
            [
                new ColumnDefinition { Name = "left", Type = PortableType.String, IsNullable = false },
                new ColumnDefinition { Name = "right", Type = PortableType.String, IsNullable = false },
                new ColumnDefinition { Name = "owner", Type = PortableType.String, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["left", "right"] }
        };
        var delimiterA = RowWrite.CompareAndDelete(twoPart,
            new StorageKey(new Dictionary<string, object?> { ["left"] = "a\u001fb", ["right"] = "c" }),
            new Dictionary<string, object?> { ["owner"] = "worker-a" });
        var delimiterB = RowWrite.CompareAndDelete(twoPart,
            new StorageKey(new Dictionary<string, object?> { ["left"] = "a", ["right"] = "b\u001fc" }),
            new Dictionary<string, object?> { ["owner"] = "worker-a" });

        Assert.NotEqual(delimiterA.Fingerprint, delimiterB.Fingerprint);
    }

    [Fact]
    public void Unsupported_compare_shapes_are_rejected_before_provider_work()
    {
        var unit = Unit();
        using var connection = new InMemoryProviderFactory().Create("memory://compare-delete-admission");
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        var observer = new WritePathObserver();

        Assert.Throws<ArgumentException>(() => session.CompareAndDelete(
            Key("claim-1"),
            new Dictionary<string, object?> { ["missing"] = "value" },
            new WriteOptions { Observer = observer }));
        Assert.Equal(0, observer.RoundTrips);

        var jsonUnit = unit with
        {
            Columns = [..unit.Columns, new ColumnDefinition { Name = "payload", Type = PortableType.Json }]
        };
        connection.Schema.Apply(jsonUnit);
        Assert.Throws<ArgumentException>(() => connection.OpenSession(jsonUnit, StorageAccess.Global).CompareAndDelete(
            Key("claim-1"),
            new Dictionary<string, object?> { ["payload"] = "{\"a\":1}" },
            new WriteOptions { Observer = observer }));
        Assert.Equal(0, observer.RoundTrips);
    }

    private static StorageUnit Unit() => new()
    {
        Id = new StorageUnitId("compare-delete"),
        Name = "compare_delete",
        Columns =
        [
            new ColumnDefinition { Name = "id", Type = PortableType.String, MaxLength = 64, IsNullable = false },
            new ColumnDefinition { Name = "owner", Type = PortableType.String, MaxLength = 64, IsNullable = false },
            new ColumnDefinition { Name = "fence", Type = PortableType.Int64, IsNullable = false }
        ],
        Key = new KeyDefinition { Columns = ["id"] }
    };

    private static StorageUnit MarkerUnit() => new()
    {
        Id = new StorageUnitId("compare-delete-marker"),
        Name = "compare_delete_marker",
        Columns =
        [
            new ColumnDefinition { Name = "id", Type = PortableType.String, MaxLength = 64, IsNullable = false },
            new ColumnDefinition { Name = "value", Type = PortableType.String, MaxLength = 64, IsNullable = false }
        ],
        Key = new KeyDefinition { Columns = ["id"] }
    };

    private static StorageValues Values(string id, string owner, long fence) => new(new Dictionary<string, object?>
    {
        ["id"] = id,
        ["owner"] = owner,
        ["fence"] = fence
    });

    private static StorageKey Key(string id) => new(new Dictionary<string, object?> { ["id"] = id });
}
