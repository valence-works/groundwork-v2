using Groundwork.Kernel;
using Groundwork.Query.Linq.Execution;
using Groundwork.Query.Model;
using Groundwork.Query.Planning;
using Groundwork.Store;
using Xunit;

namespace Groundwork.Differential.Tests;

/// <summary>
/// Public-surface evidence that one declared reference has the same result, ordering, continuation,
/// scope isolation, two-valued null behavior, and coverage refusal on all four shipped providers.
/// </summary>
[Collection(NativeProviderDifferentialCollection.Name)]
public sealed class JoinExecutionDifferentialTests
{
    [Fact]
    public void SQLite_tracer_proves_joined_ordering_paging_and_two_valued_nulls()
    {
        using var matrix = JoinExecutionMatrix.OpenSqlite();

        AssertOrderedPagesAndNullComplement(matrix);
    }

    [SkippableFact]
    public void Joined_ordering_paging_and_two_valued_nulls_match_on_all_four_providers()
    {
        using var matrix = JoinExecutionMatrix.OpenAll();

        AssertProviderNames(matrix);
        AssertOrderedPagesAndNullComplement(matrix);
    }

    [Fact]
    public void SQLite_tracer_proves_scoped_joins_do_not_cross_scope_storage()
    {
        using var matrix = JoinExecutionMatrix.OpenSqlite(ScopePolicy.Scoped);

        AssertScopedIsolation(matrix);
    }

    [SkippableFact]
    public void Scoped_joins_match_only_rows_from_the_current_scope_on_all_four_providers()
    {
        using var matrix = JoinExecutionMatrix.OpenAll(ScopePolicy.Scoped);

        AssertProviderNames(matrix);
        AssertScopedIsolation(matrix);
    }

    [Fact]
    public void SQLite_tracer_refuses_an_uncovered_target_join_shape()
    {
        using var matrix = JoinExecutionMatrix.OpenSqlite(includeTargetPredicateIndex: false);

        AssertUncoveredRefusalParity(matrix);
    }

    [SkippableFact]
    public void Uncovered_target_join_shapes_refuse_identically_on_all_four_providers()
    {
        using var matrix = JoinExecutionMatrix.OpenAll(includeTargetPredicateIndex: false);

        AssertProviderNames(matrix);
        AssertUncoveredRefusalParity(matrix);
    }

    private static void AssertScopedIsolation(JoinExecutionMatrix matrix)
    {
        var request = matrix.ScopedRows();
        foreach (var provider in matrix.Providers)
        {
            var first = Assert.Single(Query(provider, provider.FirstScope, request).Rows);
            var second = Assert.Single(Query(provider, provider.SecondScope!, request).Rows);

            Assert.Equal("1|Ada-A", Signature(request, matrix, first, includeNickname: false));
            Assert.Equal("1|Ada-B", Signature(request, matrix, second, includeNickname: false));
        }
    }

    private static void AssertUncoveredRefusalParity(JoinExecutionMatrix matrix)
    {
        var refusals = new Dictionary<string, QueryCoverageException>(StringComparer.Ordinal);
        foreach (var provider in matrix.Providers)
        {
            refusals[provider.Name] = Assert.Throws<QueryCoverageException>(() =>
                QueryCoverageEnforcer.EnsureCovered(
                    matrix.UncoveredTargetPredicate(),
                    CoverageCandidates(provider)));
        }

        var expected = refusals.Values.First();
        Assert.Equal("GW-COVER-006", expected.Code);
        Assert.Contains("target", expected.Message, StringComparison.OrdinalIgnoreCase);
        Assert.All(refusals.Values, actual =>
        {
            Assert.Equal(expected.Code, actual.Code);
            Assert.Equal(expected.Message, actual.Message);
        });
    }

    private static void AssertOrderedPagesAndNullComplement(JoinExecutionMatrix matrix)
    {
        foreach (var provider in matrix.Providers)
        {
            var firstRequest = matrix.OrderedPage(Paging.Keyset(2));
            var first = Query(provider, provider.FirstScope, firstRequest);
            Assert.Equal(
                ["4|Cy|ok", "1|Bea|blocked"],
                first.Rows.Select(row => Signature(firstRequest, matrix, row)));
            Assert.NotNull(first.NextContinuationToken);

            var secondRequest = matrix.OrderedPage(Paging.Continuation(first.NextContinuationToken!, 2));
            var second = Query(provider, provider.FirstScope, secondRequest);
            Assert.Equal(
                ["2|Ada|null", "5|Ada|null"],
                second.Rows.Select(row => Signature(secondRequest, matrix, row)));
            Assert.Null(second.NextContinuationToken);

            var nullRequest = matrix.TwoValuedNullPredicate();
            var nullComplement = Query(provider, provider.FirstScope, nullRequest);
            Assert.Equal(
                ["2|null", "4|ok", "5|null"],
                nullComplement.Rows.Select(row => Signature(
                    nullRequest,
                    matrix,
                    row,
                    includeName: false)));
        }
    }

    private static QueryMaterializedResult Query(
        JoinProvider provider,
        IStorageSession session,
        QueryRequest request)
    {
        QueryCoverageEnforcer.EnsureCovered(request, CoverageCandidates(provider));
        return session.Query(request);
    }

    private static QueryCoverageCandidates CoverageCandidates(JoinProvider provider) => new(
        StorageUnitCoverage.PortableIndexes(provider.FirstScope.Unit),
        StorageUnitCoverage.PortableIndexes(provider.Target.Unit));

    private static string Signature(
        QueryRequest request,
        JoinExecutionMatrix matrix,
        IReadOnlyDictionary<string, object?> row,
        bool includeName = true,
        bool includeNickname = true)
    {
        var values = new List<string>
        {
            Convert.ToString(
                row[QueryRequestExecution.ResultFieldName(request, matrix.SourceId)],
                System.Globalization.CultureInfo.InvariantCulture)!
        };
        if (includeName)
        {
            values.Add((string)row[
                QueryRequestExecution.ResultFieldName(request, matrix.TargetName)]!);
        }
        if (includeNickname)
        {
            values.Add(row[
                QueryRequestExecution.ResultFieldName(request, matrix.TargetNickname)] as string ?? "null");
        }
        return string.Join('|', values);
    }

    private static void AssertProviderNames(JoinExecutionMatrix matrix) =>
        Assert.Equal(
            ["SQLite", "PostgreSQL", "SQL Server", "MongoDB"],
            matrix.Providers.Select(provider => provider.Name));
}
