using Groundwork.Kernel;
using Groundwork.LiveDatabases;
using Groundwork.MongoDb;
using Groundwork.PostgreSql;
using Groundwork.Query.Model;
using Groundwork.Query.Model.Tests;
using Groundwork.Query.Planning;
using Groundwork.SqlServer;
using Groundwork.Sqlite;
using Groundwork.Testing;
using Groundwork.Store;
using Groundwork.Diagnostics;
using System.Collections.Immutable;
using Xunit;

namespace Groundwork.Differential.Tests;

public sealed class CorpusDifferentialTests
{
    private static readonly string RunTableName = "g2_edge_row_" + Guid.NewGuid().ToString("N");
    private static readonly string SparseTableName = "g2_sparse_" + Guid.NewGuid().ToString("N");
    private static readonly string SemanticEdgeTableName = "g2_semantic_edge_" + Guid.NewGuid().ToString("N");
    private static readonly string LatestTableName = "g2_latest_" + Guid.NewGuid().ToString("N");
    private static readonly string ScopedTableName = "g2_scoped_" + Guid.NewGuid().ToString("N");
    private static readonly string ExplainTableName = "g2_explain_" + Guid.NewGuid().ToString("N");
    private static readonly string PrefixTableName = "g2_prefix_" + Guid.NewGuid().ToString("N");

    [Fact]
    public void Differential_corpus_marks_only_coverage_proven_queries_for_explain_assertion()
    {
        var table = new TableId(RunTableName);
        var number = new ColumnRef(table, "numberValue", QueryType.Decimal, true, decimalPrecision: 18, decimalScale: 4);
        var text = new ColumnRef(table, "textSearch", QueryType.String, true, maxLength: 320);
        var covered = new QueryRequest(table, new Predicate.Equal(number, QueryConstant.Of(number, 10m)), [], Projection.All, Paging.None);
        var uncovered = new QueryRequest(table, new Predicate.Equal(text, QueryConstant.Of(text, "x")), [], Projection.All, Paging.None);
        var orderOnly = new QueryRequest(
            table,
            Predicate.AlwaysTrue.Instance,
            [new OrderTerm(number, OrderDirection.Ascending, NullOrder.First)],
            Projection.All,
            Paging.Keyset(5));

        Assert.Equal("ix_number", Options(covered).FindPinnedIndex()?.Name);
        Assert.Null(Options(uncovered).FindPinnedIndex());
        Assert.Null(Options(orderOnly).FindPinnedIndex());
    }

