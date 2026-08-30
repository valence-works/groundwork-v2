using Xunit;

namespace Groundwork.Packaging.Tests;

public sealed class CiWorkflowContractTests
{
    [Fact]
    public void MySql_live_evidence_covers_correctness_schema_tool_and_main_concurrency()
    {
        var correctness = ReadWorkflow("ci.yml");
        var concurrency = ReadWorkflow("concurrency.yml");

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

    [Fact]
    public void SqlServer_w2_captures_hang_diagnostics_before_the_job_timeout()
    {
        var workflow = ReadWorkflow("concurrency.yml");
        var correctness = ReadWorkflow("ci.yml");
        var jobStart = workflow.IndexOf("  sqlserver-w2-conformance:", StringComparison.Ordinal);
        var jobEnd = workflow.IndexOf("  retention-concurrency:", jobStart, StringComparison.Ordinal);
        Assert.True(jobStart >= 0 && jobEnd > jobStart);
        var job = workflow[jobStart..jobEnd];

        Assert.Contains("timeout-minutes: 30", job, StringComparison.Ordinal);
        Assert.Contains("--filter \"Category=Concurrency\"", job, StringComparison.Ordinal);
        Assert.DoesNotContain("sqlserver-w2-conformance:", correctness, StringComparison.Ordinal);
        Assert.Contains(
            "EXPECTED_REF: ${{ inputs.ref || github.sha }}",
            job,
            StringComparison.Ordinal);
        Assert.Contains(
            "run: bash eng/verify-exact-head.sh \"$EXPECTED_REF\"",
            job,
            StringComparison.Ordinal);
        Assert.Contains(
            "--blame-hang --blame-hang-timeout 20m --blame-hang-dump-type full",
            job,
            StringComparison.Ordinal);
        Assert.Contains(
            "--results-directory artifacts/sqlserver-w2/${{ matrix.target-framework }}",
            job,
            StringComparison.Ordinal);
        Assert.Contains("if: failure()", job, StringComparison.Ordinal);
        Assert.Contains(
            "path: artifacts/sqlserver-w2/${{ matrix.target-framework }}/**",
            job,
            StringComparison.Ordinal);
        Assert.Contains(
            "proof=\"Groundwork.SqlServer.Tests.SqlServerProviderTests." +
            "W2_concurrency_harness_holds_every_named_invariant_for_the_full_matrix\"",
            job,
            StringComparison.Ordinal);
        Assert.Contains(
            "results=\"artifacts/sqlserver-w2/$TFM/sqlserver-w2-$TFM.trx\"",
            job,
            StringComparison.Ordinal);
        Assert.Contains(
            "if [ \"$passed\" -ne 1 ] || [ \"$notExecuted\" -ne 0 ] || " +
            "[ \"$observed\" -ne 1 ]; then",
            job,
            StringComparison.Ordinal);

        var exactHead = job.IndexOf("bash eng/verify-exact-head.sh", StringComparison.Ordinal);
        var test = job.IndexOf("--blame-hang", StringComparison.Ordinal);
        var exactOnce = job.IndexOf("Refuse a run whose SQL Server W2 proof", StringComparison.Ordinal);
        var upload = job.IndexOf("Upload SQL Server W2 diagnostics", StringComparison.Ordinal);
        Assert.True(exactHead >= 0 && exactHead < test && test < exactOnce && exactOnce < upload);
    }

    private static string ReadWorkflow(string name) =>
        File.ReadAllText(Path.Combine(RepositoryRoot.Find(), ".github/workflows", name));
}
