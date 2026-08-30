using Xunit;

namespace Groundwork.Packaging.Tests;

public sealed class CiWorkflowContractTests
{
    [Fact]
    public void Measured_performance_evidence_is_manual_and_separate_from_correctness_gates()
    {
        var workflow = ReadWorkflow("performance.yml").ReplaceLineEndings("\n");
        var correctness = ReadWorkflow("ci.yml");
        var concurrency = ReadWorkflow("concurrency.yml");
        var solution = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "Groundwork.slnx"));
        var triggers = workflow[workflow.IndexOf("\non:", StringComparison.Ordinal)..workflow.IndexOf("\npermissions:", StringComparison.Ordinal)];

        Assert.Contains("workflow_dispatch:", triggers, StringComparison.Ordinal);
        Assert.DoesNotContain("pull_request:", triggers, StringComparison.Ordinal);
        Assert.DoesNotContain("push:", triggers, StringComparison.Ordinal);
        Assert.Contains("bash eng/verify-exact-head.sh \"$EXPECTED_REF\"", workflow, StringComparison.Ordinal);
        Assert.Contains("Diagnostic evidence (not a comparative latency baseline)", workflow, StringComparison.Ordinal);
        Assert.Contains("evidence_kind=diagnostic-not-comparative-baseline", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("benchmarks --filter", workflow, StringComparison.Ordinal);

        Assert.Contains(
            "dotnet test Groundwork.slnx --no-restore --configuration Release --filter \"Category!=Concurrency\"",
            correctness,
            StringComparison.Ordinal);
        Assert.Contains("tests/Groundwork.Benchmarks.Tests/Groundwork.Benchmarks.Tests.csproj", solution, StringComparison.Ordinal);
        Assert.DoesNotContain("benchmarks --filter", correctness, StringComparison.Ordinal);
        Assert.DoesNotContain("benchmarks --filter", concurrency, StringComparison.Ordinal);

        var collector = File.ReadAllText(Path.Combine(
            RepositoryRoot.Find(),
            "eng",
            "collect-comparative-performance.sh"));
        Assert.Contains("GROUNDWORK_CONFIRM_IDLE_HOST", collector, StringComparison.Ordinal);
        Assert.Contains("eng/verify-exact-head.sh", collector, StringComparison.Ordinal);
        Assert.Contains("benchmarks --list flat", collector, StringComparison.Ordinal);
        const string allWorkloads =
            "--filter '*PointRead*' '*CoveredQuery*' '*PagedQuery*' '*BatchedWrite*' '*UnitOfWorkCommit*'";
        Assert.Equal(2, collector.Split(allWorkloads, StringSplitOptions.None).Length - 1);
        Assert.Contains("--exporters json markdown csv", collector, StringComparison.Ordinal);
        Assert.DoesNotContain("threshold", collector, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("baseline.json", collector, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Comparative_benchmark_key_scenarios_are_an_explicit_stable_catalog()
    {
        var root = RepositoryRoot.Find();
        var scenarios = File.ReadAllLines(Path.Combine(
            root,
            "benchmarks/Groundwork.Benchmarks/evidence/key-scenarios.txt"));

        Assert.Equal(
            new[]
            {
                "Groundwork.Benchmarks.StorageBenchmarks.BatchedWrite_Groundwork",
                "Groundwork.Benchmarks.StorageBenchmarks.BatchedWrite_EFCoreCompiledModel",
                "Groundwork.Benchmarks.StorageBenchmarks.BatchedWrite_Dapper",
                "Groundwork.Benchmarks.StorageBenchmarks.CoveredQuery_Groundwork",
                "Groundwork.Benchmarks.StorageBenchmarks.CoveredQuery_EFCoreCompiledModel",
                "Groundwork.Benchmarks.StorageBenchmarks.CoveredQuery_Dapper",
                "Groundwork.Benchmarks.StorageBenchmarks.PagedQuery_Groundwork",
                "Groundwork.Benchmarks.StorageBenchmarks.PagedQuery_EFCoreCompiledModel",
                "Groundwork.Benchmarks.StorageBenchmarks.PagedQuery_Dapper",
                "Groundwork.Benchmarks.StorageBenchmarks.PointRead_Groundwork",
                "Groundwork.Benchmarks.StorageBenchmarks.PointRead_EFCoreCompiledModel",
                "Groundwork.Benchmarks.StorageBenchmarks.PointRead_Dapper",
                "Groundwork.Benchmarks.StorageBenchmarks.UnitOfWorkCommit_Groundwork",
                "Groundwork.Benchmarks.StorageBenchmarks.UnitOfWorkCommit_EFCoreCompiledModel",
                "Groundwork.Benchmarks.StorageBenchmarks.UnitOfWorkCommit_Dapper",
            },
            scenarios);
    }

    [Fact]
    public void Full_solution_recurrence_harness_preserves_the_clean_window_contract()
    {
        var script = File.ReadAllText(Path.Combine(
            RepositoryRoot.Find(),
            "eng",
            "run-full-solution-recurrence.sh"));

        Assert.Contains("GROUNDWORK_CONFIRM_IDLE_HOST", script, StringComparison.Ordinal);
        Assert.Contains("eng/verify-exact-head.sh", script, StringComparison.Ordinal);
        Assert.Contains("git status --porcelain", script, StringComparison.Ordinal);
        Assert.Contains("/tmp/groundwork-tests.lock", script, StringComparison.Ordinal);
        Assert.Contains("flock -n 9", script, StringComparison.Ordinal);
        Assert.Contains("lockf -t 0 9", script, StringComparison.Ordinal);
        Assert.Contains("[d]otnet[[:space:]]+test|[t]esthost|[v]stest", script, StringComparison.Ordinal);
        Assert.Contains("seq 1 11", script, StringComparison.Ordinal);
        Assert.Contains("sleep 30", script, StringComparison.Ordinal);
        Assert.Contains("load <= 1.0", script, StringComparison.Ordinal);
        Assert.Contains("seq 1 5", script, StringComparison.Ordinal);
        Assert.Contains("dotnet restore Groundwork.slnx", script, StringComparison.Ordinal);
        Assert.Contains("dotnet test Groundwork.slnx", script, StringComparison.Ordinal);
        Assert.Contains("--no-restore --configuration Release", script, StringComparison.Ordinal);
        Assert.Contains("--logger trx", script, StringComparison.Ordinal);
        Assert.Contains("--results-directory \"$run_directory\"", script, StringComparison.Ordinal);
        Assert.Contains("--blame-hang --blame-hang-timeout 20m --blame-hang-dump-type full", script, StringComparison.Ordinal);
        Assert.Contains("PIPESTATUS[0]", script, StringComparison.Ordinal);
        Assert.DoesNotContain("--filter", script, StringComparison.Ordinal);
        Assert.DoesNotContain("-m:1", script, StringComparison.Ordinal);

        var restore = script.IndexOf("dotnet restore Groundwork.slnx", StringComparison.Ordinal);
        var test = script.IndexOf("dotnet test Groundwork.slnx", StringComparison.Ordinal);
        Assert.True(restore >= 0 && restore < test);
    }

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
