using Groundwork.Kernel;
using Groundwork.LiveDatabases;
using Groundwork.MongoDb;
using Groundwork.PostgreSql;
using Groundwork.Query.Model;
using Groundwork.SqlServer;
using Groundwork.Sqlite;
using Groundwork.Store;
using Groundwork.Substrate.Relational;
using MongoDB.Bson;
using Xunit;

namespace Groundwork.Differential.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NativeProviderDifferentialCollection
{
    public const string Name = "Native provider differential tests";
}

/// <summary>
/// Differential acceptance proof for the native, scoped aggregation contract in #292.
///
/// The test deliberately calls the public provider session's Aggregate method. Its internal
/// artifact fact additionally proves the provider-owned scope command/pipeline shape without
/// widening the public API or claiming an ordinary Query observer.
/// </summary>
[Collection(NativeProviderDifferentialCollection.Name)]
public sealed class ScopedAggregationDifferentialTests
{
    [Fact]
    public void ScopedAggregation_native_provider_artifacts_are_bounded_and_scope_owned()
    {
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("s9_scope_artifact"),
            Name = "s9_scope_artifact",
            Scope = ScopePolicy.Scoped,
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, IsNullable = false },
                new() { Name = "groupKey", Type = PortableType.Int64, IsNullable = false },
                new() { Name = "label", Type = PortableType.String, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] }
        };
        var profile = new AggregationProfile
        {
            Name = "summary",
            GroupByColumns = ["groupKey"],
            Aggregates = [new Aggregate.Count("label")],
            MaxInputRows = 20,
            MaxGroups = 10
        };
        var query = new AggregationQuery("summary")
        {
            OrderByTerms =
            [
                new AggregationOrderTerm("label", SortDirection.Descending),
                new AggregationOrderTerm("groupKey", SortDirection.Ascending)
            ],
            Take = 5
        };

        AssertRelationalArtifact(new SqliteDialect(), SqliteSchemaCoordinator.ScopeColumn, "COUNT(*)", "LIMIT 5", "GROUNDWORK_UTF16_ORDINAL");
        AssertRelationalArtifact(new PostgreSqlDialect(), PostgreSqlSchemaCoordinator.ScopeColumn, "COUNT(*)", "LIMIT 5", "string_to_array");
        AssertRelationalArtifact(new SqlServerDialect(), SqlServerSchemaCoordinator.ScopeColumn, "COUNT_BIG(*)", "FETCH NEXT 5 ROWS ONLY", "Latin1_General_100_BIN2");

        var stages = MongoStorageSession.RenderNativeAggregationPipeline(unit, profile, query);
        var pipeline = string.Join("\n", stages.Select(stage => stage.ToJson()));
        var applied = new MongoAppliedUnit(unit, unit.Name);
        var firstCollection = MongoSchemaCoordinator.CollectionName(
            applied,
            MongoStorageAccess.Scoped(new StorageScope("scope-a")));
        var secondCollection = MongoSchemaCoordinator.CollectionName(
            applied,
            MongoStorageAccess.Scoped(new StorageScope("scope-b")));

        Assert.Contains("\"$group\"", pipeline, StringComparison.Ordinal);
        Assert.Contains("\"label\" : { \"$sum\" : 1", pipeline, StringComparison.Ordinal);
        Assert.Contains("\"$sort\"", pipeline, StringComparison.Ordinal);
        Assert.Contains("\"$limit\" : 5", pipeline, StringComparison.Ordinal);
        Assert.DoesNotContain("__groundwork_aggregation_order_key_label", pipeline, StringComparison.Ordinal);
        Assert.NotEqual(firstCollection, secondCollection);
        Assert.Contains("__scope__", firstCollection, StringComparison.Ordinal);
        Assert.Contains("__scope__", secondCollection, StringComparison.Ordinal);

        void AssertRelationalArtifact(
            RelationalDialect dialect,
            string scopeColumn,
            string count,
            string limit,
            string stringOrderMarker)
        {
            var scope = new ColumnRef(new TableId(unit.Name), scopeColumn, QueryType.String, isNullable: false);
            var providerPredicate = new Predicate.Equal(scope, QueryConstant.Of(scope, "scope-a"));
            var command = RelationalAggregationRenderer.RenderWithProviderPredicate(
                dialect, unit, profile, query, providerPredicate).CommandText;
            var probe = RelationalAggregationRenderer.RenderBudgetProbeWithProviderPredicate(
                dialect, unit, profile, query, providerPredicate).CommandText;

            Assert.StartsWith("WITH ", command, StringComparison.Ordinal);
            Assert.Contains(scopeColumn, command, StringComparison.Ordinal);
            Assert.Contains(count, command, StringComparison.Ordinal);
            Assert.Contains("GROUP BY", command, StringComparison.Ordinal);
            Assert.Contains(limit, command, StringComparison.Ordinal);
            var orderStart = command.IndexOf(" ORDER BY ", StringComparison.OrdinalIgnoreCase);
            Assert.True(orderStart >= 0, "The native aggregation command must render an output order.");
            Assert.DoesNotContain(stringOrderMarker, command[orderStart..], StringComparison.Ordinal);
            Assert.Contains(scopeColumn, probe, StringComparison.Ordinal);
            Assert.Contains("GROUP BY", probe, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SQLite_native_scoped_aggregation_is_isolated_and_bounded()
    {
        AssertProvider("SQLite", () => new SqliteProviderFactory().Create(
            "Data Source=file:groundwork_s9_" + Guid.NewGuid().ToString("N") + "?mode=memory&cache=shared"));
    }

    [SkippableFact]
    public void PostgreSQL_native_scoped_aggregation_is_isolated_and_bounded()
    {
        AssertProvider("PostgreSQL", () => new PostgreSqlProviderFactory().Create(
            Required("GROUNDWORK_POSTGRES_CONNECTION")));
    }

    [SkippableFact]
    public void SQLServer_native_scoped_aggregation_is_isolated_and_bounded()
    {
        AssertProvider("SQL Server", () => new SqlServerProviderFactory().Create(
            LiveSqlServer.Required()));
    }

    [SkippableFact]
    public void MongoDB_transactional_native_scoped_aggregation_is_isolated_and_bounded()
    {
        AssertProvider("MongoDB", () => new MongoProviderFactory().Create(
            LiveMongo.Required()));
    }

    private static void AssertProvider(string providerName, Func<IStorageProviderConnection> open)
    {
        using var connection = open();
        var unit = CreateUnit("s9_scope_" + Guid.NewGuid().ToString("N"));
        Assert.True(connection.Schema.Apply(unit).Applied, providerName);

        var first = connection.OpenSession(unit, StorageAccess.Scoped(new StorageScope("scope-a")));
        var second = connection.OpenSession(unit, StorageAccess.Scoped(new StorageScope("scope-b")));
        var firstRows = Rows("a");
        var secondRows = Rows("b");

        // The physical row keys and group keys are intentionally identical in both scopes. Only
        // values differ, so any cross-scope leak changes the reduction rather than being hidden by
        // different input identities.
        foreach (var row in firstRows)
            Assert.Equal(WriteOutcomeStatus.Inserted, first.Insert(new StorageValues(row)).Status);
        foreach (var row in secondRows)
            Assert.Equal(WriteOutcomeStatus.Inserted, second.Insert(new StorageValues(row)).Status);

        var orderedQuery = new AggregationQuery("summary")
        {
            OrderByTerms =
            [
                new AggregationOrderTerm("count", SortDirection.Descending),
                new AggregationOrderTerm("groupKey", SortDirection.Ascending)
            ],
            Take = 5
        };
        var firstTop = first.Aggregate(orderedQuery);
        var secondTop = second.Aggregate(orderedQuery);

        Assert.Equal(
            ["shared", "a", "b", "all-null", "c"],
            firstTop.Rows.Select(row => row["groupKey"]).ToArray());
        Assert.Equal(
            ["shared", "a", "b", "all-null", "c"],
            secondTop.Rows.Select(row => row["groupKey"]).ToArray());
        Assert.Equal(
            new long[] { 3, 2, 2, 1, 1 },
            firstTop.Rows.Select(row => Assert.IsType<long>(row["count"])).ToArray());
        Assert.Equal(
            new long[] { 3, 2, 2, 1, 1 },
            secondTop.Rows.Select(row => Assert.IsType<long>(row["count"])).ToArray());

        // This is the adversarial UTF-16 ordering pair: U+10000 is encoded as D800 DC00 and must
        // sort before U+E000 under the declared ordinal contract, even though scalar-value order
        // alone would suggest the same result for this pair only by accident in some providers.
        var supplementary = char.ConvertFromUtf32(0x10000);
        var privateUse = "\uE000";
        var complete = first.Aggregate(orderedQuery with { Take = null });
        Assert.Equal(
            ["shared", "a", "b", "all-null", "c", "d", supplementary, privateUse],
            complete.Rows.Select(row => row["groupKey"]).ToArray());
        Assert.Equal(1L, Assert.Single(complete.Rows, row => Equals(row["groupKey"], "all-null"))["count"]);
        Assert.Null(Assert.Single(complete.Rows, row => Equals(row["groupKey"], "all-null"))["total"]);
        Assert.Equal(3L, Assert.Single(complete.Rows, row => Equals(row["groupKey"], "shared"))["count"]);
        Assert.Equal(3L, Assert.Single(complete.Rows, row => Equals(row["groupKey"], "shared"))["total"]);

        // Same shape, different scope-bound values. The result identity must distinguish the
        // scopes while retaining one provider-neutral shape fingerprint.
        Assert.NotEqual(firstTop.ValueFingerprint, secondTop.ValueFingerprint);
        Assert.Equal(firstTop.ShapeFingerprint, secondTop.ShapeFingerprint);
        Assert.Equal(3L, Assert.Single(secondTop.Rows, row => Equals(row["groupKey"], "shared"))["count"]);
        Assert.Equal(22L, Assert.Single(secondTop.Rows, row => Equals(row["groupKey"], "shared"))["total"]);

        // Source predicates run before grouping. The literal 20 is in different groups in the two
        // scopes, proving that source filtering is not applied after a shared reduction.
        var amount = new ColumnRef(new TableId(unit.Name), "amount", QueryType.Int64, isNullable: true);
        var source = new AggregationQuery("summary")
        {
            SourcePredicate = new Predicate.Equal(amount, QueryConstant.Of(amount, 20L))
        };
        Assert.Equal("a", Assert.Single(first.Aggregate(source).Rows)["groupKey"]);
        Assert.Equal("shared", Assert.Single(second.Aggregate(source).Rows)["groupKey"]);

        // Post predicates see reduced aliases. This filters the shared group by Count after the
        // native reduction rather than changing the input scan.
        var post = new AggregationQuery("summary")
        {
            PostPredicate = new AggregationPredicate.Comparison(
                "count", AggregationPredicateOperator.Equal, [3L])
        };
        var postResult = first.Aggregate(post);
        Assert.Equal("shared", Assert.Single(postResult.Rows)["groupKey"]);
        Assert.Equal(3L, postResult.Rows[0]["count"]);

        AssertBudgetRefusals(providerName, connection);
    }

    private static void AssertBudgetRefusals(string providerName, IStorageProviderConnection connection)
    {
        var inputUnit = CreateUnit("s9_input_" + Guid.NewGuid().ToString("N")) with
        {
            AggregationProfiles = [CreateProfile() with { MaxInputRows = 2 }]
        };
        Assert.True(connection.Schema.Apply(inputUnit).Applied, providerName + " input budget schema");
        var input = connection.OpenSession(inputUnit, StorageAccess.Scoped(new StorageScope("budget")));
        foreach (var row in Rows("input").Take(3))
            input.Insert(new StorageValues(row));

        var inputException = Assert.Throws<AggregationBudgetExceededException>(() =>
            input.Aggregate(new AggregationQuery("summary")));
        Assert.Equal("GW-AGG-BOUND-004", inputException.Code);
        Assert.Equal(3, ReadIds(input, inputUnit).Count);

        var groupUnit = CreateUnit("s9_groups_" + Guid.NewGuid().ToString("N")) with
        {
            AggregationProfiles = [CreateProfile() with { MaxGroups = 1 }]
        };
        Assert.True(connection.Schema.Apply(groupUnit).Applied, providerName + " group budget schema");
        var groups = connection.OpenSession(groupUnit, StorageAccess.Scoped(new StorageScope("budget")));
        groups.Insert(new StorageValues(Row("one", "one", 1L)));
        groups.Insert(new StorageValues(Row("two", "two", 2L)));

        var groupException = Assert.Throws<AggregationBudgetExceededException>(() =>
            groups.Aggregate(new AggregationQuery("summary")));
        Assert.Equal("GW-AGG-BOUND-005", groupException.Code);
        Assert.Equal(2, ReadIds(groups, groupUnit).Count);
    }

    private static IReadOnlyList<string> ReadIds(IStorageSession session, StorageUnit unit)
    {
        var table = new TableId(unit.Name);
        var id = new ColumnRef(table, "id", QueryType.String, isNullable: false, maxLength: 64);
        return session.Query(new QueryRequest(
                table,
                Predicate.AlwaysTrue.Instance,
                [new OrderTerm(id, OrderDirection.Ascending, NullOrder.First)],
                Projection.ColumnsOnly(id),
                Paging.None))
            .Rows
            .Select(row => Assert.IsType<string>(row["id"]))
            .ToArray();
    }

    private static StorageUnit CreateUnit(string name) => new()
    {
        Id = new StorageUnitId(name),
        Name = name,
        Scope = ScopePolicy.Scoped,
        Columns =
        [
            new() { Name = "id", Type = PortableType.String, IsNullable = false, MaxLength = 64 },
            new() { Name = "groupKey", Type = PortableType.String, IsNullable = false, MaxLength = 64 },
            new() { Name = "amount", Type = PortableType.Int64, IsNullable = true }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        AggregationProfiles = [CreateProfile()]
    };

    private static AggregationProfile CreateProfile() => new()
    {
        Name = "summary",
        GroupByColumns = ["groupKey"],
        Aggregates = [new Aggregate.Count("count"), new Aggregate.Sum("total", "amount")],
        AllowedPredicates =
        [
            new AggregationPredicateAllowance
            {
                Alias = "count",
                SupportedPredicates = new HashSet<AggregationPredicateOperator>
                {
                    AggregationPredicateOperator.Equal
                }
            }
        ],
        MaxGroups = 16,
        MaxInputRows = 32
    };

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows(string suffix) =>
    [
        Row("shared-1", "shared", suffix == "a" ? 2L : 20L),
        Row("shared-2", "shared", null),
        Row("shared-3", "shared", suffix == "a" ? 1L : 2L),
        Row("a-1", "a", suffix == "a" ? 10L : 5L),
        Row("a-2", "a", suffix == "a" ? 20L : 6L),
        Row("b-1", "b", suffix == "a" ? 4L : 9L),
        Row("b-2", "b", suffix == "a" ? 5L : 10L),
        Row("c-1", "c", suffix == "a" ? 7L : 30L),
        Row("d-1", "d", suffix == "a" ? 8L : 40L),
        Row("all-null-1", "all-null", null),
        Row("supplementary-1", char.ConvertFromUtf32(0x10000), suffix == "a" ? 11L : 50L),
        Row("private-use-1", "\uE000", suffix == "a" ? 12L : 60L)
    ];

    private static IReadOnlyDictionary<string, object?> Row(string id, string groupKey, long? amount) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = id,
            ["groupKey"] = groupKey,
            ["amount"] = amount
        };

    private static string Required(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        Skip.If(string.IsNullOrWhiteSpace(value), "Set " + variable + " to run this live provider proof.");
        return value!;
    }
}
