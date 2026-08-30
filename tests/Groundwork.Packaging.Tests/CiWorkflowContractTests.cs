using Xunit;

namespace Groundwork.Packaging.Tests;

public sealed class CiWorkflowContractTests
{
    [Fact]
    public void MySql_live_evidence_covers_correctness_schema_tool_and_main_concurrency()
    {
        var root = RepositoryRoot.Find();
        var correctness = File.ReadAllText(Path.Combine(root, ".github/workflows/ci.yml"));
        var concurrency = File.ReadAllText(Path.Combine(root, ".github/workflows/concurrency.yml"));

        Assert.Contains("mysql-provider:", correctness, StringComparison.Ordinal);
        Assert.Contains("image: mysql:8.4.6", correctness, StringComparison.Ordinal);
        Assert.Contains("GROUNDWORK_MYSQL_CONNECTION:", correctness, StringComparison.Ordinal);
        Assert.Contains("tests/Groundwork.MySql.Tests/Groundwork.MySql.Tests.csproj", correctness, StringComparison.Ordinal);
        Assert.Contains("SchemaToolMySqlEndToEndTests", correctness, StringComparison.Ordinal);
        Assert.Contains("Refuse a run whose MySQL proofs did not execute", correctness, StringComparison.Ordinal);

        Assert.Contains("image: mysql:8.4.6", concurrency, StringComparison.Ordinal);
        Assert.Contains("GROUNDWORK_MYSQL_CONNECTION:", concurrency, StringComparison.Ordinal);
        Assert.Contains("MySQL: live provider-neutral harness", concurrency, StringComparison.Ordinal);
    }
}
