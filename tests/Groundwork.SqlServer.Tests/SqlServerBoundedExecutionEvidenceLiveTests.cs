using System.Text.Json;
using System.Xml.Linq;
using Groundwork.Kernel;
using Groundwork.Query.Model;
using Groundwork.SqlServer;
using Groundwork.Substrate.Relational;
using Groundwork.Store;
using Groundwork.LiveDatabases;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Groundwork.SqlServer.Tests;

/// <summary>
/// Live acceptance coverage for the provider-neutral bounded-query evidence contract. The rows are
/// written through public sessions so scope binding, native rendering, and result materialization
/// are exercised together.
/// </summary>
[Collection(SqlServerLiveDatabase.Name)]
public sealed class SqlServerBoundedExecutionEvidenceLiveTests(SqlServerFixture database)
{
    [Fact]
    public void Native_plan_diagnostic_reports_structure_without_native_values()
    {
        var summary = DescribePlan("""
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
              <RelOp NodeId="0" PhysicalOp="Sort" LogicalOp="TopN Sort"><TopSort Rows="3">
                <RelOp NodeId="1" PhysicalOp="Index Scan"><IndexScan>
                  <Object Database="[private-database]" Schema="[dbo]" Table="[private-table]" Index="[private-index]" />
                  <Predicate><ScalarOperator ScalarString="private-value" /></Predicate>
                </IndexScan></RelOp>
              </TopSort></RelOp>
            </ShowPlanXML>
            """, "private-database", "private-table", new(true, new HashSet<string> { "private-index" }));

        Assert.Contains("physical=Sort;logical=TopN Sort;payloads=TopSort;children=1", summary);
        Assert.Contains("target=True;catalogIndex=True", summary);
        Assert.DoesNotContain("private-", summary);
        Assert.Equal("Unknown", SafeOperator("private-operator"));
        Assert.Equal("Unknown", SafePayload("private-payload"));
    }

