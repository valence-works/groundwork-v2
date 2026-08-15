using Groundwork.Kernel;
using Groundwork.MongoDb;
using Groundwork.PostgreSql;
using Groundwork.Query.Model;
using Groundwork.Query.Model.Tests;
using Groundwork.SqlServer;
using Groundwork.Sqlite;
using Groundwork.Testing;
using System.Collections.Immutable;
using Xunit;

namespace Groundwork.Differential.Tests;

public sealed class CorpusDifferentialTests
{
    private static readonly string RunTableName = "g2-edge-row-" + Guid.NewGuid().ToString("N");

    [SkippableFact]
    public void Pinned_40_row_300_shape_corpus_is_bit_identical_through_public_provider_sessions()
    {
        var postgres = Required("GROUNDWORK_POSTGRES_CONNECTION");
        var sqlServer = Required("GROUNDWORK_SQLSERVER_CONNECTION");
        var mongo = Required("GROUNDWORK_MONGO_CONNECTION");
        using var sqlite = OpenSqlite();
        using var pg = OpenPostgreSql(postgres);
        using var sql = OpenSqlServer(sqlServer);
        using var mongoSession = OpenMongo(mongo);
        var providers = new[] { sqlite, pg, sql, mongoSession };
        var options = Options();

        Assert.Equal(G2Q1Corpus.ExpectedShapeCount, G2Q1Corpus.Shapes.Count);
        Assert.Equal(243, G2Q1Corpus.Shapes.Count(shape => shape.Decision == Q1CorpusDecision.Normalize));
        Assert.Equal(57, G2Q1Corpus.Shapes.Count(shape => shape.Decision == Q1CorpusDecision.Refuse));

        foreach (var shape in G2Q1Corpus.Shapes)
        {
            if (shape.PublicConstructionRejects)
            {
                Assert.ThrowsAny<ArgumentException>(() => shape.Exercise());
                continue;
            }

            var exercise = shape.Exercise();
            var request = Retarget(exercise.Request);
            var expectedValidation = PortableQuerySemantics.Validate(request);
            var observations = providers.Select(provider => Observe(provider, request, options)).ToArray();
            if (shape.Decision == Q1CorpusDecision.Normalize)
            {
                Assert.True(expectedValidation.IsPortable, $"{shape.Number}: {shape.Description}: {string.Join("; ", expectedValidation.Refusals.Select(refusal => refusal.Code))}");
                Assert.All(observations, observation => Assert.True(observation.IsSuccess, $"{shape.Number}: {shape.Description}: {observation.Error}"));
                var expected = observations[0].Result!;
                Assert.All(observations.Skip(1), observation => Assert.True(
                    string.Equals(expected, observation.Result, StringComparison.Ordinal),
                    $"{shape.Number}: {shape.Description}: {observation.Provider} differed from SQLite. Expected={expected} Actual={observation.Result}"));
            }
            else
            {
                Assert.False(expectedValidation.IsPortable, $"{shape.Number}: {shape.Description} unexpectedly normalized.");
                Assert.All(observations, observation => Assert.False(observation.IsSuccess, $"{shape.Number}: {shape.Description} unexpectedly rendered."));
                var expected = observations[0].Error!;
                Assert.All(observations.Skip(1), observation => Assert.True(
                    string.Equals(expected, observation.Error, StringComparison.Ordinal),
                    $"{shape.Number}: {shape.Description}: {observation.Provider} refusal differed from SQLite."));
            }
        }
    }

    [SkippableFact]
    public void Public_nullable_keyset_continuation_is_equivalent_on_all_four_providers()
    {
        var postgres = Required("GROUNDWORK_POSTGRES_CONNECTION");
        var sqlServer = Required("GROUNDWORK_SQLSERVER_CONNECTION");
        var mongo = Required("GROUNDWORK_MONGO_CONNECTION");
        using var sqlite = OpenSqlite();
        using var pg = OpenPostgreSql(postgres);
        using var sql = OpenSqlServer(sqlServer);
        using var mongoSession = OpenMongo(mongo);
        var providers = new[] { sqlite, pg, sql, mongoSession };
        var table = new TableId(RunTableName);
        var amount = new ColumnRef(table, "numberValue", QueryType.Decimal, isNullable: true, decimalPrecision: 18, decimalScale: 4);
        var id = new ColumnRef(table, "id", QueryType.Int64, isNullable: false);
        var options = Options();
        var firstRequest = new QueryRequest(
            table,
            Predicate.AlwaysTrue.Instance,
            [new OrderTerm(amount, OrderDirection.Ascending, NullOrder.First)],
            Projection.ColumnsOnly(amount),
            Paging.Keyset(5));

        var firstPages = providers.Select(provider => provider.Query(firstRequest, options)).ToArray();
        Assert.All(firstPages, page => Assert.NotNull(page.NextContinuationToken));
        var firstCanonical = Canonical(firstPages[0]);
        Assert.All(firstPages.Skip(1), page => Assert.Equal(firstCanonical, Canonical(page)));

        var secondRequest = new QueryRequest(
            table,
            Predicate.AlwaysTrue.Instance,
            firstRequest.Order,
            firstRequest.Projection,
            Paging.Continuation(firstPages[0].NextContinuationToken!, 5));
        var secondPages = providers.Select(provider => provider.Query(secondRequest, options)).ToArray();
        var secondCanonical = Canonical(secondPages[0]);
        Assert.All(secondPages.Skip(1), page => Assert.Equal(secondCanonical, Canonical(page)));
    }

