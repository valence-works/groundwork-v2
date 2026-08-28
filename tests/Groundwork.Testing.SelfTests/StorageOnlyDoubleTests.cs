using Groundwork.Kernel;
using Groundwork.Store;
using Groundwork.Testing;

namespace Groundwork.Testing.SelfTests;

/// <summary>
/// The write-path half of storage-only <see cref="PortableType.Double"/>: the value-domain rule
/// lives in the one validator every provider write path funnels through, so it holds on the
/// reference provider exactly as it does on the four native ones.
/// </summary>
public sealed class StorageOnlyDoubleTests
{
    public static TheoryData<double> RefusedValues => new()
    {
        double.NaN,
        double.PositiveInfinity,
        double.NegativeInfinity,
        -0d
    };

    [Theory]
    [MemberData(nameof(RefusedValues))]
    public void Insert_update_and_upsert_all_refuse_a_value_outside_the_storable_domain(double value)
    {
        var session = Open(nameof(Insert_update_and_upsert_all_refuse_a_value_outside_the_storable_domain) + value);
        session.Insert(Values("r-1", 1.5d));

        foreach (var write in new Action[]
        {
            () => session.Insert(Values("r-2", value)),
            () => session.Update(Values("r-1", value)),
            () => session.Upsert(Values("r-1", value))
        })
        {
            var refusal = Assert.Throws<ArgumentException>(write);
            Assert.StartsWith("GW-VALUE-DOUBLE-001: ", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("Double column 'reading'", refusal.Message, StringComparison.Ordinal);
        }

        // Refused before the store saw it: the new row is absent and the existing one unchanged.
        Assert.Null(session.Read(Key("r-2")));
        Assert.Equal(1.5d, session.Read(Key("r-1"))!.Values.Values["reading"]);
    }

    [Fact]
    public void A_finite_value_round_trips_through_the_reference_provider()
    {
        var session = Open(nameof(A_finite_value_round_trips_through_the_reference_provider));
        session.Insert(Values("r-1", double.Epsilon));

        Assert.Equal(
            BitConverter.DoubleToInt64Bits(double.Epsilon),
            BitConverter.DoubleToInt64Bits((double)session.Read(Key("r-1"))!.Values.Values["reading"]!));
    }

    [Fact]
    public void A_null_reading_is_not_a_double_and_is_left_alone()
    {
        var session = Open(nameof(A_null_reading_is_not_a_double_and_is_left_alone));
        session.Insert(Values("r-1", null));

        Assert.Null(session.Read(Key("r-1"))!.Values.Values["reading"]);
    }

    /// <summary>
    /// Comparing a stored double is the trap the type is refused for, so compare-and-delete
    /// declines a Double comparison column rather than comparing bit patterns behind the caller.
    /// </summary>
    [Fact]
    public void Compare_and_delete_refuses_a_double_comparison_column()
    {
        var session = Open(nameof(Compare_and_delete_refuses_a_double_comparison_column));
        session.Insert(Values("r-1", 1.5d));

        var refusal = Assert.Throws<ArgumentException>(() => session.CompareAndDelete(
            Key("r-1"),
            new Dictionary<string, object?> { ["reading"] = 1.5d }));
        Assert.Contains("not compatible with declared type Double", refusal.Message, StringComparison.Ordinal);
        Assert.NotNull(session.Read(Key("r-1")));
    }

    private static IStorageSession Open(string name)
    {
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("telemetry"),
            Name = "telemetry",
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, MaxLength = 32, IsNullable = false },
                new() { Name = "reading", Type = PortableType.Double, IsNullable = true }
            ],
            Key = new KeyDefinition { Columns = ["id"] }
        };
        var connection = new InMemoryProviderFactory().Create("memory://double-" + name.GetHashCode());
        connection.Schema.Apply(unit);
        return connection.OpenSession(unit, StorageAccess.Global);
    }

    private static StorageValues Values(string id, double? reading) =>
        new(new Dictionary<string, object?> { ["id"] = id, ["reading"] = reading });

    private static StorageKey Key(string id) =>
        new(new Dictionary<string, object?> { ["id"] = id });
}
