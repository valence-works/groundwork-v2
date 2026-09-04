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
