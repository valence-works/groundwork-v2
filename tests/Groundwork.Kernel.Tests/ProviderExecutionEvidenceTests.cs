using System.Reflection;
using Groundwork.Kernel;
using Groundwork.Query.Model;
using Xunit;

namespace Groundwork.Kernel.Tests;

public sealed class ProviderExecutionEvidenceTests
{
    [Fact]
    public void Default_enums_are_unknown_and_cannot_be_success_claims()
    {
        Assert.Equal(ProviderEvidenceAvailability.Unknown, default(ProviderEvidenceAvailability));
        Assert.Equal(ProviderExecutionOperation.Unknown, default(ProviderExecutionOperation));
        Assert.Equal(ProviderExecutionOutcome.Unknown, default(ProviderExecutionOutcome));
        Assert.Equal(ProviderPointReadUniquenessStatus.Unknown, default(ProviderPointReadUniquenessStatus));
        Assert.NotEqual(ProviderExecutionOutcome.Succeeded, default(ProviderExecutionOutcome));
    }

    [Fact]
    public void Collection_inputs_are_snapshotted()
    {
        var predicateFact = Predicate("tenant", ProviderPredicateOperator.Equal, ProviderPredicateBindingRole.Scope, 10);
        var facts = new List<ProviderPredicateFact> { predicateFact };
        var predicate = new ProviderConjunctionPredicate(facts);

        var transforms = new List<ProviderOrderingTransform> { ProviderOrderingTransform.PhysicalSearchKey };
        var order = new ProviderOrderTerm("updated", OrderDirection.Descending, NullOrder.Last, transforms);
        var ordering = new List<ProviderOrderTerm> { order };
        var columns = new List<string> { "id", "updated" };
        var projection = new ProviderProjection(false, columns);
        var bounded = new ProviderBoundedQueryEvidence(
            predicate,
            ordering,
            projection,
            ProviderNativeBound.Explicit(0),
            ProviderNativeBound.Explicit(2),
            hasContinuation: true,
            hasLookahead: true,
            includesTotalCount: false);

        var keyBounds = new List<ProviderPointReadKeyBound> { KeyBound("id", ProviderPointReadBindingRole.Key, 20) };
        var point = new ProviderPointReadEvidence(
            keyBounds,
            new ProviderPointReadUniqueness(ProviderPointReadUniquenessStatus.NotObserved),
            ProviderNativeBound.Absent,
            materializerReadsAtMostOne: true,
            lockMode: ProviderPointReadLockMode.None);

        facts.Clear();
        transforms.Clear();
        ordering.Clear();
        columns.Clear();
        keyBounds.Clear();

        Assert.Single(predicate.Facts);
        Assert.Single(bounded.Ordering);
        Assert.Single(bounded.Ordering[0].Transforms);
        Assert.Equal(new[] { "id", "updated" }, bounded.Projection.LogicalColumns);
        Assert.Single(point.KeyBounds);
    }

    [Fact]
    public void Successful_command_and_failed_plan_are_independently_representable()
    {
        var evidence = new ProviderExecutionEvidence(
            new ProviderIdentity("SQLite", "test"),
            ProviderExecutionOperation.BoundedQuery,
            ProviderCommandKind.Read,
            ProviderExecutionRole.Statement,
            Identity(),
            Target(),
            ProviderExecutionOutcome.Succeeded,
            failureCategory: null,
            shapeAvailability: ProviderEvidenceAvailability.Collected,
            boundedQuery: BoundedQuery(),
            plan: new ProviderPlanEvidence(
                ProviderEvidenceAvailability.Failed,
                ProviderPlanProvenance.ExplainReplay,
                failureCategory: ProviderExecutionFailureCategory.PlanCollection));

        Assert.Equal(ProviderExecutionOutcome.Succeeded, evidence.Outcome);
        Assert.Equal(ProviderEvidenceAvailability.Failed, evidence.Plan.Availability);
        Assert.Equal(ProviderPlanProvenance.ExplainReplay, evidence.Plan.Provenance);
        Assert.Equal(ProviderExecutionFailureCategory.PlanCollection, evidence.Plan.FailureCategory);
    }

