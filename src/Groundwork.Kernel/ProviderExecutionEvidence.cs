using System.Collections.Immutable;
using Groundwork.Query.Model;

namespace Groundwork.Kernel;

/// <summary>Whether structured evidence was collected for a particular part of an execution.</summary>
public enum ProviderEvidenceAvailability
{
    /// <summary>No claim was made about availability.</summary>
    Unknown,

    /// <summary>The caller did not request this part of the evidence.</summary>
    NotRequested,

    /// <summary>The provider has not mapped this part of the evidence.</summary>
    Unsupported,

    /// <summary>The provider collected this part of the evidence.</summary>
    Collected,

    /// <summary>Collection was attempted but failed.</summary>
    Failed
}

/// <summary>The logical operation represented by one terminal provider observation.</summary>
public enum ProviderExecutionOperation
{
    Unknown,
    BoundedQuery,
    PointRead,
    Other
}

/// <summary>The role of the command within its logical operation.</summary>
public enum ProviderExecutionRole
{
    Unknown,
    Statement,
    Probe
}

/// <summary>The terminal outcome of the actual provider command.</summary>
public enum ProviderExecutionOutcome
{
    /// <summary>No terminal outcome was supplied; this value is never a successful claim.</summary>
    Unknown,

    Succeeded,
    Failed,
    Cancelled
}

/// <summary>A stable category for a failed command or failed plan collection.</summary>
public enum ProviderExecutionFailureCategory
{
    Unknown,
    Provider,
    Cancellation,
    PlanCollection
}

/// <summary>How a native plan observation was obtained.</summary>
public enum ProviderPlanProvenance
{
    Unknown,
    EstimatedExplain,
    ExplainReplay,
    OriginalExecutionTelemetry
}

/// <summary>How a storage scope was bound by the emitted command.</summary>
public enum ProviderScopeBindingMode
{
    Unknown,
    Unscoped,
    Predicate,
    PhysicalTarget,
    PrivilegedAcrossScopes
}

/// <summary>The source of a value-free predicate binding.</summary>
public enum ProviderPredicateBindingRole
{
    Unknown,
    Caller,
    Scope,
    Continuation
}

/// <summary>The closed predicate operators supported by the first evidence slice.</summary>
public enum ProviderPredicateOperator
{
    Unknown,
    Equal,
    In,
    LowerBound,
    UpperBound,
    /// <summary>A null test on the column; binds no value.</summary>
    IsNull,
    /// <summary>A non-null test on the column; binds no value.</summary>
    IsNotNull,
    /// <summary>An emitted contradiction (no row can satisfy the branch); binds no value.</summary>
    None
}

/// <summary>The comparison or collation semantics emitted for a predicate fact.</summary>
public enum ProviderPredicateComparison
{
    Unknown,
    Exact,
    Ordinal,
    UnicodeOrdinalIgnoreCase,
    AsciiIgnoreCase
}

/// <summary>Whether a range bound includes its endpoint.</summary>
public enum ProviderPredicateBoundInclusivity
{
    Unknown,
    NotApplicable,
    Inclusive,
    Exclusive
}

/// <summary>A provider-side transformation applied to one semantic order term.</summary>
public enum ProviderOrderingTransform
{
    Unknown,
    NullRank,
    OrdinalStringKey,
    PhysicalSearchKey
}

/// <summary>Whether a native offset or limit is absent, explicit, or unknown.</summary>
public enum ProviderNativeBoundKind
{
    Unknown,
    Absent,
    Explicit
}

/// <summary>The source of a point-read key equality.</summary>
public enum ProviderPointReadBindingRole
{
    Unknown,
    Key,
    Scope
}

/// <summary>Whether a point-read uniqueness witness was mapped by the provider.</summary>
public enum ProviderPointReadUniquenessStatus
{
    Unknown,
    NotObserved,
    Observed
}

/// <summary>The locking behavior emitted for a point read.</summary>
public enum ProviderPointReadLockMode
{
    Unknown,
    None,
    ForUpdate
}

/// <summary>An opaque, capture-local identity. Its contents are not a query or provider payload.</summary>
public sealed record ProviderOpaqueIdentity
{
    public ProviderOpaqueIdentity(Guid value)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("Opaque identities must be non-empty GUIDs.", nameof(value));

        Value = value;
    }

    public Guid Value { get; }

    public override string ToString() => Value.ToString("D");
}

