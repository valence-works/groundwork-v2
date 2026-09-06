using Groundwork.Kernel;
using Groundwork.MongoDb;
using Groundwork.Query.Model;
using MongoDB.Bson;
using Xunit;

namespace Groundwork.MongoDb.Tests;

public sealed class MongoExecutionPlanCollectionTests
{
    [Fact]
    public void Explain_command_replays_the_emitted_find_shape_with_execution_stats()
    {
        var query = new MongoQueryCommand(
            new BsonDocument("status", "ready"),
            new BsonDocument("name", 1),
            new BsonDocument("_id", 0),
            skip: 2,
            limit: 3,
            hint: "status_name",
            includesTotalCount: false,
            isMatchNone: false,
            appliedOrder: ["name"]);

        var explain = MongoNativeExplainCommand.Build(query, "tenant_collection");
        var native = explain["explain"].AsBsonDocument;

        Assert.Equal("executionStats", explain["verbosity"].AsString);
        Assert.Equal("tenant_collection", native["find"].AsString);
        Assert.Equal("ready", native["filter"].AsBsonDocument["status"].AsString);
        Assert.Equal(1, native["sort"].AsBsonDocument["name"].ToInt32());
        Assert.Equal(0, native["projection"].AsBsonDocument["_id"].ToInt32());
        Assert.Equal(2, native["skip"].ToInt32());
        Assert.Equal(3, native["limit"].ToInt32());
        Assert.Equal("status_name", native["hint"].AsString);
    }

    [Fact]
    public void Explain_command_replays_the_emitted_aggregate_pipeline()
    {
        var query = new MongoQueryCommand(
            new BsonDocument(),
            new BsonDocument(),
            new BsonDocument(),
            skip: null,
            limit: null,
            hint: null,
            includesTotalCount: false,
            isMatchNone: false,
            appliedOrder: [],
            pipeline: [new BsonDocument("$match", new BsonDocument("state", "ready"))]);

        var explain = MongoNativeExplainCommand.Build(query, "physical_collection");
        var native = explain["explain"].AsBsonDocument;

        Assert.Equal("executionStats", explain["verbosity"].AsString);
        Assert.Equal("physical_collection", native["aggregate"].AsString);
        Assert.Single(native["pipeline"].AsBsonArray);
        Assert.Equal("ready", native["pipeline"].AsBsonArray[0].AsBsonDocument["$match"].AsBsonDocument["state"].AsString);
        Assert.True(native.Contains("cursor"));
        Assert.False(native.Contains("find"));
    }

    [Fact]
    public void Failed_plan_collection_is_separate_from_actual_outcome_and_hides_raw_failure()
    {
        var result = MongoNativePlanCollectionResult.Failed(
            new InvalidOperationException("secret Mongo explain payload"),
            legacyAssertionRequested: false);

        Assert.Equal(ProviderEvidenceAvailability.Failed, result.Evidence.Availability);
        Assert.Equal(ProviderExecutionFailureCategory.PlanCollection, result.Evidence.FailureCategory);
        Assert.Equal(1, result.Evidence.CollectionCommandCount);
        Assert.DoesNotContain("secret Mongo explain payload", result.Evidence.ToString(), StringComparison.Ordinal);
        var failure = Assert.Throws<InvalidOperationException>(() => result.AssertLegacy());
        Assert.Equal("secret Mongo explain payload", failure.Message);
    }

    [Fact]
    public void Legacy_explain_failure_is_rethrown_after_the_terminal_callback_path()
    {
        var result = MongoNativePlanCollectionResult.LegacyFailure(
            new InvalidOperationException("legacy explain failure"));

        Assert.Equal(ProviderEvidenceAvailability.NotRequested, result.Evidence.Availability);
        var failure = Assert.Throws<InvalidOperationException>(() => result.AssertLegacy());
        Assert.Equal("legacy explain failure", failure.Message);
    }

