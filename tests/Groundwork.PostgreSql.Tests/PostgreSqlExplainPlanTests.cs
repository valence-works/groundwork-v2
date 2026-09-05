using System.Globalization;
using System.Text.Json;
using Groundwork.Diagnostics;
using Groundwork.Kernel;
using Groundwork.PostgreSql;
using Groundwork.Query.Model;
using Groundwork.Store;
using Npgsql;
using Xunit;

namespace Groundwork.PostgreSql.Tests;

public sealed class PostgreSqlExplainPlanTests
{
    [Fact]
    public void Explain_command_uses_verbose_json_and_preserves_the_parameterized_statement()
    {
        const string statement = "SELECT \"id\" FROM \"records\" WHERE \"value\" = @p0;  ";

        Assert.Equal(
            "EXPLAIN (VERBOSE, FORMAT JSON) SELECT \"id\" FROM \"records\" WHERE \"value\" = @p0",
            PostgreSqlStorageSession.ExplainCommandText(statement));
    }

    [SkippableFact]
    public void Explain_assertion_retains_verbose_output_and_the_existing_artifact_name()
    {
        using var environment = new ExplainEnvironment();
        using var database = PostgreSqlFixture.OpenOrSkip();
        using var connection = new PostgreSqlProviderFactory().Create(database.ConnectionString);
        var name = "pg_explain_verbose_" + Guid.NewGuid().ToString("N");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId(name),
            Name = name,
            Columns =
            [
                new() { Name = "id", Type = PortableType.Int64, IsNullable = false },
                new() { Name = "value", Type = PortableType.Int32, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Indexes =
            [
                new IndexDefinition
                {
                    Name = "by_value",
                    Columns = [new IndexColumn("value"), new IndexColumn("id")]
                }
            ]
        };

        Assert.True(connection.Schema.Apply(unit).Applied);
        using var session = connection.OpenOwnedSession(unit, StorageAccess.Global);
        for (var value = 1; value <= 2_000; value++)
        {
            session.Insert(new StorageValues(new Dictionary<string, object?>
            {
                ["id"] = (long)value,
                ["value"] = value
            }));
        }

        using (var analyzeConnection = new NpgsqlConnection(database.ConnectionString))
        {
            analyzeConnection.Open();
            using var analyze = analyzeConnection.CreateCommand();
            analyze.CommandText = "ANALYZE \"" + name + "\";";
            analyze.ExecuteNonQuery();
        }

        var table = new TableId(name);
        var id = new ColumnRef(table, "id", QueryType.Int64, isNullable: false);
        var valueColumn = new ColumnRef(table, "value", QueryType.Int32, isNullable: false);
        var request = new QueryRequest(
            table,
            new Predicate.Equal(valueColumn, QueryConstant.Of(valueColumn, 1_999)),
            [],
            Projection.ColumnsOnly(id, valueColumn),
            Paging.None);
        var options = new QueryRenderOptions(
            [new QueryIndexDeclaration(
                "by_value",
                [
                    new QueryIndexColumn("value", isNullable: false, QueryType.Int32),
                    new QueryIndexColumn("id", isNullable: false, QueryType.Int64)
                ],
                QueryIndexPinning.Pinned)],
            selectedIndex: "by_value");

        var result = session.Query(request, options);

        Assert.Equal("by_value", result.SelectedIndex);
        Assert.Equal(1_999L, Assert.Single(result.Rows)["id"]);
        var artifact = Assert.Single(Directory.GetFiles(environment.ArtifactDirectory, "*.json"));
        Assert.Matches(
            @"^\d{6}-PostgreSQL-optimizer-selected-by_value\.json$",
            Path.GetFileName(artifact));
        var plan = File.ReadAllText(artifact);
        Assert.Contains("\"Output\"", plan, StringComparison.Ordinal);
        Assert.Contains("\"value\"", plan, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Explain_assertion_selects_a_declared_index_for_bounded_non_nullable_ordering()
    {
        using var environment = new ExplainEnvironment();
        using var database = PostgreSqlFixture.OpenOrSkip();
        using var connection = new PostgreSqlProviderFactory().Create(database.ConnectionString);
        var name = "pg_explain_order_" + Guid.NewGuid().ToString("N");
        var unit = StorageUnit.Declare(name, name)
            .String("traceKey", 64, column => column.Required())
            .Int64("startTime", column => column.Required())
            .String("payload", 1_024, column => column.Required())
            .Key("traceKey")
            .Index("by_start_time", index => index
                .Descending("startTime")
                .Ascending("traceKey"))
            .Scoped()
            .Build();

        Assert.True(connection.Schema.Apply(unit).Applied);
        using (var seed = new NpgsqlConnection(database.ConnectionString))
        {
            seed.Open();
            using var command = seed.CreateCommand();
            command.CommandText =
                $"INSERT INTO \"{name}\" (\"traceKey\", \"startTime\", \"payload\", \"__groundwork_scope\") " +
                "SELECT lpad(value::text, 64, '0'), value, repeat('p', 512), 'scope-a' " +
                "FROM generate_series(1, 100000) AS value; " +
                $"ANALYZE \"{name}\";";
            command.ExecuteNonQuery();
        }

        using var session = connection.OpenOwnedSession(
            unit,
            StorageAccess.Scoped(new StorageScope("scope-a")));
        var table = new TableId(name);
        var traceKey = new ColumnRef(table, "traceKey", QueryType.String, isNullable: false, maxLength: 64);
        var startTime = new ColumnRef(table, "startTime", QueryType.Int64, isNullable: false);
        var request = new QueryRequest(
            table,
            Predicate.AlwaysTrue.Instance,
            [
                new OrderTerm(startTime, OrderDirection.Descending, NullOrder.First),
                new OrderTerm(traceKey, OrderDirection.Ascending, NullOrder.Last)
            ],
            Projection.All,
            Paging.Keyset(127));
        var options = new QueryRenderOptions(
            [new QueryIndexDeclaration(
                "by_start_time",
                [
                    new QueryIndexColumn("startTime", isNullable: false, QueryType.Int64),
                    new QueryIndexColumn("traceKey", isNullable: false, QueryType.String)
                ],
                QueryIndexPinning.ProviderDefault)],
            selectedIndex: "by_start_time");

        var result = session.Query(request, options);

        Assert.Equal(127, result.Rows.Count);
        Assert.Equal(100_000L, result.Rows[0]["startTime"]);
        Assert.Single(Directory.GetFiles(environment.ArtifactDirectory, "*.json"));
    }

    [SkippableFact]
    public void Explain_assertion_selects_the_ordinal_identity_index_without_a_sort_for_keyset_pages()
    {
        using var environment = new ExplainEnvironment();
        using var database = PostgreSqlFixture.OpenOrSkip();
        using var connection = new PostgreSqlProviderFactory().Create(database.ConnectionString);
        var name = "pg_explain_ordinal_" + Guid.NewGuid().ToString("N");
        const string identityName = "__groundwork_ordinal_value";
        var unit = StorageUnit.Declare(name, name)
            .Int64("id", column => column.Required())
            .String("value", 32, column => column.Required().OrdinalIdentity(identityName))
            .Key("id")
            .Index("by_value", index => index
                .UseOrdinalIdentities()
                .Column("value")
                .Column("id"))
            .Build();

        Assert.True(connection.Schema.Apply(unit).Applied);
        var rows = Enumerable.Range(1, 10_000)
            .Select(value => (id: (long)value, text: value.ToString("D8", CultureInfo.InvariantCulture)))
            .ToArray();
        // Use the public batch-write path so the provider owns persisted ordinal-identity
        // projection; the direct SQL below is fixture-only statistics maintenance.
        using (var work = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, unit))
        {
            foreach (var row in rows)
            {
                work.Stage(RowWrite.Insert(unit, new StorageValues(new Dictionary<string, object?>
                {
                    ["id"] = row.id,
                    ["value"] = row.text
                })));
            }
            Assert.True(work.CommitWithOutcomes().IsSuccessful);
        }

        using (var seed = new NpgsqlConnection(database.ConnectionString))
        {
            seed.Open();
            using var command = seed.CreateCommand();
            command.CommandText = $"ANALYZE \"{name}\";";
            command.ExecuteNonQuery();
        }

        using var session = connection.OpenOwnedSession(unit, StorageAccess.Global);
        var table = new TableId(name);
        var id = new ColumnRef(table, "id", QueryType.Int64, isNullable: false);
        var valueColumn = new ColumnRef(table, "value", QueryType.String, isNullable: false, maxLength: 32);
        var request = new QueryRequest(
            table,
            Predicate.AlwaysTrue.Instance,
            [new OrderTerm(valueColumn, OrderDirection.Ascending, NullOrder.Last)],
            Projection.ColumnsOnly(id, valueColumn),
            Paging.Keyset(127));
        var options = unit.CreateQueryRenderOptions("by_value");

        var firstPage = session.Query(request, options);

        Assert.Equal(127, firstPage.Rows.Count);
        Assert.NotNull(firstPage.NextContinuationToken);
        var nextPage = session.Query(
            new QueryRequest(
                table,
                request.Where,
                request.Order,
                request.Projection,
                Paging.Continuation(firstPage.NextContinuationToken!, 127)),
            options);
        Assert.Equal(127, nextPage.Rows.Count);

        var artifacts = Directory.GetFiles(environment.ArtifactDirectory, "*.json");
        Assert.Equal(2, artifacts.Length);
        var physicalIndexName = PostgreSqlDialect.PhysicalIndexName(name, "by_value");
        foreach (var artifact in artifacts)
        {
            var plan = File.ReadAllText(artifact);
            using var document = JsonDocument.Parse(plan);
            Assert.False(ContainsPlanProperty(document.RootElement, "Node Type", "Sort"), plan);
            Assert.False(ContainsPlanProperty(document.RootElement, "Node Type", "Incremental Sort"), plan);
            Assert.True(ContainsPlanProperty(document.RootElement, "Index Name", physicalIndexName), plan);
        }
    }

    [SkippableFact]
    public void Explain_assertion_preserves_three_term_required_keyset_ordering_at_the_captured_late_cursor()
    {
        using var environment = new ExplainEnvironment();
        using var database = PostgreSqlFixture.OpenOrSkip();
        using var connection = new PostgreSqlProviderFactory().Create(database.ConnectionString);
        var name = "pg_explain_three_term_" + Guid.NewGuid().ToString("N");
        const string scope = "scope-a";
        const string traceKeyValue = "trace-a";
        const string ordinalIdentity = "__groundwork_ordinal_spanId";
        const string indexName = "by_trace_order";
        const int pageSize = 127;
        const int totalRows = 100_000;
        const int lateCursorSequence = 98_806;
        const int warmupOffset = lateCursorSequence - pageSize;
        var epoch = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var unit = StorageUnit.Declare(name, name)
            .String("traceKey", 64, column => column.Required())
            .DateTimeOffset("startTime", column => column.Required())
            .String("spanId", 64, column => column.Required().OrdinalIdentity(ordinalIdentity))
            .Int64("sequence", column => column.Required())
            .String("payload", 768, column => column.Required())
            .Key("sequence")
            .Index(indexName, index => index
                .UseOrdinalIdentities()
                .Ascending("traceKey")
                .Ascending("startTime")
                .Ascending("spanId")
                .Ascending("sequence"))
            .Scoped()
            .Build();

        Assert.True(connection.Schema.Apply(unit).Applied);
        var rows = Enumerable.Range(1, totalRows)
            .Select(value =>
            {
                var group = (value - 1) / 4;
                return (
                    sequence: (long)value,
                    traceKey: traceKeyValue,
                    startTime: epoch.AddSeconds(group),
                    spanId: group.ToString("D8", CultureInfo.InvariantCulture),
                    payload: new string('p', 768));
            })
            .ToArray();

        // Use the public batch path so the provider owns both persisted ordinal-identity
        // projection and logical-value round-tripping. The direct SQL below is only statistics
        // maintenance, matching the existing explain-plan fixtures in this class.
        using (var work = connection.BeginUnitOfWork(
                   StorageAccess.Scoped(new StorageScope(scope)),
                   BatchWriteOptions.Exact,
                   unit))
        {
            foreach (var row in rows)
            {
                work.Stage(RowWrite.Insert(unit, new StorageValues(new Dictionary<string, object?>
                {
                    ["traceKey"] = row.traceKey,
                    ["startTime"] = row.startTime,
                    ["spanId"] = row.spanId,
                    ["sequence"] = row.sequence,
                    ["payload"] = row.payload
                })));
            }
            Assert.True(work.CommitWithOutcomes().IsSuccessful);
        }

        using (var analyzeConnection = new NpgsqlConnection(database.ConnectionString))
        {
            analyzeConnection.Open();
            using var analyze = analyzeConnection.CreateCommand();
            analyze.CommandText = $"ANALYZE \"{name}\";";
            analyze.ExecuteNonQuery();
        }

        var selectedDeclaration = unit.Indexes.Single(index => index.Name == indexName);
        Assert.True(selectedDeclaration.UseOrdinalIdentities);
        Assert.Equal(
            ["traceKey", "startTime", "spanId", "sequence"],
            selectedDeclaration.Columns.Select(column => column.Column));
        var spanDefinition = unit.Columns.Single(column => column.Name == "spanId");
        Assert.False(spanDefinition.IsNullable);
        Assert.Equal(ordinalIdentity, spanDefinition.OrdinalIdentity!.PhysicalColumn);
        var physicalIndex = ProviderOwnedColumns.Physicalize(
                unit,
                new ProviderOwnedColumnPolicy { ProviderName = "PostgreSQL" })
            .Indexes.Single(index => index.Name == indexName);
        Assert.Contains(ordinalIdentity, physicalIndex.Columns.Select(column => column.Column));
        Assert.Contains("spanId", physicalIndex.IncludedColumns!);

        using var session = connection.OpenOwnedSession(
            unit,
            StorageAccess.Scoped(new StorageScope(scope)));
        var table = new TableId(name);
        var traceKey = new ColumnRef(table, "traceKey", QueryType.String, isNullable: false, maxLength: 64);
        // Keep caller-side metadata nullable to model the diagnostics query shape. The selected
        // index declaration below is the authoritative required-key proof that the renderer must
        // carry into its native continuation terms; the old renderer therefore grew impossible
        // NULL alternatives on the late page.
        var startTime = new ColumnRef(table, "startTime", QueryType.DateTimeOffset, isNullable: true);
        var spanId = new ColumnRef(table, "spanId", QueryType.String, isNullable: true, maxLength: 64);
        var sequence = new ColumnRef(table, "sequence", QueryType.Int64, isNullable: true);
        var request = new QueryRequest(
            table,
            new Predicate.Equal(traceKey, QueryConstant.Of(traceKey, traceKeyValue)),
            [
                new OrderTerm(startTime, OrderDirection.Ascending, NullOrder.Last),
                new OrderTerm(spanId, OrderDirection.Ascending, NullOrder.Last),
                new OrderTerm(sequence, OrderDirection.Ascending, NullOrder.Last)
            ],
            Projection.All,
            Paging.OffsetLimit(warmupOffset, pageSize));
        var options = unit.CreateQueryRenderOptions(indexName);
        var selectedIndex = options.FindSelectedIndex();
        Assert.NotNull(selectedIndex);
        Assert.Equal(indexName, selectedIndex!.Name);
        Assert.Empty(selectedIndex.NullableColumns);

        // The warm-up query is the public source of the continuation token. It ends at the
        // captured sequence value after 778 complete 127-row pages, so the second query exercises
        // the same continuation contract at the late page without issuing 778 explain probes.
        var warmup = session.Query(request, options);
        Assert.Equal(pageSize, warmup.Rows.Count);
        Assert.Equal(indexName, warmup.SelectedIndex);
        Assert.NotNull(warmup.NextContinuationToken);
        Assert.Equal(lateCursorSequence, Assert.IsType<long>(warmup.Rows[^1]["sequence"]));
        var latePage = session.Query(
            new QueryRequest(
                table,
                request.Where,
                request.Order,
                request.Projection,
                Paging.Continuation(warmup.NextContinuationToken!, pageSize)),
            options);
        Assert.Equal(pageSize, latePage.Rows.Count);
        Assert.Equal(indexName, latePage.SelectedIndex);
        Assert.NotNull(latePage.NextContinuationToken);
        Assert.Equal(lateCursorSequence + 1, Assert.IsType<long>(latePage.Rows[0]["sequence"]));

        var actual = warmup.Rows.Concat(latePage.Rows).ToArray();
        var expected = rows
            .OrderBy(row => row.startTime)
            .ThenBy(row => row.spanId, StringComparer.Ordinal)
            .ThenBy(row => row.sequence)
            .Skip(warmupOffset)
            .Take(actual.Length)
            .ToArray();
        Assert.Equal(pageSize * 2, actual.Length);
        Assert.Equal(actual.Length, actual.Select(row => row["sequence"]).Distinct().Count());
        for (var index = 0; index < actual.Length; index++)
        {
            Assert.Equal(expected[index].traceKey, Assert.IsType<string>(actual[index]["traceKey"]));
            Assert.Equal(expected[index].startTime, Assert.IsType<DateTimeOffset>(actual[index]["startTime"]));
            Assert.Equal(expected[index].spanId, Assert.IsType<string>(actual[index]["spanId"]));
            Assert.Equal(expected[index].sequence, Assert.IsType<long>(actual[index]["sequence"]));
            Assert.Equal(expected[index].payload, Assert.IsType<string>(actual[index]["payload"]));
        }

        var artifacts = Directory.GetFiles(environment.ArtifactDirectory, "*.json");
        Assert.Equal(2, artifacts.Length);
        var physicalIndexName = PostgreSqlDialect.PhysicalIndexName(name, indexName);
        foreach (var artifact in artifacts)
        {
            var plan = File.ReadAllText(artifact);
            using var document = JsonDocument.Parse(plan);
            Assert.False(ContainsPlanProperty(document.RootElement, "Node Type", "Sort"), plan);
            Assert.False(ContainsPlanProperty(document.RootElement, "Node Type", "Incremental Sort"), plan);
            Assert.False(ContainsPlanProperty(document.RootElement, "Node Type", "Bitmap Heap Scan"), plan);
            Assert.False(ContainsPlanProperty(document.RootElement, "Node Type", "Bitmap Index Scan"), plan);
            Assert.False(ContainsPlanProperty(document.RootElement, "Node Type", "BitmapAnd"), plan);
            Assert.False(ContainsPlanProperty(document.RootElement, "Node Type", "BitmapOr"), plan);
            Assert.True(ContainsPlanProperty(document.RootElement, "Index Name", physicalIndexName), plan);
        }
    }

    private static bool ContainsPlanProperty(JsonElement element, string propertyName, string expected)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(propertyName, out var value) &&
                value.ValueKind == JsonValueKind.String &&
                string.Equals(value.GetString(), expected, StringComparison.Ordinal))
                return true;
            return element.EnumerateObject().Any(property => ContainsPlanProperty(property.Value, propertyName, expected));
        }

        return element.ValueKind == JsonValueKind.Array &&
            element.EnumerateArray().Any(item => ContainsPlanProperty(item, propertyName, expected));
    }

    private sealed class ExplainEnvironment : IDisposable
    {
        private readonly string? previousFlag = Environment.GetEnvironmentVariable("GW_EXPLAIN_ASSERT");
        private readonly string? previousDirectory = Environment.GetEnvironmentVariable("GW_EXPLAIN_ARTIFACT_DIR");

        internal ExplainEnvironment()
        {
            ArtifactDirectory = Path.Combine(
                Path.GetTempPath(),
                "groundwork-postgresql-explain-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("GW_EXPLAIN_ASSERT", "1");
            Environment.SetEnvironmentVariable("GW_EXPLAIN_ARTIFACT_DIR", ArtifactDirectory);
        }

        internal string ArtifactDirectory { get; }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("GW_EXPLAIN_ASSERT", previousFlag);
            Environment.SetEnvironmentVariable("GW_EXPLAIN_ARTIFACT_DIR", previousDirectory);
            if (Directory.Exists(ArtifactDirectory))
                Directory.Delete(ArtifactDirectory, recursive: true);
        }
    }
}