/// <summary>Correlates one command and one native statement within a bounded evidence capture.</summary>
public sealed record ProviderExecutionIdentity
{
    public ProviderExecutionIdentity(
        ProviderOpaqueIdentity captureId,
        ProviderOpaqueIdentity invocationId,
        ProviderOpaqueIdentity commandId,
        ProviderOpaqueIdentity statementId,
        int commandOrdinal,
        int statementOrdinal)
    {
        CaptureId = captureId ?? throw new ArgumentNullException(nameof(captureId));
        InvocationId = invocationId ?? throw new ArgumentNullException(nameof(invocationId));
        CommandId = commandId ?? throw new ArgumentNullException(nameof(commandId));
        StatementId = statementId ?? throw new ArgumentNullException(nameof(statementId));
        if (commandOrdinal < 0)
            throw new ArgumentOutOfRangeException(nameof(commandOrdinal));
        if (statementOrdinal < 0)
            throw new ArgumentOutOfRangeException(nameof(statementOrdinal));
        CommandOrdinal = commandOrdinal;
        StatementOrdinal = statementOrdinal;
    }

    public ProviderOpaqueIdentity CaptureId { get; }
    public ProviderOpaqueIdentity InvocationId { get; }
    public ProviderOpaqueIdentity CommandId { get; }
    public ProviderOpaqueIdentity StatementId { get; }
    /// <summary>Zero-based command position within <see cref="InvocationId"/>, not within the capture.</summary>
    public int CommandOrdinal { get; }
    /// <summary>Zero-based native statement position within <see cref="CommandId"/>.</summary>
    public int StatementOrdinal { get; }
}

/// <summary>Logical and opaque physical target identities for one emitted command.</summary>
public sealed record ProviderExecutionTarget
{
    public ProviderExecutionTarget(
        StorageUnitId logicalUnitId,
        ProviderOpaqueIdentity physicalTargetId,
        ProviderScopeBindingMode scopeBinding)
    {
        if (string.IsNullOrWhiteSpace(logicalUnitId.Value))
            throw new ArgumentException("A logical storage-unit identity is required.", nameof(logicalUnitId));
        PhysicalTargetId = physicalTargetId ?? throw new ArgumentNullException(nameof(physicalTargetId));
        if (!Enum.IsDefined(scopeBinding))
            throw new ArgumentOutOfRangeException(nameof(scopeBinding));

        LogicalUnitId = logicalUnitId;
        ScopeBinding = scopeBinding;
    }

    public StorageUnitId LogicalUnitId { get; }
    public ProviderOpaqueIdentity PhysicalTargetId { get; }
    public ProviderScopeBindingMode ScopeBinding { get; }
}

/// <summary>One value-free predicate fact emitted as part of a complete conjunction.</summary>
public sealed record ProviderPredicateFact
{
    public ProviderPredicateFact(
        string logicalColumn,
        ProviderPredicateOperator @operator,
        QueryType valueType,
        ProviderPredicateComparison comparison,
        ProviderPredicateBoundInclusivity boundInclusivity,
        ProviderPredicateBindingRole bindingRole,
        ProviderOpaqueIdentity? bindingId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalColumn);
        if (!Enum.IsDefined(@operator) || @operator == ProviderPredicateOperator.Unknown)
            throw new ArgumentOutOfRangeException(nameof(@operator), @operator, "A known predicate operator is required.");
        var bindsValue = @operator is not (ProviderPredicateOperator.IsNull or ProviderPredicateOperator.IsNotNull or ProviderPredicateOperator.None);
        if (bindsValue != (bindingId is not null))
            throw new ArgumentException("Value-comparing predicates carry a binding identity; null tests and contradictions carry none.", nameof(bindingId));
        if (!Enum.IsDefined(valueType))
            throw new ArgumentOutOfRangeException(nameof(valueType));
        if (!Enum.IsDefined(comparison) || comparison == ProviderPredicateComparison.Unknown)
            throw new ArgumentOutOfRangeException(nameof(comparison), comparison, "Known comparison semantics are required.");
        if (!Enum.IsDefined(boundInclusivity) || boundInclusivity == ProviderPredicateBoundInclusivity.Unknown)
            throw new ArgumentOutOfRangeException(nameof(boundInclusivity), boundInclusivity, "Known bound inclusivity is required.");
        if (!Enum.IsDefined(bindingRole) || bindingRole == ProviderPredicateBindingRole.Unknown)
            throw new ArgumentOutOfRangeException(nameof(bindingRole), bindingRole, "A known binding role is required.");
        if (@operator is ProviderPredicateOperator.Equal or ProviderPredicateOperator.In or
            ProviderPredicateOperator.IsNull or ProviderPredicateOperator.IsNotNull or ProviderPredicateOperator.None)
        {
            if (boundInclusivity != ProviderPredicateBoundInclusivity.NotApplicable)
                throw new ArgumentException("Only range predicates have a range-bound inclusivity.", nameof(boundInclusivity));
        }
        else if (boundInclusivity == ProviderPredicateBoundInclusivity.NotApplicable)
        {
            throw new ArgumentException("Range predicates require explicit endpoint inclusivity.", nameof(boundInclusivity));
        }