    [SkippableFact]
    public void Public_result_materialization_preserves_count_and_hides_provider_fields()
    {
        var postgres = Required("GROUNDWORK_POSTGRES_CONNECTION");
        var sqlServer = Required("GROUNDWORK_SQLSERVER_CONNECTION");
        var mongo = Required("GROUNDWORK_MONGO_CONNECTION");
        using var sqlite = OpenSqlite();
        using var pg = OpenPostgreSql(postgres);
        using var sql = OpenSqlServer(sqlServer);
        using var mongoSession = OpenMongo(mongo);
        var table = new TableId(RunTableName);
        var amount = new ColumnRef(table, "numberValue", QueryType.Decimal, isNullable: true, decimalPrecision: 18, decimalScale: 4);
        var id = new ColumnRef(table, "id", QueryType.Int64, isNullable: false);
        var request = new QueryRequest(
            table,
            Predicate.AlwaysTrue.Instance,
            [new OrderTerm(amount, OrderDirection.Ascending, NullOrder.First)],
            Projection.ColumnsOnly(id, amount),
            Paging.None,
            ResultShape.TotalCount.Instance);
        var providers = new[] { sqlite, pg, sql, mongoSession };

        foreach (var provider in providers)
        {
            var result = provider.Query(request, Options());
            Assert.Equal(40, result.TotalCount);
            Assert.Equal(40, result.Rows.Count);
            Assert.All(result.Rows, row =>
            {
                Assert.Equal(new[] { "id", "numberValue" }, row.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray());
                Assert.DoesNotContain(row.Keys, key => key.StartsWith("_groundwork_", StringComparison.Ordinal));
            });

            var allColumns = provider.Query(new QueryRequest(
                table,
                request.Where,
                request.Order,
                Projection.All,
                Paging.None,
                ResultShape.TotalCount.Instance), Options());
            Assert.Equal(40, allColumns.TotalCount);
            Assert.All(allColumns.Rows, row => Assert.DoesNotContain(
                row.Keys, key => key.StartsWith("_groundwork_", StringComparison.Ordinal)));
        }

        var hinted = sql.Query(request, Options());
        Assert.True(hinted.IndexHintApplied);
        Assert.Equal("ix_number", hinted.SelectedIndex);
    }

    [Fact]
    public void Bound_cursor_rejects_a_different_identity_order()
    {
        var table = new TableId("cursor");
        var value = new ColumnRef(table, "value", QueryType.Int32, isNullable: true);
        var id = new ColumnRef(table, "id", QueryType.Int64, isNullable: false);
        var request = new QueryRequest(table, Predicate.AlwaysTrue.Instance,
            [new OrderTerm(value, OrderDirection.Ascending, NullOrder.First)], Projection.All, Paging.Keyset(2));
        var options = new QueryRenderOptions(tieBreakColumns: [id]);
        var token = QueryContinuationToken.Encode(request, options,
            [QueryConstant.Of(value, null), QueryConstant.Of(id, 1L)]);
        var other = new QueryRenderOptions(tieBreakColumns: [new ColumnRef(table, "otherId", QueryType.Int64, isNullable: false)]);
        Assert.Throws<FormatException>(() => QueryContinuationToken.Decode(token, request, other));
    }

    private static Observation Observe(CorpusSession provider, QueryRequest request, QueryRenderOptions options)
    {
        try
        {
            return new Observation(provider.Name, true, Canonical(provider.Query(request, options)), null);
        }
        catch (QueryRenderException exception)
        {
            return new Observation(provider.Name, false, null, exception.Code + "|" + exception.Message);
        }
    }