    [Fact]
    public void Point_read_keeps_key_bound_uniqueness_native_limit_and_materialization_separate()
    {
        var relational = new ProviderPointReadEvidence(
            new[]
            {
                KeyBound("id", ProviderPointReadBindingRole.Key, 30),
                KeyBound(null, ProviderPointReadBindingRole.Scope, 31)
            },
            new ProviderPointReadUniqueness(ProviderPointReadUniquenessStatus.NotObserved),
            ProviderNativeBound.Absent,
            materializerReadsAtMostOne: true,
            lockMode: ProviderPointReadLockMode.None);
        var observed = new ProviderPointReadUniqueness(
            ProviderPointReadUniquenessStatus.Observed,
            new[] { "id", "scope" },
            includesScopeBinding: true);
        var mongo = new ProviderPointReadEvidence(
            new[] { KeyBound("id", ProviderPointReadBindingRole.Key, 32) },
            observed,
            ProviderNativeBound.Explicit(1),
            materializerReadsAtMostOne: true,
            lockMode: ProviderPointReadLockMode.None);

        Assert.True(relational.IncludesScopeBinding);
        Assert.Equal(ProviderNativeBoundKind.Absent, relational.NativeLimit.Kind);
        Assert.Null(relational.NativeLimit.Value);
        Assert.True(relational.MaterializerReadsAtMostOne);
        Assert.Equal(ProviderPointReadUniquenessStatus.NotObserved, relational.Uniqueness.Status);
        Assert.Equal(ProviderNativeBoundKind.Explicit, mongo.NativeLimit.Kind);
        Assert.Equal(1, mongo.NativeLimit.Value);
        Assert.Equal(new[] { "id", "scope" }, mongo.Uniqueness.EnforcedKeyColumns);
        Assert.True(mongo.Uniqueness.IncludesScopeBinding);
    }

    [Fact]
    public void Collected_plan_requires_at_least_one_typed_fact()
    {
        Assert.Throws<ArgumentException>(() => new ProviderPlanEvidence(
            ProviderEvidenceAvailability.Collected,
            ProviderPlanProvenance.ExplainReplay));
    }

    [Fact]
    public void Other_operation_cannot_claim_a_collected_plan_with_unsupported_shape()
    {
        Assert.Throws<ArgumentException>(() => new ProviderExecutionEvidence(
            new ProviderIdentity("SQLite", "test"),
            ProviderExecutionOperation.Other,
            ProviderCommandKind.Read,
            ProviderExecutionRole.Statement,
            Identity(),
            Target(),
            ProviderExecutionOutcome.Succeeded,
            failureCategory: null,
            shapeAvailability: ProviderEvidenceAvailability.Unsupported,
            plan: new ProviderPlanEvidence(
                ProviderEvidenceAvailability.Collected,
                ProviderPlanProvenance.ExplainReplay,
                choseExpectedIndex: true,
                expectedLogicalIndex: "ix_expected")));
    }