        LogicalColumn = logicalColumn;
        Operator = @operator;
        ValueType = valueType;
        Comparison = comparison;
        BoundInclusivity = boundInclusivity;
        BindingRole = bindingRole;
        BindingId = bindingId;
    }

    public string LogicalColumn { get; }
    public ProviderPredicateOperator Operator { get; }
    public QueryType ValueType { get; }
    public ProviderPredicateComparison Comparison { get; }
    public ProviderPredicateBoundInclusivity BoundInclusivity { get; }
    public ProviderPredicateBindingRole BindingRole { get; }
    /// <summary>Null for null tests and contradictions, which bind no value.</summary>
    public ProviderOpaqueIdentity? BindingId { get; }
}

/// <summary>How a keyset continuation predicate was emitted.</summary>
public enum ProviderContinuationForm
{
    Unknown,
    /// <summary>A disjunction of lexicographic branches: branch <c>i</c> fixes the first <c>i</c> order terms and bounds term <c>i</c>.</summary>
    Lexicographic,
    /// <summary>One native row-value (tuple) comparison over every order term.</summary>
    Tuple
}

/// <summary>
/// One lexicographic keyset branch: equalities on the order prefix, then the boundary on the next
/// order term. A boundary that admits null rows records that the provider emitted the null
/// alternative alongside the strict bound.
/// </summary>
public sealed record ProviderContinuationBranch
{
    public ProviderContinuationBranch(
        IEnumerable<ProviderPredicateFact> equalities,
        ProviderPredicateFact boundary,
        bool boundaryAdmitsNull)
    {
        Equalities = (equalities ?? throw new ArgumentNullException(nameof(equalities))).ToImmutableArray();
        Boundary = boundary ?? throw new ArgumentNullException(nameof(boundary));
        if (Equalities.Any(fact => fact is null))
            throw new ArgumentException("Branch equalities cannot contain null references.", nameof(equalities));
        if (Equalities.Any(fact => fact.BindingRole != ProviderPredicateBindingRole.Continuation ||
                fact.Operator is not (ProviderPredicateOperator.Equal or ProviderPredicateOperator.IsNull)))
            throw new ArgumentException("Branch equalities are continuation-bound equalities or null tests.", nameof(equalities));
        if (Boundary.BindingRole != ProviderPredicateBindingRole.Continuation)
            throw new ArgumentException("The branch boundary is continuation-bound.", nameof(boundary));
        var bounded = Boundary.Operator is ProviderPredicateOperator.LowerBound or ProviderPredicateOperator.UpperBound;
        if (!bounded && Boundary.Operator is not (ProviderPredicateOperator.IsNotNull or ProviderPredicateOperator.None))
            throw new ArgumentException("The branch boundary is a strict bound, a non-null test or a contradiction.", nameof(boundary));
        if (bounded && Boundary.BoundInclusivity != ProviderPredicateBoundInclusivity.Exclusive)
            throw new ArgumentException("A keyset boundary is exclusive.", nameof(boundary));
        if (boundaryAdmitsNull && !bounded)
            throw new ArgumentException("Only a bounded boundary can admit null rows.", nameof(boundaryAdmitsNull));
        BoundaryAdmitsNull = boundaryAdmitsNull;
    }

    public ImmutableArray<ProviderPredicateFact> Equalities { get; }
    public ProviderPredicateFact Boundary { get; }
    public bool BoundaryAdmitsNull { get; }
}

/// <summary>
/// The value-free keyset continuation predicate a provider emitted for a page: either the
/// lexicographic disjunction (one branch per order term) or one native tuple comparison whose
/// bounds are the order terms in order. Consumers never reconstruct this from the request.
/// </summary>
public sealed record ProviderContinuationPredicate
{
    private ProviderContinuationPredicate(
        ProviderContinuationForm form,
        ImmutableArray<ProviderContinuationBranch> branches,
        ImmutableArray<ProviderPredicateFact> tupleBounds)
    {
        Form = form;
        Branches = branches;
        TupleBounds = tupleBounds;
    }

    public static ProviderContinuationPredicate Lexicographic(IEnumerable<ProviderContinuationBranch> branches)
    {
        var snapshot = (branches ?? throw new ArgumentNullException(nameof(branches))).ToImmutableArray();
        if (snapshot.Length == 0 || snapshot.Any(branch => branch is null))
            throw new ArgumentException("A lexicographic continuation has at least one non-null branch.", nameof(branches));
        for (var index = 0; index < snapshot.Length; index++)
        {
            if (snapshot[index].Equalities.Length != index)
                throw new ArgumentException($"Branch {index} must fix exactly {index} order terms before its boundary.", nameof(branches));
        }
        return new(ProviderContinuationForm.Lexicographic, snapshot, []);
    }