    [Fact]
    public void Explain_assert_flag_executes_a_native_plan_check_and_retains_the_sqlite_plan()
    {
        using var environment = new ExplainEnvironment("positive");
        using var sqlite = OpenSqlite();
        var table = new TableId(RunTableName);
        var number = new ColumnRef(table, "numberValue", QueryType.Decimal, isNullable: true, decimalPrecision: 18, decimalScale: 4);
        var request = new QueryRequest(
            table,
            new Predicate.Equal(number, QueryConstant.Of(number, 10m)),
            [],
            Projection.ColumnsOnly(number),
            Paging.None);

        var result = sqlite.Query(request, Options(request));

        Assert.Equal("ix_number", result.SelectedIndex);
        var artifact = Assert.Single(Directory.GetFiles(environment.ArtifactDirectory, "*.txt"));
        Assert.Contains(
            "INDEX " + SqliteDialect.PhysicalIndexName(RunTableName, "ix_number"),
            File.ReadAllText(artifact),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Explain_assert_runtime_rejects_a_misdeclared_index_and_retains_the_plan()
    {
        using var environment = new ExplainEnvironment("negative");
        using var sqlite = OpenSqlite();
        var table = new TableId(RunTableName);
        var number = new ColumnRef(table, "numberValue", QueryType.Decimal, true, decimalPrecision: 18, decimalScale: 4);
        var request = new QueryRequest(
            table,
            new Predicate.Equal(number, QueryConstant.Of(number, 10m)),
            [],
            Projection.ColumnsOnly(number),
            Paging.None);
        var wrongOptions = new QueryRenderOptions(
            [new QueryIndexDeclaration("ix_misdeclared", [new QueryIndexColumn("numberValue", true, QueryType.Decimal)], QueryIndexPinning.Pinned)],
            selectedIndex: "ix_misdeclared");

        var exception = Assert.Throws<ExplainAssertionException>(() => sqlite.Query(request, wrongOptions));

        Assert.Equal("ix_misdeclared", Path.GetFileNameWithoutExtension(exception.ArtifactPath).Split('-').Last());
        Assert.NotEmpty(File.ReadAllText(exception.ArtifactPath));
    }

    [SkippableFact]
    public void Prefix_search_keys_match_the_same_edge_corpus_on_all_four_providers()
    {
        var postgres = Required("GROUNDWORK_POSTGRES_CONNECTION");
        var sqlServer = LiveSqlServer.Required();
        var mongo = LiveMongo.Required();
        using var sqlite = OpenSqlite(PrefixUnit, PrefixRows);
        using var pg = OpenPostgreSql(postgres, PrefixUnit, PrefixRows);
        using var sql = OpenSqlServer(sqlServer, PrefixUnit, PrefixRows);
        using var mongoSession = OpenMongo(mongo, PrefixUnit, PrefixRows);
        var providers = new[] { sqlite, pg, sql, mongoSession };
        var table = new TableId(PrefixTableName);
        var folded = new ColumnRef(table, "folded", QueryType.String, true, 64,
            stringComparison: QueryStringComparisonPolicy.UnicodeOrdinalIgnoreCase);
        var ascii = new ColumnRef(table, "ascii", QueryType.String, true, 64,
            stringComparison: QueryStringComparisonPolicy.AsciiIgnoreCase);
        var ordinal = new ColumnRef(table, "ordinal", QueryType.String, true, 64);
        var id = new ColumnRef(table, "id", QueryType.Int64, false);
        var cases = new[]
        {
            ("unicode-I", folded, "i", new long[] { 3, 4 }),
            ("sharp-S-prefix", folded, "Straß", new long[] { 7 }),
            ("sharp-SS-prefix", folded, "STRAS", new long[] { 8 }),
            ("supplementary", folded, "𐐀", new long[] { 9, 10 }),
            ("unicode-maximum", folded, "\U0010FFFF", new long[] { 11 }),
            ("unicode-empty", folded, "", Enumerable.Range(2, 10).Select(value => (long)value).ToArray()),
            ("ascii-Turkish-I", ascii, "I", new long[] { 3, 4 }),
            ("ordinal-D7FF", ordinal, "\uD7FF", new long[] { 5 }),
            ("ordinal-maximum", ordinal, "\uDBFF\uDFFF", new long[] { 8, 9 }),
            ("ordinal-empty", ordinal, "", Enumerable.Range(2, 10).Select(value => (long)value).ToArray())
        };

        foreach (var (name, column, prefix, expected) in cases)
        {
            var request = new QueryRequest(
                table,
                new Predicate.StartsWith(column, prefix),
                [new OrderTerm(id, OrderDirection.Ascending, NullOrder.First)],
                Projection.ColumnsOnly(id, column),
                Paging.None);
            foreach (var provider in providers)
            {
                var actual = provider.Query(request, QueryRenderOptions.Default).Rows
                    .Select(row => (long)row["id"]!)
                    .ToArray();
                Assert.True(expected.SequenceEqual(actual),
                    $"{provider.Name}/{name}: expected [{string.Join(",", expected)}], actual [{string.Join(",", actual)}]");
            }
        }
    }

    [SkippableFact]
    public void Explain_assert_uses_a_selective_compound_index_after_postgresql_statistics_are_current()
    {
        Skip.If(!ExplainAssertionMode.Enabled, "Set GW_EXPLAIN_ASSERT=1 to run native plan proof.");
        var postgres = Required("GROUNDWORK_POSTGRES_CONNECTION");
        var sqlServer = LiveSqlServer.Required();
        var mongo = LiveMongo.Required();
        var rows = Enumerable.Range(1, 2_000)
            .Select(value => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["id"] = (long)value,
                ["numberValue"] = (decimal)value
            })
            .ToArray();
        using var sqlite = OpenSqlite(ExplainUnit, rows);
        using var pg = OpenPostgreSql(postgres, ExplainUnit, rows);
        using var sql = OpenSqlServer(sqlServer, ExplainUnit, rows);
        using var mongoSession = OpenMongo(mongo, ExplainUnit, rows);
        using (var analyzeConnection = new Npgsql.NpgsqlConnection(postgres))
        {
            analyzeConnection.Open();
            using var analyze = analyzeConnection.CreateCommand();
            analyze.CommandText = "ANALYZE \"" + ExplainTableName.Replace("\"", "\"\"", StringComparison.Ordinal) + "\";";
            analyze.ExecuteNonQuery();
        }
        var table = new TableId(ExplainTableName);
        var number = new ColumnRef(table, "numberValue", QueryType.Decimal, false, decimalPrecision: 18, decimalScale: 4);
        var id = new ColumnRef(table, "id", QueryType.Int64, false);
        var request = new QueryRequest(
            table,
            new Predicate.Equal(number, QueryConstant.Of(number, 1_999m)),
            [],
            Projection.ColumnsOnly(id, number),
            Paging.None);
        var options = new QueryRenderOptions(
            [new QueryIndexDeclaration("ix_number_id", [
                new QueryIndexColumn("numberValue", false, QueryType.Decimal),
                new QueryIndexColumn("id", false, QueryType.Int64)
            ], QueryIndexPinning.Pinned)],
            selectedIndex: "ix_number_id",
            tieBreakColumns: [id]);

        foreach (var provider in new[] { sqlite, pg, sql, mongoSession })
        {
            var result = provider.Query(request, options);
            Assert.Equal(1_999L, Assert.Single(result.Rows)["id"]);
        }
    }

    [SkippableFact]
    public void Scoped_queries_isolate_rows_counts_and_continuation_tokens_on_all_four_providers()
    {
        var postgres = Required("GROUNDWORK_POSTGRES_CONNECTION");
        var sqlServer = LiveSqlServer.Required();
        var mongo = LiveMongo.Required();
        var unit = ScopedUnit;
        using var sqlite = new SqliteProviderFactory().Create("Data Source=file:g2q4_scope_" + Guid.NewGuid().ToString("N") + "?mode=memory&cache=shared");
        using var pg = new PostgreSqlProviderFactory().Create(postgres);
        using var sql = new SqlServerProviderFactory().Create(sqlServer);
        var table = new TableId(ScopedTableName);
        var idColumn = new ColumnRef(table, "id", QueryType.Int64, false);
        var valueColumn = new ColumnRef(table, "value", QueryType.String, false);
        QueryRequest Request(Paging paging) => new(table, Predicate.AlwaysTrue.Instance,
            [new OrderTerm(idColumn, OrderDirection.Ascending, NullOrder.First)],
            Projection.ColumnsOnly(valueColumn), paging, ResultShape.TotalCount.Instance);
        foreach (var connection in new IStorageProviderConnection[] { sqlite, pg, sql })
        {
            connection.Schema.Apply(unit);
            var first = connection.OpenSession(unit, StorageAccess.Scoped(new StorageScope("scope-a")));
            var second = connection.OpenSession(unit, StorageAccess.Scoped(new StorageScope("scope-b")));
            foreach (var id in new[] { 1L, 2L })
            {
                first.Insert(new StorageValues(new Dictionary<string, object?> { ["id"] = id, ["value"] = "a" + id }));
                second.Insert(new StorageValues(new Dictionary<string, object?> { ["id"] = id, ["value"] = "b" + id }));
            }
            var firstPage = first.Query(Request(Paging.Keyset(1)));
            var secondPage = second.Query(Request(Paging.Keyset(1)));
            Assert.Equal(2L, firstPage.TotalCount);
            Assert.Equal(2L, secondPage.TotalCount);
            Assert.Equal("a1", firstPage.Rows.Single()["value"]);
            Assert.Equal("b1", secondPage.Rows.Single()["value"]);
            Assert.NotNull(firstPage.NextContinuationToken);
            Assert.Throws<QueryRenderException>(() => second.Query(Request(Paging.Continuation(firstPage.NextContinuationToken!, 1))));
        }

        using var mongoConnection = new MongoDbProviderFactory().Create(mongo);
        mongoConnection.Schema.Apply(unit);
        var mongoFirst = mongoConnection.OpenSession(unit, MongoStorageAccess.Scoped(new StorageScope("scope-a")));
        var mongoSecond = mongoConnection.OpenSession(unit, MongoStorageAccess.Scoped(new StorageScope("scope-b")));
        foreach (var id in new[] { 1L, 2L })
        {
            mongoFirst.Insert(new MongoStorageValues(new Dictionary<string, object?> { ["id"] = id, ["value"] = "a" + id }));
            mongoSecond.Insert(new MongoStorageValues(new Dictionary<string, object?> { ["id"] = id, ["value"] = "b" + id }));
        }
        var mongoFirstPage = mongoFirst.Query(Request(Paging.Keyset(1)));
        var mongoSecondPage = mongoSecond.Query(Request(Paging.Keyset(1)));
        Assert.Equal(2L, mongoFirstPage.TotalCount);
        Assert.Equal(2L, mongoSecondPage.TotalCount);
        Assert.Equal("a1", mongoFirstPage.Rows.Single()["value"]);
        Assert.Equal("b1", mongoSecondPage.Rows.Single()["value"]);
        Assert.NotNull(mongoFirstPage.NextContinuationToken);
        Assert.DoesNotContain("scope-a", mongoFirstPage.NextContinuationToken!, StringComparison.Ordinal);
        Assert.Throws<QueryRenderException>(() => mongoSecond.Query(Request(Paging.Continuation(mongoFirstPage.NextContinuationToken!, 1))));
    }

    [SkippableFact]
    public void Pinned_40_row_300_shape_corpus_is_bit_identical_through_public_provider_sessions()
    {
        var postgres = Required("GROUNDWORK_POSTGRES_CONNECTION");
        var sqlServer = LiveSqlServer.Required();
        var mongo = LiveMongo.Required();
        using var sqlite = OpenSqlite();
        using var pg = OpenPostgreSql(postgres);
        using var sql = OpenSqlServer(sqlServer);
        using var mongoSession = OpenMongo(mongo);
        var providers = new[] { sqlite, pg, sql, mongoSession };

        // This corpus proves four providers return bit-identical rows for 300 shapes over 40 rows.
        // It is not a native-plan proof and cannot be one at that size: the table is a single page,
        // so once PostgreSQL has statistics it correctly costs a sequential scan below an index scan
        // and declines ix_number. The assertion only ever passed while the table's statistics were
        // still absent, which made it a race against autovacuum — three tests here seed the same
        // table, crossing the analyze threshold partway through the class. The native-plan proof
        // lives in Explain_assert_uses_a_selective_compound_index_after_postgresql_statistics_are_current,
        // which seeds 2,000 rows and runs ANALYZE so the index really is the cheaper plan.
        // Rendering, index pinning, and refusal parity across providers are unchanged.
        using var withoutNativePlanClaim = ExplainAssertionMode.Suppress();

        Assert.Equal(G2Q1Corpus.ExpectedShapeCount, G2Q1Corpus.Shapes.Count);
        Assert.Equal(251, G2Q1Corpus.Shapes.Count(shape => shape.Decision == Q1CorpusDecision.Normalize));
        Assert.Equal(49, G2Q1Corpus.Shapes.Count(shape => shape.Decision == Q1CorpusDecision.Refuse));

        foreach (var shape in G2Q1Corpus.Shapes)
        {
            if (shape.PublicConstructionRejects)
            {
                Assert.ThrowsAny<ArgumentException>(() => shape.Exercise());
                continue;
            }

            var exercise = shape.Exercise();
            var request = Retarget(exercise.Request);
            var options = Options(request);
            var expectedValidation = PortableQuerySemantics.Validate(request);
            var observations = providers.Select(provider => Observe(provider, request, options)).ToArray();
            if (shape.Decision == Q1CorpusDecision.Normalize)
            {
                Assert.True(expectedValidation.IsPortable, $"{shape.Number}: {shape.Description}: {string.Join("; ", expectedValidation.Refusals.Select(refusal => refusal.Code))}");
                Assert.All(observations, observation => Assert.True(observation.IsSuccess, $"{shape.Number}: {shape.Description}: {observation.Error}"));
                var expected = Oracle(request, options);
                Assert.Equal(expected, observations[0].Result);
                Assert.All(observations.Skip(1), observation => Assert.True(
                    string.Equals(expected, observation.Result, StringComparison.Ordinal),
                    $"{shape.Number}: {shape.Description}: {observation.Provider} differed from the PortableQuerySemantics oracle. Expected={expected} Actual={observation.Result}"));
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
        var sqlServer = LiveSqlServer.Required();
        var mongo = LiveMongo.Required();
        using var sqlite = OpenSqlite();
        using var pg = OpenPostgreSql(postgres);
        using var sql = OpenSqlServer(sqlServer);
        using var mongoSession = OpenMongo(mongo);
        var providers = new[] { sqlite, pg, sql, mongoSession };
        var table = new TableId(RunTableName);
        var amount = new ColumnRef(table, "numberValue", QueryType.Decimal, isNullable: true, decimalPrecision: 18, decimalScale: 4);
        var id = new ColumnRef(table, "id", QueryType.Int64, isNullable: false);
        var firstRequest = new QueryRequest(
            table,
            Predicate.AlwaysTrue.Instance,
            [new OrderTerm(amount, OrderDirection.Ascending, NullOrder.First)],
            Projection.ColumnsOnly(amount),
            Paging.Keyset(5));
        var options = Options(firstRequest);

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
        var sqlServer = LiveSqlServer.Required();
        var mongo = LiveMongo.Required();
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
            var result = provider.Query(request, Options(request));
            Assert.Equal(40, result.TotalCount);
            Assert.Equal(40, result.Rows.Count);
            Assert.All(result.Rows, row =>
            {
                Assert.Equal(new[] { "id", "numberValue" }, row.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray());
                Assert.DoesNotContain(row.Keys, key => key.StartsWith("_groundwork_", StringComparison.Ordinal));
            });

            var allRequest = new QueryRequest(
                table,
                request.Where,
                request.Order,
                Projection.All,
                Paging.None,
                ResultShape.TotalCount.Instance);
            var allColumns = provider.Query(allRequest, Options(allRequest));
            Assert.Equal(40, allColumns.TotalCount);
            Assert.All(allColumns.Rows, row => Assert.DoesNotContain(
                row.Keys, key => key.StartsWith("_groundwork_", StringComparison.Ordinal)));

            var beyondEndRequest = new QueryRequest(
                table,
                request.Where,
                request.Order,
                request.Projection,
                Paging.OffsetLimit(100, 5),
                ResultShape.TotalCount.Instance);
            var beyondEnd = provider.Query(beyondEndRequest, Options(beyondEndRequest));
            Assert.Empty(beyondEnd.Rows);
            Assert.True(beyondEnd.TotalCount == 40, $"{provider.Name} reported {beyondEnd.TotalCount} for an empty counted page.");

            var firstCountedRequest = new QueryRequest(
                table,
                request.Where,
                request.Order,
                request.Projection,
                Paging.Keyset(5),
                ResultShape.TotalCount.Instance);
            var firstCountedPage = provider.Query(firstCountedRequest, Options(firstCountedRequest));
            Assert.Equal(40, firstCountedPage.TotalCount);
            Assert.NotNull(firstCountedPage.NextContinuationToken);
            var laterCountedRequest = new QueryRequest(
                table,
                request.Where,
                request.Order,
                request.Projection,
                Paging.Continuation(firstCountedPage.NextContinuationToken!, 5),
                ResultShape.TotalCount.Instance);
            var laterCountedPage = provider.Query(laterCountedRequest, Options(laterCountedRequest));
            Assert.Equal(40, laterCountedPage.TotalCount);
        }

        var hintedRequest = new QueryRequest(
            table,
            new Predicate.Equal(amount, QueryConstant.Of(amount, 10m)),
            [],
            Projection.ColumnsOnly(id, amount),
            Paging.None);
        var hinted = sql.Query(hintedRequest, Options(hintedRequest));
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

    [SkippableFact]
    public void Empty_in_keeps_a_pinned_partial_index_usable_on_all_four_providers()
    {
        var postgres = Required("GROUNDWORK_POSTGRES_CONNECTION");
        var sqlServer = LiveSqlServer.Required();
        var mongo = LiveMongo.Required();
        using var sqlite = OpenSqlite(SparseUnit, SparseRows);
        using var pg = OpenPostgreSql(postgres, SparseUnit, SparseRows);
        using var sql = OpenSqlServer(sqlServer, SparseUnit, SparseRows);
        using var mongoSession = OpenMongo(mongo, SparseUnit, SparseRows);
        var providers = new[] { sqlite, pg, sql, mongoSession };
        var number = new ColumnRef(new TableId(SparseTableName), "numberValue", QueryType.Decimal, isNullable: true, decimalPrecision: 18, decimalScale: 4);
        var request = new QueryRequest(
            number.Table,
            new Predicate.In(number, ImmutableArray<QueryConstant>.Empty),
            [],
            Projection.All,
            Paging.None);
        var options = new QueryRenderOptions(
            [new QueryIndexDeclaration("ix_sparse", [new QueryIndexColumn("numberValue", true, QueryType.Decimal)], QueryIndexPinning.Pinned, includesNulls: false)],
            selectedIndex: "ix_sparse");

        foreach (var provider in providers)
        {
            var result = provider.Query(request, options);
            Assert.Empty(result.Rows);
            Assert.Equal("ix_sparse", result.SelectedIndex);
        }
        Assert.True(sql.Query(request, options).IndexHintApplied);
        Assert.True(mongoSession.Query(request, options).IndexHintApplied);
    }

    [SkippableFact]
    public void Adversarial_scalar_ordering_matches_the_portable_oracle_on_all_four_providers()
    {
        var postgres = Required("GROUNDWORK_POSTGRES_CONNECTION");
        var sqlServer = LiveSqlServer.Required();
        var mongo = LiveMongo.Required();
        using var sqlite = OpenSqlite(SemanticEdgeUnit, SemanticEdgeRows);
        using var pg = OpenPostgreSql(postgres, SemanticEdgeUnit, SemanticEdgeRows);
        using var sql = OpenSqlServer(sqlServer, SemanticEdgeUnit, SemanticEdgeRows);
        using var mongoSession = OpenMongo(mongo, SemanticEdgeUnit, SemanticEdgeRows);
        var providers = new[] { sqlite, pg, sql, mongoSession };
        var table = new TableId(SemanticEdgeTableName);
        var cases = new[]
        {
            new ScalarOrderCase(
                "decimal(18,4)",
                new ColumnRef(table, "decimalValue", QueryType.Decimal, false, decimalPrecision: 18, decimalScale: 4),
                [12345678901234.1235m, 12345678901234.1234m]),
            new ScalarOrderCase(
                "UTF-16 ordinal text",
                new ColumnRef(table, "textValue", QueryType.String, false),
                ["\U00010000", "\uE000"]),
            new ScalarOrderCase(
                "UTC ticks",
                new ColumnRef(table, "instantValue", QueryType.DateTimeOffset, false),
                [new DateTimeOffset(638000000000000002L, TimeSpan.Zero), new DateTimeOffset(638000000000000001L, TimeSpan.Zero)]),
            new ScalarOrderCase(
                "RFC4122 GUID bytes",
                new ColumnRef(table, "guidValue", QueryType.Guid, false),
                [Guid.Parse("01112200-4455-6677-8899-aabbccddeeff"), Guid.Parse("00112233-4455-6677-8899-aabbccddeeff")])
        };

        foreach (var scalar in cases)
        {
            var expected = SemanticEdgeRows
                .Where(row => scalar.Values.Any(value => Equals(row[scalar.Column.Name], value)))
                .OrderBy(row => row, new PortableRowComparer([new OrderTerm(scalar.Column, OrderDirection.Ascending, NullOrder.First)]))
                .Select(row => (long)row["id"]!)
                .ToArray();
            Assert.Equal(2, expected.Length);
            var request = new QueryRequest(
                table,
                new Predicate.In(scalar.Column, scalar.Values.Select(value => QueryConstant.Of(scalar.Column, value))),
                [new OrderTerm(scalar.Column, OrderDirection.Ascending, NullOrder.First)],
                Projection.ColumnsOnly(new ColumnRef(table, "id", QueryType.Int64, false), scalar.Column),
                Paging.None);

            foreach (var provider in providers)
            {
                var result = provider.Query(request, QueryRenderOptions.Default);
                var actual = result.Rows.Select(row => (long)row["id"]!).ToArray();
                Assert.True(expected.SequenceEqual(actual), $"{provider.Name}/{scalar.Name}: expected [{string.Join(",", expected)}], actual [{string.Join(",", actual)}]");
            }
        }
    }

    [SkippableFact]
    public void Latest_per_key_is_native_and_preserves_full_count_across_pages_on_all_four_providers()
    {
        var postgres = Required("GROUNDWORK_POSTGRES_CONNECTION");
        var sqlServer = LiveSqlServer.Required();
        var mongo = LiveMongo.Required();
        using var sqlite = OpenSqlite(LatestUnit, LatestRows);
        using var pg = OpenPostgreSql(postgres, LatestUnit, LatestRows);
        using var sql = OpenSqlServer(sqlServer, LatestUnit, LatestRows);
        using var mongoSession = OpenMongo(mongo, LatestUnit, LatestRows);
        var providers = new[] { sqlite, pg, sql, mongoSession };
        var table = new TableId(LatestTableName);
        var group = new ColumnRef(table, "groupKey", QueryType.String, false);
        var timestamp = new ColumnRef(table, "createdAt", QueryType.DateTimeOffset, false);
        var id = new ColumnRef(table, "id", QueryType.Int64, false);
        var order = new[] { new OrderTerm(group, OrderDirection.Ascending, NullOrder.First) }.ToImmutableArray();
        var latest = new LatestPerKey(group, timestamp);
        var projection = Projection.ColumnsOnly(id, group, timestamp);

        foreach (var provider in providers)
        {
            var first = provider.Query(new QueryRequest(table, Predicate.AlwaysTrue.Instance, order, projection,
                Paging.Keyset(1), ResultShape.TotalCount.Instance, latest), QueryRenderOptions.Default);
            Assert.Equal(2, first.TotalCount);
            Assert.Equal(new[] { 2L }, first.Rows.Select(row => (long)row["id"]!).ToArray());
            Assert.NotNull(first.NextContinuationToken);

            var second = provider.Query(new QueryRequest(table, Predicate.AlwaysTrue.Instance, order, projection,
                Paging.Continuation(first.NextContinuationToken!, 1), ResultShape.TotalCount.Instance, latest), QueryRenderOptions.Default);
            Assert.Equal(2, second.TotalCount);
            Assert.Equal(new[] { 5L }, second.Rows.Select(row => (long)row["id"]!).ToArray());
            Assert.True(second.NextContinuationToken is null,
                $"{provider.Name}: the final latest-per-key page unexpectedly produced a continuation token.");

            var beyondEnd = provider.Query(new QueryRequest(table, Predicate.AlwaysTrue.Instance, order, projection,
                Paging.OffsetLimit(100, 1), ResultShape.TotalCount.Instance, latest), QueryRenderOptions.Default);
            Assert.Empty(beyondEnd.Rows);
            Assert.Equal(2, beyondEnd.TotalCount);
        }
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

    private static string Oracle(QueryRequest request, QueryRenderOptions options)
    {
        var rows = Rows
            .Where(row => PortableQuerySemantics.Evaluate(request.Where, row))
            .OrderBy(row => row, new PortableRowComparer(options.GetEffectiveOrder(request)))
            .ToArray();
        if (request.Paging.Offset is int offset)
            rows = rows.Skip(offset).ToArray();
        if (request.Paging.Limit is int limit)
            rows = rows.Take(limit).ToArray();

        var shaped = rows.Select(row => request.Projection.AllColumns
            ? row
            : (IReadOnlyDictionary<string, object?>)request.Projection.Columns
                .Where(column => row.ContainsKey(column.Name))
                .ToDictionary(column => column.Name, column => row[column.Name], StringComparer.Ordinal)).ToArray();
        return Canonical(new QueryMaterializedResult(shaped, null, null));
    }

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

    private sealed class PortableRowComparer(IReadOnlyList<OrderTerm> order) : IComparer<IReadOnlyDictionary<string, object?>>
    {
        public int Compare(IReadOnlyDictionary<string, object?>? left, IReadOnlyDictionary<string, object?>? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            foreach (var term in order)
            {
                left.TryGetValue(term.Column.Name, out var leftValue);
                right.TryGetValue(term.Column.Name, out var rightValue);
                var comparison = leftValue is null || rightValue is null
                    ? CompareNulls(leftValue, rightValue, term.NullOrder)
                    : CompareValue(leftValue, rightValue);
                if (comparison != 0)
                    return leftValue is not null && rightValue is not null && term.Direction == OrderDirection.Descending
                        ? -comparison
                        : comparison;
            }
            return 0;
        }

        private static int CompareNulls(object? left, object? right, NullOrder nullOrder)
        {
            return left is null && right is null ? 0 : left is null
                ? nullOrder == NullOrder.First ? -1 : 1
                : nullOrder == NullOrder.First ? 1 : -1;
        }

        private static int CompareValue(object left, object right)
        {
            if (left is string leftText && right is string rightText)
                return string.CompareOrdinal(leftText, rightText);
            if (left is DateTimeOffset leftInstant && right is DateTimeOffset rightInstant)
                return leftInstant.UtcTicks.CompareTo(rightInstant.UtcTicks);
            if (left is Guid leftGuid && right is Guid rightGuid)
                return CompareBytes(GuidBytes(leftGuid), GuidBytes(rightGuid));
            if (left is byte[] leftBytes && right is byte[] rightBytes)
                return CompareBytes(leftBytes, rightBytes);
            return ((IComparable)left).CompareTo(right);
        }

        private static byte[] GuidBytes(Guid value)
        {
            var text = value.ToString("N");
            var bytes = new byte[16];
            for (var index = 0; index < bytes.Length; index++)
                bytes[index] = byte.Parse(text.Substring(index * 2, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
            return bytes;
        }

        private static int CompareBytes(byte[] left, byte[] right)
        {
            for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
            {
                var comparison = left[index].CompareTo(right[index]);
                if (comparison != 0) return comparison;
            }
            return left.Length.CompareTo(right.Length);
        }
    }

    private static QueryRenderOptions Options(QueryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var coverageIndex = new CoverageIndex(
            "ix_number",
            [
                new CoverageIndexColumn("numberValue", OrderDirection.Ascending, isNullable: true),
                new CoverageIndexColumn("id", OrderDirection.Ascending, isNullable: false)
            ]);
        var id = new ColumnRef(new TableId(RunTableName), "id", QueryType.Int64, isNullable: false);
        var tieBreakOptions = new QueryRenderOptions(tieBreakColumns: [id]);
        var effectiveRequest = new QueryRequest(
            request.Table,
            request.Where,
            tieBreakOptions.GetEffectiveOrder(request),
            request.Projection,
            request.Paging,
            request.Result,
            request.LatestPerKey,
            request.AcceptedScan);
        var selected = request.Where is not Predicate.AlwaysTrue &&
                       PortableQuerySemantics.Validate(effectiveRequest).IsPortable &&
                       QueryCoverageChecker.Check(effectiveRequest, [coverageIndex]).Index is not null
            ? "ix_number"
            : null;
        return new QueryRenderOptions(
            indexes:
            [
                new QueryIndexDeclaration(
                    "ix_number",
                    [
                        new QueryIndexColumn("numberValue", true, QueryType.Decimal),
                        new QueryIndexColumn("id", false, QueryType.Int64)
                    ],
                    selected is null ? QueryIndexPinning.ProviderDefault : QueryIndexPinning.Pinned)
            ],
            selectedIndex: selected,
            tieBreakColumns: [id]);
    }

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
        Indexes = [new IndexDefinition { Name = "ix_number", Columns = [new IndexColumn("numberValue"), new IndexColumn("id")] }]
    };

    private static StorageUnit SparseUnit => new()
    {
        Id = new StorageUnitId(SparseTableName),
        Name = SparseTableName,
        Columns =
        [
            new() { Name = "id", Type = PortableType.Int64, IsNullable = false },
            new() { Name = "numberValue", Type = PortableType.Decimal, IsNullable = true, Precision = 18, Scale = 4 }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        Indexes =
        [
            new IndexDefinition
            {
                Name = "ix_sparse",
                Columns = [new IndexColumn("numberValue")],
                MissingValues = MissingValueBehavior.Excluded
            }
        ]
    };

    private static StorageUnit ExplainUnit => new()
    {
        Id = new StorageUnitId(ExplainTableName),
        Name = ExplainTableName,
        Columns =
        [
            new() { Name = "id", Type = PortableType.Int64, IsNullable = false },
            new() { Name = "numberValue", Type = PortableType.Decimal, IsNullable = false, Precision = 18, Scale = 4 }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        Indexes = [new IndexDefinition { Name = "ix_number_id", Columns = [new IndexColumn("numberValue"), new IndexColumn("id")] }]
    };

    private static StorageUnit PrefixUnit => new()
    {
        Id = new StorageUnitId(PrefixTableName),
        Name = PrefixTableName,
        Columns =
        [
            new() { Name = "id", Type = PortableType.Int64, IsNullable = false },
            new() { Name = "folded", Type = PortableType.String, IsNullable = true, MaxLength = 64, Collation = PortableCollation.UnicodeOrdinalIgnoreCase },
            new() { Name = "ascii", Type = PortableType.String, IsNullable = true, MaxLength = 64, Collation = PortableCollation.OrdinalIgnoreCase },
            new() { Name = "ordinal", Type = PortableType.String, IsNullable = true, MaxLength = 64, Collation = PortableCollation.Ordinal }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        Indexes =
        [
            new IndexDefinition { Name = "by_folded", Columns = [new IndexColumn("folded")] },
            new IndexDefinition { Name = "by_ascii", Columns = [new IndexColumn("ascii")] },
            new IndexDefinition { Name = "by_ordinal", Columns = [new IndexColumn("ordinal")] }
        ]
    };

    private static StorageUnit SemanticEdgeUnit => new()
    {
        Id = new StorageUnitId(SemanticEdgeTableName),
        Name = SemanticEdgeTableName,
        Columns =
        [
            new() { Name = "id", Type = PortableType.Int64, IsNullable = false },
            new() { Name = "decimalValue", Type = PortableType.Decimal, IsNullable = true, Precision = 18, Scale = 4 },
            new() { Name = "textValue", Type = PortableType.String, IsNullable = true, MaxLength = 64 },
            new() { Name = "instantValue", Type = PortableType.DateTimeOffset, IsNullable = true },
            new() { Name = "guidValue", Type = PortableType.Guid, IsNullable = true }
        ],
        Key = new KeyDefinition { Columns = ["id"] }
    };

    private static StorageUnit LatestUnit => new()
    {
        Id = new StorageUnitId(LatestTableName),
        Name = LatestTableName,
        Columns =
        [
            new() { Name = "id", Type = PortableType.Int64, IsNullable = false },
            new() { Name = "groupKey", Type = PortableType.String, IsNullable = false, MaxLength = 32 },
            new() { Name = "createdAt", Type = PortableType.DateTimeOffset, IsNullable = false },
            new() { Name = "value", Type = PortableType.Int32, IsNullable = false }
        ],
        Key = new KeyDefinition { Columns = ["id"] }
    };

    private static StorageUnit ScopedUnit => new()
    {
        Id = new StorageUnitId(ScopedTableName),
        Name = ScopedTableName,
        Scope = ScopePolicy.Scoped,
        Columns =
        [
            new() { Name = "id", Type = PortableType.Int64, IsNullable = false },
            new() { Name = "value", Type = PortableType.String, IsNullable = false, MaxLength = 32 }
        ],
        Key = new KeyDefinition { Columns = ["id"] }
    };

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> SemanticEdgeRows =>
    [
        new Dictionary<string, object?>
        {
            ["id"] = 1L,
            ["decimalValue"] = 12345678901234.1235m,
            ["textValue"] = null,
            ["instantValue"] = null,
            ["guidValue"] = null
        },
        new Dictionary<string, object?>
        {
            ["id"] = 2L,
            ["decimalValue"] = 12345678901234.1234m,
            ["textValue"] = null,
            ["instantValue"] = null,
            ["guidValue"] = null
        },
        new Dictionary<string, object?>
        {
            ["id"] = 3L,
            ["decimalValue"] = null,
            ["textValue"] = "\U00010000",
            ["instantValue"] = null,
            ["guidValue"] = null
        },
        new Dictionary<string, object?>
        {
            ["id"] = 4L,
            ["decimalValue"] = null,
            ["textValue"] = "\uE000",
            ["instantValue"] = null,
            ["guidValue"] = null
        },
        new Dictionary<string, object?>
        {
            ["id"] = 5L,
            ["decimalValue"] = null,
            ["textValue"] = null,
            ["instantValue"] = new DateTimeOffset(638000000000000002L, TimeSpan.Zero),
            ["guidValue"] = null
        },
        new Dictionary<string, object?>
        {
            ["id"] = 6L,
            ["decimalValue"] = null,
            ["textValue"] = null,
            ["instantValue"] = new DateTimeOffset(638000000000000001L, TimeSpan.Zero),
            ["guidValue"] = null
        },
        new Dictionary<string, object?>
        {
            ["id"] = 7L,
            ["decimalValue"] = null,
            ["textValue"] = null,
            ["instantValue"] = null,
            ["guidValue"] = Guid.Parse("01112200-4455-6677-8899-aabbccddeeff")
        },
        new Dictionary<string, object?>
        {
            ["id"] = 8L,
            ["decimalValue"] = null,
            ["textValue"] = null,
            ["instantValue"] = null,
            ["guidValue"] = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff")
        }
    ];

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> LatestRows =>
    [
        new Dictionary<string, object?> { ["id"] = 1L, ["groupKey"] = "a", ["createdAt"] = DateTimeOffset.UnixEpoch.AddTicks(10), ["value"] = 10 },
        new Dictionary<string, object?> { ["id"] = 2L, ["groupKey"] = "a", ["createdAt"] = DateTimeOffset.UnixEpoch.AddTicks(20), ["value"] = 20 },
        new Dictionary<string, object?> { ["id"] = 3L, ["groupKey"] = "a", ["createdAt"] = DateTimeOffset.UnixEpoch.AddTicks(20), ["value"] = 30 },
        new Dictionary<string, object?> { ["id"] = 4L, ["groupKey"] = "b", ["createdAt"] = DateTimeOffset.UnixEpoch.AddTicks(5), ["value"] = 40 },
        new Dictionary<string, object?> { ["id"] = 5L, ["groupKey"] = "b", ["createdAt"] = DateTimeOffset.UnixEpoch.AddTicks(15), ["value"] = 50 }
    ];

    private sealed record ScalarOrderCase(string Name, ColumnRef Column, IReadOnlyList<object> Values);

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> SparseRows =>
    [
        new Dictionary<string, object?> { ["id"] = 1L, ["numberValue"] = null },
        new Dictionary<string, object?> { ["id"] = 2L, ["numberValue"] = 2.5m }
    ];

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

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> PrefixRows =>
    [
        new Dictionary<string, object?> { ["id"] = 1L, ["folded"] = null, ["ascii"] = null, ["ordinal"] = null },
        new Dictionary<string, object?> { ["id"] = 2L, ["folded"] = string.Empty, ["ascii"] = string.Empty, ["ordinal"] = string.Empty },
        new Dictionary<string, object?> { ["id"] = 3L, ["folded"] = "I", ["ascii"] = "I", ["ordinal"] = "I" },
        new Dictionary<string, object?> { ["id"] = 4L, ["folded"] = "i", ["ascii"] = "i", ["ordinal"] = "i" },
        new Dictionary<string, object?> { ["id"] = 5L, ["folded"] = "İ", ["ascii"] = "S", ["ordinal"] = "\uD7FF" },
        new Dictionary<string, object?> { ["id"] = 6L, ["folded"] = "ı", ["ascii"] = "s", ["ordinal"] = "\U00010000" },
        new Dictionary<string, object?> { ["id"] = 7L, ["folded"] = "Straße", ["ascii"] = "Open", ["ordinal"] = "\uE000" },
        new Dictionary<string, object?> { ["id"] = 8L, ["folded"] = "STRASSE", ["ascii"] = "other", ["ordinal"] = "\uDBFF\uDFFF" },
        new Dictionary<string, object?> { ["id"] = 9L, ["folded"] = "𐐀", ["ascii"] = "TURKISH", ["ordinal"] = "\uDBFF\uDFFFsuffix" },
        new Dictionary<string, object?> { ["id"] = 10L, ["folded"] = "𐐨", ["ascii"] = "value", ["ordinal"] = "max" },
        new Dictionary<string, object?> { ["id"] = 11L, ["folded"] = "\U0010FFFF", ["ascii"] = "~", ["ordinal"] = "z" }
    ];

    private static CorpusSession OpenSqlite() => OpenSqlite(Unit, Rows);

    private static CorpusSession OpenSqlite(StorageUnit unit, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var connection = new SqliteProviderFactory().Create("Data Source=file:g2q4_" + Guid.NewGuid().ToString("N") + "?mode=memory&cache=shared");
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        foreach (var row in rows)
        {
            session.Delete(new StorageKey(new Dictionary<string, object?> { ["id"] = row["id"] }));
            session.Insert(new StorageValues(row));
        }
        return new CorpusSession("SQLite", session.Query, connection.Dispose);
    }

    private static CorpusSession OpenPostgreSql(string connectionString) => OpenPostgreSql(connectionString, Unit, Rows);

    private static CorpusSession OpenPostgreSql(string connectionString, StorageUnit unit, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var connection = new PostgreSqlProviderFactory().Create(connectionString);
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        foreach (var row in rows)
        {
            session.Delete(new StorageKey(new Dictionary<string, object?> { ["id"] = row["id"] }));
            session.Insert(new StorageValues(row));
        }
        return new CorpusSession("PostgreSQL", session.Query, connection.Dispose);
    }

    private static CorpusSession OpenSqlServer(string connectionString) => OpenSqlServer(connectionString, Unit, Rows);

    private static CorpusSession OpenSqlServer(string connectionString, StorageUnit unit, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var connection = new SqlServerProviderFactory().Create(connectionString);
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        foreach (var row in rows)
        {
            session.Delete(new StorageKey(new Dictionary<string, object?> { ["id"] = row["id"] }));
            session.Insert(new StorageValues(row));
        }
        return new CorpusSession("SQL Server", session.Query, connection.Dispose);
    }

    private static MongoCorpusSession OpenMongo(string connectionString) => OpenMongo(connectionString, Unit, Rows);

    private static MongoCorpusSession OpenMongo(string connectionString, StorageUnit unit, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var connection = new MongoDbProviderFactory().Create(connectionString);
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, MongoStorageAccess.Global);
        foreach (var row in rows)
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

    private sealed class ExplainEnvironment : IDisposable
    {
        private readonly string? previousFlag = Environment.GetEnvironmentVariable("GW_EXPLAIN_ASSERT");
        private readonly string? previousDirectory = Environment.GetEnvironmentVariable("GW_EXPLAIN_ARTIFACT_DIR");

        internal ExplainEnvironment(string label)
        {
            ArtifactDirectory = Path.Combine(Path.GetTempPath(), "groundwork-q11-" + label + "-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("GW_EXPLAIN_ASSERT", "1");
            Environment.SetEnvironmentVariable("GW_EXPLAIN_ARTIFACT_DIR", ArtifactDirectory);
        }

        internal string ArtifactDirectory { get; }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("GW_EXPLAIN_ASSERT", previousFlag);
            Environment.SetEnvironmentVariable("GW_EXPLAIN_ARTIFACT_DIR", previousDirectory);
            if (Directory.Exists(ArtifactDirectory)) Directory.Delete(ArtifactDirectory, recursive: true);
        }
    }

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
