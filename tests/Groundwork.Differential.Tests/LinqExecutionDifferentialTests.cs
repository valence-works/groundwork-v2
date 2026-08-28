using Groundwork.PostgreSql;
using Groundwork.Query.Linq;
using Groundwork.Query.Model;
using Groundwork.Query.Planning;
using Groundwork.Sqlite;
using Groundwork.SqlServer;
using Groundwork.Store;
using Xunit;

namespace Groundwork.Differential.Tests;

/// <summary>
/// Four-way differential coverage of the LINQ <em>execution</em> path — rows, ordering, paging, and
/// refusals — rather than of the query model the four providers happen to share. Refusals are
/// asserted with the same weight as rows: a provider that returns the right rows but refuses a
/// different set of queries has not been proven portable.
/// <para>
/// Serialized with the other live-provider differentials, which is what every live-provider suite in
/// this assembly already does. xUnit runs collections in parallel, and two suites writing and reading
/// one SQL Server instance can deadlock each other — a fact about how the tests are run rather than
/// about the providers.
/// </para>
/// </summary>
[Collection(NativeProviderDifferentialCollection.Name)]
public sealed class LinqExecutionDifferentialTests
{
    [Fact]
    public void Differential_descriptions_compare_decimal_values_without_scale()
    {
        Assert.Equal("24", Describe(24.00m));
        Assert.Equal("24", Describe(24m));
        Assert.NotEqual("24", Describe(24.01m));
    }

    [SkippableFact]
    public async Task Linq_terminals_materialize_identical_rows_on_every_provider()
    {
        using var matrix = LinqExecutionMatrix.OpenAll();

        await AssertSameAsync(matrix, provider => provider.Table.Query
            .Where(ticket => ticket.Status == "open")
            .OrderBy(ticket => ticket.SortKey)
            .ToListAsync(provider.Executor));
    }

    [SkippableFact]
    public async Task Linq_ordering_ranks_nulls_identically_on_every_provider()
    {
        using var matrix = LinqExecutionMatrix.OpenAll();

        // Ascending ordering ranks nulls last, descending ranks them first. Both are asserted so a
        // provider cannot pass by defaulting to whichever rank its own engine prefers. The Take is
        // what makes an ordered read index-covered rather than a scan.
        var ascending = await AssertSameAsync(matrix, provider => provider.Table.Query
            .OrderBy(ticket => ticket.Region)
            .ThenBy(ticket => ticket.SortKey)
            .Take(LinqExecutionMatrix.Rows.Count)
            .ToListAsync(provider.Executor));
        var descending = await AssertSameAsync(matrix, provider => provider.Table.Query
            .OrderByDescending(ticket => ticket.Region)
            .ThenBy(ticket => ticket.SortKey)
            .Take(LinqExecutionMatrix.Rows.Count)
            .ToListAsync(provider.Executor));

        Assert.Null(ascending[^1].Region);
        Assert.Null(descending[0].Region);
    }

    [SkippableFact]
    public async Task Linq_offset_paging_windows_identically_on_every_provider()
    {
        using var matrix = LinqExecutionMatrix.OpenAll();

        var page = await AssertSameAsync(matrix, provider => provider.Table.Query
            .Where(ticket => ticket.Status == "open")
            .OrderBy(ticket => ticket.SortKey)
            .Skip(1)
            .Take(2)
            .ToListAsync(provider.Executor));

        Assert.Equal([5L, 1L], page.Select(ticket => ticket.Id));
    }