    public static ProviderContinuationPredicate Tuple(IEnumerable<ProviderPredicateFact> bounds)
    {
        var snapshot = (bounds ?? throw new ArgumentNullException(nameof(bounds))).ToImmutableArray();
        if (snapshot.Length < 2 || snapshot.Any(bound => bound is null))
            throw new ArgumentException("A tuple continuation bounds at least two order terms.", nameof(bounds));
        var @operator = snapshot[0].Operator;
        if (@operator is not (ProviderPredicateOperator.LowerBound or ProviderPredicateOperator.UpperBound) ||
            snapshot.Any(bound => bound.Operator != @operator ||
                bound.BoundInclusivity != ProviderPredicateBoundInclusivity.Exclusive ||
                bound.BindingRole != ProviderPredicateBindingRole.Continuation))
            throw new ArgumentException("Tuple bounds are exclusive continuation-bound bounds in one direction.", nameof(bounds));
        return new(ProviderContinuationForm.Tuple, [], snapshot);
    }

    public ProviderContinuationForm Form { get; }
    /// <summary>The lexicographic branches in emitted order; empty for the tuple form.</summary>
    public ImmutableArray<ProviderContinuationBranch> Branches { get; }
    /// <summary>The tuple's bounds in order-term order; empty for the lexicographic form.</summary>
    public ImmutableArray<ProviderPredicateFact> TupleBounds { get; }
}

/// <summary>Complete conjunction-only predicate evidence. Unsupported Boolean shapes have no instance.</summary>
public sealed record ProviderConjunctionPredicate
{
    public ProviderConjunctionPredicate(IEnumerable<ProviderPredicateFact> facts)
    {
        Facts = (facts ?? throw new ArgumentNullException(nameof(facts))).ToImmutableArray();
        if (Facts.Any(fact => fact is null))
            throw new ArgumentException("Predicate facts cannot contain null references.", nameof(facts));
    }

    public ImmutableArray<ProviderPredicateFact> Facts { get; }
}

/// <summary>One logical order term and its provider-emitted transforms.</summary>
public sealed record ProviderOrderTerm
{
    public ProviderOrderTerm(
        string logicalColumn,
        OrderDirection direction,
        NullOrder? nullPlacement,
        IEnumerable<ProviderOrderingTransform>? transforms = null,
        ProviderPredicateComparison comparison = ProviderPredicateComparison.Unknown)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalColumn);
        if (!Enum.IsDefined(direction))
            throw new ArgumentOutOfRangeException(nameof(direction));
        if (nullPlacement is { } placement && !Enum.IsDefined(placement))
            throw new ArgumentOutOfRangeException(nameof(nullPlacement));
        if (!Enum.IsDefined(comparison))
            throw new ArgumentOutOfRangeException(nameof(comparison));

        var transformSnapshot = (transforms ?? []).ToImmutableArray();
        if (transformSnapshot.Any(transform => !Enum.IsDefined(transform) || transform == ProviderOrderingTransform.Unknown))
            throw new ArgumentException("Ordering transforms must be known values.", nameof(transforms));

        LogicalColumn = logicalColumn;
        Direction = direction;
        NullPlacement = nullPlacement;
        Transforms = transformSnapshot;
        Comparison = comparison;
    }

    public string LogicalColumn { get; }
    public OrderDirection Direction { get; }
    /// <summary>Null when no null-placement expression was emitted; this is not a nullability witness.</summary>
    public NullOrder? NullPlacement { get; }
    public ImmutableArray<ProviderOrderingTransform> Transforms { get; }
    /// <summary>The comparison semantics of the emitted order expression; unknown is not ordinal.</summary>
    public ProviderPredicateComparison Comparison { get; }
}

/// <summary>The logical projection emitted by a bounded query.</summary>
public sealed record ProviderProjection
{
    public ProviderProjection(bool allColumns, IEnumerable<string> logicalColumns)
    {
        var columns = (logicalColumns ?? throw new ArgumentNullException(nameof(logicalColumns))).ToImmutableArray();
        if (columns.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Projection columns cannot be blank.", nameof(logicalColumns));
        if (allColumns && columns.Length != 0)
            throw new ArgumentException("An all-column projection cannot list selected columns.", nameof(logicalColumns));

        AllColumns = allColumns;
        LogicalColumns = columns;
    }

    public bool AllColumns { get; }
    public ImmutableArray<string> LogicalColumns { get; }
}

/// <summary>A value-free native offset or limit fact.</summary>
public readonly record struct ProviderNativeBound
{
    private ProviderNativeBound(ProviderNativeBoundKind kind, int? value)
    {
        Kind = kind;
        Value = value;
    }

    public ProviderNativeBoundKind Kind { get; }
    public int? Value { get; }

    public static ProviderNativeBound Unknown => new(ProviderNativeBoundKind.Unknown, null);
    public static ProviderNativeBound Absent => new(ProviderNativeBoundKind.Absent, null);

    public static ProviderNativeBound Explicit(int value)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(nameof(value));
        return new(ProviderNativeBoundKind.Explicit, value);
    }
}

