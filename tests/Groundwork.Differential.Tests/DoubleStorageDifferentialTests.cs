using System.Globalization;
using Groundwork.Kernel;
using Groundwork.LiveDatabases;
using Groundwork.MongoDb;
using Groundwork.PostgreSql;
using Groundwork.Sqlite;
using Groundwork.SqlServer;
using Groundwork.Store;
using Xunit;

namespace Groundwork.Differential.Tests;

/// <summary>
/// The load-bearing claim behind storage-only <see cref="PortableType.Double"/> is that IEEE-754
/// binary64 survives a write and a read bit-for-bit on PostgreSQL <c>double precision</c>,
/// SQL Server <c>float</c>, SQLite <c>REAL</c>, and MongoDB <c>double</c>. This class proves it by
/// writing and reading, on all four at once, the values that break a naive implementation:
/// subnormals, the extremes, and values whose shortest round-trippable text form matters.
///
/// It also proves the other half of the policy — that the values which do <em>not</em> survive on
/// all four are refused before any provider sees them, rather than being written and quietly
/// changed. Which values those are was established by writing them, not by reading documentation:
/// SQL Server's driver refuses NaN and both infinities outright, SQLite's refuses NaN, and SQLite
/// and MongoDB both return positive zero for a stored negative zero.
///
/// These share the live SQL Server and MongoDB with every other differential class, so the class
/// joins the collection that serializes them.
/// </summary>
[Collection(NativeProviderDifferentialCollection.Name)]
public sealed class DoubleStorageDifferentialTests
{
    /// <summary>
    /// Values pinned as (literal, the exact binary64 bit pattern it must still have after a
    /// round-trip). The bit patterns are written out rather than recomputed from the value, so a
    /// change in how the code under test formats or parses a double cannot move the target.
    /// </summary>
    public static TheoryData<string, double, long> StorableValues => new()
    {
        { "zero", 0d, 0x0000000000000000L },
        { "one tenth", 0.1d, 0x3FB999999999999AL },
        { "0.1 + 0.2", 0.1d + 0.2d, 0x3FD3333333333334L },
        { "one third", 1d / 3d, 0x3FD5555555555555L },
        { "double.Epsilon", double.Epsilon, 0x0000000000000001L },
        { "subnormal 1e-320", 1e-320d, 0x00000000000007E8L },
        { "smallest normal", 2.2250738585072014e-308d, 0x0010000000000000L },
        { "double.MaxValue", double.MaxValue, 0x7FEFFFFFFFFFFFFFL },
        { "double.MinValue", double.MinValue, unchecked((long)0xFFEFFFFFFFFFFFFFUL) },
        { "2^53", 9007199254740992d, 0x4340000000000000L },
        { "shortest form matters", 5.960464477539063e-8d, 0x3E70000000000000L }
    };

    /// <summary>The values no supported store returns unchanged, and so are refused at the write.</summary>
    public static TheoryData<string, double> RefusedValues => new()
    {
        { "NaN", double.NaN },
        { "positive infinity", double.PositiveInfinity },
        { "negative infinity", double.NegativeInfinity },
        { "negative zero", -0d }
    };

    [SkippableTheory]
    [MemberData(nameof(StorableValues))]
    public void Every_provider_returns_the_same_bits_it_was_given(string name, double value, long bits)
    {
        // The pinned literal and the pinned bit pattern have to agree before either is evidence.
        Assert.Equal(Hex(bits), Hex(BitConverter.DoubleToInt64Bits(value)));
        using var matrix = DoubleMatrix.OpenAll();
        var readBack = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var provider in matrix.Providers)
        {
            provider.Session.Insert(new StorageValues(new Dictionary<string, object?>
            {
                ["id"] = 1L,
                ["reading"] = value
            }));
            var stored = provider.Session.Read(Key(1L));
            Assert.NotNull(stored);
            var actual = Assert.IsType<double>(stored!.Values.Values["reading"]);
            readBack[provider.Name] = Hex(BitConverter.DoubleToInt64Bits(actual));
        }