    [SkippableFact]
    public async Task Linq_executor_resumes_a_keyset_continuation_identically_on_every_provider()
    {
        using var matrix = LinqExecutionMatrix.OpenAll();
        var table = new TableId(matrix.TableName);
        var status = new ColumnRef(table, "status", QueryType.String, isNullable: false, maxLength: 32);
        var sortKey = new ColumnRef(table, "sort_key", QueryType.Int32, isNullable: false);
        var first = new QueryRequest(
            table,
            new Predicate.Equal(status, QueryConstant.Of(status, "open")),
            [new OrderTerm(sortKey, OrderDirection.Ascending, NullOrder.Last)],
            Projection.All,
            Paging.Keyset(2));

        // The token is issued by each provider's own session and then resumed through its executor,
        // so this proves the executor carries the continuation rather than that the tokens happen to
        // encode identically.
        var resumed = await AssertSameAsync(matrix, provider =>
        {
            var page = provider.Session.Query(first, matrix.Unit.CreateQueryRenderOptions());
            Assert.Equal(2, page.Rows.Count);
            Assert.NotNull(page.NextContinuationToken);
            var next = new QueryRequest(table, first.Where, first.Order, Projection.All,
                Paging.Continuation(page.NextContinuationToken!, 2));
            return provider.Executor.ToListAsync(next, matrix.Model);
        });

        Assert.Equal([1L, 4L], resumed.Select(ticket => ticket.Id));
    }

    [SkippableFact]
    public async Task Linq_counts_and_existence_probes_agree_on_every_provider()
    {
        using var matrix = LinqExecutionMatrix.OpenAll();

        Assert.Equal(4, await AssertSameAsync(matrix, provider => provider.Table.Query
            .Where(ticket => ticket.Status == "open")
            .CountAsync(provider.Executor)));
        Assert.True(await AssertSameAsync(matrix, provider => provider.Table.Query
            .Where(ticket => ticket.Status == "closed")
            .AnyAsync(provider.Executor)));
        Assert.False(await AssertSameAsync(matrix, provider => provider.Table.Query
            .Where(ticket => ticket.Status == "archived")
            .AnyAsync(provider.Executor)));
    }

    [SkippableFact]
    public async Task Linq_cardinality_distinct_and_nullable_reductions_agree_on_every_provider()
    {
        using var matrix = LinqExecutionMatrix.OpenAll();

        var first = await AssertSameAsync(matrix, provider => provider.Table.Query
            .Where(ticket => ticket.Status == "open")
            .OrderBy(ticket => ticket.SortKey)
            .FirstAsync(provider.Executor));
        Assert.Equal(2L, first.Id);

        var empty = await AssertSameAsync(matrix, provider => provider.Table.Query
            .Where(ticket => ticket.Status == "archived")
            .OrderBy(ticket => ticket.SortKey)
            .FirstOrDefaultAsync(provider.Executor));
        Assert.Null(empty);

        var one = await AssertSameAsync(matrix, provider => provider.Table.Query
            .Where(ticket => ticket.Id == 4L)
            .SingleAsync(provider.Executor));
        Assert.Equal(4L, one.Id);

        foreach (var provider in matrix.Providers)
            await Assert.ThrowsAsync<InvalidOperationException>(() => provider.Table.Query
                .Where(ticket => ticket.Status == "open")
                .SingleAsync(provider.Executor));

        // Select has no mapped result model, so this intentionally exercises the executor's scalar
        // materializer directly. The region index makes the distinct source covered.
        var distinct = await AssertSameAsync(matrix, provider => provider.Executor.ToListAsync<string?>(provider.Table.Query
            .Where(ticket => ticket.Region == "eu" || ticket.Region == "us" || ticket.Region == null)
            .OrderBy(ticket => ticket.Region)
            .Select(ticket => ticket.Region)
            .Distinct()
            .Take(2)
            .ToQueryRequest()));
        Assert.Equal(["eu", "us"], distinct);

        Assert.Equal(24L, await AssertSameAsync(matrix, provider => provider.Table.Query
            .Where(ticket => ticket.Status == "open")
            .SumAsync(provider.Executor, ticket => ticket.Amount)));
        Assert.Equal(240L, await AssertSameAsync(matrix, provider => provider.Table.Query
            .Where(ticket => ticket.Status == "open")
            .SumAsync(provider.Executor, ticket => ticket.LongAmount)));
        Assert.Equal(24.00m, await AssertSameAsync(matrix, provider => provider.Table.Query
            .Where(ticket => ticket.Status == "open")
            .SumAsync(provider.Executor, ticket => ticket.DecimalAmount)));
        Assert.Equal(4, await AssertSameAsync(matrix, provider => provider.Table.Query
            .Where(ticket => ticket.Status == "open")
            .MinAsync(provider.Executor, ticket => ticket.Amount)));
        Assert.Equal(10L, await AssertSameAsync(matrix, provider => provider.Table.Query
            .Where(ticket => ticket.Status == "open")
            .MaxAsync(provider.Executor, ticket => ticket.LongAmount)));
        Assert.Equal(4.00m, await AssertSameAsync(matrix, provider => provider.Table.Query
            .Where(ticket => ticket.Status == "open")
            .MinAsync(provider.Executor, ticket => ticket.DecimalAmount)));

        Assert.Null(await AssertSameAsync(matrix, provider => provider.Table.Query
            .Where(ticket => ticket.Status == "closed" && ticket.Amount == null)
            .SumAsync(provider.Executor, ticket => ticket.Amount)));
        Assert.Null(await AssertSameAsync(matrix, provider => provider.Table.Query
            .Where(ticket => ticket.Status == "archived")
            .MaxAsync(provider.Executor, ticket => ticket.LongAmount)));
    }