/// <summary>Facts emitted for a bounded native query whose predicate is a complete conjunction.</summary>
public sealed record ProviderBoundedQueryEvidence
{
    public ProviderBoundedQueryEvidence(
        ProviderConjunctionPredicate predicate,
        IEnumerable<ProviderOrderTerm> ordering,
        ProviderProjection projection,
        ProviderNativeBound nativeOffset,
        ProviderNativeBound nativeLimit,
        bool hasContinuation,
        bool hasLookahead,
        bool includesTotalCount,
        ProviderContinuationPredicate? continuation = null)
    {
        Predicate = predicate ?? throw new ArgumentNullException(nameof(predicate));
        // A represented continuation is the only continuation a bounded shape can attest to.
        if (hasContinuation != (continuation is not null))
            throw new ArgumentException("A continuation page carries its emitted continuation predicate, and only then.", nameof(continuation));
        Ordering = (ordering ?? throw new ArgumentNullException(nameof(ordering))).ToImmutableArray();
        if (Ordering.Any(term => term is null))
            throw new ArgumentException("Ordering terms cannot contain null references.", nameof(ordering));
        Projection = projection ?? throw new ArgumentNullException(nameof(projection));
        ValidateBound(nativeOffset, nameof(nativeOffset), allowZero: true);
        ValidateBound(nativeLimit, nameof(nativeLimit), allowZero: false);
        if (hasLookahead && nativeLimit.Kind != ProviderNativeBoundKind.Explicit)
            throw new ArgumentException("Look-ahead requires an explicit native limit.", nameof(hasLookahead));

        NativeOffset = nativeOffset;
        NativeLimit = nativeLimit;
        HasContinuation = hasContinuation;
        HasLookahead = hasLookahead;
        IncludesTotalCount = includesTotalCount;
        Continuation = continuation;
    }

    public ProviderConjunctionPredicate Predicate { get; }
    public ImmutableArray<ProviderOrderTerm> Ordering { get; }
    public ProviderProjection Projection { get; }
    public ProviderNativeBound NativeOffset { get; }
    public ProviderNativeBound NativeLimit { get; }
    public bool HasContinuation { get; }
    public bool HasLookahead { get; }
    public bool IncludesTotalCount { get; }
    /// <summary>The emitted keyset continuation predicate; null when the page is the first page.</summary>
    public ProviderContinuationPredicate? Continuation { get; }

    private static void ValidateBound(ProviderNativeBound bound, string parameterName, bool allowZero)
    {
        if (!Enum.IsDefined(bound.Kind))
            throw new ArgumentOutOfRangeException(parameterName);
        if (bound.Kind != ProviderNativeBoundKind.Explicit)
        {
            if (bound.Value is not null)
                throw new ArgumentException("Unknown and absent bounds cannot carry a value.", parameterName);
            return;
        }

        if (bound.Value is not int value || (!allowZero && value == 0))
            throw new ArgumentOutOfRangeException(parameterName, bound.Value, "The explicit bound is outside its valid range.");
    }
}

/// <summary>One provider-rendered point-read key equality, without its value.</summary>
public sealed record ProviderPointReadKeyBound
{
    public ProviderPointReadKeyBound(
        string? logicalColumn,
        QueryType valueType,
        ProviderPointReadBindingRole bindingRole,
        ProviderOpaqueIdentity bindingId)
    {
        if (!Enum.IsDefined(valueType))
            throw new ArgumentOutOfRangeException(nameof(valueType));
        if (!Enum.IsDefined(bindingRole) || bindingRole == ProviderPointReadBindingRole.Unknown)
            throw new ArgumentOutOfRangeException(nameof(bindingRole), bindingRole, "A known point-read binding role is required.");
        if (bindingRole == ProviderPointReadBindingRole.Key)
            ArgumentException.ThrowIfNullOrWhiteSpace(logicalColumn);
        else if (logicalColumn is not null)
            throw new ArgumentException("Scope key bounds cannot expose a provider-owned column name.", nameof(logicalColumn));

        LogicalColumn = logicalColumn;
        ValueType = valueType;
        BindingRole = bindingRole;
        BindingId = bindingId;
    }