    [SkippableFact]
    public void Public_bounded_query_reports_scope_predicate_nullable_order_lookahead_and_projection()
    {
        using var fixture = new Fixture(database);

        var result = fixture.Session.Query(Query(fixture.Unit), fixture.Unit.CreateQueryRenderOptions());

        Assert.Equal(2, result.Rows.Count);
        Assert.All(result.Rows, row => Assert.Equal(2, row.Count));
        Assert.Equal("a-null", result.Rows[0]["id"]);
        Assert.Equal(Fixture.ScopeAPayloadNull, result.Rows[0]["payload"]);
        Assert.Equal("a-ready", result.Rows[1]["id"]);
        Assert.Equal(Fixture.ScopeAPayloadReady, result.Rows[1]["payload"]);
        Assert.NotNull(result.NextContinuationToken);

        var evidence = Assert.Single(fixture.Observer.Executions);
        Assert.Equal(ProviderExecutionOperation.BoundedQuery, evidence.Operation);
        Assert.Equal(ProviderExecutionOutcome.Succeeded, evidence.Outcome);
        Assert.Equal(ProviderEvidenceAvailability.Collected, evidence.ShapeAvailability);
        Assert.Equal(ProviderScopeBindingMode.Predicate, evidence.Target.ScopeBinding);
        Assert.Equal(fixture.Unit.Id, evidence.Target.LogicalUnitId);
        Assert.Equal("SQL Server", evidence.Provider.Name);
        Assert.Equal(ProviderEvidenceAvailability.NotRequested, evidence.Plan.Availability);
        Assert.Single(fixture.Observer.Commands);

        var shape = Assert.IsType<ProviderBoundedQueryEvidence>(evidence.BoundedQuery);
        Assert.Equal(2, shape.Predicate.Facts.Length);
        var caller = Assert.Single(shape.Predicate.Facts,
            fact => fact.BindingRole == ProviderPredicateBindingRole.Caller);
        Assert.Equal("category", caller.LogicalColumn);
        Assert.Equal(ProviderPredicateOperator.Equal, caller.Operator);
        Assert.Equal(QueryType.String, caller.ValueType);
        Assert.Equal(ProviderPredicateComparison.Ordinal, caller.Comparison);
        Assert.Equal(ProviderPredicateBoundInclusivity.NotApplicable, caller.BoundInclusivity);
        var scope = Assert.Single(shape.Predicate.Facts,
            fact => fact.BindingRole == ProviderPredicateBindingRole.Scope);
        Assert.Equal(ProviderPredicateOperator.Equal, scope.Operator);
        Assert.Equal(QueryType.String, scope.ValueType);
        Assert.Equal(ProviderPredicateComparison.Ordinal, scope.Comparison);
        Assert.NotEqual(caller.BindingId, scope.BindingId);

        Assert.Collection(shape.Ordering,
            status =>
            {
                Assert.Equal("status", status.LogicalColumn);
                Assert.Equal(OrderDirection.Ascending, status.Direction);
                Assert.Equal(NullOrder.First, status.NullPlacement);
                Assert.Contains(ProviderOrderingTransform.NullRank, status.Transforms);
                Assert.Contains(ProviderOrderingTransform.OrdinalStringKey, status.Transforms);
                Assert.Equal(ProviderPredicateComparison.Ordinal, status.Comparison);
            },
            id =>
            {
                Assert.Equal("id", id.LogicalColumn);
                Assert.Equal(OrderDirection.Ascending, id.Direction);
                // The request selects no index, but the unit-derived options declare id required, and
                // the renderer accepts that declaration as the non-null witness (#441): no null rank is
                // emitted and evidence records the plain ordinal ordering it actually emitted.
                Assert.Null(id.NullPlacement);
                Assert.Equal(
                    new[] { ProviderOrderingTransform.OrdinalStringKey },
                    id.Transforms.ToArray());
                Assert.Equal(ProviderPredicateComparison.Ordinal, id.Comparison);
            });

        Assert.False(shape.Projection.AllColumns);
        // Native paging retains the ordering key; public materialization hides that extra field.
        Assert.Equal(new[] { "id", "payload", "status" }, shape.Projection.LogicalColumns.ToArray());
        Assert.Equal(ProviderNativeBoundKind.Explicit, shape.NativeOffset.Kind);
        Assert.Equal(0, shape.NativeOffset.Value);
        Assert.Equal(ProviderNativeBoundKind.Explicit, shape.NativeLimit.Kind);
        Assert.Equal(3, shape.NativeLimit.Value);
        Assert.True(shape.HasLookahead);
        Assert.False(shape.HasContinuation);
        Assert.False(shape.IncludesTotalCount);

        var serialized = JsonSerializer.Serialize(evidence);
        Assert.DoesNotContain(Fixture.ScopeA, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(Fixture.ScopeB, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(Fixture.Category, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(Fixture.ScopeAPayloadNull, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(Fixture.ScopeAPayloadReady, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(Fixture.ScopeBPayload, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.ConnectionString, serialized, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Public_bounded_query_maps_the_actual_sql_server_plan_without_assuming_access_choice()
    {
        using var fixture = new Fixture(database);
        fixture.Observer.EvidenceOptions = ProviderExecutionEvidenceOptions.ShapeAndPlans;
        fixture.Observer.Commands.Clear();

        var result = fixture.Session.Query(PlanQuery(fixture.Unit));

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("a-null", result.Rows[0]["id"]);
        Assert.Equal("a-ready", result.Rows[1]["id"]);

        var evidence = Assert.Single(fixture.Observer.Executions);
        Assert.Equal(ProviderExecutionOutcome.Succeeded, evidence.Outcome);
        Assert.Equal(ProviderEvidenceAvailability.Collected, evidence.ShapeAvailability);
        if (evidence.Plan.Availability != ProviderEvidenceAvailability.Collected)
            Assert.Fail(CapturePlanDiagnostic(fixture));
        Assert.Equal(ProviderEvidenceAvailability.Collected, evidence.Plan.Availability);
        Assert.Equal(ProviderPlanProvenance.ExplainReplay, evidence.Plan.Provenance);
        Assert.Equal(4, evidence.Plan.CollectionCommandCount);
        Assert.Single(fixture.Observer.Commands);

        var forest = Assert.IsType<ProviderPlanForest>(evidence.Plan.WinningPlan);
        var access = Assert.Single(forest.Nodes, node => node.TargetId is not null);
        Assert.Equal(evidence.Target.PhysicalTargetId, access.TargetId);
        Assert.Contains(access.Operation, new[]
        {
            ProviderPlanOperator.IndexSearch,
            ProviderPlanOperator.IndexScan,
            ProviderPlanOperator.TableScan,
            ProviderPlanOperator.PrimaryKeySearch
        });
        // The declared index is category/status/id while this plan orders by payload. Every
        // supported native access path therefore requires a real native sort; the access choice
        // itself remains provider/optimizer-owned and is intentionally not pinned here.
        Assert.Contains(forest.Nodes, node => node.Operation is ProviderPlanOperator.Sort or ProviderPlanOperator.TopNSort);
        Assert.Contains(forest.Nodes, node => node.Operation is ProviderPlanOperator.Limit or ProviderPlanOperator.TopNSort);
    }

    private static QueryRequest Query(StorageUnit unit)
    {
        var table = new TableId(unit.Name);
        var id = new ColumnRef(table, "id", QueryType.String, isNullable: false, maxLength: 128);
        var category = new ColumnRef(table, "category", QueryType.String, isNullable: false, maxLength: 128);
        var status = new ColumnRef(table, "status", QueryType.String, isNullable: true, maxLength: 128);
        var payload = new ColumnRef(table, "payload", QueryType.String, isNullable: false, maxLength: 256);
        return new QueryRequest(
            table,
            new Predicate.Equal(category, QueryConstant.Of(category, Fixture.Category)),
            [
                new OrderTerm(status, OrderDirection.Ascending, NullOrder.First),
                new OrderTerm(id, OrderDirection.Ascending, NullOrder.First)
            ],
            Projection.ColumnsOnly(id, payload),
            Paging.Keyset(2));
    }

    private static QueryRequest PlanQuery(StorageUnit unit)
    {
        var table = new TableId(unit.Name);
        var id = new ColumnRef(table, "id", QueryType.String, isNullable: false, maxLength: 128);
        var category = new ColumnRef(table, "category", QueryType.String, isNullable: false, maxLength: 128);
        var payload = new ColumnRef(table, "payload", QueryType.String, isNullable: false, maxLength: 256);
        return new QueryRequest(
            table,
            new Predicate.Equal(category, QueryConstant.Of(category, Fixture.Category)),
            [new OrderTerm(payload, OrderDirection.Ascending, NullOrder.First)],
            Projection.ColumnsOnly(id, payload),
            Paging.Keyset(2));
    }

    private static string CapturePlanDiagnostic(Fixture fixture)
    {
        var stage = "prepare";
        try
        {
            var physicalUnit = SqlServerSchemaCoordinator.Physicalize(fixture.Unit);
            var physicalIndexNames = physicalUnit.Indexes.ToDictionary(
                index => index.Name,
                index => SqlServerDialect.PhysicalIndexName(physicalUnit.Name, index.Name),
                StringComparer.Ordinal);
            var prepared = RelationalSessionPolicy.PrepareQuery(
                physicalUnit,
                StorageAccess.Scoped(new StorageScope(Fixture.ScopeA)),
                PlanQuery(fixture.Unit),
                options: null,
                physicalIndexNames);
            var rendered = new SqlServerQueryRenderer().RenderForExecution(
                prepared.ExecutionRequest,
                prepared.RenderOptions,
                prepared.ExecutionRequest.Paging.Limit > prepared.ExecutionSource.Paging.Limit);
            var emittedCommand = fixture.Observer.Commands
                .LastOrDefault(command => command.Operation == "sqlserver.query")
                .CommandText;

            stage = "open";
            using var connection = new SqlConnection(new SqlConnectionStringBuilder(fixture.ConnectionString)
            {
                Pooling = false
            }.ConnectionString);
            connection.Open();
            stage = "catalog";
            var catalog = ReadCatalogWitness(connection, physicalUnit.Name);
            stage = "plan";
            var summaries = ReadPlanSummaries(connection, rendered.Command, connection.Database, physicalUnit.Name, catalog);
            var commandMatches = string.Equals(emittedCommand, rendered.Command.CommandText, StringComparison.Ordinal);
            return $"native-plan diagnostic: commandMatches={commandMatches}; catalogTarget={catalog.TargetFound}; catalogIndexCount={catalog.Indexes.Count}; planCount={summaries.Count}; " +
                   (summaries.Count == 0 ? "plans=none" : string.Join(" || ", summaries));
        }
        catch
        {
            // A diagnostic must not expose provider messages, SQL text, or plan values.
            return $"native-plan diagnostic unavailable: stage={stage}";
        }
    }

    private static CatalogWitness ReadCatalogWitness(SqlConnection connection, string target)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT t.name, i.name " +
            "FROM sys.tables AS t " +
            "JOIN sys.schemas AS s ON s.schema_id = t.schema_id " +
            "LEFT JOIN sys.indexes AS i ON i.object_id = t.object_id " +
            "AND i.index_id > 0 AND i.is_disabled = 0 AND i.is_hypothetical = 0 " +
            "WHERE s.name = @schema AND t.name = @table;";
        command.Parameters.AddWithValue("@schema", "dbo");
        command.Parameters.AddWithValue("@table", target);
        using var reader = command.ExecuteReader();
        var targetFound = false;
        var indexes = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            targetFound = true;
            if (!reader.IsDBNull(1))
                indexes.Add(reader.GetString(1));
        }

        return new(targetFound, indexes);
    }

    private static IReadOnlyList<string> ReadPlanSummaries(
        SqlConnection connection,
        RelationalQueryCommand query,
        string physicalDatabase,
        string physicalTarget,
        CatalogWitness catalog)
    {
        var summaries = new List<string>();
        var showplanEnabled = false;
        try
        {
            using (var enable = connection.CreateCommand())
            {
                enable.CommandText = "SET STATISTICS XML ON";
                showplanEnabled = true;
                enable.ExecuteNonQuery();
            }

            using var command = connection.CreateCommand();
            command.CommandText = query.CommandText;
            RelationalQueryResultReader.AddParameters(command, query);
            using var reader = command.ExecuteReader();
            do
            {
                while (reader.Read())
                {
                    for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
                    {
                        if (!string.Equals(
                                reader.GetName(ordinal),
                                SqlServerShowplanValueReader.ColumnName,
                                StringComparison.OrdinalIgnoreCase))
                            continue;
                        var plan = SqlServerShowplanValueReader.Read(
                            reader.GetName(ordinal),
                            reader.GetValue(ordinal));
                        if (plan is not null)
                            summaries.Add(DescribePlan(plan, physicalDatabase, physicalTarget, catalog));
                    }
                }
            } while (reader.NextResult());
        }
        finally
        {
            if (showplanEnabled)
            {
                using var disable = connection.CreateCommand();
                disable.CommandText = "SET STATISTICS XML OFF";
                disable.ExecuteNonQuery();
            }
        }

        return summaries;
    }

    private static string DescribePlan(
        string rawPlan,
        string physicalDatabase,
        string physicalTarget,
        CatalogWitness catalog)
    {
        var document = XDocument.Parse(rawPlan, LoadOptions.None);
        var root = document.Root;
        var rootNamespace = root?.Name == ShowplanNamespace + "ShowPlanXML";
        var relOps = document.Descendants()
            .Where(element => element.Name.LocalName == "RelOp")
            .Select(element => DescribeRelOp(element, physicalDatabase, physicalTarget, catalog))
            .ToArray();
        return $"root={SafePayload(root?.Name.LocalName)};rootNamespace={rootNamespace};relops=" +
               (relOps.Length == 0 ? "none" : string.Join("|", relOps));
    }

    private static string DescribeRelOp(
        XElement relOp,
        string physicalDatabase,
        string physicalTarget,
        CatalogWitness catalog)
    {
        var elements = relOp.Elements().ToArray();
        var objects = relOp.Descendants(ShowplanNamespace + "Object")
            .Where(element => element.Ancestors().FirstOrDefault(ancestor => ancestor.Name.LocalName == "RelOp") == relOp)
            .ToArray();
        var targetMatch = objects.Length == 1 && catalog.TargetFound &&
                          IsQuotedIdentifier(objects[0].Attribute("Database")?.Value, physicalDatabase) &&
                          IsQuotedIdentifier(objects[0].Attribute("Schema")?.Value, "dbo") &&
                          IsQuotedIdentifier(objects[0].Attribute("Table")?.Value, physicalTarget);
        var indexMatch = objects.Length == 1 &&
                         objects[0].Attribute("Index")?.Value is { } index &&
                         catalog.Indexes.Any(name => IsQuotedIdentifier(index, name));
        var namespaceValid = relOp.Name.Namespace == ShowplanNamespace &&
                             elements.All(element => element.Name.Namespace == ShowplanNamespace);
        var nodeId = int.TryParse((string?)relOp.Attribute("NodeId"), out var parsedId)
            ? parsedId.ToString()
            : "?";
        var payloads = elements.Length == 0
            ? "none"
            : string.Join(",", elements.Select(element => SafePayload(element.Name.LocalName)));
        var directChildren = relOp.Descendants()
            .Count(element => element.Name.LocalName == "RelOp" &&
                              element.Ancestors().FirstOrDefault(parent => parent.Name.LocalName == "RelOp") == relOp);
        return $"node={nodeId};physical={SafeOperator((string?)relOp.Attribute("PhysicalOp"))};logical={SafeOperator((string?)relOp.Attribute("LogicalOp"))};payloads={payloads};children={directChildren};namespace={namespaceValid};target={targetMatch};catalogIndex={indexMatch}";
    }

    private static bool IsQuotedIdentifier(string? value, string expected) =>
        string.Equals(value, "[" + expected + "]", StringComparison.Ordinal);

    private static string SafeOperator(string? value) =>
        value is not null && OperatorVocabulary.Contains(value) ? value : "Unknown";

    private static string SafePayload(string? value) =>
        value is not null && PayloadVocabulary.Contains(value) ? value : "Unknown";

    private static readonly XNamespace ShowplanNamespace =
        "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    private static readonly IReadOnlySet<string> OperatorVocabulary = new HashSet<string>(StringComparer.Ordinal)
    {
        "Index Seek", "Clustered Index Seek", "Index Scan", "Clustered Index Scan", "Table Scan",
        "Sort", "Top", "Top N Sort", "TopN Sort", "Compute Scalar", "Filter", "Key Lookup", "RID Lookup",
        "Nested Loops", "Hash Match", "Merge Join", "Stream Aggregate", "Hash Aggregate", "Aggregate",
        "Sequence Project", "Segment", "Window Aggregate", "Table Spool", "Index Spool", "Constant Scan",
        "Concatenation", "Assert", "Parallelism", "Bitmap"
    };

    private static readonly IReadOnlySet<string> PayloadVocabulary = new HashSet<string>(StringComparer.Ordinal)
    {
        "ShowPlanXML", "BatchSequence", "Batch", "Statements", "StmtSimple", "QueryPlan", "RelOp",
        "IndexScan", "TableScan", "Sort", "TopSort", "Top", "ComputeScalar", "Filter", "KeyLookup",
        "RIDLookup", "NestedLoops", "HashMatch", "MergeJoin", "StreamAggregate", "HashAggregate",
        "Aggregate", "SequenceProject", "Segment", "WindowAggregate", "TableSpool", "IndexSpool",
        "ConstantScan", "Concatenation", "Assert", "Parallelism", "Bitmap", "Object", "OutputList",
        "OrderBy", "OrderByColumn", "Predicate", "SeekPredicates", "DefinedValues", "RunTimeInformation",
        "Warnings", "MemoryFractions", "TopRows"
    };

    private sealed record CatalogWitness(bool TargetFound, IReadOnlySet<string> Indexes);

    private sealed class Fixture : IDisposable
    {
        internal const string ScopeA = "scope-a-secret";
        internal const string ScopeB = "scope-b-sentinel";
        internal const string Category = "category-secret";
        internal const string ScopeAPayloadNull = "payload-a-null-secret";
        internal const string ScopeAPayloadReady = "payload-a-ready-secret";
        internal const string ScopeBPayload = "payload-b-sentinel";

        private readonly IStorageProviderConnection connection;

        internal Fixture(SqlServerFixture database)
        {
            ConnectionString = database.Reset();
            connection = new SqlServerProviderFactory().Create(ConnectionString);
            var name = "w2_sqlserver_bounded_evidence_" + Guid.NewGuid().ToString("N");
            Unit = StorageUnit.Declare(name, name)
                .String("id", 128, column => column.Required())
                .String("category", 128, column => column.Required())
                .String("status", 128, column => column.Nullable())
                .String("payload", 256, column => column.Required())
                .Key("id")
                .Index("by_category_status_id", index => index
                    .Ascending("category")
                    .Ascending("status")
                    .Ascending("id"))
                .Scoped()
                .Build();

            try
            {
                Assert.True(connection.Schema.Apply(Unit).Applied);
                using (var scopeA = connection.OpenOwnedSession(
                           Unit,
                           StorageAccess.Scoped(new StorageScope(ScopeA))))
                {
                    Insert(scopeA, "a-null", null, ScopeAPayloadNull);
                    Insert(scopeA, "a-ready", "ready", ScopeAPayloadReady);
                    Insert(scopeA, "a-zulu", "zulu", "payload-a-zulu");
                    Insert(scopeA, "a-other", "other", "payload-a-other", "category-other");
                }

                using (var scopeB = connection.OpenOwnedSession(
                           Unit,
                           StorageAccess.Scoped(new StorageScope(ScopeB))))
                    Insert(scopeB, "b-wanted", null, ScopeBPayload);

                Observer = new EvidenceObserver();
                Session = connection.OpenOwnedSession(
                    Unit,
                    StorageAccess.Scoped(new StorageScope(ScopeA)),
                    Observer);
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        internal string ConnectionString { get; }
        internal StorageUnit Unit { get; }
        internal IOwnedStorageSession Session { get; }
        internal EvidenceObserver Observer { get; }

        public void Dispose()
        {
            Session.Dispose();
            connection.Dispose();
        }

        private static void Insert(
            IOwnedStorageSession session,
            string id,
            string? status,
            string payload,
            string category = Category)
        {
            Assert.Equal(WriteOutcomeStatus.Inserted, session.Insert(new StorageValues(
                new Dictionary<string, object?>
                {
                    ["id"] = id,
                    ["category"] = category,
                    ["status"] = status,
                    ["payload"] = payload
                })).Status);
        }
    }

    private sealed class EvidenceObserver : IProviderExecutionObserver
    {
        internal ProviderExecutionEvidenceOptions EvidenceOptions { get; set; } =
            ProviderExecutionEvidenceOptions.ShapeOnly;
        internal List<ProviderCommandEvent> Commands { get; } = [];
        internal List<ProviderExecutionEvidence> Executions { get; } = [];

        ProviderExecutionEvidenceOptions IProviderExecutionObserver.EvidenceOptions => EvidenceOptions;

        public void Observe(ProviderCommandEvent command) => Commands.Add(command);

        public void ObserveExecution(ProviderExecutionEvidence evidence) => Executions.Add(evidence);
    }
}
