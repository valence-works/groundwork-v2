using Groundwork.Kernel;
using Groundwork.Query.Model;
using Groundwork.Testing;
using Groundwork.Store;

namespace Groundwork.Testing.SelfTests;

public sealed class KeyedBatchReadTests
{
    private static StorageUnit Unit(string id) => new()
    {
        Id = new StorageUnitId(id),
        Name = id.Replace('-', '_'),
        Columns =
        [
            new ColumnDefinition { Name = "id", Type = PortableType.Int64, IsNullable = false },
            new ColumnDefinition { Name = "owner", Type = PortableType.String, IsNullable = false, MaxLength = 64 },
            new ColumnDefinition { Name = "region", Type = PortableType.String, IsNullable = true, MaxLength = 32 }
        ],
        Key = new KeyDefinition { Columns = ["id"] }
    };

    private static readonly ColumnRef IdColumn = new("id", QueryType.Int64, isNullable: false);
    private static readonly ColumnRef OwnerColumn = new("owner", QueryType.String, isNullable: false, maxLength: 64);
    private static readonly ColumnRef RegionColumn = new("region", QueryType.String, maxLength: 32);

    private static IStorageSession Seed(IStorageProviderConnection connection, StorageUnit unit, int rowCount)
    {
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        for (var index = 1; index <= rowCount; index++)
        {
            session.Insert(new StorageValues(new Dictionary<string, object?>
            {
                ["id"] = (long)index,
                ["owner"] = "owner-" + index,
                ["region"] = index % 2 == 0 ? "eu" : "us"
            }));
        }

        return session;
    }

    [Fact]
    public void Matched_rows_are_ordered_by_the_caller_s_deduplicated_key_order()
    {
        using var connection = new InMemoryProviderFactory().Create("memory://batch-read-order");
        var unit = Unit("batch-read-order");
        var session = Seed(connection, unit, rowCount: 10);

        var request = new KeyedBatchReadRequest(
            new TableId(unit.Name),
            IdColumn,
            new object?[] { 7L, 2L, 9L, 2L, 7L });

        var result = session.BatchRead(request);

        Assert.Equal(new long[] { 7, 2, 9 }, result.Rows.Select(row => (long)row.Values["id"]!));
        Assert.Empty(result.MissingKeys);
    }

    [Fact]
    public void Keys_that_match_no_row_are_reported_as_missing_in_first_occurrence_order()
    {
        using var connection = new InMemoryProviderFactory().Create("memory://batch-read-missing");
        var unit = Unit("batch-read-missing");
        var session = Seed(connection, unit, rowCount: 5);

        var request = new KeyedBatchReadRequest(
            new TableId(unit.Name),
            IdColumn,
            new object?[] { 2L, 999L, 4L, 1000L });

        var result = session.BatchRead(request);

        Assert.Equal(new long[] { 2, 4 }, result.Rows.Select(row => (long)row.Values["id"]!));
        Assert.Equal(new long[] { 999, 1000 }, result.MissingKeys.Select(key => (long)key.Value!));
    }

    [Fact]
    public void A_key_set_far_beyond_the_thousand_value_cap_is_chunked_internally_rather_than_refused()
    {
        using var connection = new InMemoryProviderFactory().Create("memory://batch-read-large");
        var unit = Unit("batch-read-large");
        var session = Seed(connection, unit, rowCount: 2_500);

        var keys = Enumerable.Range(1, 2_500).Select(id => (object?)(long)id).ToArray();
        var request = new KeyedBatchReadRequest(new TableId(unit.Name), IdColumn, keys);

        var result = session.BatchRead(request);

        Assert.Equal(2_500, result.Rows.Count);
        Assert.Empty(result.MissingKeys);
        Assert.Equal(
            Enumerable.Range(1, 2_500).Select(id => (long)id),
            result.Rows.Select(row => (long)row.Values["id"]!));
    }