    public string? LogicalColumn { get; }
    public QueryType ValueType { get; }
    public ProviderPointReadBindingRole BindingRole { get; }
    public ProviderOpaqueIdentity BindingId { get; }
}

/// <summary>A provider-mapped, ordered uniqueness witness for a point read.</summary>
public sealed record ProviderPointReadUniqueness
{
    public ProviderPointReadUniqueness(
        ProviderPointReadUniquenessStatus status,
        IEnumerable<string>? enforcedKeyColumns = null,
        bool includesScopeBinding = false)
    {
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));

        EnforcedKeyColumns = (enforcedKeyColumns ?? []).ToImmutableArray();
        if (EnforcedKeyColumns.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Enforced key columns cannot be blank.", nameof(enforcedKeyColumns));
        if (EnforcedKeyColumns.Length != EnforcedKeyColumns.Distinct(StringComparer.Ordinal).Count())
            throw new ArgumentException("Enforced key columns cannot repeat.", nameof(enforcedKeyColumns));
        if (status == ProviderPointReadUniquenessStatus.Observed && EnforcedKeyColumns.Length == 0)
            throw new ArgumentException("An observed uniqueness witness requires its ordered key columns.", nameof(enforcedKeyColumns));
        if (status != ProviderPointReadUniquenessStatus.Observed && (EnforcedKeyColumns.Length != 0 || includesScopeBinding))
            throw new ArgumentException("Only an observed uniqueness witness can carry enforced key facts.", nameof(status));

        Status = status;
        IncludesScopeBinding = includesScopeBinding;
    }

    public ProviderPointReadUniquenessStatus Status { get; }
    public ImmutableArray<string> EnforcedKeyColumns { get; }
    public bool IncludesScopeBinding { get; }
}

/// <summary>Facts emitted for one provider-rendered point read.</summary>
public sealed record ProviderPointReadEvidence
{
    public ProviderPointReadEvidence(
        IEnumerable<ProviderPointReadKeyBound> keyBounds,
        ProviderPointReadUniqueness uniqueness,
        ProviderNativeBound nativeLimit,
        bool materializerReadsAtMostOne,
        ProviderPointReadLockMode lockMode)
    {
        KeyBounds = (keyBounds ?? throw new ArgumentNullException(nameof(keyBounds))).ToImmutableArray();
        if (KeyBounds.Length == 0 || KeyBounds.Any(bound => bound is null))
            throw new ArgumentException("A point read requires non-null key bounds.", nameof(keyBounds));
        var duplicate = KeyBounds
            .Where(bound => bound.BindingRole == ProviderPointReadBindingRole.Key)
            .GroupBy(bound => bound.LogicalColumn, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException("A point read cannot repeat a logical key column.", nameof(keyBounds));
        Uniqueness = uniqueness ?? throw new ArgumentNullException(nameof(uniqueness));
        if (!Enum.IsDefined(nativeLimit.Kind))
            throw new ArgumentOutOfRangeException(nameof(nativeLimit));
        if (nativeLimit.Kind == ProviderNativeBoundKind.Explicit && nativeLimit.Value is not > 0)
            throw new ArgumentOutOfRangeException(nameof(nativeLimit), nativeLimit.Value, "A native point-read limit must be positive.");
        if (nativeLimit.Kind != ProviderNativeBoundKind.Explicit && nativeLimit.Value is not null)
            throw new ArgumentException("Unknown and absent limits cannot carry a value.", nameof(nativeLimit));
        if (!Enum.IsDefined(lockMode))
            throw new ArgumentOutOfRangeException(nameof(lockMode));

        NativeLimit = nativeLimit;
        MaterializerReadsAtMostOne = materializerReadsAtMostOne;
        LockMode = lockMode;
    }

    public ImmutableArray<ProviderPointReadKeyBound> KeyBounds { get; }
    public ProviderPointReadUniqueness Uniqueness { get; }
    public ProviderNativeBound NativeLimit { get; }
    public bool MaterializerReadsAtMostOne { get; }
    public ProviderPointReadLockMode LockMode { get; }
    public bool IncludesScopeBinding => KeyBounds.Any(bound => bound.BindingRole == ProviderPointReadBindingRole.Scope);
}

/// <summary>Native-plan facts, intentionally limited to typed provider-owned facts.</summary>
public sealed record ProviderPlanEvidence
{
    public ProviderPlanEvidence(
        ProviderEvidenceAvailability availability,
        ProviderPlanProvenance? provenance = null,
        bool? choseExpectedIndex = null,
        string? expectedLogicalIndex = null,
        ProviderOpaqueIdentity? chosenPhysicalIndexId = null,
        ProviderExecutionFailureCategory? failureCategory = null,
        int? collectionCommandCount = null,
        ProviderPlanForest? winningPlan = null)
    {
        if (!Enum.IsDefined(availability))
            throw new ArgumentOutOfRangeException(nameof(availability));
        if (provenance is { } value && (!Enum.IsDefined(value) || value == ProviderPlanProvenance.Unknown))
            throw new ArgumentOutOfRangeException(nameof(provenance));
        if ((availability is ProviderEvidenceAvailability.Unknown or ProviderEvidenceAvailability.NotRequested or ProviderEvidenceAvailability.Unsupported) && provenance is not null)
            throw new ArgumentException("Plan provenance requires an attempted collection.", nameof(provenance));
        if (choseExpectedIndex is not null && string.IsNullOrWhiteSpace(expectedLogicalIndex))
            throw new ArgumentException("An index-choice fact requires its expected logical index identity.", nameof(expectedLogicalIndex));
        if (availability == ProviderEvidenceAvailability.Collected && provenance is null)
            throw new ArgumentException("Collected plan evidence requires explicit provenance.", nameof(provenance));
        if (availability == ProviderEvidenceAvailability.Collected &&
            choseExpectedIndex is null && chosenPhysicalIndexId is null && winningPlan is null)
            throw new ArgumentException("Collected plan evidence requires at least one typed mapped fact.", nameof(choseExpectedIndex));
        if (provenance == ProviderPlanProvenance.EstimatedExplain &&
            winningPlan?.Nodes.Any(node => node.Details?.Spill is not null) == true)
            throw new ArgumentException("Estimated plan evidence cannot carry observed runtime spill facts.", nameof(winningPlan));
        if (availability == ProviderEvidenceAvailability.Failed && failureCategory is null)
            throw new ArgumentException("Failed plan evidence requires a stable failure category.", nameof(failureCategory));
        if (availability != ProviderEvidenceAvailability.Failed && failureCategory is not null)
            throw new ArgumentException("A plan failure category requires failed plan evidence.", nameof(failureCategory));
        if (availability != ProviderEvidenceAvailability.Collected &&
            (choseExpectedIndex is not null || expectedLogicalIndex is not null || chosenPhysicalIndexId is not null || winningPlan is not null))
            throw new ArgumentException("Mapped plan facts require collected plan evidence.", nameof(availability));
        if (failureCategory is { } failure && (!Enum.IsDefined(failure) || failure == ProviderExecutionFailureCategory.Unknown))
            throw new ArgumentOutOfRangeException(nameof(failureCategory));
        if (collectionCommandCount is < 0)
            throw new ArgumentOutOfRangeException(nameof(collectionCommandCount));

        Availability = availability;
        Provenance = provenance;
        ChoseExpectedIndex = choseExpectedIndex;
        ExpectedLogicalIndex = expectedLogicalIndex;
        ChosenPhysicalIndexId = chosenPhysicalIndexId;
        FailureCategory = failureCategory;
        CollectionCommandCount = collectionCommandCount;
        WinningPlan = winningPlan;
    }

