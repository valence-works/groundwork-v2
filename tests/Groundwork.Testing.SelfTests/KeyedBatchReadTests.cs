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
    public void Folded_string_keys_dedupe_by_execution_key_and_return_the_first_requested_representative()
    {
        using var connection = new InMemoryProviderFactory().Create("memory://batch-read-folded-key");
        var plain = Unit("batch-read-folded-key");
        var unit = plain with
        {
            Columns = plain.Columns.Select(column => column.Name == "owner"
                ? column with { Collation = PortableCollation.OrdinalIgnoreCase }
                : column).ToArray()
        };
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        session.Insert(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = 1L, ["owner"] = "foo", ["region"] = "eu"
        }));

        var owner = new ColumnRef(
            new TableId(unit.Name),
            "owner",
            QueryType.String,
            isNullable: false,
            maxLength: 64,
            stringComparison: QueryStringComparisonPolicy.AsciiIgnoreCase);
        var request = new KeyedBatchReadRequest(
            new TableId(unit.Name),
            owner,
            new object?[] { "FOO", "foo", "missing" });

        var result = session.BatchRead(request);

        var row = Assert.Single(result.Rows);
        Assert.Equal("FOO", row.Key.Value);
        Assert.Equal("foo", row.Values["owner"]);
        Assert.Equal(new[] { "missing" }, result.MissingKeys.Select(key => key.Value));
    }

    [Theory]
    [InlineData(QueryStringComparisonPolicy.Ordinal)]
    [InlineData(QueryStringComparisonPolicy.UnicodeOrdinalIgnoreCase)]
    public void Folded_string_keys_refuse_a_column_policy_that_does_not_match_the_schema_mapping(
        QueryStringComparisonPolicy stringComparison)
    {
        using var connection = new InMemoryProviderFactory().Create(
            "memory://batch-read-folded-policy-" + stringComparison);
        var plain = Unit("batch-read-folded-policy-" + stringComparison);
        var unit = plain with
        {
            Columns = plain.Columns.Select(column => column.Name == "owner"
                ? column with { Collation = PortableCollation.OrdinalIgnoreCase }
                : column).ToArray()
        };
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        var owner = new ColumnRef(
            new TableId(unit.Name),
            "owner",
            QueryType.String,
            isNullable: false,
            maxLength: 64,
            stringComparison: stringComparison);
        var request = new KeyedBatchReadRequest(
            new TableId(unit.Name),
            owner,
            new object?[] { "FOO" });

        var failure = Assert.Throws<QueryRenderException>(() => session.BatchRead(request));

        Assert.Equal("GW-QUERY-031", failure.Code);
        Assert.Contains("owner", failure.Message, StringComparison.Ordinal);
        Assert.Contains(stringComparison.ToString(), failure.Message, StringComparison.Ordinal);
        Assert.Contains(QuerySearchKeyPolicy.AsciiIgnoreCase.ToString(), failure.Message, StringComparison.Ordinal);
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
    public void A_null_key_is_refused_before_column_constant_validation()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new KeyedBatchReadRequest(TableId.Empty, IdColumn, new object?[] { 1L, null }));
        Assert.Contains("GW-BATCHREAD-002", exception.Message);
    }

    [Fact]
    public void The_portable_batch_read_key_budget_is_999()
    {
        Assert.Equal(999, QueryAdmissionProfile.Default.MaximumBatchReadKeys);
        Assert.Equal(15L * 1024 * 1024, QueryAdmissionProfile.Default.MaximumBatchReadPayloadBytes);
    }

    [Fact]
    public void A_batch_read_without_a_connection_refuses_a_key_over_the_conservative_payload_budget()
    {
        using var connection = new InMemoryProviderFactory().Create("memory://batch-read-default-payload");
        var unit = Unit("batch-read-default-payload");
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        var key = new string('a', 15 * 1024 * 1024);
        var keyColumn = new ColumnRef(
            new TableId(unit.Name),
            "owner",
            QueryType.String,
            isNullable: false,
            maxLength: key.Length + 1);
        var request = new KeyedBatchReadRequest(
            new TableId(unit.Name),
            keyColumn,
            new object?[] { key });

        var failure = Assert.Throws<ArgumentException>(() => session.BatchRead(request));

        Assert.Contains("GW-BATCHREAD-004", failure.Message);
    }

    [Fact]
    public void A_scoped_session_reserves_one_key_slot_for_its_scope_parameter()
    {
        var request = new KeyedBatchReadRequest(
            new TableId("batch-read-scoped"),
            IdColumn,
            Enumerable.Range(1, 1_000).Select(value => (object?)(long)value).ToArray());

        var chunks = KeyedBatchReadPlanner.Chunk(
            request,
            new QueryAdmissionProfile { MaximumBatchReadKeys = 999 },
            reserveScopedParameter: true).ToArray();

        Assert.Equal(998, chunks[0].Count);
        Assert.Equal(2, chunks.Length);
    }

    [Fact]
    public void A_payload_budget_splits_large_keys_even_when_the_count_budget_is_unbounded()
    {
        var keyColumn = new ColumnRef("id", QueryType.String);
        var request = new KeyedBatchReadRequest(
            new TableId("batch-read-payload"),
            keyColumn,
            new object?[] { new string('a', 300), new string('b', 300) });

        var chunks = KeyedBatchReadPlanner.Chunk(
            request,
            new QueryAdmissionProfile
            {
                MaximumBatchReadKeys = int.MaxValue,
                MaximumBatchReadPayloadBytes = 1_000
            }).ToArray();

        Assert.Equal(2, chunks.Length);
        Assert.All(chunks, chunk => Assert.Single(chunk));
    }

    [Fact]
    public void Batch_read_raises_the_render_in_limit_to_the_admitted_chunk_size()
    {
        using var connection = new InMemoryProviderFactory().Create("memory://batch-read-render-limit");
        var unit = Unit("batch-read-render-limit");
        var session = Seed(connection, unit, rowCount: 1_100);
        var request = new KeyedBatchReadRequest(
            new TableId(unit.Name),
            IdColumn,
            Enumerable.Range(1, 1_100).Select(value => (object?)(long)value).ToArray());
        var admission = new AdmissionConnection(connection, new QueryAdmissionProfile
        {
            MaximumParameters = 1_100,
            MaximumBatchReadKeys = 1_100
        });

        var result = session.BatchRead(request, admission);

        Assert.Equal(1_100, result.Rows.Count);
    }

    [Fact]
    public void A_single_key_that_exceeds_the_payload_budget_is_refused_by_name()
    {
        var keyColumn = new ColumnRef("id", QueryType.String);
        var request = new KeyedBatchReadRequest(
            new TableId("batch-read-payload"),
            keyColumn,
            new object?[] { new string('a', 300) });

        var exception = Assert.Throws<ArgumentException>(() => KeyedBatchReadPlanner.Chunk(
            request,
            new QueryAdmissionProfile
            {
                MaximumBatchReadKeys = int.MaxValue,
                MaximumBatchReadPayloadBytes = 100
            }).ToArray());

        Assert.Contains("GW-BATCHREAD-004", exception.Message);
    }

    [Fact]
    public void A_request_for_an_undeclared_session_table_is_refused_before_query_execution()
    {
        using var connection = new InMemoryProviderFactory().Create("memory://batch-read-table-validation");
        var unit = Unit("batch-read-table-validation");
        var session = Seed(connection, unit, rowCount: 1);
        var request = new KeyedBatchReadRequest(new TableId("other_table"), IdColumn, new object?[] { 1L });

        var exception = Assert.Throws<ArgumentException>(() => session.BatchRead(request));

        Assert.Contains("GW-BATCHREAD-001", exception.Message);
    }

    [Fact]
    public void A_request_for_an_undeclared_key_column_is_refused_before_query_execution()
    {
        using var connection = new InMemoryProviderFactory().Create("memory://batch-read-column-validation");
        var unit = Unit("batch-read-column-validation");
        var session = Seed(connection, unit, rowCount: 1);
        var request = new KeyedBatchReadRequest(
            new TableId(unit.Name),
            new ColumnRef("not_declared", QueryType.Int64, isNullable: false),
            new object?[] { 1L });

        var exception = Assert.Throws<ArgumentException>(() => session.BatchRead(request));

        Assert.Contains("GW-BATCHREAD-001", exception.Message);
    }

    [Fact]
    public void A_request_with_key_column_metadata_that_disagrees_with_the_unit_is_refused()
    {
        using var connection = new InMemoryProviderFactory().Create("memory://batch-read-type-validation");
        var unit = Unit("batch-read-type-validation");
        var session = Seed(connection, unit, rowCount: 1);
        var request = new KeyedBatchReadRequest(
            new TableId(unit.Name),
            new ColumnRef("id", QueryType.String),
            new object?[] { "1" });

        var exception = Assert.Throws<ArgumentException>(() => session.BatchRead(request));

        Assert.Contains("GW-BATCHREAD-001", exception.Message);
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

    [Fact]
    public async Task An_empty_async_batch_read_honors_an_already_cancelled_token()
    {
        using var connection = new InMemoryProviderFactory().Create("memory://batch-read-empty-cancelled");
        var unit = Unit("batch-read-empty-cancelled");
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        var request = new KeyedBatchReadRequest(new TableId(unit.Name), IdColumn, Array.Empty<object?>());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            session.BatchReadAsync(request, cancellationToken: cancellation.Token).AsTask());
    }

    [Fact]
    public void An_empty_batch_read_on_a_privileged_session_is_refused_as_an_ordinary_query()
    {
        using var connection = new InMemoryProviderFactory().Create("memory://batch-read-empty-privileged");
        var unit = Unit("batch-read-empty-privileged") with { Scope = ScopePolicy.Scoped };
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, PrivilegedAccess());
        var request = new KeyedBatchReadRequest(new TableId(unit.Name), IdColumn, Array.Empty<object?>());

        var failure = Assert.Throws<InvalidOperationException>(() => session.BatchRead(request));

        Assert.Contains("GW-ACCESS-004", failure.Message);
    }

    [Fact]
    public async Task An_empty_async_batch_read_on_a_privileged_session_is_refused_as_an_ordinary_query()
    {
        using var connection = new InMemoryProviderFactory().Create("memory://batch-read-empty-privileged-async");
        var unit = Unit("batch-read-empty-privileged-async") with { Scope = ScopePolicy.Scoped };
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, PrivilegedAccess());
        var request = new KeyedBatchReadRequest(new TableId(unit.Name), IdColumn, Array.Empty<object?>());

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.BatchReadAsync(request).AsTask());

        Assert.Contains("GW-ACCESS-004", failure.Message);
    }

    private static StorageAccess PrivilegedAccess() => StorageAccess.PrivilegedAcrossScopes(
        new StorageAccessAudit("batch-read-tests", "verify empty privileged batch reads"));

    private sealed class AdmissionConnection(
        IStorageProviderConnection inner,
        QueryAdmissionProfile admission) : IStorageProviderConnection, IQueryAdmissionProviderConnection
    {
        public QueryAdmissionProfile QueryAdmission => admission;
        public IProviderCatalog Catalog => inner.Catalog;
        public ISchemaCoordinator Schema => inner.Schema;
        public IReadOnlyList<CapabilityDescriptor> Capabilities => inner.Capabilities;

        public IStorageSession OpenSession(
            StorageUnit unit,
            StorageAccess access,
            IProviderCommandObserver? observer = null) => inner.OpenSession(unit, access, observer);

        public IOwnedStorageSession OpenOwnedSession(
            StorageUnit unit,
            StorageAccess access,
            IProviderCommandObserver? observer = null) => inner.OpenOwnedSession(unit, access, observer);

        public IUnitOfWork BeginUnitOfWork(StorageAccess access, params StorageUnit[] units) =>
            inner.BeginUnitOfWork(access, units);

        public IUnitOfWork BeginUnitOfWork(
            StorageAccess access,
            BatchWriteOptions options,
            params StorageUnit[] units) => inner.BeginUnitOfWork(access, options, units);

        public IUnitOfWork BeginUnitOfWork(
            StorageAccess access,
            BatchWriteOptions options,
            IProviderCommandObserver? observer,
            params StorageUnit[] units) => inner.BeginUnitOfWork(access, options, observer, units);

        public void Dispose()
        {
        }
    }
}