    [Fact]
    public void Requested_plan_failure_publishes_successful_actual_evidence_then_rethrows_original_diagnostic()
    {
        var observer = new RecordingObserver();
        var capture = new MongoExecutionEvidenceCapture(observer, CreateUnit(), MongoStorageAccess.Global);
        var invocation = capture.BeginInvocation();
        var diagnostic = new InvalidOperationException("explain diagnostic");
        var result = MongoNativePlanCollectionResult.Failed(diagnostic, legacyAssertionRequested: false);

        var failure = Assert.Throws<InvalidOperationException>(() =>
            MongoExecutionEvidenceCompletion.PublishSuccessfulQuery(
                capture,
                invocation,
                commandOrdinal: 0,
                boundedQuery: null,
                planCollection: result));

        Assert.Same(diagnostic, failure);
        var evidence = Assert.Single(observer.Observed);
        Assert.Equal(ProviderExecutionOutcome.Succeeded, evidence.Outcome);
        Assert.Equal(ProviderEvidenceAvailability.Failed, evidence.Plan.Availability);
        Assert.Equal(ProviderExecutionFailureCategory.PlanCollection, evidence.Plan.FailureCategory);
    }

    [Fact]
    public void Observer_failure_does_not_mask_pending_plan_diagnostic()
    {
        var observer = new RecordingObserver { ThrowOnObserve = true };
        var capture = new MongoExecutionEvidenceCapture(observer, CreateUnit(), MongoStorageAccess.Global);
        var diagnostic = new InvalidOperationException("explain diagnostic");
        var result = MongoNativePlanCollectionResult.Failed(diagnostic, legacyAssertionRequested: false);

        var failure = Assert.Throws<InvalidOperationException>(() =>
            MongoExecutionEvidenceCompletion.PublishSuccessfulQuery(
                capture,
                capture.BeginInvocation(),
                commandOrdinal: 0,
                boundedQuery: null,
                planCollection: result));

        Assert.Same(diagnostic, failure);
        Assert.Single(observer.Observed);
    }

    [Fact]
    public void Observer_failure_propagates_when_completion_has_no_pending_diagnostic()
    {
        var observer = new RecordingObserver { ThrowOnObserve = true };
        var capture = new MongoExecutionEvidenceCapture(observer, CreateUnit(), MongoStorageAccess.Global);

        var failure = Assert.Throws<InvalidOperationException>(() =>
            MongoExecutionEvidenceCompletion.PublishSuccessfulQuery(
                capture,
                capture.BeginInvocation(),
                commandOrdinal: 0,
                boundedQuery: null,
                planCollection: MongoNativePlanCollectionResult.NotRequested));

        Assert.Equal("observer failure", failure.Message);
        Assert.Single(observer.Observed);
    }

    [Fact]
    public void Observer_failure_does_not_mask_pending_legacy_assertion_failure()
    {
        var observer = new RecordingObserver { ThrowOnObserve = true };
        var capture = new MongoExecutionEvidenceCapture(observer, CreateUnit(), MongoStorageAccess.Global);
        var diagnostic = new InvalidOperationException("legacy explain diagnostic");
        var result = MongoNativePlanCollectionResult.LegacyFailure(diagnostic);

        var failure = Assert.Throws<InvalidOperationException>(() =>
            MongoExecutionEvidenceCompletion.PublishSuccessfulQuery(
                capture,
                capture.BeginInvocation(),
                commandOrdinal: 0,
                boundedQuery: null,
                planCollection: result));

        Assert.Same(diagnostic, failure);
        Assert.Single(observer.Observed);
    }