    public static ProviderPlanEvidence NotRequested { get; } = new(ProviderEvidenceAvailability.NotRequested);

    public ProviderEvidenceAvailability Availability { get; }
    public ProviderPlanProvenance? Provenance { get; }
    public bool? ChoseExpectedIndex { get; }
    /// <summary>The declared logical index identity the provider was asked to select.</summary>
    public string? ExpectedLogicalIndex { get; }

    /// <summary>The capture-local opaque identity of the physical index selected by the native plan.</summary>
    public ProviderOpaqueIdentity? ChosenPhysicalIndexId { get; }
    public ProviderExecutionFailureCategory? FailureCategory { get; }
    /// <summary>Additional native commands used to collect this plan; null means not mapped.</summary>
    public int? CollectionCommandCount { get; }
    /// <summary>The completely mapped native winning-plan operator structure; null means unknown, not empty.</summary>
    public ProviderPlanForest? WinningPlan { get; }
}

/// <summary>Options for the additive structured execution observer.</summary>
public sealed record ProviderExecutionEvidenceOptions
{
    public ProviderExecutionEvidenceOptions(bool collectNativePlans = false) => CollectNativePlans = collectNativePlans;

    /// <summary>Shape evidence is collected whenever the observer capability is attached.</summary>
    public bool CollectNativePlans { get; }

    public static ProviderExecutionEvidenceOptions ShapeOnly { get; } = new();
    public static ProviderExecutionEvidenceOptions ShapeAndPlans { get; } = new(collectNativePlans: true);
}

/// <summary>
/// Optional terminal structured evidence capability attached through the existing command-observer option.
/// Implementations still receive the legacy command event, while providers call <see cref="ObserveExecution"/>
/// only after the actual command has a terminal outcome.
/// </summary>
public interface IProviderExecutionObserver : IProviderCommandObserver
{
    ProviderExecutionEvidenceOptions EvidenceOptions { get; }

