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
            includesTotalCount: false,
            Continuation("updated", ProviderPredicateOperator.UpperBound));

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
            includesTotalCount: false,
            Continuation("updated", ProviderPredicateOperator.UpperBound));

    private static ProviderPointReadEvidence PointRead() =>
        new(
            new[] { KeyBound("id", ProviderPointReadBindingRole.Key, 60) },
            new ProviderPointReadUniqueness(ProviderPointReadUniquenessStatus.NotObserved),
            ProviderNativeBound.Absent,
            materializerReadsAtMostOne: true,
            lockMode: ProviderPointReadLockMode.None);

    private static ProviderContinuationPredicate Continuation(string column, ProviderPredicateOperator bound) =>
        ProviderContinuationPredicate.Lexicographic(
        [
            new ProviderContinuationBranch([], ContinuationFact(column, bound), boundaryAdmitsNull: true)
        ]);

    private static ProviderPredicateFact ContinuationFact(string column, ProviderPredicateOperator @operator) =>
        new(column, @operator, QueryType.Int64, ProviderPredicateComparison.Exact,
            @operator is ProviderPredicateOperator.LowerBound or ProviderPredicateOperator.UpperBound
                ? ProviderPredicateBoundInclusivity.Exclusive
                : ProviderPredicateBoundInclusivity.NotApplicable,
            ProviderPredicateBindingRole.Continuation,
            @operator is ProviderPredicateOperator.IsNull or ProviderPredicateOperator.IsNotNull or ProviderPredicateOperator.None
                ? null
                : new ProviderOpaqueIdentity(Guid.NewGuid()));

    /// <summary>#422: a continuation page carries its emitted predicate, and only then.</summary>
    [Fact]
    public void A_continuation_page_carries_its_represented_predicate_and_a_first_page_carries_none()
    {
        var predicate = new ProviderConjunctionPredicate([]);
        var order = new[] { new ProviderOrderTerm("updated", OrderDirection.Descending, null) };
        var projection = new ProviderProjection(true, []);

        var withoutShape = Assert.Throws<ArgumentException>(() => new ProviderBoundedQueryEvidence(
            predicate, order, projection, ProviderNativeBound.Absent, ProviderNativeBound.Explicit(2),
            hasContinuation: true, hasLookahead: true, includesTotalCount: false));
        Assert.Equal("continuation", withoutShape.ParamName);

        var firstPageWithShape = Assert.Throws<ArgumentException>(() => new ProviderBoundedQueryEvidence(
            predicate, order, projection, ProviderNativeBound.Absent, ProviderNativeBound.Explicit(2),
            hasContinuation: false, hasLookahead: true, includesTotalCount: false,
            Continuation("updated", ProviderPredicateOperator.UpperBound)));
        Assert.Equal("continuation", firstPageWithShape.ParamName);
    }

    [Fact]
    public void Null_tests_and_contradictions_bind_no_value_and_value_comparisons_must()
    {
        Assert.Null(ContinuationFact("updated", ProviderPredicateOperator.IsNull).BindingId);
        Assert.Null(ContinuationFact("updated", ProviderPredicateOperator.IsNotNull).BindingId);
        Assert.Null(ContinuationFact("updated", ProviderPredicateOperator.None).BindingId);
        Assert.NotNull(ContinuationFact("updated", ProviderPredicateOperator.Equal).BindingId);

        var boundWithoutBinding = Assert.Throws<ArgumentException>(() => new ProviderPredicateFact(
            "updated", ProviderPredicateOperator.Equal, QueryType.Int64, ProviderPredicateComparison.Exact,
            ProviderPredicateBoundInclusivity.NotApplicable, ProviderPredicateBindingRole.Continuation, null));
        Assert.Equal("bindingId", boundWithoutBinding.ParamName);
        var nullTestWithBinding = Assert.Throws<ArgumentException>(() => new ProviderPredicateFact(
            "updated", ProviderPredicateOperator.IsNull, QueryType.Int64, ProviderPredicateComparison.Exact,
            ProviderPredicateBoundInclusivity.NotApplicable, ProviderPredicateBindingRole.Continuation,
            new ProviderOpaqueIdentity(Guid.NewGuid())));
        Assert.Equal("bindingId", nullTestWithBinding.ParamName);
    }

    [Fact]
    public void Lexicographic_branches_fix_the_prefix_and_bound_the_next_term_and_tuples_bound_every_term_one_way()
    {
        var first = new ProviderContinuationBranch([], ContinuationFact("updated", ProviderPredicateOperator.UpperBound), false);
        var second = new ProviderContinuationBranch(
            [ContinuationFact("updated", ProviderPredicateOperator.Equal)],
            ContinuationFact("id", ProviderPredicateOperator.LowerBound), false);
        var lexicographic = ProviderContinuationPredicate.Lexicographic([first, second]);
        Assert.Equal(ProviderContinuationForm.Lexicographic, lexicographic.Form);
        Assert.Equal(2, lexicographic.Branches.Length);
        Assert.Empty(lexicographic.TupleBounds);

        Assert.Equal("branches", Assert.Throws<ArgumentException>(() => ProviderContinuationPredicate.Lexicographic([second])).ParamName);
        Assert.Equal("branches", Assert.Throws<ArgumentException>(() => ProviderContinuationPredicate.Lexicographic([])).ParamName);

        var nullCursor = new ProviderContinuationBranch([ContinuationFact("updated", ProviderPredicateOperator.IsNull)],
            ContinuationFact("id", ProviderPredicateOperator.IsNotNull), false);
        Assert.Equal(ProviderPredicateOperator.IsNotNull, nullCursor.Boundary.Operator);
        Assert.Equal("boundaryAdmitsNull", Assert.Throws<ArgumentException>(() => new ProviderContinuationBranch([],
            ContinuationFact("id", ProviderPredicateOperator.None), boundaryAdmitsNull: true)).ParamName);
        Assert.Equal("boundary", Assert.Throws<ArgumentException>(() => new ProviderContinuationBranch([],
            ContinuationFact("id", ProviderPredicateOperator.Equal), false)).ParamName);
        var callerBound = new ProviderPredicateFact("id", ProviderPredicateOperator.LowerBound, QueryType.Int64,
            ProviderPredicateComparison.Exact, ProviderPredicateBoundInclusivity.Exclusive, ProviderPredicateBindingRole.Caller,
            new ProviderOpaqueIdentity(Guid.NewGuid()));
        Assert.Equal("boundary", Assert.Throws<ArgumentException>(() => new ProviderContinuationBranch([], callerBound, false)).ParamName);

        var tuple = ProviderContinuationPredicate.Tuple(
        [
            ContinuationFact("updated", ProviderPredicateOperator.UpperBound),
            ContinuationFact("id", ProviderPredicateOperator.UpperBound)
        ]);
        Assert.Equal(ProviderContinuationForm.Tuple, tuple.Form);
        Assert.Empty(tuple.Branches);
        Assert.Equal(2, tuple.TupleBounds.Length);
        Assert.Equal("bounds", Assert.Throws<ArgumentException>(() => ProviderContinuationPredicate.Tuple(
            [ContinuationFact("updated", ProviderPredicateOperator.UpperBound)])).ParamName);
        Assert.Equal("bounds", Assert.Throws<ArgumentException>(() => ProviderContinuationPredicate.Tuple(
        [
            ContinuationFact("updated", ProviderPredicateOperator.UpperBound),
            ContinuationFact("id", ProviderPredicateOperator.LowerBound)
        ])).ParamName);
    }
}