    private static string Canonical(QueryMaterializedResult result) =>
        string.Join("\n", result.Rows.Select(row => string.Join(";", row.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + Value(pair.Value)))));

    private static string Value(object? value) => value switch
    {
        null => "null",
        byte[] bytes => "b:" + Convert.ToBase64String(bytes),
        DateTimeOffset instant => "t:" + instant.ToUniversalTime().Ticks,
        Guid guid => "g:" + guid.ToString("D"),
        decimal number => "d:" + number.ToString("G29", System.Globalization.CultureInfo.InvariantCulture),
        bool boolean => boolean ? "true" : "false",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
    };

    private static QueryRenderOptions Options() => new(
        indexes: [new QueryIndexDeclaration("ix_number", ["numberValue"], QueryIndexPinning.Pinned)],
        selectedIndex: "ix_number",
        tieBreakColumns: [new ColumnRef(new TableId(RunTableName), "id", QueryType.Int64, isNullable: false)]);

    private static StorageUnit Unit => new()
    {
        Id = new StorageUnitId(RunTableName),
        Name = RunTableName,
        Columns =
        [
            new() { Name = "id", Type = PortableType.Int64, IsNullable = false },
            new() { Name = "textSearch", Type = PortableType.String, IsNullable = true, MaxLength = 320 },
            new() { Name = "numberValue", Type = PortableType.Decimal, IsNullable = true, Precision = 18, Scale = 4 },
            new() { Name = "boolValue", Type = PortableType.Boolean, IsNullable = true },
            new() { Name = "boolValueKey", Type = PortableType.Int32, IsNullable = false },
            new() { Name = "dateTicks", Type = PortableType.DateTimeOffset, IsNullable = true },
            new() { Name = "guidKey", Type = PortableType.Guid, IsNullable = true },
            new() { Name = "binaryValue", Type = PortableType.Binary, IsNullable = true, MaxLength = 64 }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        Indexes = [new IndexDefinition { Name = "ix_number", Columns = [new IndexColumn("numberValue")] }]
    };

    private static QueryRequest Retarget(QueryRequest request)
    {
        var table = new TableId(RunTableName);
        return new QueryRequest(
            table,
            Retarget(request.Where, table),
            request.Order.Select(term => new OrderTerm(Retarget(term.Column, table), term.Direction, term.NullOrder)).ToImmutableArray(),
            request.Projection.AllColumns
                ? Projection.All
                : Projection.ColumnsOnly(request.Projection.Columns.Select(column => Retarget(column, table))),
            request.Paging,
            request.Result,
            request.LatestPerKey is null
                ? null
                : new LatestPerKey(Retarget(request.LatestPerKey.Key, table), Retarget(request.LatestPerKey.Timestamp, table)),
            request.AcceptedScan);
    }

    private static ColumnRef Retarget(ColumnRef column, TableId table) => new(
        table,
        column.Name,
        column.Type,
        column.IsNullable,
        column.MaxLength,
        column.DecimalPrecision,
        column.DecimalScale,
        column.StringComparison);

    private static QueryConstant Retarget(QueryConstant value, ColumnRef column) => QueryConstant.Of(column, value.Value);

    private static Predicate Retarget(Predicate predicate, TableId table) => predicate switch
    {
        Predicate.AlwaysTrue => predicate,
        Predicate.AlwaysFalse => predicate,
        Predicate.Equal equal => new Predicate.Equal(Retarget(equal.Column, table), Retarget(equal.Value, Retarget(equal.Column, table))),
        Predicate.In membership => new Predicate.In(
            Retarget(membership.Column, table),
            membership.Values.Select(value => Retarget(value, Retarget(membership.Column, table)))),
        Predicate.Range range => new Predicate.Range(
            Retarget(range.Column, table),
            range.Lower is null ? null : (range.Lower.IsInclusive ? Bound.Inclusive(Retarget(range.Lower.Value, Retarget(range.Column, table))) : Bound.Exclusive(Retarget(range.Lower.Value, Retarget(range.Column, table)))),
            range.Upper is null ? null : (range.Upper.IsInclusive ? Bound.Inclusive(Retarget(range.Upper.Value, Retarget(range.Column, table))) : Bound.Exclusive(Retarget(range.Upper.Value, Retarget(range.Column, table))))),
        Predicate.StartsWith startsWith => new Predicate.StartsWith(Retarget(startsWith.Column, table), startsWith.Prefix),
        Predicate.Substring substring => new Predicate.Substring(Retarget(substring.Column, table), substring.Needle, substring.Anchor),
        Predicate.ElementOf elementOf => new Predicate.ElementOf(
            elementOf.Set,
            elementOf.Values.Select(value => elementOf.Set.Type is QueryType type
                ? QueryConstant.Of(new ColumnRef(elementOf.Set.Name, type), value.Value)
                : QueryConstant.Of(value.Value)),
            elementOf.Quantifier),
        Predicate.ColumnCompare compare => new Predicate.ColumnCompare(Retarget(compare.Left, table), compare.Op, Retarget(compare.Right, table)),
        Predicate.Not not => new Predicate.Not(Retarget(not.Inner, table)),
        Predicate.And and => new Predicate.And(and.Terms.Select(term => Retarget(term, table))),
        Predicate.Or or => new Predicate.Or(or.Terms.Select(term => Retarget(term, table))),
        _ => throw new ArgumentOutOfRangeException(nameof(predicate), predicate.GetType(), null)
    };

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows => Enumerable.Range(1, 40).Select(index =>
        (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = (long)index,
            ["textSearch"] = new object?[] { null, string.Empty, " ", "I", "i", "İ", "ı", "Straße", "e\u0301", "é" }[(index - 1) % 10],
            ["numberValue"] = (index % 7 - 3) + (index % 5) / 10m,
            ["boolValue"] = index % 5 == 0 ? null : index % 2 == 0,
            ["boolValueKey"] = index % 5 == 0 ? 2 : index % 2 == 0 ? 1 : 0,
            ["dateTicks"] = index % 6 == 0 ? null : DateTimeOffset.UnixEpoch.AddDays(index).ToOffset(index % 2 == 0 ? TimeSpan.FromHours(1) : TimeSpan.Zero),
            ["guidKey"] = index % 9 == 0 ? null : Guid.Parse($"00112233-4455-6677-8899-{index:D12}"),
            ["binaryValue"] = index % 8 == 0 ? null : new byte[] { (byte)index, (byte)(255 - index), 0 }
        }).ToArray();

    private static CorpusSession OpenSqlite()
    {
        var connection = new SqliteProviderFactory().Create("Data Source=file:g2q4_" + Guid.NewGuid().ToString("N") + "?mode=memory&cache=shared");
        connection.Schema.Apply(Unit);
        var session = connection.OpenSession(Unit, StorageAccess.Global);
        foreach (var row in Rows)
        {
            session.Delete(new StorageKey(new Dictionary<string, object?> { ["id"] = row["id"] }));
            session.Insert(new StorageValues(row));
        }
        return new CorpusSession("SQLite", session.Query, connection.Dispose);
    }

    private static CorpusSession OpenPostgreSql(string connectionString)
    {
        var connection = new PostgreSqlProviderFactory().Create(connectionString);
        connection.Schema.Apply(Unit);
        var session = connection.OpenSession(Unit, StorageAccess.Global);
        foreach (var row in Rows)
        {
            session.Delete(new StorageKey(new Dictionary<string, object?> { ["id"] = row["id"] }));
            session.Insert(new StorageValues(row));
        }
        return new CorpusSession("PostgreSQL", session.Query, connection.Dispose);
    }

    private static CorpusSession OpenSqlServer(string connectionString)
    {
        var connection = new SqlServerProviderFactory().Create(connectionString);
        connection.Schema.Apply(Unit);
        var session = connection.OpenSession(Unit, StorageAccess.Global);
        foreach (var row in Rows)
        {
            session.Delete(new StorageKey(new Dictionary<string, object?> { ["id"] = row["id"] }));
            session.Insert(new StorageValues(row));
        }
        return new CorpusSession("SQL Server", session.Query, connection.Dispose);
    }

    private static MongoCorpusSession OpenMongo(string connectionString)
    {
        var connection = new MongoDbProviderFactory().Create(connectionString);
        connection.Schema.Apply(Unit);
        var session = connection.OpenSession(Unit, MongoStorageAccess.Global);
        foreach (var row in Rows)
        {
            session.Delete(new MongoStorageKey(new Dictionary<string, object?> { ["id"] = row["id"] }));
            session.Insert(new MongoStorageValues(row));
        }
        return new MongoCorpusSession(session.Query, connection.Dispose);
    }

    private static string Required(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            Skip.If(true, $"Set {name} to run the live Q4 corpus.");
        return value!;
    }

    private sealed record Observation(string Provider, bool IsSuccess, string? Result, string? Error);

    private class CorpusSession(string name, Func<QueryRequest, QueryRenderOptions?, QueryMaterializedResult> query, Action dispose) : IDisposable
    {
        public string Name { get; } = name;
        public QueryMaterializedResult Query(QueryRequest request, QueryRenderOptions options) => query(request, options);
        public void Dispose() => dispose();
    }

    private sealed class MongoCorpusSession(Func<QueryRequest, QueryRenderOptions?, QueryMaterializedResult> query, Action dispose) : CorpusSession("MongoDB", query, dispose)
    {
    }
}