    void ObserveExecution(ProviderExecutionEvidence evidence);
}

/// <summary>One immutable terminal observation of an actual provider command.</summary>
public sealed record ProviderExecutionEvidence
{
    public ProviderExecutionEvidence(
        ProviderIdentity provider,
        ProviderExecutionOperation operation,
        ProviderCommandKind commandKind,
        ProviderExecutionRole role,
        ProviderExecutionIdentity identity,
        ProviderExecutionTarget target,
        ProviderExecutionOutcome outcome,
        ProviderExecutionFailureCategory? failureCategory,
        ProviderEvidenceAvailability shapeAvailability,
        ProviderBoundedQueryEvidence? boundedQuery = null,
        ProviderPointReadEvidence? pointRead = null,
        ProviderPlanEvidence? plan = null)
    {
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        if (!Enum.IsDefined(operation) || operation == ProviderExecutionOperation.Unknown)
            throw new ArgumentOutOfRangeException(nameof(operation), operation, "A known operation is required.");
        if (!Enum.IsDefined(commandKind))
            throw new ArgumentOutOfRangeException(nameof(commandKind));
        if (!Enum.IsDefined(role) || role == ProviderExecutionRole.Unknown)
            throw new ArgumentOutOfRangeException(nameof(role), role, "A known command role is required.");
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        Target = target ?? throw new ArgumentNullException(nameof(target));
        if (!Enum.IsDefined(outcome) || outcome == ProviderExecutionOutcome.Unknown)
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "A terminal command outcome is required.");
        if (outcome == ProviderExecutionOutcome.Succeeded && failureCategory is not null)
            throw new ArgumentException("A successful command cannot carry a failure category.", nameof(failureCategory));
        if ((outcome is ProviderExecutionOutcome.Failed or ProviderExecutionOutcome.Cancelled) && failureCategory is null)
            throw new ArgumentException("A failed or cancelled command requires a stable failure category.", nameof(failureCategory));
        if (failureCategory is { } failure && (!Enum.IsDefined(failure) || failure is ProviderExecutionFailureCategory.Unknown or ProviderExecutionFailureCategory.PlanCollection))
            throw new ArgumentOutOfRangeException(nameof(failureCategory));
        if (!Enum.IsDefined(shapeAvailability))
            throw new ArgumentOutOfRangeException(nameof(shapeAvailability));
        if (plan is null)
            throw new ArgumentNullException(nameof(plan));
        if (boundedQuery is not null && pointRead is not null)
            throw new ArgumentException("A terminal observation cannot carry both bounded-query and point-read shapes.", nameof(pointRead));
        if (operation == ProviderExecutionOperation.Other && plan.Availability == ProviderEvidenceAvailability.Collected)
            throw new ArgumentException("Other operations cannot claim collected plan evidence.", nameof(plan));

        if (shapeAvailability == ProviderEvidenceAvailability.Collected)
        {
            if (operation == ProviderExecutionOperation.BoundedQuery && boundedQuery is null)
                throw new ArgumentException("A collected bounded-query shape is required.", nameof(boundedQuery));
            if (operation == ProviderExecutionOperation.PointRead && pointRead is null)
                throw new ArgumentException("A collected point-read shape is required.", nameof(pointRead));
            if (operation == ProviderExecutionOperation.Other)
                throw new ArgumentException("Other operations cannot claim a collected shape.", nameof(shapeAvailability));
        }
        else if (boundedQuery is not null || pointRead is not null)
        {
            throw new ArgumentException("An unavailable shape cannot carry partial query or point-read facts.", nameof(shapeAvailability));
        }

        Operation = operation;
        CommandKind = commandKind;
        Role = role;
        Outcome = outcome;
        FailureCategory = failureCategory;
        ShapeAvailability = shapeAvailability;
        BoundedQuery = boundedQuery;
        PointRead = pointRead;
        Plan = plan;
    }

    public ProviderIdentity Provider { get; }
    public ProviderExecutionOperation Operation { get; }
    public ProviderCommandKind CommandKind { get; }
    public ProviderExecutionRole Role { get; }
    public ProviderExecutionIdentity Identity { get; }
    public ProviderExecutionTarget Target { get; }
    public ProviderExecutionOutcome Outcome { get; }
    public ProviderExecutionFailureCategory? FailureCategory { get; }
    public ProviderEvidenceAvailability ShapeAvailability { get; }
    public ProviderBoundedQueryEvidence? BoundedQuery { get; }
    public ProviderPointReadEvidence? PointRead { get; }
    public ProviderPlanEvidence Plan { get; }
}