public sealed class ProviderPlanWithheldReasonTests
{
    [Fact]
    public void Withheld_plan_evidence_names_its_structural_reason_and_command_count()
    {
        var evidence = ProviderPlanEvidence.Withheld(ProviderPlanWithheldReason.NoSinglePlan, collectionCommandCount: 4);

        Assert.Equal(ProviderEvidenceAvailability.Unsupported, evidence.Availability);
        Assert.Equal(ProviderPlanWithheldReason.NoSinglePlan, evidence.WithheldReason);
        Assert.Equal(4, evidence.CollectionCommandCount);
        Assert.Null(evidence.Provenance);
        Assert.Null(evidence.WinningPlan);
    }

    [Fact]
    public void A_withheld_reason_requires_unsupported_evidence_and_a_defined_value()
    {
        Assert.Throws<ArgumentException>(() => new ProviderPlanEvidence(
            ProviderEvidenceAvailability.NotRequested, withheldReason: ProviderPlanWithheldReason.NotAttempted));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProviderPlanEvidence(
            ProviderEvidenceAvailability.Unsupported, withheldReason: ProviderPlanWithheldReason.Unknown));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProviderPlanEvidence(
            ProviderEvidenceAvailability.Unsupported, withheldReason: (ProviderPlanWithheldReason)99));
        Assert.Null(new ProviderPlanEvidence(ProviderEvidenceAvailability.Unsupported).WithheldReason);
    }
}
