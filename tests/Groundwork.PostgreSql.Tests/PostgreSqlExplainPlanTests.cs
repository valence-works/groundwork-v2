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
        foreach (var artifact in artifacts)
        {
            var plan = File.ReadAllText(artifact);
            Assert.Contains("by_value", plan, StringComparison.Ordinal);
            using var document = JsonDocument.Parse(plan);
            Assert.False(ContainsNodeType(document.RootElement, "Sort"));
        }
    }

    private static bool ContainsNodeType(JsonElement element, string nodeType)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("Node Type", out var value) &&
                value.ValueKind == JsonValueKind.String &&
                string.Equals(value.GetString(), nodeType, StringComparison.Ordinal))
                return true;
            return element.EnumerateObject().Any(property => ContainsNodeType(property.Value, nodeType));
        }

        return element.ValueKind == JsonValueKind.Array &&
            element.EnumerateArray().Any(item => ContainsNodeType(item, nodeType));
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