    [Fact]
    public void Unsupported_mapper_payload_does_not_leak_legacy_index_choice()
    {
        var observer = new RecordingObserver();
        var capture = new MongoExecutionEvidenceCapture(observer, CreateUnit(), MongoStorageAccess.Global);
        var query = new MongoQueryCommand(
            new BsonDocument(),
            new BsonDocument(),
            new BsonDocument(),
            skip: null,
            limit: 3,
            hint: "expected_1",
            includesTotalCount: false,
            isMatchNone: false,
            appliedOrder: [],
            expectedIndex: "expected");
        var options = new QueryRenderOptions(
            [new QueryIndexDeclaration("expected", ["status"])],
            selectedIndex: "expected") with
        {
            PhysicalIndexNames = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["expected"] = "expected_1"
            }
        };
        var result = MongoNativePlanCollectionResult.FromExplain(
            BsonDocument.Parse("""
            {
              "queryPlanner": {
                "namespace": "db.foreign",
                "winningPlan": { "stage": "IXSCAN", "indexName": "expected_1" }
              }
            }
            """),
            capture,
            query,
            "db.expected",
            options,
            legacyAssertionRequested: false);

        Assert.Equal(ProviderEvidenceAvailability.Unsupported, result.Evidence.Availability);
        Assert.Equal(1, result.Evidence.CollectionCommandCount);
        Assert.Null(result.Evidence.ExpectedLogicalIndex);
        Assert.Null(result.Evidence.ChoseExpectedIndex);
        Assert.Null(result.Evidence.ChosenPhysicalIndexId);
        Assert.Null(result.Evidence.WinningPlan);
    }

    [Fact]
    public void Structured_index_choice_comes_only_from_the_mapped_winning_forest()
    {
        var observer = new RecordingObserver();
        var capture = new MongoExecutionEvidenceCapture(observer, CreateUnit(), MongoStorageAccess.Global);
        var query = new MongoQueryCommand(
            new BsonDocument(),
            new BsonDocument(),
            new BsonDocument(),
            skip: null,
            limit: 3,
            hint: "other_1",
            includesTotalCount: false,
            isMatchNone: false,
            appliedOrder: [],
            expectedIndex: "expected");
        var options = new QueryRenderOptions(
            [
                new QueryIndexDeclaration("expected", ["status"]),
                new QueryIndexDeclaration("other", ["other"])
            ],
            selectedIndex: "expected") with
        {
            PhysicalIndexNames = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["expected"] = "expected_1",
                ["other"] = "other_1"
            }
        };
        var result = MongoNativePlanCollectionResult.FromExplain(
            BsonDocument.Parse("""
            {
              "queryPlanner": {
                "namespace": "db.expected",
                "winningPlan": {
                  "stage": "FETCH",
                  "inputStage": { "stage": "IXSCAN", "indexName": "other_1" }
                },
                "rejectedPlans": [
                  { "stage": "IXSCAN", "indexName": "expected_1" }
                ]
              }
            }
            """),
            capture,
            query,
            "db.expected",
            options,
            legacyAssertionRequested: false);

        Assert.Equal(ProviderEvidenceAvailability.Collected, result.Evidence.Availability);
        Assert.Equal(ProviderPlanProvenance.ExplainReplay, result.Evidence.Provenance);
        Assert.Equal("expected", result.Evidence.ExpectedLogicalIndex);
        Assert.False(result.Evidence.ChoseExpectedIndex);
        Assert.Null(result.Evidence.ChosenPhysicalIndexId);
        Assert.NotNull(result.Evidence.WinningPlan);
    }

    private static StorageUnit CreateUnit() => new()
    {
        Id = new StorageUnitId("mongo-plan-evidence"),
        Name = "mongo_plan_evidence",
        Columns = [new() { Name = "id", Type = PortableType.String, IsNullable = false }],
        Key = new KeyDefinition { Columns = ["id"] }
    };

    private sealed class RecordingObserver : IProviderExecutionObserver
    {
        public ProviderExecutionEvidenceOptions EvidenceOptions => ProviderExecutionEvidenceOptions.ShapeAndPlans;

        public List<ProviderExecutionEvidence> Observed { get; } = [];

        public bool ThrowOnObserve { get; init; }

        public void Observe(ProviderCommandEvent command)
        {
        }

        public void ObserveExecution(ProviderExecutionEvidence evidence)
        {
            Observed.Add(evidence);
            if (ThrowOnObserve)
                throw new InvalidOperationException("observer failure");
        }
    }
}