        // Asserting on the whole map at once names the provider that diverged, and the value.
        Assert.Equal(
            matrix.Providers.ToDictionary(provider => provider.Name, _ => Hex(bits), StringComparer.Ordinal),
            readBack);
        Assert.Equal(4, readBack.Count);
        Assert.NotEmpty(name);
    }

    [SkippableTheory]
    [MemberData(nameof(RefusedValues))]
    public void Every_provider_refuses_a_value_no_store_returns_unchanged(string name, double value)
    {
        using var matrix = DoubleMatrix.OpenAll();
        var refusals = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var provider in matrix.Providers)
        {
            var refusal = Assert.Throws<ArgumentException>(() =>
                provider.Session.Insert(new StorageValues(new Dictionary<string, object?>
                {
                    ["id"] = 2L,
                    ["reading"] = value
                })));
            refusals[provider.Name] = refusal.Message[..refusal.Message.IndexOf(':', StringComparison.Ordinal)];
        }

        Assert.Equal(
            matrix.Providers.ToDictionary(provider => provider.Name, _ => "GW-VALUE-DOUBLE-001", StringComparer.Ordinal),
            refusals);
        Assert.NotEmpty(name);

        // Nothing was written on any provider: a refusal that had already reached the store would
        // leave the row behind.
        foreach (var provider in matrix.Providers)
            Assert.Null(provider.Session.Read(Key(2L)));
    }

    private static string Hex(long bits) => "0x" + bits.ToString("x16", CultureInfo.InvariantCulture);

    /// <summary>
    /// A null in a nullable Double column is a null on every provider, not a zero. Read back as a
    /// null rather than as the default the column type would otherwise produce.
    /// </summary>
    [SkippableFact]
    public void Every_provider_round_trips_a_null_reading()
    {
        using var matrix = DoubleMatrix.OpenAll();
        foreach (var provider in matrix.Providers)
        {
            provider.Session.Insert(new StorageValues(new Dictionary<string, object?>
            {
                ["id"] = 3L,
                ["reading"] = null
            }));
            var stored = provider.Session.Read(Key(3L));
            Assert.NotNull(stored);
            Assert.Null(stored!.Values.Values["reading"]);
        }
    }

    /// <summary>
    /// A declared default is rendered into DDL by each provider and then read back out of its
    /// catalog to be compared with the declaration, so a Double default exercises the literal
    /// renderer and the catalog round-trip that a written value never touches.
    /// </summary>
    [SkippableFact]
    public void Every_provider_renders_and_applies_a_declared_double_default()
    {
        using var matrix = DoubleMatrix.OpenAll();
        var defaults = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var provider in matrix.Providers)
        {
            provider.Session.Insert(new StorageValues(new Dictionary<string, object?> { ["id"] = 4L }));
            var stored = provider.Session.Read(Key(4L));
            Assert.NotNull(stored);
            defaults[provider.Name] = Hex(BitConverter.DoubleToInt64Bits(
                Assert.IsType<double>(stored!.Values.Values["calibration"])));
        }

        Assert.Equal(
            matrix.Providers.ToDictionary(provider => provider.Name, _ => "0x3fb999999999999a", StringComparer.Ordinal),
            defaults);
    }

    /// <summary>
    /// A batched write does not go through the single-row parameter path. SQL Server builds a
    /// table-valued parameter whose DataTable columns are typed from the declaration, so a Double
    /// column only survives a batch if that mapping names it. Batched here on all four providers
    /// for the same reason the single-row case is.
    /// </summary>
    [SkippableFact]
    public void Every_provider_carries_a_double_through_a_batched_write()
    {
        using var matrix = DoubleMatrix.OpenAll();
        var readBack = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var provider in matrix.Providers)
        {
            var batched = Assert.IsAssignableFrom<IBatchedStorageSession>(provider.Session);
            var outcomes = batched.ApplyBatch(
            [
                RowWrite.Insert(matrix.Unit, Row(10L, 0.1d)),
                RowWrite.Insert(matrix.Unit, Row(11L, double.Epsilon)),
                RowWrite.Insert(matrix.Unit, Row(12L, double.MaxValue))
            ]);
            Assert.Equal(3, outcomes.Count);

            readBack[provider.Name] = string.Join(",", new[] { 10L, 11L, 12L }.Select(id =>
            {
                var stored = provider.Session.Read(Key(id));
                Assert.NotNull(stored);
                return Hex(BitConverter.DoubleToInt64Bits(
                    Assert.IsType<double>(stored!.Values.Values["reading"])));
            }));
        }

        Assert.Equal(
            matrix.Providers.ToDictionary(
                provider => provider.Name,
                _ => "0x3fb999999999999a,0x0000000000000001,0x7fefffffffffffff",
                StringComparer.Ordinal),
            readBack);
    }

    private static StorageValues Row(long id, double reading) => new(new Dictionary<string, object?>
    {
        ["id"] = id,
        ["reading"] = reading,
        ["calibration"] = 0.1d
    });

    private static StorageKey Key(long id) =>
        new(new Dictionary<string, object?> { ["id"] = id });

    /// <summary>
    /// One Double declaration opened on all four providers. All four run or none does: a two-way
    /// pass would stop being evidence for the other two.
    /// </summary>
    private sealed class DoubleMatrix : IDisposable
    {
        private readonly List<IStorageProviderConnection> connections = [];

        private DoubleMatrix(string table) => Unit = Declare(table);

        internal StorageUnit Unit { get; }

        internal List<DoubleProvider> Providers { get; } = [];

        internal static DoubleMatrix OpenAll()
        {
            var postgres = Environment.GetEnvironmentVariable("GROUNDWORK_POSTGRES_CONNECTION");
            Skip.If(string.IsNullOrWhiteSpace(postgres),
                "Set GROUNDWORK_POSTGRES_CONNECTION to run the four-way Double storage matrix.");
            var sqlServer = LiveSqlServer.Required();
            var mongo = LiveMongo.Required();
            var matrix = new DoubleMatrix("g2_double_" + Guid.NewGuid().ToString("N"));
            try
            {
                matrix.Add("SQLite", new SqliteProviderFactory().Create(
                    "Data Source=file:g2double_" + Guid.NewGuid().ToString("N") + "?mode=memory&cache=shared"));
                matrix.Add("PostgreSQL", new PostgreSqlProviderFactory().Create(postgres!));
                matrix.Add("SQL Server", new SqlServerProviderFactory().Create(sqlServer));
                matrix.Add("MongoDB", new MongoProviderFactory().Create(mongo));
                return matrix;
            }
            catch
            {
                matrix.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            foreach (var connection in connections)
                connection.Dispose();
        }

        private void Add(string name, IStorageProviderConnection connection)
        {
            connections.Add(connection);
            connection.Schema.Apply(Unit);
            Providers.Add(new DoubleProvider(name, connection.OpenSession(Unit, StorageAccess.Global)));
        }

        private static StorageUnit Declare(string table) => new()
        {
            Id = new StorageUnitId(table),
            Name = table,
            Columns =
            [
                new() { Name = "id", Type = PortableType.Int64, IsNullable = false },
                new() { Name = "reading", Type = PortableType.Double, IsNullable = true },
                new() { Name = "calibration", Type = PortableType.Double, IsNullable = false, Default = new PortableDefault(0.1d) }
            ],
            Key = new KeyDefinition { Columns = ["id"] }
        };
    }

    private sealed record DoubleProvider(string Name, IStorageSession Session);
}