    [Fact]
    public void Invalid_or_partial_claims_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new ProviderOpaqueIdentity(Guid.Empty));
        Assert.Throws<ArgumentException>(() => Predicate(
            "id",
            ProviderPredicateOperator.Equal,
            ProviderPredicateBindingRole.Caller,
            40,
            ProviderPredicateBoundInclusivity.Inclusive));
        Assert.Throws<ArgumentException>(() => new ProviderPointReadUniqueness(
            ProviderPointReadUniquenessStatus.NotObserved,
            new[] { "id" }));
        Assert.Throws<ArgumentException>(() => new ProviderPlanEvidence(
            ProviderEvidenceAvailability.NotRequested,
            expectedLogicalIndex: "ix_expected"));
        Assert.Throws<ArgumentException>(() => new ProviderExecutionEvidence(
            new ProviderIdentity("SQLite", "test"),
            ProviderExecutionOperation.BoundedQuery,
            ProviderCommandKind.Read,
            ProviderExecutionRole.Statement,
            Identity(),
            Target(),
            ProviderExecutionOutcome.Succeeded,
            failureCategory: ProviderExecutionFailureCategory.Provider,
            shapeAvailability: ProviderEvidenceAvailability.Unsupported,
            plan: ProviderPlanEvidence.NotRequested));
        Assert.Throws<ArgumentException>(() => new ProviderExecutionEvidence(
            new ProviderIdentity("SQLite", "test"),
            ProviderExecutionOperation.BoundedQuery,
            ProviderCommandKind.Read,
            ProviderExecutionRole.Statement,
            Identity(),
            Target(),
            ProviderExecutionOutcome.Succeeded,
            failureCategory: null,
            shapeAvailability: ProviderEvidenceAvailability.Collected,
            boundedQuery: BoundedQuery(),
            pointRead: PointRead(),
            plan: ProviderPlanEvidence.NotRequested));
    }

    [Fact]
    public void Public_contract_has_no_raw_command_plan_or_object_payload_slots()
    {
        var contractTypes = new[]
        {
            typeof(ProviderExecutionEvidence),
            typeof(ProviderBoundedQueryEvidence),
            typeof(ProviderPointReadEvidence),
            typeof(ProviderPointReadKeyBound),
            typeof(ProviderPointReadUniqueness),
            typeof(ProviderConjunctionPredicate),
            typeof(ProviderPredicateFact),
            typeof(ProviderOrderTerm),
            typeof(ProviderProjection),
            typeof(ProviderPlanEvidence)
        };
        var properties = contractTypes.SelectMany(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public));
        var forbiddenNames = new[] { "CommandText", "RawPlan", "Parameters", "ParameterValues", "Filter", "Pipeline", "Document", "RawXml" };

        foreach (var property in properties)
        {
            Assert.DoesNotContain(property.Name, forbiddenNames);
            Assert.NotEqual(typeof(object), property.PropertyType);
        }
    }

    [Fact]
    public void Structured_observer_is_additive_to_legacy_command_observer()
    {
        Assert.True(typeof(IProviderCommandObserver).IsAssignableFrom(typeof(IProviderExecutionObserver)));
        Assert.NotNull(typeof(IProviderExecutionObserver).GetProperty(nameof(IProviderExecutionObserver.EvidenceOptions)));
        Assert.NotNull(typeof(IProviderExecutionObserver).GetMethod(nameof(IProviderExecutionObserver.ObserveExecution)));
    }

    private static ProviderExecutionIdentity Identity() => new(Opaque(1), Opaque(2), Opaque(3), Opaque(4), 0, 0);

    private static ProviderExecutionTarget Target() => new(new StorageUnitId("workflows"), Opaque(5), ProviderScopeBindingMode.Predicate);

    private static ProviderOpaqueIdentity Opaque(int value) => new(Guid.Parse($"00000000-0000-0000-0000-{value:D12}"));

    private static ProviderPredicateFact Predicate(
        string column,
        ProviderPredicateOperator @operator,
        ProviderPredicateBindingRole bindingRole,
        int identity,
        ProviderPredicateBoundInclusivity boundInclusivity = ProviderPredicateBoundInclusivity.NotApplicable) =>
        new(column, @operator, QueryType.Int64, ProviderPredicateComparison.Exact, boundInclusivity, bindingRole, Opaque(identity));

    private static ProviderPointReadKeyBound KeyBound(string? column, ProviderPointReadBindingRole role, int identity) =>
        new(column, QueryType.Guid, role, Opaque(identity));

    private static ProviderBoundedQueryEvidence BoundedQuery() =>
        new(
            new ProviderConjunctionPredicate(new[]
            {
                Predicate("tenant", ProviderPredicateOperator.Equal, ProviderPredicateBindingRole.Scope, 50),
                Predicate("updated", ProviderPredicateOperator.LowerBound, ProviderPredicateBindingRole.Continuation, 51, ProviderPredicateBoundInclusivity.Inclusive)
            }),
            new[] { new ProviderOrderTerm("updated", OrderDirection.Descending, NullOrder.Last) },
            new ProviderProjection(false, new[] { "id", "updated" }),
            ProviderNativeBound.Explicit(0),
            ProviderNativeBound.Explicit(2),
            hasContinuation: true,
            hasLookahead: true,
            includesTotalCount: false);

    private static ProviderPointReadEvidence PointRead() =>
        new(
            new[] { KeyBound("id", ProviderPointReadBindingRole.Key, 60) },
            new ProviderPointReadUniqueness(ProviderPointReadUniquenessStatus.NotObserved),
            ProviderNativeBound.Absent,
            materializerReadsAtMostOne: true,
            lockMode: ProviderPointReadLockMode.None);
}