    [Fact]
    public async Task BatchReadAsync_produces_the_same_result_as_the_synchronous_path()
    {
        using var connection = new InMemoryProviderFactory().Create("memory://batch-read-async");
        var unit = Unit("batch-read-async");
        var session = Seed(connection, unit, rowCount: 1_500);

        var keys = Enumerable.Range(1, 1_500).Select(id => (object?)(long)id).ToArray();
        var request = new KeyedBatchReadRequest(new TableId(unit.Name), IdColumn, keys);

        var result = await session.BatchReadAsync(request);

        Assert.Equal(1_500, result.Rows.Count);
        Assert.Empty(result.MissingKeys);
    }

    [Fact]
    public void A_projection_that_omits_the_key_column_still_matches_rows_but_does_not_return_it()
    {
        using var connection = new InMemoryProviderFactory().Create("memory://batch-read-projection");
        var unit = Unit("batch-read-projection");
        var session = Seed(connection, unit, rowCount: 3);

        var request = new KeyedBatchReadRequest(
            new TableId(unit.Name),
            IdColumn,
            new object?[] { 1L, 2L },
            projection: Projection.ColumnsOnly(OwnerColumn));

        var result = session.BatchRead(request);

        Assert.Equal(2, result.Rows.Count);
        Assert.All(result.Rows, row =>
        {
            Assert.False(row.Values.ContainsKey("id"));
            Assert.True(row.Values.ContainsKey("owner"));
        });
    }

    [Fact]
    public void An_additional_predicate_narrows_every_chunk_s_membership_test()
    {
        using var connection = new InMemoryProviderFactory().Create("memory://batch-read-additional-predicate");
        var unit = Unit("batch-read-additional-predicate");
        var session = Seed(connection, unit, rowCount: 6);

        var request = new KeyedBatchReadRequest(
            new TableId(unit.Name),
            IdColumn,
            new object?[] { 1L, 2L, 3L, 4L },
            additionalPredicate: new Predicate.Equal(RegionColumn, QueryConstant.Of(RegionColumn, "eu")),
            additionalPredicateParameterCount: 1);

        var result = session.BatchRead(request);

        Assert.Equal(new long[] { 2, 4 }, result.Rows.Select(row => (long)row.Values["id"]!));
        Assert.Equal(new long[] { 1, 3 }, result.MissingKeys.Select(key => (long)key.Value!));
    }

    [Fact]
    public void Duplicate_input_keys_are_deduplicated_before_execution()
    {
        var request = new KeyedBatchReadRequest(
            TableId.Empty,
            IdColumn,
            new object?[] { 1L, 1L, 2L, 1L });

        Assert.Equal(new long[] { 1, 2 }, request.Keys.Select(key => (long)key.Value!));
    }

    [Fact]
    public void A_null_key_is_refused()
    {
        var nullableIdColumn = new ColumnRef("id", QueryType.Int64, isNullable: true);
        var exception = Assert.Throws<ArgumentException>(() =>
            new KeyedBatchReadRequest(TableId.Empty, nullableIdColumn, new object?[] { 1L, null }));
        Assert.Contains("GW-BATCHREAD-002", exception.Message);
    }

    [Fact]
    public void A_key_column_bound_to_a_different_table_is_refused()
    {
        var otherTableColumn = new ColumnRef(new TableId("other"), "id", QueryType.Int64, isNullable: false);
        var exception = Assert.Throws<ArgumentException>(() =>
            new KeyedBatchReadRequest(new TableId("expected"), otherTableColumn, new object?[] { 1L }));
        Assert.Contains("GW-BATCHREAD-001", exception.Message);
    }

    [Fact]
    public void An_empty_key_set_returns_an_empty_result_without_executing_a_query()
    {
        using var connection = new InMemoryProviderFactory().Create("memory://batch-read-empty");
        var unit = Unit("batch-read-empty");
        var session = Seed(connection, unit, rowCount: 3);

        var request = new KeyedBatchReadRequest(new TableId(unit.Name), IdColumn, Array.Empty<object?>());
        var result = session.BatchRead(request);

        Assert.Empty(result.Rows);
        Assert.Empty(result.MissingKeys);
    }
}