    [SkippableFact]
    public async Task Uncovered_shapes_are_refused_with_the_same_code_and_fix_on_every_provider()
    {
        using var matrix = LinqExecutionMatrix.OpenAll();

        // No declared index leads with sort_key, so no provider can serve this without a scan.
        var uncovered = await AssertRefusedAsync(matrix, provider => provider.Table.Query
            .Where(ticket => ticket.SortKey == 20)
            .ToListAsync(provider.Executor));
        Assert.Equal("GW-COVER-006", uncovered.Code);
        Assert.Contains("Add: [GwIndex(", uncovered.Message, StringComparison.Ordinal);

        var unbounded = await AssertRefusedAsync(matrix, provider => provider.Table.Query
            .CountAsync(provider.Executor));
        Assert.Equal("GW-COVER-005", unbounded.Code);
        Assert.Contains("full counts are scans", unbounded.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Scan_acceptances_are_honored_and_policed_identically_on_every_provider()
    {
        using var matrix = LinqExecutionMatrix.OpenAll();
        var expiry = DateTimeOffset.UtcNow.AddYears(5);

        var accepted = await AssertSameAsync(matrix, provider => provider.Table.Query
            .Where(ticket => ticket.SortKey == 20)
            .AcceptScan("GW-SCAN-0042", "four-way execution differential", "groundwork-tests", expiry)
            .ToListAsync(provider.Executor));
        Assert.Equal([3L, 5L], accepted.Select(ticket => ticket.Id).Order());

        // An acceptance on a query that is already covered is scan debt nobody needs, and an expired
        // one is scan debt nobody renewed. Both refuse, everywhere, with their own codes.
        var stale = await AssertRefusedAsync(matrix, provider => provider.Table.Query
            .Where(ticket => ticket.Status == "open")
            .AcceptScan("GW-SCAN-0043", "already covered", "groundwork-tests", expiry)
            .ToListAsync(provider.Executor));
        Assert.Equal("GW-COVER-901", stale.Code);

        var lapsed = await AssertRefusedAsync(matrix, provider => provider.Table.Query
            .Where(ticket => ticket.SortKey == 20)
            .AcceptScan("GW-SCAN-0044", "lapsed", "groundwork-tests", DateTimeOffset.UtcNow.AddDays(-1))
            .ToListAsync(provider.Executor));
        Assert.Equal("GW-COVER-903", lapsed.Code);
        Assert.Contains("GW-SCAN-0044", lapsed.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The declared key is a coverage candidate on every provider. The matrix unit declares indexes
    /// on <c>status</c> and <c>region</c> only, so nothing but the key admits a read filtered on
    /// <c>id</c>, and every provider must admit it and answer with the same row.
    /// </summary>
    [SkippableFact]
    public async Task Linq_declared_key_equality_is_admitted_on_every_provider()
    {
        using var matrix = LinqExecutionMatrix.OpenAll();

        // Named rather than counted, so a matrix that quietly lost a provider fails here instead of
        // passing as a narrower proof than the test claims to be.
        Assert.Equal(
            ["SQLite", "PostgreSQL", "SQL Server", "MongoDB"],
            matrix.Providers.Select(provider => provider.Name));

        var rows = await AssertSameAsync(matrix, provider => provider.Table.Query
            .Where(ticket => ticket.Id == 4L)
            .ToListAsync(provider.Executor));

        Assert.Equal("4/open/us/40", Assert.Single(rows).ToString());
    }

    [Fact]
    public void Documented_parameter_budgets_are_the_ones_the_renderers_enforce()
    {
        // These three numbers are published in the wiki. They are asserted here so the documentation
        // and the renderers cannot drift apart.
        Assert.Equal(999, SqliteQueryRenderer.ParameterBudget);
        Assert.Equal(2_100, SqlServerQueryRenderer.ParameterBudget);
        Assert.Equal(65_535, PostgreSqlQueryRenderer.ParameterBudget);
    }

    [SkippableFact]
    public void Every_provider_connection_advertises_the_budget_its_own_renderer_enforces()
    {
        using var matrix = LinqExecutionMatrix.OpenAll();

        var budgets = matrix.Providers.ToDictionary(
            provider => provider.Name,
            provider => provider.Connection.GetQueryAdmission().MaximumParameters,
            StringComparer.Ordinal);

        Assert.Equal(SqliteQueryRenderer.ParameterBudget, budgets["SQLite"]);
        Assert.Equal(PostgreSqlQueryRenderer.ParameterBudget, budgets["PostgreSQL"]);
        Assert.Equal(SqlServerQueryRenderer.ParameterBudget, budgets["SQL Server"]);
        // MongoDB binds no SQL parameters. Its ordinary membership ceiling remains the portable
        // 1,000-value renderer limit, while the parameter budget truthfully reports no ceiling.
        Assert.Equal(int.MaxValue, budgets["MongoDB"]);
    }

    /// <summary>Runs one terminal on every provider and requires a single agreed answer.</summary>
    private static async Task<TResult> AssertSameAsync<TResult>(
        LinqExecutionMatrix matrix,
        Func<LinqProvider, Task<TResult>> terminal)
    {
        var results = new List<(string Provider, TResult Value)>();
        foreach (var provider in matrix.Providers)
            results.Add((provider.Name, await terminal(provider).ConfigureAwait(false)));
        var expected = results[0];
        foreach (var actual in results.Skip(1))
        {
            Assert.True(Describe(expected.Value) == Describe(actual.Value),
                $"{actual.Provider} answered {Describe(actual.Value)}; " +
                $"{expected.Provider} answered {Describe(expected.Value)}.");
        }
        return expected.Value;
    }

    /// <summary>Runs one terminal on every provider and requires the same refusal from each.</summary>
    private static async Task<QueryCoverageException> AssertRefusedAsync<TResult>(
        LinqExecutionMatrix matrix,
        Func<LinqProvider, Task<TResult>> terminal)
    {
        var refusals = new List<(string Provider, QueryCoverageException Refusal)>();
        foreach (var provider in matrix.Providers)
        {
            var refusal = await Assert.ThrowsAsync<QueryCoverageException>(() => terminal(provider))
                .ConfigureAwait(false);
            refusals.Add((provider.Name, refusal));
        }
        var expected = refusals[0];
        foreach (var actual in refusals.Skip(1))
        {
            Assert.True(expected.Refusal.Code == actual.Refusal.Code,
                $"{actual.Provider} refused with {actual.Refusal.Code}; " +
                $"{expected.Provider} refused with {expected.Refusal.Code}.");
            Assert.True(expected.Refusal.Message == actual.Refusal.Message,
                $"{actual.Provider} refused with '{actual.Refusal.Message}'; " +
                $"{expected.Provider} refused with '{expected.Refusal.Message}'.");
        }
        return expected.Refusal;
    }

    private static string Describe<TResult>(TResult value) => value switch
    {
        IReadOnlyList<Ticket> tickets => string.Join(" | ", tickets),
        IReadOnlyList<string?> values => string.Join(" | ", values.Select(value => value ?? "<null>")),
        decimal number => number.ToString("G29", System.Globalization.CultureInfo.InvariantCulture),
        null => "<null>",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "<null>"
    };
}
